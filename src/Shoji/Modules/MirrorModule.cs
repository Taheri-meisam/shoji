using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;
using Image = System.Windows.Controls.Image;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Shoji.Modules;

/// <summary>
/// Live copy of another window (Discord, OBS stats, a game's minimap…) or of an area of the main screen.
/// Captured a few times a second on a background thread with GDI; nothing leaves the PC.
/// </summary>
public sealed class MirrorModule : Module
{
    readonly Image _image = new() { Stretch = Stretch.Uniform };

    // False while the tile is on a hidden page or the dashboard isn't showing: then nothing is captured.
    // Nor while motion is paused (screen off, PC locked, night mode black).
    volatile bool _visible;
    readonly System.Windows.Controls.TextBlock _status;
    readonly CancellationTokenSource _cts = new();
    readonly string? _window, _screen, _ownScreen;
    readonly Rectangle? _region, _crop;
    readonly int _fps;
    WriteableBitmap? _bitmap;
    volatile bool _busy;
    // Reused from frame to frame (owned by the capture loop): the tile-size frame, the window's full-size snapshot
    // and the pixel buffer handed to the UI. Sizes rarely change, so there's no per-frame garbage.
    Bitmap? _frame;
    GdiSurface? _snapshot;
    byte[] _pixels = Array.Empty<byte>();
    DateTime _lastError;
    // Size of the tile in physical pixels: whole screens are scaled down to this while copying.
    volatile int _targetW = 640, _targetH = 360;

    public MirrorModule(ModuleConfig config, AppConfig app) : base(config, "Mirror")
    {
        _screen = config.Opt("Screen");
        _window = _screen == null ? config.Opt("Window") : null;
        _region = _screen == null ? ParseRect(config.Opt("Region")) : null;
        _crop = ParseRect(config.Opt("Crop"));
        _fps = Math.Clamp(config.OptInt("Fps", 5), 1, 30);
        _ownScreen = ScreenPlacer.Find(app.Monitor)?.DeviceName;
        bool configured = _screen != null || _window != null || _region != null;
        _status = W.Text(configured ? "" : L.T("Nothing to show yet: Settings → Pages & tiles → this Mirror tile → Choose what to mirror…"),
            Theme.Small, Theme.Dim);
        _status.TextWrapping = TextWrapping.Wrap;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        var body = new System.Windows.Controls.Grid();
        body.Children.Add(_image);
        body.Children.Add(_status);
        body.SizeChanged += (_, e) =>
        {
            // The page is scaled (LayoutTransform), so measure the tile's size as it ends up on screen, in pixels.
            try
            {
                var window = Window.GetWindow(body);
                var shown = window == null ? new Rect(e.NewSize) : body.TransformToAncestor(window).TransformBounds(new Rect(e.NewSize));
                var dpi = VisualTreeHelper.GetDpi(body);
                _targetW = Math.Max(64, (int)(shown.Width * dpi.DpiScaleX));
                _targetH = Math.Max(36, (int)(shown.Height * dpi.DpiScaleY));
            }
            catch (InvalidOperationException)
            {
                // not in a window yet; the next size change will do it
            }
        };
        string caption = _screen != null ? (_screen.Equals("main", StringComparison.OrdinalIgnoreCase) ? L.T("main screen") : L.F("screen {0}", _screen))
            : _window ?? (_region != null ? L.T("screen area") : "");
        View = MakeTile(body, W.Text(caption, Theme.Small, Theme.Dim));
        View.IsVisibleChanged += (_, _) => _visible = View.IsVisible;

        if (configured)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>"main" / "primary", a number (left to right, like the tray's monitor list) or a device name.</summary>
    internal static System.Windows.Forms.Screen? ResolveScreen(string value)
    {
        if (value.Equals("main", StringComparison.OrdinalIgnoreCase) || value.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return System.Windows.Forms.Screen.PrimaryScreen;
        var all = ScreenPlacer.Ordered();
        if (int.TryParse(value, out int n))
            return n >= 1 && n <= all.Length ? all[n - 1] : null;
        return all.FirstOrDefault(s => s.DeviceName.EndsWith(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copies a whole monitor straight into a bitmap the size of the tile (GDI scales while copying, so a 4K screen
    /// costs about as much as a small window), with the mouse pointer drawn in.
    /// </summary>
    internal static Bitmap CaptureScreen(Rectangle src, int maxW, int maxH)
    {
        double scale = Math.Min(maxW / (double)src.Width, maxH / (double)src.Height);
        int w = Math.Max(16, (int)(src.Width * Math.Min(1, scale))), h = Math.Max(9, (int)(src.Height * Math.Min(1, scale)));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            CopyScreen(src, bmp, cursor: true);
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
        return bmp;
    }

    /// <summary>Copies an area of the desktop into <paramref name="dst"/>, scaled to its size while copying.</summary>
    static void CopyScreen(Rectangle src, Bitmap dst, bool cursor)
    {
        int w = dst.Width, h = dst.Height;
        using var g = Graphics.FromImage(dst);
        IntPtr hdc = g.GetHdc(), screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            StretchCopy(hdc, w, h, screen, src);
            if (!cursor)
                return;
            var ci = new NativeMethods.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            if (NativeMethods.GetCursorInfo(ref ci) && (ci.flags & NativeMethods.CURSOR_SHOWING) != 0 && src.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y))
            {
                double k = w / (double)src.Width;
                int size = Math.Max(12, (int)(32 * k * 1.6)); // a bit larger than true scale so it stays visible
                NativeMethods.DrawIconEx(hdc, (int)((ci.ptScreenPos.X - src.X) * k), (int)((ci.ptScreenPos.Y - src.Y) * k), ci.hCursor, size, size, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            g.ReleaseHdc(hdc);
        }
    }

    /// <summary>GDI's good-quality scaling (HALFTONE averages pixels instead of dropping them).</summary>
    static void StretchCopy(IntPtr dst, int w, int h, IntPtr src, Rectangle area)
    {
        NativeMethods.SetStretchBltMode(dst, NativeMethods.HALFTONE);
        NativeMethods.SetBrushOrgEx(dst, 0, 0, IntPtr.Zero);
        NativeMethods.StretchBlt(dst, 0, 0, w, h, src, area.X, area.Y, area.Width, area.Height, NativeMethods.SRCCOPY);
    }

    /// <summary>The part of a <paramref name="width"/> × <paramref name="height"/> capture to show: the Crop if set (in its pixels), else all of it.</summary>
    Rectangle Area(int width, int height)
    {
        var all = new Rectangle(0, 0, width, height);
        if (_crop is Rectangle c)
        {
            var area = Rectangle.Intersect(c, all);
            if (area.Width > 0 && area.Height > 0)
                return area;
        }
        return all;
    }

    /// <summary>The reused frame, sized to fit <paramref name="area"/> into the tile (never larger than the source).</summary>
    Bitmap FrameFor(System.Drawing.Size area)
    {
        double scale = Math.Min(1, Math.Min(_targetW / (double)area.Width, _targetH / (double)area.Height));
        int w = Math.Max(1, (int)(area.Width * scale)), h = Math.Max(1, (int)(area.Height * scale));
        if (_frame == null || _frame.Width != w || _frame.Height != h)
        {
            _frame?.Dispose();
            _frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }
        return _frame;
    }

    static Rectangle? ParseRect(string? text)
    {
        var p = (text ?? "").Split(',', StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out int v) ? v : -1).ToArray();
        return p.Length == 4 && p[2] > 0 && p[3] > 0 ? new Rectangle(p[0], p[1], p[2], p[3]) : null;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        IntPtr hwnd = IntPtr.Zero;
        DateTime lookedAt = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (!_visible || Motion.Paused)
            {
                try { await Task.Delay(400, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                Bitmap? frame = null;
                string? message = null;
                if (_screen != null)
                {
                    var screen = ResolveScreen(_screen);
                    if (screen == null)
                        message = L.F("No screen \"{0}\" (use main, 1, 2…)", _screen);
                    else if (screen.DeviceName == _ownScreen)
                        message = L.T("That's the screen Shoji is on. Pick another one (e.g. main).");
                    else
                        frame = CaptureArea(screen.Bounds, cursor: true);
                }
                else if (_region is Rectangle r)
                {
                    frame = CaptureArea(r, cursor: false);
                }
                else
                {
                    if ((hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd)) && DateTime.UtcNow - lookedAt > TimeSpan.FromSeconds(3))
                    {
                        lookedAt = DateTime.UtcNow;
                        hwnd = FindWindow(_window!);
                    }
                    if (hwnd == IntPtr.Zero)
                        message = L.F("Waiting for \"{0}\"…", _window);
                    else if (NativeMethods.IsIconic(hwnd))
                        message = L.T("Window is minimised");
                    else
                        frame = CaptureWindowScaled(hwnd);
                }
                Show(frame, message);
            }
            catch (Exception ex)
            {
                // Expected while the PC is locked or a UAC prompt is up; log once a minute, not every frame.
                if (DateTime.UtcNow - _lastError > TimeSpan.FromMinutes(1))
                {
                    _lastError = DateTime.UtcNow;
                    Log.Write($"Mirror capture failed: {ex.Message}");
                }
                hwnd = IntPtr.Zero;
            }

            try { await Task.Delay(1000 / _fps, ct); }
            catch (OperationCanceledException) { break; }
        }
        _frame?.Dispose();
        _snapshot?.Dispose();
    }

    /// <summary>An area of the desktop (a screen or a region), with the Crop applied, scaled to the tile.</summary>
    Bitmap CaptureArea(Rectangle bounds, bool cursor)
    {
        var area = Area(bounds.Width, bounds.Height);
        area.Offset(bounds.Location);
        var frame = FrameFor(area.Size);
        CopyScreen(area, frame, cursor);
        return frame;
    }

    /// <summary>
    /// A window, with the Crop applied, scaled to the tile. PrintWindow can only draw at full size, so it draws into a
    /// reused off-screen surface and only the scaled-down copy becomes the frame.
    /// </summary>
    Bitmap? CaptureWindowScaled(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0)
            return null;
        if (_snapshot == null || _snapshot.Width != r.Width || _snapshot.Height != r.Height)
        {
            _snapshot?.Dispose();
            _snapshot = null;
            _snapshot = new GdiSurface(r.Width, r.Height);
        }
        // PW_RENDERFULLCONTENT also captures GPU-drawn content (Chromium, Electron apps like Discord).
        NativeMethods.PrintWindow(hwnd, _snapshot.Dc, NativeMethods.PW_RENDERFULLCONTENT);

        var area = Area(r.Width, r.Height);
        var frame = FrameFor(area.Size);
        using var g = Graphics.FromImage(frame);
        IntPtr hdc = g.GetHdc();
        try
        {
            StretchCopy(hdc, frame.Width, frame.Height, _snapshot.Dc, area);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        return frame;
    }

    /// <summary>A plain GDI memory bitmap: something PrintWindow can draw into without GDI+ copying it around.</summary>
    sealed class GdiSurface : IDisposable
    {
        public readonly int Width, Height;
        public readonly IntPtr Dc;
        readonly IntPtr _bitmap, _old;

        public GdiSurface(int width, int height)
        {
            Width = width;
            Height = height;
            IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                Dc = NativeMethods.CreateCompatibleDC(screen);
                _bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
            if (Dc == IntPtr.Zero || _bitmap == IntPtr.Zero)
            {
                Dispose();
                throw new InvalidOperationException($"No memory for a {width}×{height} window snapshot");
            }
            _old = NativeMethods.SelectObject(Dc, _bitmap);
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero)
            {
                if (_old != IntPtr.Zero)
                    NativeMethods.SelectObject(Dc, _old);
                NativeMethods.DeleteDC(Dc);
            }
            if (_bitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(_bitmap);
        }
    }

    /// <summary>First visible top-level window whose process name or title matches.</summary>
    static IntPtr FindWindow(string match)
    {
        IntPtr found = IntPtr.Zero;
        var title = new StringBuilder(256);
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h))
                return true;
            title.Clear();
            NativeMethods.GetWindowText(h, title, title.Capacity);
            if (title.Length == 0)
                return true;
            bool hit = title.ToString().Contains(match, StringComparison.OrdinalIgnoreCase);
            if (!hit)
            {
                NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    hit = p.ProcessName.Equals(match, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    // exited
                }
            }
            if (hit && OwnerPid(h) != Environment.ProcessId)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;

        static int OwnerPid(IntPtr h)
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint p);
            return (int)p;
        }
    }

    internal static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0)
            return null;
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr hdc = g.GetHdc();
        try
        {
            // PW_RENDERFULLCONTENT also captures GPU-drawn content (Chromium, Electron apps like Discord).
            NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }

    /// <summary>Hands the frame's pixels to the UI. The frame stays the loop's (it's reused); the pixels are copied.</summary>
    void Show(Bitmap? frame, string? message)
    {
        if (frame == null)
        {
            View.Dispatcher.BeginInvoke(() => _status.Text = message ?? "");
            return;
        }
        if (_busy)
            return; // UI still drawing the previous frame (and still reading the buffer); drop this one
        _busy = true;
        bool queued = false;
        try
        {
            int w = frame.Width, h = frame.Height;
            var data = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            try
            {
                if (_pixels.Length < stride * h)
                    _pixels = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _pixels, 0, stride * h);
            }
            finally
            {
                frame.UnlockBits(data);
            }

            var pixels = _pixels;
            View.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                    {
                        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                        _image.Source = _bitmap;
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
                    _status.Text = "";
                }
                finally
                {
                    _busy = false;
                }
            });
            queued = true;
        }
        finally
        {
            if (!queued)
                _busy = false; // LockBits or the copy failed: don't freeze the tile
        }
    }

    public override void Update(Snapshot s) { }

    protected override void OnDispose() => _cts.Cancel();
}
