using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Shoji.Core;
using Shoji.Linux;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>
/// Live copy of a window or screen (Discord, OBS stats, a minimap…). Wayland lets no app look at others, so it goes
/// through the desktop's screen-sharing portal: a tap on the tile opens the desktop's dialog (on the main screen) to pick
/// what to show, and the restore token it hands back lets later starts skip the dialog. The frames come from PipeWire
/// through a gst-launch-1.0 pipeline that scales them down to the tile and writes raw BGRA to its stdout; they're
/// swapped in on <see cref="FrameClock"/> ticks. Nothing runs while the tile isn't on screen. Nothing leaves the PC.
/// </summary>
public sealed class MirrorModule : Module
{
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly Grid _body = new() { Background = Brushes.Transparent, ClipToBounds = true };
    readonly TextBlock _icon, _status, _caption;
    readonly StackPanel _prompt;
    readonly string _key;
    readonly int _fps;
    readonly PixelRect? _crop;
    readonly CancellationTokenSource _cts = new();

    ScreenCastSession? _session;
    MirrorFeed? _feed;
    WriteableBitmap? _bitmap;
    bool _starting, _ticking, _needsTap, _disposed;
    DateTime _startedAt;

    public MirrorModule(ModuleConfig config, AppConfig app) : base(config, "Mirror")
    {
        _fps = Math.Clamp(config.OptInt("Fps", 5), 1, 10); // software rendering: every frame is a repaint
        _crop = ParseRect(config.Opt("Crop"));
        // Restore tokens are kept per Mirror tile (by its order), so two tiles can show different things.
        int index = app.AllModules.Where(m => m.Type.Equals("mirror", StringComparison.OrdinalIgnoreCase)).ToList().IndexOf(config);
        _key = "mirror" + Math.Max(0, index);

        _icon = W.Text("screen_share", 30, UI.Theme.Dim);
        _icon.FontFamily = UI.Theme.Icons;
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _status = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.TextAlignment = TextAlignment.Center;
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.Margin = new Thickness(8, 6, 8, 0);
        _prompt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { _icon, _status } };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.MediumQuality);
        _body.Children.Add(_image);
        _body.Children.Add(_prompt);
        Tap.On(_body, () => Begin(interactive: true, forget: false));

        // "Change": pick something else (the dialog again, ignoring the saved choice).
        var change = W.Text("swap_horiz", UI.Theme.Small + 4, UI.Theme.Dim);
        change.FontFamily = UI.Theme.Icons;
        _caption = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _caption, change } };
        Tap.On(change, () => { Stop(); Begin(interactive: true, forget: true); });

        _needsTap = MirrorTokens.Get(_key) == null;
        Show(_needsTap ? L.T("Tap to choose a window or screen") : "");
        View = MakeTile(_body, header);
        View.DetachedFromVisualTree += (_, _) => Stop();

        // Say up front if the tools are missing, instead of after a tap.
        _ = MirrorFeed.MissingTools().ContinueWith(t =>
        {
            if (t.Result is string fix)
                Dispatcher.UIThread.Post(() => Show(L.F("Needs: {0}", fix)));
        }, TaskScheduler.Default);
    }

    void Show(string status, bool frame = false)
    {
        _status.Text = status;
        _prompt.IsVisible = !frame || status.Length > 0;
        _icon.IsVisible = !frame;
        _image.Opacity = frame ? 1 : 0.35; // a paused copy stays as a faint backdrop behind the prompt
    }

    /// <summary>On the page that's shown, in a dashboard window that's open, with motion running (not idle, locked or black at night).</summary>
    bool OnScreen => View.IsEffectivelyVisible && TopLevel.GetTopLevel(View) is { IsVisible: true } && !FrameClock.Paused;

    public override void Update(Snapshot s)
    {
        // Called about once a second, also for tiles on hidden pages: stop when nobody can see the tile,
        // and come back (silently, with the saved choice) when it's on screen again.
        if (!OnScreen)
        {
            if (_feed != null)
                Stop();
            return;
        }
        if (_feed == null && !_starting && !_needsTap && !_disposed)
            Begin(interactive: false, forget: false);
    }

    void Begin(bool interactive, bool forget)
    {
        if (_starting || _feed != null || _disposed)
            return;
        _starting = true;
        _ = StartAsync(interactive, forget);
    }

    async Task StartAsync(bool interactive, bool forget)
    {
        try
        {
            if (await MirrorFeed.MissingTools() is string fix)
            {
                Show(L.F("Needs: {0}", fix));
                _needsTap = true;
                return;
            }
            // Test hook (headless screenshots): a GStreamer test picture instead of the portal, so no dialog appears.
            if (Environment.GetEnvironmentVariable("SHOJI_MIRROR_TEST") is { Length: > 0 } test)
            {
                var size = FrameSize(new ScreenCastStream(0, 1280, 720, false));
                _feed = MirrorFeed.Start(new[] { "videotestsrc", "is-live=true", "pattern=" + test }, null, size.W, size.H, _fps);
                _startedAt = DateTime.UtcNow;
                _needsTap = false;
                _ticking = true;
                FrameClock.Subscribe(Frame);
                return;
            }
            Show(L.T(interactive ? "Pick a window or screen in the sharing dialog on your main screen" : "Connecting…"));
            string? token = forget ? null : MirrorTokens.Get(_key);
            ScreenCastSession session;
            try
            {
                session = await Task.Run(() => ScreenCastPortal.OpenAsync(token, interactive, _cts.Token));
            }
            catch (ScreenCastPortal.CancelledException)
            {
                // Without a tap, a dialog means the saved choice no longer works (the window closed): forget it.
                if (!interactive)
                    MirrorTokens.Set(_key, null);
                _needsTap = true;
                Show(L.T(interactive ? "Nothing chosen. Tap to choose a window or screen" : "Tap to choose a window or screen"));
                return;
            }
            if (_disposed)
            {
                session.Dispose();
                return;
            }
            MirrorTokens.Set(_key, session.RestoreToken);
            _session = session;
            _caption.Text = L.T(session.Stream.IsWindow ? "window" : "screen");

            var (w, h) = FrameSize(session.Stream);
            _feed = MirrorFeed.Start(MirrorFeed.PipeWireSource(session), session.PipeWire, w, h, _fps, Crop(session.Stream), session.Stream);
            session.PipeWire.Dispose(); // gst-launch has its own copy now
            _startedAt = DateTime.UtcNow;
            _needsTap = false;
            Show(L.T("Starting…"), frame: _bitmap != null);
            if (!_ticking)
            {
                _ticking = true;
                FrameClock.Subscribe(Frame);
            }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("mirror-start", $"Mirror couldn't start: {ex.Message}", TimeSpan.FromMinutes(5));
            Stop();
            _needsTap = true;
            Show(L.F("Screen sharing failed ({0}). Tap to try again", ex.Message));
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>On FrameClock ticks (UI thread): swap in the newest frame, at most the pipeline's ≤ 10 fps.</summary>
    void Frame()
    {
        var feed = _feed;
        if (feed == null)
            return;
        if (!View.IsEffectivelyVisible || TopLevel.GetTopLevel(View) is not { IsVisible: true })
        {
            Stop();
            return;
        }
        feed.Wanted();
        if (feed.Exited && feed.Unwatched)
        {
            Stop(); // it stopped itself while nobody was looking; Update starts it again
            return;
        }
        if (feed.Exited)
        {
            // The window closed, or the stream failed. A stream that died at once would only fail again: wait for a tap.
            bool quick = DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(5);
            string? error = feed.Error;
            Stop();
            _needsTap = quick || MirrorTokens.Get(_key) == null;
            Show(quick && error != null ? L.F("Screen sharing stopped: {0}. Tap to try again", error) : L.T("Sharing ended. Tap to choose a window or screen"));
            return;
        }
        if (_bitmap == null || _bitmap.PixelSize.Width != feed.Width || _bitmap.PixelSize.Height != feed.Height)
        {
            if (!feed.HasFrame)
                return;
            _bitmap = new WriteableBitmap(new PixelSize(feed.Width, feed.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
        }
        bool copied;
        using (var fb = _bitmap.Lock())
            copied = feed.TakeFrame(fb.Address, fb.RowBytes);
        if (copied)
        {
            if (_prompt.IsVisible)
                Show("", frame: true);
            _image.InvalidateVisual();
        }
    }

    void Stop()
    {
        if (_ticking)
        {
            _ticking = false;
            FrameClock.Unsubscribe(Frame);
        }
        _feed?.Dispose();
        _feed = null;
        _session?.Dispose();
        _session = null;
        if (_bitmap != null && !_starting)
            Show(_needsTap ? L.T("Tap to choose a window or screen") : "", frame: false);
    }

    /// <summary>The tile's size on screen in pixels (pages may be scaled), the source fitted in, never upscaled.</summary>
    (int W, int H) FrameSize(ScreenCastStream stream)
    {
        double scale = TopLevel.GetTopLevel(_body)?.RenderScaling ?? 1;
        if (TopLevel.GetTopLevel(_body) is { } top && _body.TransformToVisual(top) is Matrix m)
            scale *= Math.Max(0.1, Math.Abs(m.M11));
        double tw = Math.Clamp(_body.Bounds.Width * scale, 64, 1280), th = Math.Clamp(_body.Bounds.Height * scale, 36, 720);
        var crop = Crop(stream);
        double sw = crop?.Width ?? stream.Width, sh = crop?.Height ?? stream.Height;
        if (sw <= 0 || sh <= 0)
            return (Even(tw), Even(th));
        double k = Math.Min(1, Math.Min(tw / sw, th / sh));
        return (Even(sw * k), Even(sh * k));
    }

    static int Even(double v) => Math.Max(16, (int)v & ~1);

    /// <summary>The Crop option inside the shared picture (needs its size, which the portal reports).</summary>
    PixelRect? Crop(ScreenCastStream stream)
    {
        if (_crop is not PixelRect c || stream.Width <= 0 || stream.Height <= 0)
            return null;
        var area = c.Intersect(new PixelRect(0, 0, stream.Width, stream.Height));
        return area.Width > 0 && area.Height > 0 ? area : null;
    }

    static PixelRect? ParseRect(string? text)
    {
        var p = (text ?? "").Split(',', StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out int v) ? v : -1).ToArray();
        return p.Length == 4 && p[0] >= 0 && p[1] >= 0 && p[2] > 0 && p[3] > 0 ? new PixelRect(p[0], p[1], p[2], p[3]) : null;
    }

    protected override void OnDispose()
    {
        _disposed = true;
        _cts.Cancel();
        Stop();
    }
}

/// <summary>
/// One gst-launch-1.0 pipeline that writes fixed-size BGRA frames to its stdout, read on a background thread into a
/// double buffer. It stops itself when nobody has asked for a frame for a few seconds (FrameClock paused, window hidden).
/// </summary>
public sealed class MirrorFeed : IDisposable
{
    static readonly HashSet<Process> Running = new();
    static readonly object SpawnGate = new();
    static Task<string?>? _tools;

    readonly Process _process;
    readonly object _gate = new();
    byte[] _back, _ready;
    bool _fresh;
    long _wantedAt = Environment.TickCount64;
    volatile bool _exited, _unwatched;
    string? _lastError;

    public int Width { get; }
    public int Height { get; }
    public bool Exited => _exited;

    /// <summary>It stopped itself because no frame was asked for (FrameClock paused, window hidden), not because the stream ended.</summary>
    public bool Unwatched => _unwatched;
    public string? Error => _lastError;
    public bool HasFrame { get { lock (_gate) return _fresh; } }

    static MirrorFeed()
    {
        // gst-launch would keep running after us (it only notices on its next write): stop them on the way out.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Process[] all;
            lock (Running)
                all = Running.ToArray();
            foreach (var p in all)
                LinuxCli.Kill(p);
        };
    }

    MirrorFeed(Process process, int width, int height)
    {
        _process = process;
        Width = width;
        Height = height;
        _back = new byte[width * height * 4];
        _ready = new byte[width * height * 4];
        new Thread(ReadLoop) { IsBackground = true, Name = "Mirror frames" }.Start();
        _ = Task.Run(ReadErrorsAsync);
    }

    /// <summary>
    /// The install command for whatever's missing (gst-launch-1.0, the PipeWire element, the basic converters), or null.
    /// Checked once per run; a gst-inspect that's slow (it builds its plugin cache on first use) counts as fine.
    /// </summary>
    public static Task<string?> MissingTools() => _tools ??= Task.Run(async () =>
    {
        if (!LinuxSetup.Which("gst-launch-1.0"))
            return "sudo apt install gstreamer1.0-tools gstreamer1.0-pipewire";
        foreach (var (element, package) in new[] { ("pipewiresrc", "gstreamer1.0-pipewire"), ("videoconvert", "gstreamer1.0-plugins-base") })
        {
            var (code, _) = await LinuxCli.RunAsync("gst-inspect-1.0", new[] { "--exists", element }, 10_000).ConfigureAwait(false);
            if (code > 0)
                return "sudo apt install " + package;
        }
        return (string?)null;
    });

    /// <summary>The pipeline's source: the portal's PipeWire node, through the fd it opened for us.</summary>
    public static string[] PipeWireSource(ScreenCastSession s) => new[]
    {
        // keepalive-time: a still screen sends no new frames; repeat the last one now and then so the pipeline never stalls.
        "pipewiresrc", $"fd={s.PipeWire.DangerousGetHandle()}", $"path={s.Stream.Node}", "always-copy=true", "keepalive-time=1000",
    };

    /// <summary>
    /// Starts gst-launch-1.0 with <paramref name="source"/> → frame rate cut to <paramref name="fps"/> first (cheap) →
    /// optional crop → scaled to <paramref name="width"/>×<paramref name="height"/> → BGRA on stdout.
    /// <paramref name="inherit"/> is a descriptor the child must get (the PipeWire connection): .NET opens everything
    /// close-on-exec, so that flag is cleared on it just for the spawn.
    /// </summary>
    public static MirrorFeed Start(string[] source, SafeHandle? inherit, int width, int height, int fps, PixelRect? crop = null, int sourceWidth = 0, int sourceHeight = 0)
    {
        var psi = new ProcessStartInfo("gst-launch-1.0")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-q");
        foreach (var a in source)
            psi.ArgumentList.Add(a);
        void Then(params string[] args)
        {
            psi.ArgumentList.Add("!");
            foreach (var a in args)
                psi.ArgumentList.Add(a);
        }
        Then("videorate", "drop-only=true");
        Then($"video/x-raw,framerate={fps}/1");
        if (crop is PixelRect c && sourceWidth > 0 && sourceHeight > 0)
            Then("videocrop", $"left={c.X}", $"top={c.Y}", $"right={Math.Max(0, sourceWidth - c.Right)}", $"bottom={Math.Max(0, sourceHeight - c.Bottom)}");
        Then("videoscale");
        Then($"video/x-raw,width={width},height={height},pixel-aspect-ratio=1/1");
        Then("videoconvert");
        Then("video/x-raw,format=BGRA");
        Then("fdsink", "fd=1", "sync=false");

        Process? p;
        int fd = inherit == null ? -1 : (int)inherit.DangerousGetHandle();
        // One spawn at a time from here, with the fd inheritable only for that moment.
        lock (SpawnGate)
        {
            if (fd >= 0)
                CloseOnExec(fd, false);
            try
            {
                p = Process.Start(psi);
            }
            finally
            {
                if (fd >= 0)
                    CloseOnExec(fd, true);
            }
        }
        if (p == null)
            throw new InvalidOperationException("gst-launch-1.0 didn't start");
        lock (Running)
            Running.Add(p);
        return new MirrorFeed(p, width, height);
    }

    /// <summary>The cropped stream needs the source size for videocrop's margins; this overload passes it from the portal.</summary>
    public static MirrorFeed Start(string[] source, SafeHandle? inherit, int width, int height, int fps, PixelRect? crop, ScreenCastStream stream) =>
        Start(source, inherit, width, height, fps, crop, stream.Width, stream.Height);

    const int F_GETFD = 1, F_SETFD = 2, FD_CLOEXEC = 1;

    [DllImport("libc", SetLastError = true)]
    static extern int fcntl(int fd, int cmd, int arg);

    static void CloseOnExec(int fd, bool on)
    {
        int flags = fcntl(fd, F_GETFD, 0);
        if (flags < 0 || fcntl(fd, F_SETFD, on ? flags | FD_CLOEXEC : flags & ~FD_CLOEXEC) < 0)
            throw new IOException($"fcntl on fd {fd} failed (errno {Marshal.GetLastPInvokeError()})");
    }

    /// <summary>Called on every FrameClock tick while the tile is on screen; without it the pipeline stops itself after 3 s.</summary>
    public void Wanted() => Interlocked.Exchange(ref _wantedAt, Environment.TickCount64);

    /// <summary>Copies the newest frame into a bitmap's memory, if there's one that wasn't shown yet.</summary>
    public bool TakeFrame(IntPtr dest, int rowBytes)
    {
        lock (_gate)
        {
            if (!_fresh)
                return false;
            _fresh = false;
            int row = Width * 4;
            if (rowBytes == row)
                Marshal.Copy(_ready, 0, dest, _ready.Length);
            else
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(_ready, y * row, dest + y * rowBytes, row);
            return true;
        }
    }

    void ReadLoop()
    {
        try
        {
            var stdout = _process.StandardOutput.BaseStream;
            while (true)
            {
                stdout.ReadExactly(_back);
                lock (_gate)
                {
                    (_back, _ready) = (_ready, _back);
                    _fresh = true;
                }
                if (Environment.TickCount64 - Interlocked.Read(ref _wantedAt) > 3000)
                {
                    _unwatched = true; // nobody's looking
                    break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            // gst-launch ended (the shared window closed, or an error; see stderr)
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // killed
        }
        _exited = true;
        Kill();
    }

    async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                if (line.Trim().Length == 0)
                    continue;
                _lastError = line.Trim().Length > 120 ? line.Trim()[..120] : line.Trim();
                Log.WriteThrottled("mirror-gst", "Mirror: gst-launch says: " + line.Trim(), TimeSpan.FromMinutes(5));
            }
        }
        catch
        {
            // closed
        }
    }

    void Kill()
    {
        LinuxCli.Kill(_process);
        lock (Running)
            Running.Remove(_process);
    }

    public void Dispose()
    {
        Kill();
        try { _process.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>The portal's restore tokens, one per Mirror tile, in screencast.json (not config.json: saving that reloads the dashboard).</summary>
static class MirrorTokens
{
    static string FilePath => Path.Combine(AppConfig.Folder, "screencast.json");
    static readonly object Gate = new();
    static Dictionary<string, string>? _tokens;

    public static string? Get(string key)
    {
        lock (Gate)
            return Load().TryGetValue(key, out var t) ? t : null;
    }

    public static void Set(string key, string? token)
    {
        lock (Gate)
        {
            var all = Load();
            if (token == null ? !all.Remove(key) : all.TryGetValue(key, out var old) && old == token)
                return;
            if (token != null)
                all[key] = token;
            try
            {
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all));
            }
            catch (Exception ex)
            {
                Log.Write($"Mirror choice not saved: {ex.Message}");
            }
        }
    }

    static Dictionary<string, string> Load()
    {
        if (_tokens != null)
            return _tokens;
        try
        {
            _tokens = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) : null;
        }
        catch
        {
            // unreadable: start over
        }
        return _tokens ??= new Dictionary<string, string>();
    }
}
