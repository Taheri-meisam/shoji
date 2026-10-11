using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Shoji.Core;

/// <summary>
/// OBS Studio remote control over obs-websocket 5 (built into OBS 28 and later, default port 4455), for the deck's
/// "obs" buttons. It only connects while something needs it: a dashboard deck with OBS buttons holds a lease
/// (<see cref="Use"/>), and pressing a button keeps it wanted for a minute. Without either there is no socket, no
/// task and no timer. State comes from OBS's events (no polling); <see cref="Changed"/> is raised on a background
/// thread, so hosts marshal to their UI thread.
/// </summary>
public sealed class ObsClient
{
    public static ObsClient Shared { get; } = new();

    ObsClient() { }

    public static string UnreachableMessage => L.T("OBS isn't running or its WebSocket server is off (OBS → Tools → WebSocket Server Settings)");

    /// <summary>The targets an "obs" deck button understands (the part before ':' for scene: / mute:).</summary>
    public static readonly IReadOnlyList<string> Commands = new[] { "record", "pause", "stream", "replay", "replaybuffer", "virtualcam", "scene", "mute" };

    // EventSubscription bits from the obs-websocket 5 protocol: General 1 (ExitStarted), Config 2, Scenes 4,
    // Inputs 8, Transitions 16, Filters 32, Outputs 64, SceneItems 128… The high-volume ones (volume meters,
    // active / show state) are 1 << 16 and up and are deliberately left out.
    const int SubGeneral = 1, SubScenes = 4, SubInputs = 8, SubOutputs = 64;
    const int EventSubscriptions = SubGeneral | SubScenes | SubInputs | SubOutputs;

    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2), LastRetry = TimeSpan.FromSeconds(60);

    readonly record struct Settings(string Host, int Port, string Password);

    sealed class Lease : IDisposable
    {
        public Lease(ObsClient owner, string[] inputs) { Owner = owner; Inputs = inputs; }
        public ObsClient Owner { get; }
        public string[] Inputs { get; }
        int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Owner.Release(this);
        }
    }

    readonly object _gate = new();
    readonly List<Lease> _leases = new();
    readonly SemaphoreSlim _wake = new(0);
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    Settings? _settings;
    Task? _loop;
    CancellationTokenSource? _session;
    ClientWebSocket? _ws; // set once identified
    DateTime _wantedUntil; // a button press keeps the connection wanted for a while without a lease
    int _attempts, _nextId;

    // State, guarded by _gate.
    bool _connected, _recording, _recordPaused, _streaming, _replayBuffer, _virtualCam;
    bool? _replayAvailable;
    string? _error, _version, _scene;
    IReadOnlyList<string> _scenes = Array.Empty<string>();
    readonly Dictionary<string, bool> _muted = new(StringComparer.Ordinal);
    readonly HashSet<string> _missingInputs = new(StringComparer.Ordinal);
    long _recBaseMs, _recSyncTicks; // recording time: _recBaseMs at Stopwatch time _recSyncTicks (frozen while paused)

    /// <summary>Raised (on a background thread) when anything the deck shows changed.</summary>
    public event Action? Changed;

    public bool Connected { get { lock (_gate) return _connected; } }
    /// <summary>Why the last attempt failed (null while connected or before the first attempt).</summary>
    public string? Error { get { lock (_gate) return _error; } }
    /// <summary>OBS's own version ("30.2.3").</summary>
    public string? Version { get { lock (_gate) return _version; } }
    public bool Recording { get { lock (_gate) return _recording; } }
    public bool RecordPaused { get { lock (_gate) return _recordPaused; } }
    public bool Streaming { get { lock (_gate) return _streaming; } }
    public bool ReplayBuffer { get { lock (_gate) return _replayBuffer; } }
    /// <summary>False when OBS has no replay buffer set up (Settings → Output → Replay Buffer); null = unknown.</summary>
    public bool? ReplayAvailable { get { lock (_gate) return _replayAvailable; } }
    public bool VirtualCam { get { lock (_gate) return _virtualCam; } }
    public string? CurrentScene { get { lock (_gate) return _scene; } }
    /// <summary>Scene names in the order OBS's Scenes dock shows them (top first).</summary>
    public IReadOnlyList<string> Scenes { get { lock (_gate) return _scenes; } }

    /// <summary>Whether an input (e.g. "Mic/Aux") is muted; null when unknown (not tracked, or OBS has no such input).</summary>
    public bool? Muted(string input)
    {
        lock (_gate)
            return _muted.TryGetValue(input.Trim(), out var m) ? m : null;
    }

    /// <summary>How long the current recording has run (not counting pauses), computed locally from OBS's figure.</summary>
    public TimeSpan RecordElapsed
    {
        get
        {
            lock (_gate)
                return _recording ? RecordElapsedLocked() : TimeSpan.Zero;
        }
    }

    // ---------------------------------------------------------------- who needs the connection

    /// <summary>
    /// Keeps OBS connected (reconnecting with backoff) until the returned lease is disposed. <paramref name="inputs"/>
    /// are input names whose mute state should be followed (mute:… buttons). New settings reconnect.
    /// </summary>
    public IDisposable Use(ObsConfig config, IEnumerable<string>? inputs = null)
    {
        var lease = new Lease(this, (inputs ?? Array.Empty<string>()).Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToArray());
        var settings = From(config);
        bool restart, fetchMutes;
        lock (_gate)
        {
            _leases.Add(lease);
            restart = _settings != null && _settings != settings;
            _settings = settings;
            fetchMutes = _connected && !restart && lease.Inputs.Any(i => !_muted.ContainsKey(i));
        }
        if (restart)
        {
            Log.Write("OBS: connection settings changed, reconnecting");
            CancelSession();
        }
        Kick();
        if (fetchMutes)
            _ = FetchMutesAsync(lease.Inputs);
        return lease;
    }

    void Release(Lease lease)
    {
        lock (_gate)
            _leases.Remove(lease);
        // The dashboard rebuilds its tiles on every settings change: wait a moment before hanging up, so a rebuild
        // doesn't drop and redo the connection.
        HangUpWhenIdle(TimeSpan.FromSeconds(5));
    }

    /// <summary>After <paramref name="delay"/>, closes the connection if nothing wants it any more.</summary>
    void HangUpWhenIdle(TimeSpan delay)
    {
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            bool idle;
            lock (_gate)
                idle = !Wanted();
            if (idle)
                CancelSession();
        }, TaskScheduler.Default);
    }

    bool Wanted() => _leases.Count > 0 || DateTime.UtcNow < _wantedUntil; // under _gate

    HashSet<string> TrackedInputs()
    {
        lock (_gate)
            return _leases.SelectMany(l => l.Inputs).ToHashSet(StringComparer.Ordinal);
    }

    static Settings From(ObsConfig? c) =>
        new(string.IsNullOrWhiteSpace(c?.Host) ? "localhost" : c!.Host.Trim(), c?.Port is > 0 and < 65536 ? c.Port : 4455, c?.Password ?? "");

    /// <summary>Starts the connection loop if it isn't running, or cuts its retry wait short.</summary>
    void Kick()
    {
        lock (_gate)
        {
            if (_loop == null)
            {
                _loop = Task.Run(LoopAsync);
                return;
            }
        }
        _wake.Release();
    }

    void CancelSession()
    {
        CancellationTokenSource? s;
        lock (_gate)
            s = _session;
        try { s?.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task LoopAsync()
    {
        var delay = FirstRetry;
        while (true)
        {
            Settings settings;
            CancellationTokenSource session;
            lock (_gate)
            {
                if (!Wanted() || _settings == null)
                {
                    _loop = null;
                    return;
                }
                settings = _settings.Value;
                session = _session = new CancellationTokenSource();
            }

            bool opened = false;
            string? error = null;
            try
            {
                using var ws = await OpenAsync(settings, session.Token).ConfigureAwait(false);
                opened = true;
                await ReceiveLoopAsync(ws, session.Token).ConfigureAwait(false);
                if (!session.IsCancellationRequested)
                    error = UnreachableMessage; // OBS closed the connection (quit, or the server was switched off)
            }
            catch (Exception ex) when (session.IsCancellationRequested && ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // We hung up on purpose (nobody needs OBS, or the settings changed).
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                Log.WriteThrottled("obs-connect", $"OBS: {error} ({settings.Host}:{settings.Port})", TimeSpan.FromMinutes(10));
            }
            bool settingsChanged;
            lock (_gate)
            {
                _ws = null;
                _connected = false;
                _attempts++;
                if (error != null || opened)
                    _error = error;
                settingsChanged = _settings != settings;
                _session = null;
            }
            session.Dispose();
            FailPending();
            RaiseChanged();

            if (opened)
                delay = FirstRetry;
            if (settingsChanged)
                continue;
            lock (_gate)
            {
                if (!Wanted())
                    continue; // the top of the loop ends it
            }
            // Back off (2 s, 4 s … 60 s); a new lease or a button press cuts the wait short.
            await _wake.WaitAsync(delay).ConfigureAwait(false);
            while (_wake.Wait(0)) { }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LastRetry.Ticks));
        }
    }

    // ---------------------------------------------------------------- protocol

    /// <summary>Connects, says hello (with the password if OBS asks), and reads the state the deck shows.</summary>
    async Task<ClientWebSocket> OpenAsync(Settings s, CancellationToken ct, bool readState = true)
    {
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.AddSubProtocol("obswebsocket.json");
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await ws.ConnectAsync(new Uri($"ws://{(s.Host.Contains(':') && !s.Host.StartsWith('[') ? $"[{s.Host}]" : s.Host)}:{s.Port}"), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ObsException(UnreachableMessage);
                }

                // Hello (op 0) → Identify (op 1) → Identified (op 2).
                var buffer = new byte[16 * 1024];
                using var ms = new MemoryStream();
                using (var hello = await ReceiveAsync(ws, buffer, ms, timeout.Token).ConfigureAwait(false) ?? throw Closed(ws))
                {
                    var root = hello.RootElement;
                    if (Op(root) != 0)
                        throw new ObsException(L.T("Not an OBS WebSocket server (no Hello)"));
                    var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = EventSubscriptions };
                    if (root.GetProperty("d").TryGetProperty("authentication", out var auth))
                    {
                        if (s.Password.Length == 0)
                            throw new ObsException(L.T("OBS asks for a password: type the one from OBS → Tools → WebSocket Server Settings into Shoji's Settings → Control deck"));
                        identify["authentication"] = Authentication(s.Password, auth.GetProperty("salt").GetString() ?? "", auth.GetProperty("challenge").GetString() ?? "");
                    }
                    await SendAsync(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, timeout.Token).ConfigureAwait(false);
                }
                using (var identified = await ReceiveAsync(ws, buffer, ms, timeout.Token).ConfigureAwait(false) ?? throw Closed(ws))
                {
                    if (Op(identified.RootElement) != 2)
                        throw new ObsException(L.T("OBS didn't accept the connection"));
                }
            }

            lock (_gate)
                _ws = ws;
            // Responses arrive through the receive loop, which the caller starts after this returns, so read the
            // state on a separate task that waits for it.
            if (readState)
                _ = Task.Run(() => FetchStateAsync(ws, ct));
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>base64(sha256(base64(sha256(password + salt)) + challenge)), as obs-websocket 5 defines it.</summary>
    public static string Authentication(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    static int Op(JsonElement root) => root.TryGetProperty("op", out var op) && op.TryGetInt32(out int n) ? n : -1;

    static Exception Closed(ClientWebSocket ws) => (int?)ws.CloseStatus switch
    {
        4009 => new ObsException(L.T("Wrong password: check it in OBS → Tools → WebSocket Server Settings → Show Connect Info")),
        4010 => new ObsException(L.T("This OBS WebSocket version isn't supported (Shoji needs obs-websocket 5, built into OBS 28 and later)")),
        _ => new ObsException(UnreachableMessage),
    };

    static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, byte[] buffer, MemoryStream ms, CancellationToken ct)
    {
        ms.SetLength(0);
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 16 * 1024 * 1024)
                throw new ObsException(L.T("OBS sent an oversized message"));
            if (r.EndOfMessage)
                return JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
        }
    }

    async Task SendAsync(ClientWebSocket ws, JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            using var doc = await ReceiveAsync(ws, buffer, ms, ct).ConfigureAwait(false);
            if (doc == null)
                return;
            var root = doc.RootElement;
            if (!root.TryGetProperty("d", out var d))
                continue;
            switch (Op(root))
            {
                case 5:
                    OnEvent(d.TryGetProperty("eventType", out var type) ? type.GetString() : null, d.TryGetProperty("eventData", out var data) ? data : default);
                    break;
                case 7:
                    OnResponse(d);
                    break;
            }
        }
    }

    void OnResponse(JsonElement d)
    {
        string id = d.TryGetProperty("requestId", out var r) ? r.GetString() ?? "" : "";
        if (!_pending.TryRemove(id, out var tcs))
            return;
        var status = d.GetProperty("requestStatus");
        if (status.TryGetProperty("result", out var ok) && ok.ValueKind == JsonValueKind.True)
            tcs.TrySetResult(d.TryGetProperty("responseData", out var data) ? data.Clone() : default);
        else
            tcs.TrySetException(new ObsException(
                status.TryGetProperty("comment", out var c) && c.GetString() is { Length: > 0 } comment ? comment : "request failed",
                status.TryGetProperty("code", out var code) && code.TryGetInt32(out int n) ? n : 0));
    }

    void FailPending()
    {
        foreach (var id in _pending.Keys.ToArray())
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(new ObsException(UnreachableMessage));
    }

    /// <summary>Sends a request and waits (up to 5 s) for its response data.</summary>
    public async Task<JsonElement> RequestAsync(string type, JsonObject? data = null)
    {
        ClientWebSocket? ws;
        lock (_gate)
            ws = _ws;
        if (ws == null)
            throw new ObsException(UnreachableMessage);
        string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
            if (data != null)
                d["requestData"] = data;
            using var timeout = new CancellationTokenSource(RequestTimeout);
            await SendAsync(ws, new JsonObject { ["op"] = 6, ["d"] = d }, timeout.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(RequestTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            throw new ObsException(UnreachableMessage);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task<JsonElement?> TryRequestAsync(string type, JsonObject? data = null)
    {
        try { return await RequestAsync(type, data).ConfigureAwait(false); }
        catch (ObsException) { return null; }
    }

    static bool Bool(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>OBS lists scenes bottom-up (index 0 = the lowest in its Scenes dock); this returns them top-down.</summary>
    static List<string> SceneNames(JsonElement e) =>
        e.ValueKind != JsonValueKind.Array ? new List<string>() : e.EnumerateArray()
            .Select(s => (Name: Str(s, "sceneName"), Index: s.TryGetProperty("sceneIndex", out var i) && i.TryGetInt32(out int n) ? n : 0))
            .Where(s => s.Name != null).OrderByDescending(s => s.Index).Select(s => s.Name!).ToList();

    /// <summary>Everything the deck shows, read once per connection; events keep it current afterwards.</summary>
    async Task FetchStateAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            var version = await RequestAsync("GetVersion").ConfigureAwait(false);
            var record = await TryRequestAsync("GetRecordStatus").ConfigureAwait(false);
            var stream = await TryRequestAsync("GetStreamStatus").ConfigureAwait(false);
            var list = await TryRequestAsync("GetSceneList").ConfigureAwait(false);
            string? scene = Str(list, "currentProgramSceneName")
                ?? Str(await TryRequestAsync("GetCurrentProgramScene").ConfigureAwait(false), "currentProgramSceneName");
            var replay = await TryRequestAsync("GetReplayBufferStatus").ConfigureAwait(false); // fails when no replay buffer is set up
            var vcam = await TryRequestAsync("GetVirtualCamStatus").ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return;
            lock (_gate)
            {
                _version = Str(version, "obsVersion");
                _recording = Bool(record, "outputActive");
                _recordPaused = Bool(record, "outputPaused");
                _recBaseMs = record is { } r && r.TryGetProperty("outputDuration", out var dur) && dur.TryGetInt64(out long ms) ? ms : 0;
                _recSyncTicks = Stopwatch.GetTimestamp();
                _streaming = Bool(stream, "outputActive");
                _scenes = list is { } l && l.TryGetProperty("scenes", out var scenes) ? SceneNames(scenes) : new List<string>();
                _scene = scene;
                _replayAvailable = replay != null;
                _replayBuffer = Bool(replay, "outputActive");
                _virtualCam = Bool(vcam, "outputActive");
                _muted.Clear();
                _missingInputs.Clear();
                _connected = true;
                _error = null;
            }
            Log.Write($"OBS: connected to OBS {Version} ({Scenes.Count} scenes)");
            await FetchMutesAsync(TrackedInputs()).ConfigureAwait(false);
            RaiseChanged();
        }
        catch (ObsException ex)
        {
            Log.WriteThrottled("obs-state", $"OBS: reading its state failed: {ex.Message}", TimeSpan.FromMinutes(10));
            ws.Abort(); // the loop sees the connection drop and tries again later
        }
    }

    async Task FetchMutesAsync(IEnumerable<string> inputs)
    {
        bool changed = false;
        foreach (var input in inputs)
        {
            var r = await TryRequestAsync("GetInputMute", new JsonObject { ["inputName"] = input }).ConfigureAwait(false);
            lock (_gate)
            {
                if (r != null)
                {
                    _muted[input] = Bool(r, "inputMuted");
                    _missingInputs.Remove(input);
                }
                else
                {
                    _muted.Remove(input);
                    _missingInputs.Add(input); // OBS has no input by that name
                }
            }
            changed = true;
        }
        if (changed)
            RaiseChanged();
    }

    void OnEvent(string? type, JsonElement data)
    {
        bool changed = true, refetchScenes = false;
        lock (_gate)
        {
            switch (type)
            {
                case "RecordStateChanged":
                    string? state = Str(data, "outputState");
                    bool active = Bool(data, "outputActive");
                    if (state == "OBS_WEBSOCKET_OUTPUT_STARTED" || (active && !_recording))
                    {
                        _recBaseMs = 0;
                        _recSyncTicks = Stopwatch.GetTimestamp();
                        _recordPaused = false;
                    }
                    else if (state == "OBS_WEBSOCKET_OUTPUT_PAUSED" && !_recordPaused)
                    {
                        // Freeze the clock where it is; it runs on from here when resumed.
                        _recBaseMs += (long)Stopwatch.GetElapsedTime(_recSyncTicks).TotalMilliseconds;
                        _recordPaused = true;
                    }
                    else if (state == "OBS_WEBSOCKET_OUTPUT_RESUMED" && _recordPaused)
                    {
                        _recSyncTicks = Stopwatch.GetTimestamp();
                        _recordPaused = false;
                    }
                    _recording = active;
                    if (!active)
                        _recordPaused = false;
                    break;
                case "StreamStateChanged":
                    _streaming = Bool(data, "outputActive");
                    break;
                case "ReplayBufferStateChanged":
                    _replayBuffer = Bool(data, "outputActive");
                    _replayAvailable = true;
                    break;
                case "VirtualcamStateChanged":
                    _virtualCam = Bool(data, "outputActive");
                    break;
                case "CurrentProgramSceneChanged":
                    _scene = Str(data, "sceneName");
                    break;
                case "SceneListChanged":
                    _scenes = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("scenes", out var scenes) ? SceneNames(scenes) : _scenes;
                    break;
                case "SceneNameChanged":
                    if (_scene != null && _scene == Str(data, "oldSceneName"))
                        _scene = Str(data, "sceneName");
                    refetchScenes = true;
                    break;
                case "SceneCreated":
                case "SceneRemoved":
                    refetchScenes = true;
                    changed = false;
                    break;
                case "InputMuteStateChanged":
                    if (Str(data, "inputName") is { } input)
                    {
                        _muted[input] = Bool(data, "inputMuted");
                        _missingInputs.Remove(input);
                    }
                    break;
                case "ExitStarted":
                    // OBS is quitting; the socket closes in a moment and the loop backs off.
                    _connected = false;
                    _error = UnreachableMessage;
                    break;
                default:
                    changed = false;
                    break;
            }
        }
        if (refetchScenes)
            _ = Task.Run(async () =>
            {
                if (await TryRequestAsync("GetSceneList").ConfigureAwait(false) is { } l && l.TryGetProperty("scenes", out var s))
                {
                    lock (_gate)
                        _scenes = SceneNames(s);
                    RaiseChanged();
                }
            });
        if (changed)
            RaiseChanged();
    }

    void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Write($"OBS: state change handler failed: {ex.Message}"); }
    }

    static string Describe(Exception ex) => ex switch
    {
        ObsException o => o.Message,
        WebSocketException or System.Net.Sockets.SocketException or OperationCanceledException or IOException or JsonException => UnreachableMessage,
        _ => ex.Message,
    };

    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Runs a deck button's target: record, pause, stream, replay (save), replaybuffer, virtualcam, scene:Name,
    /// mute:Input. Connects first when needed. Returns a message to show (an error, or "Replay saved"), else null.
    /// Never throws.
    /// </summary>
    public async Task<string?> RunAsync(string target)
    {
        target = (target ?? "").Trim();
        int colon = target.IndexOf(':');
        string verb = (colon >= 0 ? target[..colon] : target).Trim().ToLowerInvariant();
        string arg = colon >= 0 ? target[(colon + 1)..].Trim() : "";
        if (!Commands.Contains(verb))
            return L.F("Unknown OBS target \"{0}\": use record, pause, stream, replay, replaybuffer, virtualcam, scene:Name or mute:Input", target);
        if (verb is "scene" or "mute" && arg.Length == 0)
            return verb == "scene" ? L.T("Name the OBS scene, e.g. scene:Gaming") : L.T("Name the OBS input, e.g. mute:Mic/Aux");

        try
        {
            if (!await EnsureConnectedAsync().ConfigureAwait(false))
                return Error ?? UnreachableMessage;
            switch (verb)
            {
                case "record":
                    await RequestAsync("ToggleRecord").ConfigureAwait(false);
                    return null;
                case "pause":
                    if (!Recording)
                        return L.T("OBS isn't recording");
                    await RequestAsync("ToggleRecordPause").ConfigureAwait(false);
                    return null;
                case "stream":
                    await RequestAsync("ToggleStream").ConfigureAwait(false);
                    return null;
                case "replay":
                    await RequestAsync("SaveReplayBuffer").ConfigureAwait(false);
                    return L.T("Replay saved");
                case "replaybuffer":
                    await RequestAsync("ToggleReplayBuffer").ConfigureAwait(false);
                    return null;
                case "virtualcam":
                    await RequestAsync("ToggleVirtualCam").ConfigureAwait(false);
                    return null;
                case "scene":
                    await RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = arg }).ConfigureAwait(false);
                    return null;
                default: // mute
                    var r = await RequestAsync("ToggleInputMute", new JsonObject { ["inputName"] = arg }).ConfigureAwait(false);
                    lock (_gate)
                        _muted[arg] = Bool(r, "inputMuted");
                    RaiseChanged();
                    return null;
            }
        }
        catch (ObsException ex)
        {
            // Request status codes from the protocol: 501 OutputNotRunning, 600 ResourceNotFound, 604 InvalidResourceState.
            return (verb, ex.Code) switch
            {
                ("replay", 501 or 604) => L.T("The replay buffer isn't running: start it in OBS (or with a replaybuffer button)"),
                ("replaybuffer", 604 or 504) => L.T("OBS has no replay buffer: turn it on in OBS → Settings → Output → Replay Buffer"),
                ("scene", 600) => L.F("OBS has no scene called \"{0}\"", arg),
                ("mute", 600) => L.F("OBS has no audio input called \"{0}\"", arg),
                (_, 0) => ex.Message,
                _ => "OBS: " + ex.Message,
            };
        }
        catch (Exception ex)
        {
            Log.Write($"OBS: {target} failed: {ex}");
            return "OBS: " + ex.Message;
        }
    }

    /// <summary>Connects now if needed (a button press shouldn't wait out the retry backoff); false when OBS can't be reached.</summary>
    async Task<bool> EnsureConnectedAsync()
    {
        bool known;
        lock (_gate)
            known = _settings != null;
        // No deck on the dashboard holds a lease (e.g. pressed from the phone): read the settings.
        var loaded = known ? null : AppConfig.Load().Obs;
        int attempts;
        lock (_gate)
        {
            _wantedUntil = DateTime.UtcNow.AddMinutes(1);
            if (_settings == null && loaded != null)
                _settings = From(loaded);
            if (_connected)
                return true;
            attempts = _attempts;
        }
        Kick();
        HangUpWhenIdle(TimeSpan.FromMinutes(1.1));
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100).ConfigureAwait(false);
            lock (_gate)
            {
                if (_connected)
                    return true;
                if (_attempts != attempts)
                    return false; // that attempt failed
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- Settings' test button

    /// <summary>
    /// Connects once with these settings (not touching the shared connection) and describes the result:
    /// "Connected to OBS 30.2.3 — 5 scenes", or what went wrong. Never throws.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync(ObsConfig config)
    {
        var client = new ObsClient();
        var s = From(config);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var ws = await client.OpenAsync(s, cts.Token, readState: false).ConfigureAwait(false);
            var receive = client.ReceiveLoopAsync(ws, cts.Token);
            var version = await client.RequestAsync("GetVersion").ConfigureAwait(false);
            var list = await client.TryRequestAsync("GetSceneList").ConfigureAwait(false);
            int scenes = list is { } l && l.TryGetProperty("scenes", out var sc) && sc.ValueKind == JsonValueKind.Array ? sc.GetArrayLength() : 0;
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* closing politely is best effort */ }
            cts.Cancel();
            try { await receive.ConfigureAwait(false); } catch { /* cancelled */ }
            return (true, scenes == 1 ? L.F("Connected to OBS {0} — 1 scene", Str(version, "obsVersion") ?? "?") : L.F("Connected to OBS {0} — {1} scenes", Str(version, "obsVersion") ?? "?", scenes));
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    // ---------------------------------------------------------------- how a deck button looks

    /// <summary>What an "obs" deck button shows: lit or not (and in the warning colour), the line under the label, an icon swap.</summary>
    public readonly record struct ButtonLook(bool Online, bool Active, bool Danger, string? Sub, string? Icon);

    public ButtonLook LookFor(string? target, string? icon)
    {
        target = (target ?? "").Trim();
        int colon = target.IndexOf(':');
        string verb = (colon >= 0 ? target[..colon] : target).Trim().ToLowerInvariant();
        string arg = colon >= 0 ? target[(colon + 1)..].Trim() : "";
        lock (_gate)
        {
            if (!_connected)
                return new ButtonLook(false, false, false, L.T("offline"), null);
            switch (verb)
            {
                case "record":
                    if (!_recording)
                        return new ButtonLook(true, false, false, null, null);
                    return new ButtonLook(true, true, true, _recordPaused ? L.T("PAUSED") : "REC " + Clock(RecordElapsedLocked()), null);
                case "pause":
                    return new ButtonLook(true, _recordPaused, false, _recordPaused ? L.T("paused") : _recording ? null : L.T("not recording"), null);
                case "stream":
                    return new ButtonLook(true, _streaming, false, _streaming ? "LIVE" : null, null);
                case "replay":
                    return new ButtonLook(true, false, false, _replayBuffer ? null : L.T("buffer off"), null);
                case "replaybuffer":
                    return new ButtonLook(true, _replayBuffer, false, _replayBuffer ? L.T("on") : L.T("off"), null);
                case "virtualcam":
                    return new ButtonLook(true, _virtualCam, false, _virtualCam ? L.T("on") : L.T("off"), null);
                case "scene":
                    bool current = _scene != null && string.Equals(_scene, arg, StringComparison.Ordinal);
                    return new ButtonLook(true, current, false, current ? L.T("on air") : null, null);
                case "mute":
                    if (!_muted.TryGetValue(arg, out bool muted))
                        return new ButtonLook(true, false, false, _missingInputs.Contains(arg) ? L.T("no such input") : null, null);
                    bool mic = string.IsNullOrEmpty(icon) || icon.Trim().Equals("mic", StringComparison.OrdinalIgnoreCase);
                    return new ButtonLook(true, muted, true, muted ? L.T("muted") : L.T("on"), mic ? muted ? "micoff" : "mic" : null);
                default:
                    return new ButtonLook(true, false, false, null, null);
            }
        }
    }

    TimeSpan RecordElapsedLocked() =>
        TimeSpan.FromMilliseconds(Math.Max(0, _recBaseMs + (_recordPaused ? 0 : (long)Stopwatch.GetElapsedTime(_recSyncTicks).TotalMilliseconds)));

    /// <summary>12:34, or 1:02:03 from an hour on.</summary>
    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>True for the deck buttons that are "obs" actions.</summary>
    public static bool IsObsButton(DeckButton b) => string.Equals(b.Action, "obs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The input names of a deck's mute:… buttons, so their state can be followed.</summary>
    public static IEnumerable<string> MuteInputs(IEnumerable<DeckButton> buttons) =>
        buttons.Where(IsObsButton).Select(b => (b.Target ?? "").Trim())
            .Where(t => t.StartsWith("mute:", StringComparison.OrdinalIgnoreCase)).Select(t => t[5..].Trim()).Where(t => t.Length > 0);
}

public sealed class ObsException : Exception
{
    public ObsException(string message, int code = 0) : base(message) => Code = code;

    /// <summary>obs-websocket request status code (600 = not found…), 0 for connection problems.</summary>
    public int Code { get; }
}
