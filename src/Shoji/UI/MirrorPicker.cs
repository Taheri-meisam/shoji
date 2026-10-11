using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Shoji.Core;
using Shoji.Modules;
using Screen = System.Windows.Forms.Screen;

namespace Shoji.UI;

/// <summary>What a mirror tile should show. Exactly one of Screen / Window / Region is set; Crop applies to a window.</summary>
public sealed record MirrorChoice(string? Screen, string? Window, string? Region, string? Crop, string Description);

/// <summary>
/// Pick what a mirror tile shows without typing: click a screen or an app window (with live thumbnails),
/// crop part of a window by dragging, click a window right on the screen, or draw an area of the screen.
/// </summary>
public sealed class MirrorPicker : Window
{
    const string TextSecondary = "TextFillColorSecondaryBrush", CardFill = "CardBackgroundFillColorDefaultBrush", CardStroke = "CardStrokeColorDefaultBrush";
    readonly string? _ownScreen;
    readonly StackPanel _panel = new() { Margin = new Thickness(28, 20, 28, 28) };
    MirrorChoice? _result;

    public static MirrorChoice? Ask(Window owner, string? ownScreenDevice)
    {
        var picker = new MirrorPicker(ownScreenDevice) { Owner = owner };
        picker.ShowDialog();
        return picker._result;
    }

    MirrorPicker(string? ownScreen)
    {
        _ownScreen = ownScreen;
        SettingsPalette.Apply(this, SettingsPalette.IsDark(AppConfig.Load().SettingsLook)); // Shoji's light / dark look, as in Settings
        Title = L.T("Choose what to mirror");
        Icon = SetupWindow.AppIcon();
        var area = SystemParameters.WorkArea;
        Width = Math.Min(980, area.Width - 40);
        Height = Math.Min(780, area.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Build();
    }

    void Build()
    {
        // ---- Screens
        Heading(L.T("A whole screen"), L.T("Shows everything on that monitor, with the mouse pointer."));
        var screens = new WrapPanel();
        var ordered = ScreenPlacer.Ordered();
        for (int i = 0; i < ordered.Length; i++)
        {
            var s = ordered[i];
            bool own = s.DeviceName == _ownScreen;
            string label = s.Primary ? L.T("Main screen") : L.F("Screen {0}", i + 1);
            string value = s.Primary ? "main" : (i + 1).ToString();
            var thumb = new Image { Stretch = Stretch.Uniform, Height = 130 };
            try
            {
                using var shot = MirrorModule.CaptureScreen(s.Bounds, 260, 146);
                thumb.Source = ToSource(shot);
            }
            catch
            {
                // blank thumbnail
            }
            var card = Card(thumb, label, own ? L.T("Shoji is on this screen") : $"{s.Bounds.Width}×{s.Bounds.Height}", 260);
            card.IsEnabled = !own;
            card.Opacity = own ? 0.5 : 1;
            if (!own)
                Clickable(card, () => Done(new MirrorChoice(value, null, null, null, label)));
            screens.Children.Add(card);
        }
        _panel.Children.Add(screens);

        // ---- Windows
        Heading(L.T("An app or window"), L.T("Mirror it whole, or crop to the part you care about (a chat, a minimap, OBS stats). Minimised windows can't be mirrored."));
        var windows = new WrapPanel();
        var list = AppWindows();
        var processCounts = list.GroupBy(w => w.Process, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var thumbs = new List<(IntPtr Hwnd, Image Image)>();
        foreach (var w in list)
        {
            // A process name survives title changes; use the title only when the app has several windows open.
            string match = processCounts[w.Process] == 1 ? w.Process : w.Title;
            var thumb = new Image { Stretch = Stretch.Uniform, Height = 110 };
            if (w.Icon != null)
                thumb.Source = w.Icon; // replaced by a snapshot once it's taken
            thumbs.Add((w.Hwnd, thumb));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var mirror = new Button { Content = L.T("Mirror"), Margin = new Thickness(0, 0, 6, 0) };
            mirror.SetResourceReference(StyleProperty, "AccentButtonStyle");
            mirror.Click += (_, _) => Done(new MirrorChoice(null, match, null, null, w.Title));
            var crop = new Button { Content = L.T("Crop…") };
            crop.Click += (_, _) =>
            {
                var rect = CropDialog.Ask(this, w.Hwnd, w.Title);
                if (rect != null)
                    Done(new MirrorChoice(null, match, null, rect, L.F("part of {0}", w.Title)));
            };
            actions.Children.Add(mirror);
            actions.Children.Add(crop);
            var card = Card(thumb, w.Title, w.Process + (w.Minimized ? L.T(" · minimised") : ""), 210, actions);
            if (w.Minimized) card.Opacity = 0.6;
            windows.Children.Add(card);
        }
        if (list.Count == 0)
            windows.Children.Add(Hint(L.T("No app windows found.")));
        _panel.Children.Add(windows);
        _ = LoadThumbnailsAsync(thumbs);

        // ---- Other ways
        Heading(L.T("Or pick it on the screen"), null);
        var other = new WrapPanel();
        var click = new Button { Content = L.T("Click a window on screen…"), Margin = new Thickness(0, 0, 8, 8) };
        click.Click += (_, _) => PickOnScreen(pickWindow: true);
        var draw = new Button { Content = L.T("Draw an area of the screen…"), Margin = new Thickness(0, 0, 8, 8) };
        draw.Click += (_, _) => PickOnScreen(pickWindow: false);
        other.Children.Add(click);
        other.Children.Add(draw);
        _panel.Children.Add(other);
        _panel.Children.Add(Hint(L.T("Protected video (Netflix and similar) and games in exclusive full screen show black; borderless-window games work. Everything stays on this PC.")));
    }

    /// <summary>Snapshots take a moment per window, so they fill in after the list is already showing.</summary>
    async Task LoadThumbnailsAsync(List<(IntPtr Hwnd, Image Image)> thumbs)
    {
        foreach (var (hwnd, image) in thumbs)
        {
            if (NativeMethods.IsIconic(hwnd))
                continue;
            var source = await Task.Run(() =>
            {
                try
                {
                    using var full = MirrorModule.CaptureWindow(hwnd);
                    if (full == null) return null;
                    double k = Math.Min(1, Math.Min(400.0 / full.Width, 220.0 / full.Height));
                    using var small = new System.Drawing.Bitmap(full, Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
                    return ToSource(small);
                }
                catch
                {
                    return null;
                }
            });
            if (source != null)
                image.Source = source;
        }
    }

    void PickOnScreen(bool pickWindow)
    {
        // Get Shoji's windows out of the way without hiding them: hiding a modal dialog ends it.
        var moved = new List<(Window Window, double Left, double Top)>();
        foreach (var w in new[] { Owner, this })
        {
            if (w == null) continue;
            moved.Add((w, w.Left, w.Top));
            w.Left = -100_000;
            w.Top = -100_000;
        }
        void Restore()
        {
            foreach (var (w, left, top) in moved)
            {
                w.Left = left;
                w.Top = top;
            }
            Activate();
        }

        ScreenOverlay.Pick(pickWindow, result =>
        {
            if (result is not { } r)
            {
                Restore();
                return;
            }
            if (pickWindow)
            {
                // Look up the window while Shoji's own windows are still out of the way.
                var hwnd = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = r.X, Y = r.Y }), NativeMethods.GA_ROOT);
                var w = Describe(hwnd);
                Restore();
                if (w == null || w.Value.Pid == Environment.ProcessId)
                {
                    MessageBox.Show(this, L.T("That isn't an app window Shoji can mirror. Try clicking the window's title bar."), "Shoji");
                    return;
                }
                bool unique = AppWindows().Count(x => x.Process.Equals(w.Value.Process, StringComparison.OrdinalIgnoreCase)) <= 1;
                Done(new MirrorChoice(null, unique ? w.Value.Process : w.Value.Title, null, null, w.Value.Title));
            }
            else
            {
                Restore();
                if (r.Width >= 16 && r.Height >= 16)
                    Done(new MirrorChoice(null, null, $"{r.X},{r.Y},{r.Width},{r.Height}", null, L.F("screen area {0}×{1}", r.Width, r.Height)));
            }
        });
    }

    void Done(MirrorChoice choice)
    {
        _result = choice;
        Close();
    }

    // ---------------------------------------------------------------- windows

    sealed record AppWindow(IntPtr Hwnd, string Title, string Process, ImageSource? Icon, bool Minimized);

    static List<AppWindow> AppWindows()
    {
        var list = new List<AppWindow>();
        var icons = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsAppWindow(h))
                return true;
            var d = Describe(h);
            if (d == null || d.Value.Pid == Environment.ProcessId || d.Value.Process is "ApplicationFrameHost" or "TextInputHost" or "explorer" && d.Value.Title == "Program Manager")
                return true;
            if (!icons.TryGetValue(d.Value.Process, out var icon))
                icons[d.Value.Process] = icon = AppIcon(d.Value.Path);
            list.Add(new AppWindow(h, d.Value.Title, d.Value.Process, icon, NativeMethods.IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static (string Title, string Process, string? Path, int Pid)? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;
        var title = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* elevated or protected */ }
            return (title.Length > 0 ? title.ToString() : p.ProcessName, p.ProcessName, path, (int)pid);
        }
        catch
        {
            return null;
        }
    }

    static ImageSource? AppIcon(string? exe)
    {
        if (exe == null)
            return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }

    internal static BitmapSource ToSource(System.Drawing.Bitmap bmp)
    {
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bmp.UnlockBits(data); // the caller still owns (and disposes) the bitmap
        }
    }

    // ---------------------------------------------------------------- building blocks

    void Heading(string text, string? sub)
    {
        _panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _panel.Children.Count == 0 ? 0 : 22, 0, sub == null ? 10 : 2) });
        if (sub != null)
            _panel.Children.Add(Hint(sub));
    }

    static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) };
        t.SetResourceReference(TextBlock.ForegroundProperty, TextSecondary);
        return t;
    }

    static Border Card(Image image, string title, string subtitle, double width, UIElement? extra = null)
    {
        var frame = new Border { Height = image.Height + 8, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1C)), Child = image, Padding = new Thickness(4) };
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
        var sub = new TextBlock { Text = subtitle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, TextSecondary);
        var stack = new StackPanel { Children = { frame, t, sub } };
        if (extra != null)
            stack.Children.Add(extra);
        var card = new Border { Width = width + 24, Padding = new Thickness(12), Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack, ToolTip = title };
        card.SetResourceReference(Border.BackgroundProperty, CardFill);
        card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        return card;
    }

    static void Clickable(Border card, Action click)
    {
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, SettingsPalette.Accent);
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        card.MouseLeftButtonUp += (_, _) => click();
    }
}

/// <summary>
/// Dims every screen so you can draw a rectangle (area mode) or click a window (window mode).
/// Reports physical screen pixels; Esc or right-click cancels.
/// </summary>
sealed class ScreenOverlay : Window
{
    readonly bool _pickWindow;
    readonly Action<ScreenOverlay, System.Drawing.Rectangle?> _done;
    readonly Canvas _canvas = new();
    readonly Rectangle _box = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0xC3, 0xF7)), Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)) };
    readonly TextBlock _size = new() { Foreground = Brushes.White, FontSize = 14, Visibility = Visibility.Collapsed };
    Point? _start;
    System.Drawing.Point _startPixel;

    /// <summary>Shows an overlay on every screen; <paramref name="done"/> gets the result once (null = cancelled).</summary>
    public static void Pick(bool pickWindow, Action<System.Drawing.Rectangle?> done)
    {
        var overlays = new List<ScreenOverlay>();
        bool finished = false;
        void Finish(ScreenOverlay _, System.Drawing.Rectangle? result)
        {
            if (finished) return;
            finished = true;
            foreach (var o in overlays) o.Close();
            done(result);
        }
        foreach (var s in Screen.AllScreens)
        {
            var o = new ScreenOverlay(pickWindow, Finish);
            overlays.Add(o);
            o.Cover(s);
        }
    }

    ScreenOverlay(bool pickWindow, Action<ScreenOverlay, System.Drawing.Rectangle?> done)
    {
        _pickWindow = pickWindow;
        _done = done;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0));
        Cursor = Cursors.Cross;
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1F, 0x23, 0x29)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 0, 0),
            Child = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 16,
                Text = pickWindow ? L.T("Click the window to mirror  ·  Esc to cancel") : L.T("Drag to draw the area to mirror  ·  Esc to cancel"),
            },
        };
        _canvas.Children.Add(_box);
        _canvas.Children.Add(_size);
        Content = new Grid { Children = { _canvas, hint } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _done(this, null); };
        MouseRightButtonUp += (_, _) => _done(this, null);
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
    }

    System.Drawing.Rectangle _bounds;

    void Cover(Screen s)
    {
        // Physical-pixel placement, like the dashboard window, so mixed-DPI setups line up. Moving to a monitor with
        // another scale makes WPF resize the window by its own rule, so place it again after showing and on DPI changes.
        _bounds = s.Bounds;
        new WindowInteropHelper(this).EnsureHandle();
        Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, System.Windows.Threading.DispatcherPriority.Background);
        Show();
        Place();
        Activate();
        Focus();
    }

    void Place() =>
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, NativeMethods.SWP_NOACTIVATE);

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_pickWindow)
            return; // picked on release, so the click never reaches the app underneath
        _start = e.GetPosition(this);
        _startPixel = NativeMethods.CursorPixel();
        CaptureMouse();
        _box.Visibility = _size.Visibility = Visibility.Visible;
        OnMove(sender, e);
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not Point a)
            return;
        var b = e.GetPosition(this);
        var r = new Rect(a, b);
        Canvas.SetLeft(_box, r.X);
        Canvas.SetTop(_box, r.Y);
        _box.Width = r.Width;
        _box.Height = r.Height;
        var now = NativeMethods.CursorPixel();
        _size.Text = $"{Math.Abs(now.X - _startPixel.X)} × {Math.Abs(now.Y - _startPixel.Y)}";
        Canvas.SetLeft(_size, r.X + 6);
        Canvas.SetTop(_size, r.Bottom + 6);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        var end = NativeMethods.CursorPixel();
        if (_pickWindow)
        {
            _done(this, new System.Drawing.Rectangle(end.X, end.Y, 1, 1));
            return;
        }
        if (_start == null)
            return;
        ReleaseMouseCapture();
        _start = null;
        // Both corners straight from the cursor in physical pixels: right even across monitors with different scaling.
        _done(this, new System.Drawing.Rectangle(Math.Min(_startPixel.X, end.X), Math.Min(_startPixel.Y, end.Y),
            Math.Abs(end.X - _startPixel.X), Math.Abs(end.Y - _startPixel.Y)));
    }
}

/// <summary>Shows a snapshot of a window; drag a rectangle over the part to keep. Returns "x,y,w,h" in window pixels.</summary>
sealed class CropDialog : Window
{
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    readonly Rectangle _box = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0xC3, 0xF7)), Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)) };
    readonly Button _ok = new() { Content = L.T("Use this part"), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    int _pixelW, _pixelH;
    Point? _start;
    Rect _selection;
    string? _result;

    public static string? Ask(Window owner, IntPtr hwnd, string title)
    {
        System.Drawing.Bitmap? shot;
        try { shot = MirrorModule.CaptureWindow(hwnd); } catch { shot = null; }
        if (shot == null)
        {
            MessageBox.Show(owner, L.T("Couldn't take a snapshot of that window (is it minimised?)."), "Shoji");
            return null;
        }
        using (shot)
        {
            var d = new CropDialog(shot, title) { Owner = owner };
            d.ShowDialog();
            return d._result;
        }
    }

    CropDialog(System.Drawing.Bitmap shot, string title)
    {
        SettingsPalette.Apply(this, SettingsPalette.IsDark(AppConfig.Load().SettingsLook)); // Shoji's light / dark look, as in Settings
        Title = L.F("Crop: {0}", title);
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1100, area.Width - 60);
        Height = Math.Min(760, area.Height - 60);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        _pixelW = shot.Width;
        _pixelH = shot.Height;
        _image.Source = MirrorPicker.ToSource(shot);

        _canvas.Children.Add(_box);
        var stage = new Grid { Children = { _image, _canvas }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        // The canvas measures to nothing, so the grid takes the picture's size and stretches the canvas over it.
        _canvas.MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); _canvas.CaptureMouse(); _box.Visibility = Visibility.Visible; };
        _canvas.MouseMove += (_, e) =>
        {
            if (_start is not Point a) return;
            var b = e.GetPosition(_canvas);
            b = new Point(Math.Clamp(b.X, 0, _canvas.ActualWidth), Math.Clamp(b.Y, 0, _canvas.ActualHeight));
            _selection = new Rect(a, b);
            Canvas.SetLeft(_box, _selection.X);
            Canvas.SetTop(_box, _selection.Y);
            _box.Width = _selection.Width;
            _box.Height = _selection.Height;
        };
        _canvas.MouseLeftButtonUp += (_, _) => { _start = null; _canvas.ReleaseMouseCapture(); _ok.IsEnabled = _selection.Width >= 8 && _selection.Height >= 8; };

        _ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _ok.Click += (_, _) =>
        {
            double kx = _pixelW / Math.Max(1, _canvas.ActualWidth), ky = _pixelH / Math.Max(1, _canvas.ActualHeight);
            _result = $"{(int)(_selection.X * kx)},{(int)(_selection.Y * ky)},{(int)(_selection.Width * kx)},{(int)(_selection.Height * ky)}";
            Close();
        };
        var cancel = new Button { Content = L.T("Cancel") };
        cancel.Click += (_, _) => Close();
        var hint = new TextBlock { Text = L.T("Drag over the part of the window to show on the dashboard."), VerticalAlignment = VerticalAlignment.Center };
        var bar = new DockPanel { Margin = new Thickness(20, 12, 20, 16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _ok, cancel } };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(hint);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new Border { Margin = new Thickness(20, 20, 20, 0), Child = stage });
        Content = root;
    }
}
