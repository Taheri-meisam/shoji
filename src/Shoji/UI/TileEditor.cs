using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// Press and hold on a dashboard tile (finger or mouse, ~0.65 s without moving) asks for its edit sheet.
/// The tile sinks a little and an accent outline grows while it's held. Once the hold completes, the release
/// belongs to the tile and is swallowed there, so the press doesn't also tap a button, drag a slider or swipe the page.
/// </summary>
public static class TileHold
{
    /// <summary>Called when a hold completes; null turns the gesture off (Settings → Pages & tiles).</summary>
    public static Action<Tile>? Handler { get; set; }

    const double Slop = 12; // DIPs a finger may wander and still be holding
    static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(650);
    static readonly TimeSpan FeedbackDelay = TimeSpan.FromMilliseconds(120); // a quick tap shows nothing

    static DispatcherTimer? _timer;
    static Tile? _holding;
    static Window? _window;
    static Point _start;
    static TouchDevice? _touch;
    static Tile? _swallowMouse;
    static TouchDevice? _swallowTouch;

    public static void Attach(Tile tile)
    {
        // Windows' own press and hold (the right-click ring) would also hold back the touch → mouse promotion taps rely on.
        Stylus.SetIsPressAndHoldEnabled(tile, false);

        tile.PreviewTouchDown += (_, e) =>
        {
            if (_holding != null && _touch != null && _touch != e.TouchDevice)
            {
                Cancel(); // a second finger: a pinch or two-finger gesture, not a hold
                return;
            }
            if (Window.GetWindow(tile) is { } w)
                Start(tile, w, e.GetTouchPoint(w).Position, e.TouchDevice);
        };
        tile.PreviewTouchMove += (_, e) =>
        {
            if (_holding == tile && e.TouchDevice == _touch && Moved(e.GetTouchPoint(_window).Position))
                Cancel();
        };
        tile.PreviewTouchUp += (_, e) =>
        {
            if (_swallowTouch == e.TouchDevice)
            {
                _swallowTouch = null;
                e.Handled = true;
                tile.ReleaseTouchCapture(e.TouchDevice);
            }
            if (_holding == tile && e.TouchDevice == _touch)
                Cancel();
        };

        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            ReleaseMouse(); // a release we never saw (shouldn't happen, but never keep the mouse captured)
            // A finger is followed by the touch events above; the mouse (and a pen) start a hold here.
            if (e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch)
                return;
            if (Window.GetWindow(tile) is { } w)
                Start(tile, w, e.GetPosition(w), null);
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (_holding == tile && Moved(e.GetPosition(_window)))
                Cancel();
        };
        tile.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_swallowMouse == tile)
            {
                e.Handled = true; // the hold opened the sheet: this release is not a tap
                ReleaseMouse();
            }
            if (_holding == tile)
                Cancel(); // let go early: an ordinary tap
        };
    }

    static bool Moved(Point p) => (p - _start).Length > Slop;

    static void Start(Tile tile, Window window, Point at, TouchDevice? touch)
    {
        Cancel();
        if (Handler == null)
            return;
        _holding = tile;
        _window = window;
        _start = at;
        _touch = touch;
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoldTime };
            _timer.Tick += (_, _) => Fire();
        }
        _timer.Start();
        Sink(tile);
    }

    static void Cancel()
    {
        _timer?.Stop();
        if (_holding is { } tile)
            Rise(tile);
        _holding = null;
        _window = null;
        _touch = null;
    }

    static void Fire()
    {
        var tile = _holding;
        var touch = _touch;
        Cancel();
        if (tile == null || !tile.IsVisible || Handler is not { } handler)
            return;
        bool mouse = Mouse.LeftButton == MouseButtonState.Pressed; // also true for a finger promoted to the mouse
        bool finger = touch is { IsActive: true };
        if (!mouse && !finger)
            return;
        // The release now comes to the tile, whatever is under the finger (a deck button, a slider, the sheet).
        if (mouse)
        {
            _swallowMouse = tile;
            tile.CaptureMouse();
        }
        else
        {
            _swallowTouch = touch;
            touch!.Capture(tile);
        }
        handler(tile);
    }

    static void ReleaseMouse()
    {
        if (_swallowMouse is not { } tile)
            return;
        _swallowMouse = null;
        if (tile.IsMouseCaptured)
            tile.ReleaseMouseCapture();
    }

    // ---------------------------------------------------------------- feedback

    static void Sink(Tile tile)
    {
        var duration = HoldTime - FeedbackDelay;
        var scale = Scale(tile);
        var shrink = new DoubleAnimation(0.97, duration) { BeginTime = FeedbackDelay, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var ring = Outline(tile);
        ring.BorderBrush = Theme.Accent;
        ring.CornerRadius = new CornerRadius(Theme.Current.Radius);
        ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = FeedbackDelay });
        ring.BeginAnimation(Border.BorderThicknessProperty, new ThicknessAnimation(new Thickness(0.5), new Thickness(3), duration) { BeginTime = FeedbackDelay });
    }

    static void Rise(Tile tile)
    {
        var back = new DoubleAnimation(1, TimeSpan.FromMilliseconds(140));
        var scale = Scale(tile);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, back);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, back);
        Outline(tile).BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
    }

    static ScaleTransform Scale(Tile tile)
    {
        if (tile.RenderTransform is ScaleTransform s)
            return s;
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        var created = new ScaleTransform(1, 1);
        tile.RenderTransform = created;
        return created;
    }

    const string OutlineTag = "hold-outline";

    static Border Outline(Tile tile)
    {
        if (tile.Children.OfType<Border>().FirstOrDefault(b => OutlineTag.Equals(b.Tag)) is { } existing)
            return existing;
        var ring = new Border { Tag = OutlineTag, IsHitTestVisible = false, Opacity = 0 };
        tile.Children.Add(ring);
        return ring;
    }
}

/// <summary>
/// The quick edit sheet for one tile, shown over the dashboard after a long press: order, width, style, page, hide.
/// It lives in its own layer of the dashboard window, so it stays open while the dashboard rebuilds after each change;
/// it keeps the tile as (page, index in that page's Modules, type) and finds it again in every new config.
/// </summary>
public sealed class TileEditor : Grid
{
    readonly Func<int, int> _columns;
    readonly Action<string> _toast;
    readonly Action<int> _openSettings;
    readonly Border _scrim = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
    readonly Border _sheet = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1.5) };
    readonly Border _inner = new();
    readonly DockPanel _dock = new();
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
    readonly Border _undo = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16), BorderThickness = new Thickness(1) };
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(20) };
    readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly ScaleTransform _pop = new(1, 1);

    AppConfig _config = new();
    int _page = -1, _index = -1;
    string _type = "";
    string? _rendered;
    (int Page, int Index, string Type)? _hidden;
    Brush _buttonFill = Brushes.Transparent, _buttonStroke = Brushes.Transparent, _onAccent = Brushes.White;

    /// <param name="columns">How many columns a configured page is laid out in right now.</param>
    /// <param name="toast">Shows a short message on the dashboard.</param>
    /// <param name="openSettings">Opens Settings → Pages & tiles at a page.</param>
    public TileEditor(Func<int, int> columns, Action<string> toast, Action<int> openSettings)
    {
        _columns = columns;
        _toast = toast;
        _openSettings = openSettings;

        // A tap outside the sheet closes it (only a press that started there: not the release of the long press).
        bool pressed = false;
        _scrim.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            e.Handled = true;
        };
        _scrim.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed)
                return;
            pressed = false;
            e.Handled = true;
            Close();
        };
        _scrim.MouseLeave += (_, _) => pressed = false;

        _scroll.ManipulationBoundaryFeedback += (_, e) => e.Handled = true; // don't drag the whole window at the scroll ends
        _dock.Children.Add(_scroll);
        _inner.Child = _dock;
        _sheet.Child = _inner;
        _sheet.RenderTransformOrigin = new Point(0.5, 0.5);
        _sheet.RenderTransform = _pop;

        Children.Add(_scrim);
        Children.Add(_sheet);
        Children.Add(_undo);

        _idle.Tick += (_, _) => Close();
        _undoTimer.Tick += (_, _) => HideUndo();
        // Any touch on the sheet counts as "still editing".
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => KeepOpen()), handledEventsToo: true);
        AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, _) => KeepOpen()), handledEventsToo: true);
        SizeChanged += (_, _) => FitSheet();
    }

    public bool IsOpen => _page >= 0;

    /// <summary>The tile being edited: its page and index in that page's Modules (disabled tiles included).</summary>
    public (int Page, int Index)? Target => IsOpen ? (_page, _index) : null;

    public void Open(AppConfig config, int page, int index)
    {
        if (Find(config, page, index, null) is not { } m)
            return;
        _config = config;
        _page = page;
        _index = index;
        _type = m.Type;
        HideUndo();
        _rendered = null;
        Render();
        _scroll.ScrollToHome();
        _scrim.Visibility = _sheet.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _sheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        _scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _idle.Stop();
        _idle.Start();
    }

    public void Close()
    {
        _page = _index = -1;
        _idle.Stop();
        _scrim.Visibility = _sheet.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The dashboard was rebuilt (config reloaded, theme changed): show the sheet again in the new theme with the
    /// saved state, or close it when the tile isn't there any more. Returns the tile being edited, if still open.
    /// </summary>
    public (int Page, int Index)? Reload(AppConfig config)
    {
        if (_undo.Visibility == Visibility.Visible)
            StyleUndo();
        if (!IsOpen)
            return null;
        if (Find(config, _page, _index, _type) == null)
        {
            Close();
            return null;
        }
        _config = config;
        Render();
        return Target;
    }

    void KeepOpen()
    {
        if (!IsOpen)
            return;
        _idle.Stop();
        _idle.Start();
    }

    void FitSheet()
    {
        _sheet.MaxWidth = Math.Max(200, Math.Min(ActualWidth - 24, 980));
        _sheet.MaxHeight = Math.Max(160, ActualHeight - 16);
    }

    static ModuleConfig? Find(AppConfig c, int page, int index, string? type) =>
        page >= 0 && page < c.Pages.Count && index >= 0 && index < c.Pages[page].Modules.Count
        && (type == null || c.Pages[page].Modules[index].Type == type)
            ? c.Pages[page].Modules[index]
            : null;

    // ---------------------------------------------------------------- changes

    /// <summary>Saves a change to the edited tile. False when config.json is broken or the tile was moved meanwhile.</summary>
    bool Change(Action<AppConfig, PageConfig, ModuleConfig> change)
    {
        int page = _page, index = _index;
        string type = _type;
        AppConfig? saved = null;
        bool ok = AppConfig.Edit(c =>
        {
            if (Find(c, page, index, type) is not { } m)
                return;
            change(c, c.Pages[page], m);
            saved = c;
        });
        if (!ok)
        {
            _toast(L.T("config.json can't be read right now, so nothing was saved"));
            return false;
        }
        if (saved == null)
        {
            Close();
            _toast(L.T("That tile was changed elsewhere – hold it again"));
            return false;
        }
        _config = saved; // the dashboard reloads in a moment; show the new state right away
        return true;
    }

    static int Neighbour(List<ModuleConfig> modules, int index, int direction)
    {
        for (int i = index + direction; i >= 0 && i < modules.Count; i += direction)
            if (modules[i].Enabled)
                return i;
        return -1;
    }

    void Move(int direction)
    {
        int index = _index, target = -1;
        if (!Change((_, page, _) =>
            {
                target = Neighbour(page.Modules, index, direction);
                if (target >= 0)
                    (page.Modules[index], page.Modules[target]) = (page.Modules[target], page.Modules[index]);
            }))
            return;
        if (target >= 0)
            _index = target;
        Render();
    }

    void SetWidth(int span)
    {
        if (Change((_, _, m) => m.Span = span))
            Render();
    }

    void SetStyle(string? style)
    {
        if (Change((_, _, m) => m.SetOpt("Style", style)))
            Render();
    }

    void MoveToPage(int target)
    {
        int index = _index;
        string name = _config.Pages[target].Name;
        if (!Change((c, page, m) =>
            {
                if (target < 0 || target >= c.Pages.Count)
                    return;
                page.Modules.RemoveAt(index);
                c.Pages[target].Modules.Add(m);
            }))
            return;
        Close();
        _toast(L.F("Moved to “{0}”", name));
    }

    void Hide()
    {
        var hidden = (_page, _index, _type);
        if (!Change((_, _, m) => m.Enabled = false))
            return;
        Close();
        ShowUndo(hidden);
    }

    void MoreSettings()
    {
        int page = _page;
        Close();
        _openSettings(page);
    }

    // ---------------------------------------------------------------- "Tile hidden · Undo"

    void ShowUndo((int Page, int Index, string Type) hidden)
    {
        _hidden = hidden;
        StyleUndo();
        _undo.Visibility = Visibility.Visible;
        _undo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    void HideUndo()
    {
        _undoTimer.Stop();
        _hidden = null;
        _undo.Visibility = Visibility.Collapsed;
    }

    void StyleUndo()
    {
        Colours();
        double radius = Math.Clamp(Theme.Current.Radius, 0, 14);
        _undo.CornerRadius = new CornerRadius(radius);
        _undo.Background = new SolidColorBrush(Theme.BackgroundColor); // under the tile colour, which may be see-through
        _undo.BorderBrush = Theme.Accent;
        var text = Label(L.T("Tile hidden"), 16, Theme.Text);
        text.Margin = new Thickness(0, 0, 14, 0);
        var undo = Button(L.T("Undo"), Undo, accent: true);
        undo.Margin = new Thickness(0);
        _undo.Child = new Border
        {
            Background = Theme.Tile,
            CornerRadius = new CornerRadius(Math.Max(0, radius - 1)),
            Padding = new Thickness(18, 6, 6, 6),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { text, undo } },
        };
    }

    void Undo()
    {
        if (_hidden is not { } h)
            return;
        HideUndo();
        bool found = false;
        bool ok = AppConfig.Edit(c =>
        {
            if (Find(c, h.Page, h.Index, h.Type) is { Enabled: false } m)
            {
                m.Enabled = true;
                found = true;
            }
        });
        _toast(!ok ? L.T("config.json can't be read right now, so nothing was saved") : found ? L.T("Tile is back") : L.T("That tile was changed elsewhere"));
    }

    // ---------------------------------------------------------------- the sheet

    void Colours()
    {
        _buttonFill = new SolidColorBrush(Theme.WithAlpha(Theme.TextColor, 0x18));
        _buttonStroke = new SolidColorBrush(Theme.WithAlpha(Theme.TextColor, 0x30));
        _onAccent = Theme.Contrast(Theme.AccentColor, Colors.White) >= Theme.Contrast(Theme.AccentColor, Colors.Black) ? Brushes.White : Brushes.Black;
    }

    void Render()
    {
        var config = _config;
        var page = config.Pages[_page];
        var m = page.Modules[_index];
        var info = ModuleCatalog.Find(m.Type);
        string kind = info != null ? L.T(info.Name) : m.Type;
        string name = string.IsNullOrWhiteSpace(m.Title) ? kind : m.Title!.Trim();
        int cols = Math.Max(1, _columns(_page));

        // Rebuilding the buttons under a finger would lose its tap: skip it when a reload changed nothing shown here.
        string key = string.Join("|", L.Language, Theme.Current.Name, Theme.AccentColor, Theme.BackgroundColor, Theme.TextColor, _page, _index, name, page.Name,
            m.Span, m.Opt("Style"), cols, Neighbour(page.Modules, _index, -1), Neighbour(page.Modules, _index, 1), string.Join("/", config.Pages.Select(p => p.Name)));
        if (key == _rendered)
            return;
        _rendered = key;

        // Theme colours are read every time: the theme may have changed (day / night, a scene).
        Colours();
        double radius = Math.Clamp(Theme.Current.Radius, 0, 20);
        _sheet.CornerRadius = new CornerRadius(radius);
        _sheet.Background = new SolidColorBrush(Theme.BackgroundColor);
        _sheet.BorderBrush = Theme.Accent;
        _sheet.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 30, ShadowDepth = 4, Direction = 270, Opacity = 0.5 };
        _inner.CornerRadius = new CornerRadius(Math.Max(0, radius - 1.5));
        _inner.Background = Theme.Tile;
        _inner.Padding = new Thickness(18, 12, 12, 6);
        FitSheet();

        // Header: what and where, and Done (always in view; only the rows below scroll).
        var title = Label(name, 21, Theme.Text, FontWeights.SemiBold);
        var where = Label((name != kind ? L.F("{0} tile · ", kind) : "") + L.F("page “{0}”", page.Name), 14, Theme.Dim);
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Children = { title, where } };
        var done = Button(L.T("Done"), Close, accent: true);
        done.MinWidth = 96;
        done.Margin = new Thickness(0, 0, 0, 8);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(done, Dock.Right);
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(done);
        header.Children.Add(heading);
        _dock.Children.Clear();
        _dock.Children.Add(header);
        _dock.Children.Add(_scroll);

        var rows = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };

        // Order and width. Moving skips switched-off tiles, which don't show.
        int width = Math.Clamp(m.Span, 1, cols);
        string widthText = width >= cols ? cols == 1 ? L.T("Full width") : L.F("Full width ({0} col)", cols) : L.F("{0} of {1} columns", width, cols);
        // Reaching the last column stores "full row" (like Settings), so it stays full on a screen with more columns.
        int Wider() => width + 1 >= cols ? Math.Max(9, cols) : width + 1;
        var widthLabel = Label(widthText, 15, Theme.Text);
        widthLabel.MinWidth = 130;
        widthLabel.TextAlignment = TextAlignment.Center;
        widthLabel.Margin = new Thickness(4, 0, 12, 8);
        rows.Children.Add(Row(L.T("Layout"),
            Button(L.T("◀  Move"), () => Move(-1), enabled: Neighbour(page.Modules, _index, -1) >= 0),
            Button(L.T("Move  ▶"), () => Move(1), enabled: Neighbour(page.Modules, _index, 1) >= 0),
            new Border { Width = 18 },
            Button(L.T("Narrower"), () => SetWidth(width - 1), enabled: width > 1),
            widthLabel,
            Button(L.T("Wider"), () => SetWidth(Wider()), enabled: width < cols)));

        // Style, as in Settings: no "Style" option = the theme's default.
        if (info is { Styles.Length: > 1 })
        {
            string themeDefault = Theme.StyleFor(m.Type, null);
            string defaultName = info.Styles.FirstOrDefault(st => st.Id.Equals(themeDefault, StringComparison.OrdinalIgnoreCase)).Name ?? info.Styles[0].Name;
            int current = m.Opt("Style") is { } chosen ? Array.FindIndex(info.Styles, st => st.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase)) + 1 : 0;
            var chips = new List<UIElement> { Button(L.F("Theme ({0})", L.T(defaultName)), () => SetStyle(null), selected: current <= 0) };
            for (int i = 0; i < info.Styles.Length; i++)
            {
                string id = info.Styles[i].Id;
                chips.Add(Button(L.T(info.Styles[i].Name), () => SetStyle(id), selected: current == i + 1));
            }
            rows.Children.Add(Row(L.T("Style"), chips.ToArray()));
        }

        if (config.Pages.Count > 1)
        {
            var pages = new List<UIElement>();
            for (int i = 0; i < config.Pages.Count; i++)
            {
                if (i == _page)
                    continue;
                int target = i;
                pages.Add(Button($"{i + 1} · {config.Pages[i].Name}", () => MoveToPage(target)));
            }
            rows.Children.Add(Row(L.T("Move to page"), pages.ToArray()));
        }

        rows.Children.Add(Row("", Button(L.T("Hide tile"), Hide), Button(L.T("More settings…"), MoreSettings)));
        _scroll.Content = rows;
    }

    FrameworkElement Row(string label, params UIElement[] items)
    {
        var panel = new WrapPanel();
        foreach (var item in items)
            panel.Children.Add(item);
        var caption = Label(label, 14, Theme.Dim);
        caption.Width = 104;
        caption.TextWrapping = TextWrapping.Wrap;
        caption.TextTrimming = TextTrimming.None;
        caption.VerticalAlignment = VerticalAlignment.Top;
        caption.Margin = new Thickness(0, 14, 8, 0);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(caption, Dock.Left);
        row.Children.Add(caption);
        row.Children.Add(panel);
        return row;
    }

    static TextBlock Label(string text, double size, Brush color, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        FontFamily = Theme.Font,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A finger-sized button (at least 48 × 48) in the theme's colours.</summary>
    Border Button(string text, Action action, bool enabled = true, bool selected = false, bool accent = false)
    {
        bool filled = selected || accent;
        var label = Label(text, 16, filled ? _onAccent : Theme.Text, selected ? FontWeights.SemiBold : FontWeights.Normal);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.MaxWidth = 280;
        var button = new Border
        {
            MinHeight = 48,
            MinWidth = 48,
            Padding = new Thickness(16, 0, 16, 0),
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(Math.Clamp(Theme.Current.Radius * 0.7, 0, 12)),
            Background = filled ? Theme.Accent : _buttonFill,
            BorderBrush = filled ? Theme.Accent : _buttonStroke,
            BorderThickness = new Thickness(1),
            Child = label,
        };
        if (enabled)
            Tap.On(button, action);
        else
            button.Opacity = 0.35;
        return button;
    }
}
