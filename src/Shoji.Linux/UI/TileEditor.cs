using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// Hold a tile (finger or mouse, ~650 ms, without moving) to edit it right on the dashboard: order, width, style,
/// page, hide. The sheet is a layer in the dashboard window; changes go to config.json like Settings' do, and the
/// dashboard reloads under it.
/// </summary>
public sealed class TileEditor : Grid
{
    // ---------------------------------------------------------------- long press (one per tile, hooked centrally)

    const int HoldMs = 650;
    const double Slop = 12;

    static Tile? _holdTile;
    static IPointer? _holdPointer;
    static Point _holdStart;
    static DispatcherTimer? _holdTimer;

    /// <summary>Watches a tile for a long press. Tunnel handlers, so it sees presses on buttons and lists inside it too.</summary>
    public static void Attach(Tile tile)
    {
        // The whole card is holdable, not just its text and graphics (a panel without a background isn't hit).
        tile.Background ??= Brushes.Transparent;
        tile.AddHandler(PointerPressedEvent, (_, e) => OnHoldPressed(tile, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        tile.AddHandler(PointerMovedEvent, (_, e) => OnHoldMoved(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        tile.AddHandler(PointerReleasedEvent, (_, e) => OnHoldReleased(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        tile.AddHandler(PointerCaptureLostEvent, (_, e) => { if (e.Pointer == _holdPointer) CancelHold(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        tile.DetachedFromVisualTree += (_, _) => { if (_holdTile == tile) CancelHold(); };
    }

    /// <summary>The press that opened the sheet: its release must not also tap what was under the finger.</summary>
    static IPointer? _swallow;

    static void OnHoldPressed(Tile tile, PointerPressedEventArgs e)
    {
        _swallow = null;
        if (TopLevel.GetTopLevel(tile) is not Dashboard dashboard || !dashboard.LongPressEnabled || _holdTimer?.IsEnabled == true)
            return;
        if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
            return;
        // Dragging a slider is adjusting it, not holding the tile.
        for (var v = e.Source as Visual; v != null && v != tile; v = v.GetVisualParentCompat())
            if (v is TouchSlider)
                return;
        _holdTile = tile;
        _holdPointer = e.Pointer;
        _holdStart = e.GetPosition(dashboard);
        Feedback(tile, true);
        _holdTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _holdTimer.Tick -= OnHoldElapsed;
        _holdTimer.Tick += OnHoldElapsed;
        _holdTimer.Start();
    }

    static void OnHoldMoved(PointerEventArgs e)
    {
        if (_holdTile == null || e.Pointer != _holdPointer || TopLevel.GetTopLevel(_holdTile) is not Visual top)
            return;
        var p = e.GetPosition(top);
        if (Math.Abs(p.X - _holdStart.X) > Slop || Math.Abs(p.Y - _holdStart.Y) > Slop || Dashboard.SwipeInProgress)
            CancelHold();
    }

    static void OnHoldReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer == _swallow)
        {
            e.Handled = true; // the sheet opened on this press: no tap on the button underneath
            _swallow = null;
        }
        if (e.Pointer == _holdPointer)
            CancelHold();
    }

    static void OnHoldElapsed(object? sender, EventArgs e)
    {
        _holdTimer?.Stop();
        var tile = _holdTile;
        var pointer = _holdPointer;
        CancelHold();
        if (tile == null || pointer == null || TopLevel.GetTopLevel(tile) is not Dashboard dashboard || !dashboard.LongPressEnabled)
            return;
        _swallow = pointer;
        // Let go of the button under the finger (it took the pointer on press): the release then lands on the sheet,
        // not on the button, and the button gets a PointerExited that clears its pressed look.
        pointer.Capture(null);
        dashboard.EditTile(tile);
    }

    static void CancelHold()
    {
        _holdTimer?.Stop();
        if (_holdTile != null)
            Feedback(_holdTile, false);
        _holdTile = null;
        _holdPointer = null;
    }

    /// <summary>While held, the tile sinks a little and an accent outline fades in (one-off transitions, no timer).</summary>
    static void Feedback(Tile tile, bool holding)
    {
        var outline = tile.Children.OfType<Border>().FirstOrDefault(b => b.Tag as string == "hold");
        if (outline == null)
        {
            if (!holding)
                return;
            double radius = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer or TileShape.Drafting ? 0 : UI.Theme.Current.Radius;
            outline = new Border
            {
                Tag = "hold", IsHitTestVisible = false, Opacity = 0, BorderThickness = new Thickness(2.5),
                BorderBrush = UI.Theme.Accent, CornerRadius = new CornerRadius(radius),
            };
            tile.Children.Add(outline);
        }
        var slow = TimeSpan.FromMilliseconds(HoldMs);
        if (holding)
        {
            outline.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = slow } };
            tile.RenderTransform = TransformOperations.Parse("scale(1)");
            tile.Transitions = new Transitions { new TransformOperationsTransition { Property = RenderTransformProperty, Duration = slow, Easing = new Avalonia.Animation.Easings.CubicEaseOut() } };
            outline.Opacity = 1;
            tile.RenderTransform = TransformOperations.Parse("scale(0.965)");
        }
        else
        {
            // Snap back.
            outline.Transitions = null;
            tile.Transitions = null;
            outline.Opacity = 0;
            tile.RenderTransform = null;
        }
    }

    // ---------------------------------------------------------------- the sheet

    readonly Dashboard _dashboard;
    readonly Border _scrim = new() { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)) };
    readonly Border _sheet = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 12) };
    readonly Border _undo = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18), Opacity = 0 };
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(20) };
    readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    AppConfig _cfg = new();
    int _p, _i;
    string _type = "";
    Action? _undoAction;

    public bool IsOpen => _sheet.IsVisible;

    public TileEditor(Dashboard dashboard)
    {
        _dashboard = dashboard;
        IsVisible = false;
        _scrim.IsVisible = _sheet.IsVisible = _undo.IsVisible = false;
        // A tap outside the sheet closes it (only a press that started there: not the release of the long press).
        bool scrimPressed = false;
        _scrim.PointerPressed += (_, _) => scrimPressed = true;
        _scrim.PointerReleased += (_, e) =>
        {
            if (!scrimPressed)
                return;
            scrimPressed = false;
            e.Handled = true;
            Close();
        };
        _undo.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) } };
        Children.Add(_scrim);
        Children.Add(_sheet);
        Children.Add(_undo);
        _idle.Tick += (_, _) => Close();
        _undoTimer.Tick += (_, _) => HideUndo();
        // Any touch on the sheet keeps it open another 20 s.
        AddHandler(PointerPressedEvent, (_, _) => { if (_idle.IsEnabled) { _idle.Stop(); _idle.Start(); } }, RoutingStrategies.Tunnel, handledEventsToo: true);
        SizeChanged += (_, _) => { if (IsOpen) Render(); };
    }

    /// <summary>Opens the sheet for module <paramref name="module"/> (index in the page's list, hidden tiles included).</summary>
    public void Open(AppConfig config, int page, int module)
    {
        if (page < 0 || page >= config.Pages.Count || module < 0 || module >= config.Pages[page].Modules.Count)
            return;
        HideUndo();
        _cfg = config;
        _p = page;
        _i = module;
        _type = config.Pages[page].Modules[module].Type;
        _scrim.IsVisible = _sheet.IsVisible = IsVisible = true;
        Render();
        _idle.Stop();
        _idle.Start();
    }

    public void Close()
    {
        _idle.Stop();
        _scrim.IsVisible = _sheet.IsVisible = false;
        _sheet.Child = null;
        IsVisible = _undo.IsVisible;
    }

    /// <summary>The dashboard reloaded config.json: show what it says now (or close if the tile is gone).</summary>
    public void Refresh(AppConfig config)
    {
        if (!IsOpen)
            return;
        if (_p >= config.Pages.Count || _i >= config.Pages[_p].Modules.Count || config.Pages[_p].Modules[_i].Type != _type)
        {
            Close();
            return;
        }
        _cfg = config;
        Render();
    }

    /// <summary>Applies a change to this tile in config.json (checking it is still the same tile there).</summary>
    bool Change(Action<AppConfig, List<ModuleConfig>> change)
    {
        int p = _p, i = _i;
        string type = _type;
        AppConfig? edited = null;
        bool saved = AppConfig.Edit(c =>
        {
            if (p >= c.Pages.Count || i >= c.Pages[p].Modules.Count || c.Pages[p].Modules[i].Type != type)
                return; // changed in Settings meanwhile
            change(c, c.Pages[p].Modules);
            edited = c;
        });
        if (!saved || edited == null)
        {
            _dashboard.Toast(L.T(saved ? "That tile changed in Settings meanwhile" : "Couldn't save: config.json has an error"));
            Close();
            return false;
        }
        _cfg = edited;
        return true;
    }

    void Render()
    {
        if (_p >= _cfg.Pages.Count || _i >= _cfg.Pages[_p].Modules.Count)
        {
            Close();
            return;
        }
        var page = _cfg.Pages[_p];
        var m = page.Modules[_i];
        var info = ModuleCatalog.Find(m.Type);
        string name = string.IsNullOrWhiteSpace(m.Title) ? L.T(info?.Name ?? m.Type) : m.Title!;
        double radius = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer or TileShape.Drafting ? 2 : Math.Min(18, Math.Max(8, UI.Theme.Current.Radius));

        _sheet.Width = Math.Max(300, Math.Min(720, Bounds.Width - 32));
        _sheet.MaxHeight = Math.Max(200, Bounds.Height - 24);
        _sheet.CornerRadius = new CornerRadius(radius);
        _sheet.Background = UI.Theme.WindowBackground;
        _sheet.BorderBrush = UI.Theme.Accent;
        _sheet.BorderThickness = new Thickness(1.5);
        _sheet.BoxShadow = new BoxShadows(new BoxShadow { Color = Color.FromArgb(0x80, 0, 0, 0), Blur = 24, OffsetY = 6 });

        var body = new StackPanel { Spacing = 10 };

        // Name and page, Done.
        var head = new DockPanel { LastChildFill = true };
        var done = TouchButton("check", L.T("Done"), Close, accent: true);
        DockPanel.SetDock(done, Dock.Right);
        head.Children.Add(done);
        var title = W.Text(name, 20, UI.Theme.Text, FontWeight.SemiBold);
        var where = W.Text(L.F("on \"{0}\"", page.Name), UI.Theme.Small, UI.Theme.Dim);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, where } });
        body.Children.Add(head);

        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8 };
        int row = 0;
        void AddRow(string label, Control content)
        {
            rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = W.Text(L.T(label), UI.Theme.Small, UI.Theme.Dim);
            l.Margin = new Thickness(0, 14, 14, 0);
            l.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(l, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            rows.Children.Add(l);
            rows.Children.Add(content);
            row++;
        }

        // Order and width on one line.
        int prev = -1;
        for (int k = _i - 1; k >= 0 && prev < 0; k--)
            if (page.Modules[k].Enabled)
                prev = k;
        int next = _i + 1 < page.Modules.Count ? page.Modules.FindIndex(_i + 1, x => x.Enabled) : -1;
        int cols = _dashboard.TileColumns(_p, _i);
        int span = Math.Clamp(m.Span, 1, cols);
        var layout = new WrapPanel();
        layout.Children.Add(TouchButton("chevron_left", L.T("Move"), () => MoveTo(prev), enabled: prev >= 0));
        layout.Children.Add(TouchButton(null, L.T("Move"), () => MoveTo(next), enabled: next >= 0, trailingIcon: "chevron_right"));
        layout.Children.Add(new Border { Width = 18 });
        layout.Children.Add(TouchButton("close_fullscreen", L.T("Narrower"), () => SetSpan(span - 1), enabled: span > 1));
        var width = W.Text(L.F("{0} of {1}", span, cols), 15, UI.Theme.Text, FontWeight.SemiBold);
        width.Margin = new Thickness(6, 0, 14, 6);
        width.MinWidth = 52;
        width.TextAlignment = TextAlignment.Center;
        ToolTip.SetTip(width, L.T("Columns this tile spans"));
        layout.Children.Add(width);
        layout.Children.Add(TouchButton("open_in_full", L.T("Wider"), () => SetSpan(span + 1), enabled: span < cols));
        AddRow("Layout", layout);

        // Style: the theme's choice, or one of the tile's own.
        if (info is { Styles.Length: > 1 })
        {
            var styles = new WrapPanel();
            string themeDefault = UI.Theme.StyleFor(m.Type, null);
            string? chosen = m.Opt("Style") is { } s && info.Styles.Any(st => st.Id.Equals(s, StringComparison.OrdinalIgnoreCase)) ? s : null;
            styles.Children.Add(Chip(L.F("Theme ({0})", L.T(info.Styles.First(st => st.Id == themeDefault).Name)), chosen == null, () => SetStyle(null)));
            foreach (var (id, styleName) in info.Styles)
                styles.Children.Add(Chip(L.T(styleName), chosen != null && id.Equals(chosen, StringComparison.OrdinalIgnoreCase), () => SetStyle(id)));
            AddRow("Style", styles);
        }

        // Move to another page.
        if (_cfg.Pages.Count > 1)
        {
            var pages = new WrapPanel();
            for (int q = 0; q < _cfg.Pages.Count; q++)
            {
                int target = q;
                pages.Children.Add(Chip($"{q + 1}  {_cfg.Pages[q].Name}", q == _p, () => MoveToPage(target)));
            }
            AddRow("Page", pages);
        }
        body.Children.Add(rows);

        var foot = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 0) };
        var hide = TouchButton("visibility_off", L.T("Hide tile"), HideTile);
        var more = TouchButton("tune", L.T("More settings…"), MoreSettings);
        DockPanel.SetDock(more, Dock.Right);
        foot.Children.Add(hide);
        foot.Children.Add(more);
        body.Children.Add(foot);

        _sheet.Child = new ScrollViewer
        {
            Content = new Border { Background = UI.Theme.Tile, Padding = new Thickness(18, 14, 18, 14), CornerRadius = new CornerRadius(radius), Child = body },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    // ---------------------------------------------------------------- actions

    void MoveTo(int other)
    {
        if (other < 0)
            return;
        int from = _i;
        if (Change((_, mods) => (mods[from], mods[other]) = (mods[other], mods[from])))
        {
            _i = other;
            Render();
        }
    }

    void SetSpan(int span)
    {
        int i = _i;
        span = Math.Clamp(span, 1, _dashboard.TileColumns(_p, i));
        if (Change((_, mods) => mods[i].Span = span))
            Render();
    }

    void SetStyle(string? id)
    {
        int i = _i;
        if (Change((_, mods) => mods[i].SetOpt("Style", id)))
            Render();
    }

    void MoveToPage(int target)
    {
        if (target == _p)
            return;
        int from = _i;
        string pageName = _cfg.Pages[target].Name;
        if (Change((c, mods) =>
            {
                var m = mods[from];
                mods.RemoveAt(from);
                c.Pages[target].Modules.Add(m);
            }))
        {
            Close();
            _dashboard.Toast(L.F("Moved to \"{0}\"", pageName));
        }
    }

    void HideTile()
    {
        int p = _p, i = _i;
        string type = _type;
        if (!Change((_, mods) => mods[i].Enabled = false))
            return;
        Close();
        ShowUndo(L.T("Tile hidden"), () =>
        {
            AppConfig.Edit(c =>
            {
                if (p < c.Pages.Count && i < c.Pages[p].Modules.Count && c.Pages[p].Modules[i].Type == type)
                    c.Pages[p].Modules[i].Enabled = true;
            });
        });
    }

    void MoreSettings()
    {
        int page = _p;
        Close();
        SettingsWindow.OpenPage(_dashboard, page);
    }

    // ---------------------------------------------------------------- undo bar

    void ShowUndo(string text, Action undo)
    {
        _undoAction = undo;
        var label = W.Text(text + "  ·", 15, UI.Theme.Text);
        label.Margin = new Thickness(18, 0, 4, 0);
        var button = TouchButton("undo", L.T("Undo"), () =>
        {
            var action = _undoAction;
            HideUndo();
            action?.Invoke();
        }, accent: true);
        button.Margin = new Thickness(0);
        _undo.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, button } };
        _undo.Background = UI.Theme.WindowBackground;
        _undo.BorderBrush = UI.Theme.Accent;
        _undo.BorderThickness = new Thickness(1);
        _undo.CornerRadius = new CornerRadius(10);
        _undo.Padding = new Thickness(0, 4, 6, 4);
        _undo.IsVisible = IsVisible = true;
        _undo.Opacity = 1;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    void HideUndo()
    {
        _undoTimer.Stop();
        _undoAction = null;
        _undo.Opacity = 0;
        _undo.IsVisible = false;
        IsVisible = IsOpen;
    }

    // ---------------------------------------------------------------- pieces

    /// <summary>A finger-sized (48 px) button with an optional Material icon.</summary>
    static Border TouchButton(string? icon, string text, Action action, bool enabled = true, bool accent = false, string? trailingIcon = null)
    {
        var fg = accent ? UI.Theme.Accent : UI.Theme.Text;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null)
            content.Children.Add(UI.Icon.Glyph(icon, 20, fg));
        content.Children.Add(W.Text(text, 15, fg, FontWeight.SemiBold));
        if (trailingIcon != null)
            content.Children.Add(UI.Icon.Glyph(trailingIcon, 20, fg));
        foreach (var c in content.Children.OfType<TextBlock>())
            c.VerticalAlignment = VerticalAlignment.Center;
        var b = new Border
        {
            MinHeight = 48, MinWidth = 48, Padding = new Thickness(14, 0), Margin = new Thickness(0, 0, 8, 6),
            CornerRadius = new CornerRadius(10), Background = UI.Theme.Track, Child = content,
            BorderBrush = accent ? UI.Theme.Accent : null, BorderThickness = new Thickness(accent ? 1.5 : 0),
        };
        if (enabled)
            Tap.On(b, action);
        else
            b.Opacity = 0.35;
        return b;
    }

    /// <summary>A choice; the current one is outlined in the accent colour.</summary>
    static Border Chip(string text, bool selected, Action action)
    {
        var t = W.Text(text, 15, UI.Theme.Text, selected ? FontWeight.SemiBold : FontWeight.Normal);
        var b = new Border
        {
            MinHeight = 48, MinWidth = 48, Padding = new Thickness(14, 0), Margin = new Thickness(0, 0, 8, 6),
            CornerRadius = new CornerRadius(24), Child = t,
            Background = selected ? UI.Theme.Solid(UI.Theme.AccentColor, 0x40) : UI.Theme.Track,
            BorderBrush = selected ? UI.Theme.Accent : null, BorderThickness = new Thickness(selected ? 2 : 0),
        };
        Tap.On(b, selected ? () => { } : action);
        return b;
    }
}
