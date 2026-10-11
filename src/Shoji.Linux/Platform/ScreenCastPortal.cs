using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Shoji.Core;
using Tmds.DBus.Protocol;

namespace Shoji.Linux;

/// <summary>What the desktop shares after the user picked it: a PipeWire video node and its size.</summary>
public sealed record ScreenCastStream(uint Node, int Width, int Height, bool IsWindow);

/// <summary>
/// A screen-sharing session from the desktop portal: the stream and the PipeWire connection (a file descriptor) to
/// read it. Disposing closes the fd and ends the session, so the desktop's "screen is being shared" indicator goes.
/// </summary>
public sealed class ScreenCastSession : IDisposable
{
    readonly Connection _bus;
    int _closed;

    internal ScreenCastSession(Connection bus, string handle, ScreenCastStream stream, SafeFileHandle pipeWire, string? restoreToken)
    {
        _bus = bus;
        Handle = handle;
        Stream = stream;
        PipeWire = pipeWire;
        RestoreToken = restoreToken;
    }

    public string Handle { get; }
    public ScreenCastStream Stream { get; }
    public SafeFileHandle PipeWire { get; }

    /// <summary>Lets the next session pick the same window / screen without the dialog (single use: each session hands out a new one).</summary>
    public string? RestoreToken { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
            return;
        PipeWire.Dispose();
        ScreenCastPortal.Close(_bus, Handle, "org.freedesktop.portal.Session");
    }
}

/// <summary>
/// The xdg-desktop-portal ScreenCast interface over D-Bus (Tmds.DBus.Protocol, which Avalonia already ships):
/// CreateSession → SelectSources → Start → OpenPipeWireRemote. The first three answer through a Request object's
/// Response signal; the signal is subscribed before each call (its path is known from our handle_token) so it can't be missed.
/// The dialog to pick a window or screen appears on Start, unless a restore token from last time still matches.
/// </summary>
public static class ScreenCastPortal
{
    const string Service = "org.freedesktop.portal.Desktop";
    const string DesktopPath = "/org/freedesktop/portal/desktop";
    const string ScreenCast = "org.freedesktop.portal.ScreenCast";

    // Source types and cursor modes (bit flags), from the portal spec.
    const uint Monitor = 1, Window = 2;
    const uint CursorEmbedded = 2;
    const uint PersistUntilRevoked = 2;

    static Connection? _bus;
    static readonly SemaphoreSlim Connecting = new(1, 1);
    static int _token;

    /// <summary>Thrown when the user closes the dialog without picking anything.</summary>
    public sealed class CancelledException : Exception
    {
        public CancelledException() : base("Cancelled") { }
    }

    /// <summary>One session-bus connection for every screen cast: portal sessions belong to the connection that made them.</summary>
    static async Task<Connection> BusAsync()
    {
        await Connecting.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_bus != null)
                return _bus;
            var address = Address.Session ?? throw new InvalidOperationException("No D-Bus session bus");
            var bus = new Connection(address);
            await bus.ConnectAsync().ConfigureAwait(false);
            _bus = bus;
            _ = bus.DisconnectedAsync().ContinueWith(_ => { if (_bus == bus) _bus = null; }, TaskScheduler.Default);
            return bus;
        }
        finally
        {
            Connecting.Release();
        }
    }

    /// <summary>The portal's ScreenCast version and which source types / cursor modes it offers; null without a portal. Read-only, no dialog.</summary>
    public static async Task<(uint Version, uint SourceTypes, uint CursorModes)?> InfoAsync()
    {
        try
        {
            var bus = await BusAsync().ConfigureAwait(false);
            uint version = await GetUInt32Async(bus, "version").ConfigureAwait(false);
            uint types = await GetUInt32Async(bus, "AvailableSourceTypes").ConfigureAwait(false);
            uint cursors = version >= 2 ? await GetUInt32Async(bus, "AvailableCursorModes").ConfigureAwait(false) : 0;
            return (version, types, cursors);
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("screencast-info", $"Screen-sharing portal unavailable: {ex.Message}", TimeSpan.FromHours(1));
            return null;
        }
    }

    static Task<uint> GetUInt32Async(Connection bus, string property)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(destination: Service, path: DesktopPath, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        w.WriteString(ScreenCast);
        w.WriteString(property);
        return bus.CallMethodAsync(w.CreateMessage(), (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt32());
    }

    /// <summary>
    /// Opens a screen cast of one window or screen. With a <paramref name="restoreToken"/> from an earlier session the
    /// desktop normally skips the dialog. When <paramref name="interactive"/> is false (the tile restarting on its own),
    /// a dialog that appears anyway (the window is gone, the token expired) is closed again after a few seconds and
    /// <see cref="CancelledException"/> thrown: only a tap may leave the dialog up.
    /// </summary>
    public static async Task<ScreenCastSession> OpenAsync(string? restoreToken, bool interactive, CancellationToken ct)
    {
        var bus = await BusAsync().ConfigureAwait(false);
        var info = await InfoAsync().ConfigureAwait(false) ?? throw new InvalidOperationException(L.T("This desktop has no screen-sharing portal (xdg-desktop-portal)")); // shown on the tile

        var created = await RequestAsync(bus, token => new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["session_handle_token"] = token,
        }, "CreateSession", null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        string session = created.TryGetValue("session_handle", out var sh) ? Text(sh) : throw new InvalidOperationException("The portal made no session");

        try
        {
            await RequestAsync(bus, token =>
            {
                var o = new Dictionary<string, VariantValue>
                {
                    ["handle_token"] = token,
                    ["types"] = VariantValue.UInt32((info.SourceTypes & (Monitor | Window)) != 0 ? info.SourceTypes & (Monitor | Window) : Monitor),
                    ["multiple"] = false,
                };
                if ((info.CursorModes & CursorEmbedded) != 0)
                    o["cursor_mode"] = VariantValue.UInt32(CursorEmbedded);
                if (info.Version >= 4)
                {
                    o["persist_mode"] = VariantValue.UInt32(PersistUntilRevoked);
                    if (!string.IsNullOrEmpty(restoreToken))
                        o["restore_token"] = restoreToken;
                }
                return o;
            }, "SelectSources", session, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            // Start shows the dialog (on the main screen) unless the restore token matched. A silent restore answers
            // within a moment; anything slower without a tap is a dialog nobody asked for, so it's dismissed.
            var started = await RequestAsync(bus, token => new Dictionary<string, VariantValue> { ["handle_token"] = token },
                "Start", session, interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(4), ct, parentWindow: "").ConfigureAwait(false);
            var stream = FirstStream(started) ?? throw new InvalidOperationException(L.T("Nothing was shared"));
            string? newToken = started.TryGetValue("restore_token", out var rt) ? Text(rt) : null;

            var fd = await bus.CallMethodAsync(Method(bus, "OpenPipeWireRemote", session, null, new Dictionary<string, VariantValue>()),
                (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>()).ConfigureAwait(false);
            return new ScreenCastSession(bus, session, stream, fd ?? throw new InvalidOperationException("No PipeWire connection"), newToken);
        }
        catch
        {
            Close(bus, session, "org.freedesktop.portal.Session");
            throw;
        }
    }

    /// <summary>
    /// Calls a portal method that answers later through a Request's Response signal, and waits for that answer.
    /// After <paramref name="timeout"/> the request is closed (which also closes its dialog) and it counts as cancelled.
    /// </summary>
    static async Task<Dictionary<string, VariantValue>> RequestAsync(Connection bus, Func<string, Dictionary<string, VariantValue>> options,
        string method, string? session, TimeSpan timeout, CancellationToken ct, string? parentWindow = null)
    {
        string token = $"shoji{Environment.ProcessId}_{Interlocked.Increment(ref _token)}";
        // The portal puts the Request at a path made from our unique bus name and the handle_token.
        string sender = (bus.UniqueName ?? "").TrimStart(':').Replace('.', '_');
        string requestPath = $"{DesktopPath}/request/{sender}/{token}";

        var answer = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var match = await bus.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.portal.Request", Member = "Response", Path = requestPath },
            (m, _) =>
            {
                var r = m.GetBodyReader();
                uint code = r.ReadUInt32();
                return (code, r.ReadDictionaryOfStringToVariantValue());
            },
            (ex, value, _, _) =>
            {
                if (ex != null)
                    answer.TrySetException(ex);
                else
                    answer.TrySetResult(value);
            },
            ObserverFlags.None, null, null, emitOnCapturedContext: false).ConfigureAwait(false);

        string handle = await bus.CallMethodAsync(Method(bus, method, session, parentWindow, options(token)),
            (m, _) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
        if (handle != requestPath)
            Log.Write($"Screen-sharing portal answered on {handle}, expected {requestPath}"); // very old portals; the wait below then times out

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            var (code, results) = await answer.Task.WaitAsync(timer.Token).ConfigureAwait(false);
            return code switch
            {
                0 => results,
                1 => throw new CancelledException(),
                _ => throw new InvalidOperationException($"The screen-sharing portal refused {method}"),
            };
        }
        catch (OperationCanceledException)
        {
            Close(bus, handle, "org.freedesktop.portal.Request"); // takes the dialog away
            if (ct.IsCancellationRequested)
                throw;
            throw new CancelledException();
        }
    }

    /// <summary>A ScreenCast method call: [session handle] [parent window] options.</summary>
    static MessageBuffer Method(Connection bus, string method, string? session, string? parentWindow, Dictionary<string, VariantValue> options)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(destination: Service, path: DesktopPath, @interface: ScreenCast, member: method,
            signature: (session == null ? "" : "o") + (parentWindow == null ? "" : "s") + "a{sv}");
        if (session != null)
            w.WriteObjectPath(session);
        if (parentWindow != null)
            w.WriteString(parentWindow);
        w.WriteDictionary(options);
        return w.CreateMessage();
    }

    /// <summary>Closes a portal Request or Session. Fire and forget: it's tidy-up, and the portal ends them anyway when our bus connection goes.</summary>
    internal static void Close(Connection bus, string path, string @interface)
    {
        try
        {
            using var w = bus.GetMessageWriter();
            w.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: "Close");
            _ = bus.CallMethodAsync(w.CreateMessage()).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch
        {
            // the bus is gone; so is the session
        }
    }

    static string Text(VariantValue v)
    {
        v = Unwrap(v);
        return v.Type == VariantValueType.ObjectPath ? v.GetObjectPathAsString() : v.GetString();
    }

    /// <summary>a{sv} values arrive as their own type; this also copes with one extra variant layer.</summary>
    static VariantValue Unwrap(VariantValue v) => v.Type == VariantValueType.Variant ? v.GetVariantValue() : v;

    /// <summary>The first stream of Start's "streams": a(ua{sv}) = node id + properties (size, source_type).</summary>
    static ScreenCastStream? FirstStream(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("streams", out var streams) || (streams = Unwrap(streams)).Type != VariantValueType.Array || streams.Count == 0)
            return null;
        var first = streams.GetItem(0);
        uint node = first.GetItem(0).GetUInt32();
        var props = first.GetItem(1);
        int width = 0, height = 0;
        bool window = false;
        for (int i = 0; i < props.Count; i++)
        {
            var (key, raw) = props.GetDictionaryEntry(i);
            var value = Unwrap(raw);
            switch (key.GetString())
            {
                case "size":
                    width = value.GetItem(0).GetInt32();
                    height = value.GetItem(1).GetInt32();
                    break;
                case "source_type":
                    window = value.GetUInt32() == Window;
                    break;
            }
        }
        return new ScreenCastStream(node, width, height, window);
    }
}
