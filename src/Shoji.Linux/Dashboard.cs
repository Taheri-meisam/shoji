using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Shoji.Core;
using Shoji.Modules;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji;

/// <summary>
/// The borderless full-screen dashboard on the chosen monitor (Linux twin of the Windows MainWindow): pages you
/// swipe between, scenes, the day / night theme, alerts and the phone dashboard.
/// </summary>
public sealed class Dashboard : Window, IDashboardHost
{
    /// <summary>True while a finger or mouse drag is a page swipe (taps underneath are then ignored).</summary>
    public static bool SwipeInProgress { get; private set; }

    /// <summary>One screenful. A configured page becomes several when its tiles don't fit readably on this screen.</summary>
    sealed class Page
    {
        public required PageConfig Config { get; init; }
        public required int ConfigIndex { get; init; }
        public required int Part { get; init; }
        public required List<Module> Modules { get; init; }
        public required int Columns { get; init; }
        public required double ScaleValue { get; init; }
        public Grid Grid { get; } = new();
        public LayoutTransformControl Host { get; } = new();
        public string Name => Part == 0 ? Config.Name : $"{Config.Name} ({Part + 1})";
    }

    List<(PageConfig Config, List<Module> Modules)> _built = new();

    readonly Grid _pagesHost = new();
    readonly Backdrop _backdrop = new();
    readonly ArtLayer _art = new();
    readonly Wallpaper _wallpaper = new(); // the user's own background picture (UI/Wallpaper.cs)
    readonly AlbumArtBackdrop _cover = new(); // the playing track's cover behind a media page (UI/AlbumArtBackdrop.cs)
    readonly CrtOverlay _crt = new();
    readonly AnimationLayer _animBack = new(front: false), _animFront = new(front: true);
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    readonly Border _alert = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _alertText = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };
    readonly Border _toast = new() { Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22) };
    readonly TextBlock _toastText = new() { FontSize = 15 };
    readonly TranslateTransform _shift = new(), _slide = new();
    readonly DispatcherTimer _retryTimer, _shiftTimer, _cycleTimer, _toastTimer, _pulseTimer;
    readonly Random _random = new();
    readonly SceneEngine _scenes = new();
    readonly AlertEngine _alerts = new();
    readonly TileEditor _editor;
    readonly bool _headless;

    AppConfig _config = new();
    LinuxCollector? _collector;
    string? _collectorKey;
    List<Page> _pages = new();
    int _current;
    (int ConfigIndex, int Part)? _homePage;
    bool _movedDuringScene;
    string _appliedTheme = "";
    IReadOnlyList<AlertEngine.Alert> _activeAlerts = Array.Empty<AlertEngine.Alert>();
    WebServer? _web;
    string? _webSignature;
    MqttPublisher? _mqtt;
    string? _mqttSignature;
    Snapshot? _last;
    Point? _swipeStart;

    // Night mode: a black sheet above everything, see UpdateNight.
    readonly Border _night = new() { Background = Brushes.Black, Opacity = 0, IsVisible = false, IsHitTestVisible = false, ZIndex = 10000 };
    readonly DispatcherTimer _nightTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly DispatcherTimer _wakeTimer = new();
    DateTime _wakeUntil;
    bool _nightDimmed, _nightBlack, _swallowRelease;

    /// <param name="headless">For screenshots: no monitor placement, the size is set by the caller.</param>
    public Dashboard(bool headless = false)
    {
        _headless = headless;
        Title = "Shoji";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Icon = App.WindowIcon();

        var root = new Grid { Margin = new Thickness(6), RenderTransform = _shift };
        _slide.Transitions = new Transitions { new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new Avalonia.Animation.Easings.CubicEaseOut() } };
        _pagesHost.RenderTransform = _slide;
        _pagesHost.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) } };
        root.Children.Add(_pagesHost);

        _alert.Child = _alertText;
        _alert.Padding = new Thickness(16, 8);
        _alert.CornerRadius = new CornerRadius(8);
        _alert.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(700) } };
        Tap.On(_alert, SnoozeAlerts);
        _toast.Child = _toastText;
        _toast.Padding = new Thickness(16, 7);
        _toast.CornerRadius = new CornerRadius(8);
        _toast.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) } };

        var layers = new Grid();
        layers.Children.Add(_backdrop);
        layers.Children.Add(_art);
        layers.Children.Add(_wallpaper);
        layers.Children.Add(_cover);
        layers.Children.Add(_animBack);
        layers.Children.Add(root);
        layers.Children.Add(_animFront);
        layers.Children.Add(_crt);
        layers.Children.Add(_dots);
        layers.Children.Add(_edgeLeft);
        layers.Children.Add(_edgeRight);
        layers.Children.Add(_corner);
        _editor = new TileEditor(this); // long-press edit sheet (UI/TileEditor.cs), below the alert, toast and night layers
        layers.Children.Add(_editor);
        layers.Children.Add(_meeting);
        layers.Children.Add(_alert);
        layers.Children.Add(_toast);
        // Night mode sheet: topmost (ZIndex), whatever else is added to the layers later.
        _night.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(1500) } };
        layers.Children.Add(_night);
        _nightTimer.Tick += (_, _) => UpdateNight();
        _wakeTimer.Tick += (_, _) =>
        {
            _wakeTimer.Stop();
            UpdateNight();
        };
        Content = layers;

        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _retryTimer.Tick += (_, _) => Place();
        _shiftTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _shiftTimer.Tick += (_, _) =>
        {
            _shift.X = _random.Next(-3, 4);
            _shift.Y = _random.Next(-3, 4);
        };
        _cycleTimer = new DispatcherTimer();
        _cycleTimer.Tick += (_, _) =>
        {
            if (_scenes.Active == null && _pages.Count > 1)
                ShowPageIndex((_current + 1) % _pages.Count, fromScene: true);
        };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.3) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.Opacity = 0;
        };
        // The alert banner breathes so it catches the eye.
        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
        _pulseTimer.Tick += (_, _) => _alert.Opacity = _alert.Opacity > 0.8 ? 0.55 : 1;

        SizeChanged += (_, _) =>
        {
            Arrange();
            _animBack.Build(Bounds.Width, Bounds.Height);
            _animFront.Build(Bounds.Width, Bounds.Height);
        };

        // A monitor plugged in or out, or its resolution changed: cover the configured one again (or hide). Without
        // this, unplugging the dashboard's screen leaves it full screen on whichever monitor the desktop moved it to.
        if (!headless)
            Screens.Changed += (_, _) => Dispatcher.UIThread.Post(Place);

        // First, so a tap on the dimmed screen only wakes it (no swipe, no tile underneath).
        AddHandler(PointerPressedEvent, OnNightPointer, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnNightRelease, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnSwipeStart, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnSwipeMove, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnSwipeEnd, RoutingStrategies.Tunnel);
        // Mouse and keyboard ways to change page (the wheel only when no list under it used it).
        AddHandler(PointerWheelChangedEvent, OnPageWheel, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPageButtons, RoutingStrategies.Tunnel);
        KeyDown += OnPageKey;
        PointerExited += (_, _) => ShowEdges(null);
        BuildEdge(_edgeLeft, "chevron_left", -1);
        BuildEdge(_edgeRight, "chevron_right", +1);
        BuildCorner();
        BuildMeeting();
    }

    public LinuxCollector? Collector => _collector;
    Page? CurrentPage => _current >= 0 && _current < _pages.Count ? _pages[_current] : null;
    public int CurrentPageIndex => CurrentPage?.ConfigIndex ?? 0;
    int RuntimeIndex(int configIndex) => _pages.FindIndex(p => p.ConfigIndex == configIndex);

    /// <summary>(Re)builds everything from config. Safe to call again after config.json changes.</summary>
    public void Apply(AppConfig config)
    {
        _config = config;
        L.Apply(config.Language); // the tiles below are rebuilt in it
        Topmost = config.Topmost;
        var key = LinuxCollector.KeyFor(config);
        bool newCollector = _collector == null || key != _collectorKey;
        if (newCollector)
        {
            DisposeModules();
            _collector?.Dispose();
            _collector = new LinuxCollector(config, snapshot => Dispatcher.UIThread.Post(() => Render(snapshot)));
            _collectorKey = key;
        }

        if (config.PixelShift) _shiftTimer.Start();
        else
        {
            _shiftTimer.Stop();
            _shift.X = _shift.Y = 0;
        }
        _cycleTimer.Stop();
        if (config.PageCycleSeconds > 0)
        {
            _cycleTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, config.PageCycleSeconds));
            _cycleTimer.Start();
        }

        UpdateWebServer();
        UpdateMqtt();
        UpdateWebDeck();
        Build(EffectiveTheme());
        _editor.Refresh(config); // an open tile edit sheet shows the reloaded tile
        Place();
        _nightTimer.Stop();
        if (config.Night.Enabled)
            _nightTimer.Start();
        UpdateNight();
        if (newCollector)
            _collector!.Start();
    }

    void Build(string themeId)
    {
        UI.Theme.Apply(_config, themeId);
        _appliedTheme = themeId;
        Background = UI.Theme.WindowBackground;
        _backdrop.InvalidateVisual();
        _art.Restart();
        _wallpaper.Update(_config);
        _crt.InvalidateVisual();

        int showing = CurrentPageIndex;
        DisposeModules();
        ServiceStore.BeginBuild();
        _built = _config.Pages.Select(p => (p, p.Modules.Where(m => m.Enabled)
                .Select(m => ModuleFactory.SafeCreate(m, _config, _collector!, this))
                .Where(m => m != null).Select(m => m!).ToList())).ToList();
        // Asked for inside the build, so the calendar poller counts as in use even when no tile shows a calendar.
        _meetings = _config.MeetingReminder ? CalendarFeeds.Service(CalendarFeeds.For(null, _config)) : null;
        ServiceStore.EndBuild();
        _pages = new List<Page>();
        Arrange(keepConfigPage: showing);
        _animBack.Build(Bounds.Width, Bounds.Height);
        _animFront.Build(Bounds.Width, Bounds.Height);
        StyleOverlays();
        if (_last != null)
            UpdateModules(_last);
    }

    void DisposeModules()
    {
        foreach (var m in _built.SelectMany(p => p.Modules))
        {
            try { m.Dispose(); } catch { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------- refresh

    const double IdlePauseSeconds = 600;

    void Render(Snapshot s)
    {
        _last = s;
        _web?.Publish(s, _appliedTheme);
        _mqtt?.Publish(s);
        // Nobody at the PC for a while (the screen is probably blanked) or the dashboard minimized: stop the motion,
        // which is most of what the dashboard costs. The first input brings it back within a second.
        // Also while night mode has it black, or the desktop's lock screen / screensaver is up.
        LinuxScreenLock.Update(s.IdleSeconds);
        UpdatePause();
        if (!IsVisible)
            return;
        if (!_nightBlack) // nobody can see the tiles; they catch up when it wakes
            UpdateModules(s);
        UpdateMeeting(s.Time);
        UpdateCover();
        if (_scenes.Update(_config, s))
            OnSceneChanged();
        var theme = EffectiveTheme();
        if (theme != _appliedTheme)
            Build(theme);
        var alerts = _alerts.Evaluate(_config.Alerts, s, out bool isNew);
        ShowAlerts(alerts);
        if (isNew && _config.Alerts.Sound)
            Linux.Sound.Warning();
    }

    void UpdateModules(Snapshot s)
    {
        foreach (var m in _built.SelectMany(p => p.Modules))
        {
            try
            {
                m.Update(s);
            }
            catch (Exception ex)
            {
                Log.Write($"Module {m.Config.Type} failed to update: {ex.Message}");
            }
        }
    }

    string EffectiveTheme()
    {
        var scene = _config.Scenes.FirstOrDefault(sc => sc.Name == _scenes.Active);
        if (!string.IsNullOrWhiteSpace(scene?.Theme))
            return scene.Theme!;
        var dn = _config.DayNight;
        if (dn.Enabled)
            return SceneEngine.InWindow(DateTime.Now.TimeOfDay, dn.DayStarts, dn.NightStarts) ? dn.DayTheme : dn.NightTheme;
        return _config.Theme;
    }

    void OnSceneChanged()
    {
        var scene = _config.Scenes.FirstOrDefault(sc => sc.Name == _scenes.Active);
        Log.Write($"Scene: {scene?.Name ?? "(none)"}");
        if (scene != null)
        {
            if (_homePage == null && CurrentPage is { } here)
            {
                _homePage = (here.ConfigIndex, here.Part);
                _movedDuringScene = false;
            }
            int index = RuntimeIndex(_config.PageIndex(scene.Page));
            if (index >= 0)
                ShowPageIndex(index, fromScene: true);
            Toast(scene.Name);
        }
        else
        {
            if (_homePage is { } homePage && !_movedDuringScene)
            {
                int home = _pages.FindIndex(p => p.ConfigIndex == homePage.ConfigIndex && p.Part == homePage.Part);
                if (home < 0) home = RuntimeIndex(homePage.ConfigIndex);
                if (home >= 0) ShowPageIndex(home, fromScene: true);
            }
            _homePage = null;
        }
    }

    // ---------------------------------------------------------------- pages

    public void ShowPageIndex(int index, bool animate = true, bool fromScene = false)
    {
        if (_pages.Count == 0)
            return;
        index = (index % _pages.Count + _pages.Count) % _pages.Count;
        int direction = _current < 0 ? 0 : index > _current ? 1 : index < _current ? -1 : 0;
        if (!fromScene && _scenes.Active != null)
            _movedDuringScene = true;
        _current = index;
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Host.IsVisible = i == index;
        BuildDots();
        UpdateCover();

        if (animate && direction != 0 && UI.Theme.Animate)
        {
            // Jump to the side without a transition, then glide in.
            var transitions = _slide.Transitions;
            _slide.Transitions = null;
            _slide.X = direction * 60;
            _pagesHost.Opacity = 0.2;
            _slide.Transitions = transitions;
            Dispatcher.UIThread.Post(() =>
            {
                _slide.X = 0;
                _pagesHost.Opacity = 1;
            }, DispatcherPriority.Background);
        }
        else
        {
            _slide.X = 0;
            _pagesHost.Opacity = 1;
        }
    }

    public void ShowPage(string target)
    {
        target = target.Trim();
        if (target.Equals("next", StringComparison.OrdinalIgnoreCase))
            ShowPageIndex(_current + 1);
        else if (target.Equals("prev", StringComparison.OrdinalIgnoreCase) || target.Equals("previous", StringComparison.OrdinalIgnoreCase))
            ShowPageIndex(_current - 1);
        else if (RuntimeIndex(_config.PageIndex(target)) is int i and >= 0)
            ShowPageIndex(i);
        else
            Toast(L.F("No page \"{0}\"", target));
    }

    /// <summary>Shows a configured page (from the tray menu).</summary>
    public void ShowConfigPage(int configIndex)
    {
        int i = RuntimeIndex(configIndex);
        if (i >= 0)
            ShowPageIndex(i);
    }

    void BuildDots()
    {
        _dots.Children.Clear();
        if (_pages.Count < 2)
            return;
        for (int i = 0; i < _pages.Count; i++)
        {
            int index = i;
            var dot = new Ellipse
            {
                Width = 7, Height = 7,
                Fill = i == _current ? UI.Theme.Accent : UI.Theme.Solid(UI.Theme.TextColor, 0x50),
            };
            var target = new Border { Width = 22, Height = 13, Background = Brushes.Transparent, Child = dot };
            ToolTip.SetTip(target, _pages[i].Name);
            Tap.On(target, () => ShowPageIndex(index));
            _dots.Children.Add(target);
        }
    }

    /// <summary>Splits every configured page into screenfuls that fit readably (same rules as Windows) and lays them out.</summary>
    void Arrange(int? keepConfigPage = null)
    {
        var showing = CurrentPage;
        int keepConfig = keepConfigPage ?? showing?.ConfigIndex ?? 0, keepPart = keepConfigPage == null ? showing?.Part ?? 0 : 0;
        foreach (var page in _pages)
            page.Grid.Children.Clear();
        _pagesHost.Children.Clear();

        double w = Bounds.Width - 12, h = Bounds.Height - 12;
        bool sized = w > 50 && h > 50;
        double scaling = RenderScaling;
        var pages = new List<Page>();
        for (int c = 0; c < _built.Count; c++)
        {
            var (config, modules) = _built[c];
            var spans = modules.Select(m => m.Config.Span).ToList();
            int fixedCols = config.Columns > 0 ? config.Columns : _config.Columns;
            var plans = sized
                ? TileLayout.Plan(spans, w, h, fixedCols, scaling, _config.Scale)
                : new List<TileLayout.ScreenPlan> { new(0, modules.Count, Math.Max(1, fixedCols), 1) };
            for (int part = 0; part < plans.Count; part++)
            {
                var plan = plans[part];
                var page = new Page
                {
                    Config = config, ConfigIndex = c, Part = part, Columns = plan.Columns, ScaleValue = plan.Scale,
                    Modules = modules.Skip(plan.Start).Take(plan.Count).ToList(),
                };
                page.Host.Child = page.Grid;
                page.Host.LayoutTransform = new ScaleTransform(plan.Scale, plan.Scale);
                page.Host.IsVisible = false;
                foreach (var m in page.Modules)
                {
                    (m.View.Parent as Panel)?.Children.Remove(m.View);
                    page.Grid.Children.Add(m.View);
                }
                LayoutPage(page);
                _pagesHost.Children.Add(page.Host);
                pages.Add(page);
            }
        }
        _pages = pages;
        int index = _pages.FindIndex(p => p.ConfigIndex == keepConfig && p.Part == keepPart);
        if (index < 0) index = Math.Max(0, RuntimeIndex(keepConfig));
        _current = -1;
        ShowPageIndex(index, animate: false, fromScene: true);
    }

    /// <summary>A stacked row's share of the height: what its hungriest tile can use.</summary>
    static double RowWeight(Page page, List<(int Row, int Col, int Span)> placement, int row)
    {
        double w = 0;
        for (int i = 0; i < page.Modules.Count; i++)
            if (placement[i].Row == row)
            {
                var c = page.Modules[i].Config;
                string type = c.Type.ToLowerInvariant();
                w = Math.Max(w, TileLayout.HeightWeight(type, UI.Theme.StyleFor(type, c.Opt("Style"))));
            }
        return w > 0 ? w : 1;
    }

    /// <summary>The share for a row that one photo frame or mirror fills from edge to edge (0 for any other row).</summary>
    static double ShowpieceRow(Page page, List<(int Row, int Col, int Span)> placement, int row)
    {
        int only = -1;
        for (int i = 0; i < page.Modules.Count; i++)
            if (placement[i].Row == row)
            {
                if (only >= 0)
                    return 0;
                only = i;
            }
        if (only < 0 || placement[only].Span < page.Columns)
            return 0;
        // On a single column every tile is edge to edge: there only a tile set to "Full" width was meant as the showpiece.
        var config = page.Modules[only].Config;
        return page.Columns > 1 || config.Span >= 9 ? TileLayout.ShowpieceWeight(config.Type.ToLowerInvariant(), config.OptBool("Large")) : 0;
    }

    static void LayoutPage(Page page)
    {
        var grid = page.Grid;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (page.Modules.Count == 0)
            return;
        var placement = TileLayout.Pack(page.Modules.Select(m => m.Config.Span).ToList(), page.Columns);
        for (int c = 0; c < page.Columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        int rows = placement.Max(p => p.Row) + 1;
        bool tall = TileLayout.Tall(rows, page.Columns);
        for (int r = 0; r < rows; r++)
            {
            double showpiece = rows > 1 ? ShowpieceRow(page, placement, r) : 0;
            grid.RowDefinitions.Add(new RowDefinition(showpiece > 0 ? showpiece : tall ? RowWeight(page, placement, r) : 1, GridUnitType.Star));
        }
        for (int i = 0; i < page.Modules.Count; i++)
        {
            var view = page.Modules[i].View;
            Grid.SetRow(view, placement[i].Row);
            Grid.SetColumn(view, placement[i].Col);
            Grid.SetColumnSpan(view, placement[i].Span);
        }
    }

    /// <summary>The album-art backdrop follows the track, the page and the setting.</summary>
    void UpdateCover() => _cover.Update(_config, CurrentPage?.Config, _last?.Media);

    // ---------------------------------------------------------------- page changing: wheel, keys, mouse buttons, edge arrows

    readonly Border _edgeLeft = new() { HorizontalAlignment = HorizontalAlignment.Left }, _edgeRight = new() { HorizontalAlignment = HorizontalAlignment.Right };
    long _lastFlip, _swipeStartedAt;

    /// <summary>One page forward (+1) or back (−1), with the page's name shown briefly.</summary>
    void Flip(int delta)
    {
        if (_pages.Count < 2 || _editor.IsOpen)
            return;
        ShowPageIndex(_current + delta);
        Toast(CurrentPage?.Name ?? "");
    }

    void OnPageWheel(object? sender, PointerWheelEventArgs e)
    {
        // Sideways (a touchpad swipe, a tilting wheel) wins; up / down counts when it's all there is. One page per
        // gesture: a touchpad sends a burst of small steps.
        double d = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y;
        long now = Environment.TickCount64;
        if (Math.Abs(d) < 0.2 || now - _lastFlip < 350 || _pages.Count < 2 || _editor.IsOpen)
            return;
        _lastFlip = now;
        e.Handled = true;
        Flip(d > 0 ? +1 : -1);
    }

    /// <summary>The mouse's back / forward thumb buttons.</summary>
    void OnPageButtons(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Properties;
        if (p.PointerUpdateKind == PointerUpdateKind.XButton1Pressed)
            Flip(-1);
        else if (p.PointerUpdateKind == PointerUpdateKind.XButton2Pressed)
            Flip(+1);
        else
            return;
        e.Handled = true;
    }

    void OnPageKey(object? sender, KeyEventArgs e)
    {
        if (_pages.Count < 2 || _editor.IsOpen || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox)
            return;
        switch (e.Key)
        {
            case Key.Right or Key.PageDown or Key.Space: Flip(+1); break;
            case Key.Left or Key.PageUp or Key.Back: Flip(-1); break;
            case Key.Home: ShowPageIndex(0); break;
            case Key.End: ShowPageIndex(_pages.Count - 1); break;
            case >= Key.D1 and <= Key.D9 when e.Key - Key.D1 < _pages.Count: ShowPageIndex(e.Key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad9 when e.Key - Key.NumPad1 < _pages.Count: ShowPageIndex(e.Key - Key.NumPad1); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>An arrow at a screen edge for mouse users: invisible until the pointer comes near that edge.</summary>
    void BuildEdge(Border edge, string glyph, int delta)
    {
        edge.Width = 44;
        edge.Height = 96;
        edge.VerticalAlignment = VerticalAlignment.Center;
        edge.CornerRadius = delta < 0 ? new CornerRadius(0, 14, 14, 0) : new CornerRadius(14, 0, 0, 14);
        edge.Opacity = 0;
        edge.IsHitTestVisible = false;
        edge.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) } };
        edge.Child = new TextBlock { Text = glyph, FontFamily = UI.Theme.Icons, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Tap.On(edge, () => Flip(delta));
    }

    /// <summary>Shows the arrow of the edge the mouse is near (null = none). A finger never brings them up: it swipes.</summary>
    void ShowEdges(Point? mouse)
    {
        _cornerNear = mouse is { } at && InCorner(at) && !SwipeInProgress;
        // Only real movement counts: the same position again (a redraw under a resting pointer) shows nothing.
        if (mouse is { } now && (Math.Abs(now.X - _cornerMouse.X) > 2 || Math.Abs(now.Y - _cornerMouse.Y) > 2))
        {
            _cornerMouse = now;
            PinCorner(3.5);
        }
        UpdateCorner();
        bool can = mouse != null && _pages.Count > 1 && !_editor.IsOpen && !SwipeInProgress;
        double reach = Math.Min(90, Bounds.Width * 0.12);
        Set(_edgeLeft, can && mouse!.Value.X < reach);
        Set(_edgeRight, can && mouse!.Value.X > Bounds.Width - reach);

        static void Set(Border edge, bool show)
        {
            if (edge.IsHitTestVisible == show)
                return;
            edge.IsHitTestVisible = show;
            if (show)
            {
                edge.Background = UI.Theme.Solid(UI.Theme.TileColor, 0xE6);
                edge.BorderBrush = UI.Theme.Solid(UI.Theme.TextColor, 0x30);
                edge.BorderThickness = new Thickness(1);
                ((TextBlock)edge.Child!).Foreground = UI.Theme.Text;
            }
            edge.Opacity = show ? 0.92 : 0;
        }
    }

    // ---------------------------------------------------------------- the window's own buttons: Settings and Close

    // The dashboard has no title bar, and a stock GNOME desktop shows no tray icon: without these there is nothing
    // to close it with. They show for a few seconds when the dashboard starts and whenever the mouse moves over it
    // (as a video player's controls do), on a right click, and on a long press in that corner (touch screens).
    readonly Border _corner = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) };
    readonly DispatcherTimer _cornerTimer = new(), _cornerHold = new() { Interval = TimeSpan.FromMilliseconds(650) };
    bool _cornerNear, _cornerPinned;
    Point _cornerMouse;

    void BuildCorner()
    {
        _corner.Padding = new Thickness(3);
        _corner.BorderThickness = new Thickness(1);
        _corner.Opacity = 0;
        _corner.IsHitTestVisible = false;
        _corner.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) } };
        _corner.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                CornerButton("settings", "Settings", () => UI.SettingsWindow.Open(this)),
                CornerButton("close", "Quit Shoji", () => (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown()),
            },
        };
        _cornerTimer.Tick += (_, _) =>
        {
            _cornerTimer.Stop();
            _cornerPinned = false;
            UpdateCorner();
        };
        _cornerHold.Tick += (_, _) =>
        {
            _cornerHold.Stop();
            if (!SwipeInProgress)
                PinCorner();
        };
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed)
                PinCorner();
            else if (point.Properties.IsLeftButtonPressed && InCorner(point.Position) && !_corner.IsHitTestVisible)
                _cornerHold.Start();
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _cornerHold.Stop(), RoutingStrategies.Tunnel, handledEventsToo: true);
        if (!_headless)
            Opened += (_, _) => PinCorner(8); // so they are seen once, right where they will be
    }

    Border CornerButton(string glyph, string name, Action action)
    {
        var b = new Border
        {
            Background = Brushes.Transparent,
            Child = new TextBlock { Text = glyph, FontFamily = UI.Theme.Icons, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTip.SetTip(b, L.T(name));
        // Quiet until pointed at: dim glyphs, the one under the mouse lights up.
        b.PointerEntered += (_, _) =>
        {
            b.Background = UI.Theme.Solid(UI.Theme.TextColor, 0x24);
            ((TextBlock)b.Child!).Foreground = UI.Theme.Text;
        };
        b.PointerExited += (_, _) =>
        {
            b.Background = Brushes.Transparent;
            ((TextBlock)b.Child!).Foreground = UI.Theme.Dim;
        };
        return Tap.On(b, action);
    }

    bool InCorner(Point p) => p.Y < 110 && p.X > Bounds.Width - Math.Max(220, Bounds.Width * 0.2);

    /// <summary>Shows the buttons for a few seconds wherever the pointer is (start, mouse movement, right click, long press).</summary>
    void PinCorner(double seconds = 6)
    {
        _cornerPinned = true;
        _cornerTimer.Stop();
        _cornerTimer.Interval = TimeSpan.FromSeconds(seconds);
        _cornerTimer.Start();
        UpdateCorner();
    }

    void UpdateCorner()
    {
        bool show = (_cornerNear || _cornerPinned) && !_editor.IsOpen && !_nightDimmed;
        if (_corner.IsHitTestVisible == show)
            return;
        _corner.IsHitTestVisible = show;
        if (show)
        {
            _corner.Background = UI.Theme.Solid(UI.Theme.TileColor, 0xF2);
            _corner.BorderBrush = UI.Theme.Solid(UI.Theme.TextColor, 0x30);
            // A small pill in the theme's own shape (square for the HUD themes), a little bigger on a big monitor.
            double size = Math.Round(Math.Clamp(Math.Min(Bounds.Width, Bounds.Height) / 30, 30, 40));
            bool square = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
            _corner.CornerRadius = new CornerRadius(square ? 0 : size / 2 + 3);
            foreach (var child in ((StackPanel)_corner.Child!).Children)
            {
                var button = (Border)child;
                button.Width = button.Height = size;
                button.CornerRadius = new CornerRadius(square ? 0 : size / 2);
                var glyph = (TextBlock)button.Child!;
                glyph.FontSize = Math.Round(size * 0.56);
                glyph.Foreground = UI.Theme.Dim;
            }
        }
        _corner.Opacity = show ? 1 : 0;
    }

    // ---------------------------------------------------------------- swipe

    void OnSwipeStart(object? sender, PointerPressedEventArgs e)
    {
        _swipeStart = null;
        if (_editor.IsOpen)
            return; // no page swipes under the tile edit sheet
        // Dragging a slider is not a page swipe.
        for (var v = e.Source as Avalonia.Visual; v != null; v = v.GetVisualParentCompat())
            if (v is TouchSlider)
                return;
        _swipeStart = e.GetPosition(this);
        _swipeStartedAt = Environment.TickCount64;
        SwipeInProgress = false;
    }

    void OnSwipeMove(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            ShowEdges(e.GetPosition(this));
        if (_swipeStart is not Point start || _pages.Count < 2 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var p = e.GetPosition(this);
        double dx = p.X - start.X, dy = p.Y - start.Y;
        if (!SwipeInProgress && Math.Abs(dx) > 24 && Math.Abs(dx) > Math.Abs(dy) * 1.3)
            SwipeInProgress = true;
        if (SwipeInProgress)
        {
            var transitions = _slide.Transitions;
            _slide.Transitions = null;
            _slide.X = dx * 0.6; // follow the finger (or the dragging mouse)
            _slide.Transitions = transitions;
        }
    }

    void OnSwipeEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (_swipeStart is Point start && SwipeInProgress)
        {
            e.Handled = true; // a swipe, not a tap on whatever is underneath
            double dx = e.GetPosition(this).X - start.X;
            // Far enough for this screen (a narrow one needs less), or a quick flick.
            double far = Math.Clamp(Bounds.Width * 0.12, 36, 70);
            double speed = Math.Abs(dx) / Math.Max(1, Environment.TickCount64 - _swipeStartedAt); // px per ms
            if (Math.Abs(dx) > far || speed > 0.6 && Math.Abs(dx) > 36)
            {
                ShowPageIndex(dx < 0 ? _current + 1 : _current - 1);
                Toast(CurrentPage?.Name ?? "");
            }
            else
            {
                _slide.X = 0;
            }
        }
        _swipeStart = null;
        // Let the tiles' own release handlers (which run after this tunnel pass) see the flag, then clear it.
        Dispatcher.UIThread.Post(() => SwipeInProgress = false, DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- meeting reminder

    // Two minutes before a calendar event starts (and until five minutes after) a card comes up at the top with a
    // countdown and, when the event carries a meeting link, a Join button. The calendars are the ones from
    // Settings → Weather & time; Settings → Weather & time → Calendars switches the reminder off.
    AgendaService? _meetings;
    readonly Border _meeting = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _meetingTitle = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, _meetingWhen = new(), _meetingIcon = new() { Text = "event", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _meetingJoinText = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _meetingCloseGlyph = new() { Text = "close", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _meetingJoin = new() { VerticalAlignment = VerticalAlignment.Center }, _meetingClose = new() { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    string? _meetingDismissed, _meetingLink, _meetingKey;

    void BuildMeeting()
    {
        _meetingJoin.Child = _meetingJoinText;
        _meetingClose.Child = _meetingCloseGlyph;
        Tap.On(_meetingJoin, () =>
        {
            if (_meetingLink is { } link)
                App.Open(link);
            _meetingDismissed = _meetingKey;
            _meeting.IsVisible = false;
        });
        Tap.On(_meetingClose, () =>
        {
            _meetingDismissed = _meetingKey;
            _meeting.IsVisible = false;
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _meetingTitle, _meetingWhen } };
        _meeting.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _meetingIcon, text, _meetingJoin, _meetingClose } };
    }

    void UpdateMeeting(DateTime now)
    {
        // SHOJI_MEETING_LEAD (minutes, test only) announces further ahead, to check the card without waiting for a meeting.
        double lead = double.TryParse(Environment.GetEnvironmentVariable("SHOJI_MEETING_LEAD"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minutes) ? minutes : 2;
        var e = _meetings == null || _nightDimmed || _editor.IsOpen ? null : CalendarFeeds.DueSoon(_meetings.State, now, TimeSpan.FromMinutes(lead));
        string? key = e == null ? null : $"{e.Start:O}|{e.Title}";
        if (key == null || key == _meetingDismissed)
        {
            _meeting.IsVisible = false;
            return;
        }
        if (!_meeting.IsVisible || key != _meetingKey)
        {
            // Sized for the screen and in the theme's colours, like the tiles under it.
            double size = Math.Round(Math.Clamp(Math.Min(Bounds.Width, Bounds.Height) / 34, 14, 28));
            bool square = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
            _meeting.Margin = new Thickness(16, _alert.IsVisible ? 64 : 14, 16, 0);
            _meeting.Padding = new Thickness(size, size * 0.6, size * 0.6, size * 0.6);
            _meeting.CornerRadius = new CornerRadius(square ? 0 : size * 0.8);
            _meeting.Background = UI.Theme.Solid(UI.Theme.TileColor, 0xF7);
            _meeting.BorderBrush = UI.Theme.Accent;
            _meeting.BorderThickness = new Thickness(1.5);
            _meeting.BoxShadow = BoxShadows.Parse("0 6 24 0 #60000000");
            _meetingIcon.FontFamily = _meetingCloseGlyph.FontFamily = UI.Theme.Icons;
            _meetingIcon.FontSize = size * 1.7;
            _meetingIcon.Foreground = UI.Theme.Accent;
            _meetingIcon.Margin = new Thickness(0, 0, size * 0.7, 0);
            _meetingTitle.FontFamily = _meetingWhen.FontFamily = _meetingJoinText.FontFamily = UI.Theme.Font;
            _meetingTitle.FontSize = size * 1.15;
            _meetingTitle.Foreground = UI.Theme.Text;
            _meetingTitle.MaxWidth = Math.Max(160, Bounds.Width * 0.5);
            _meetingWhen.FontSize = size * 0.9;
            _meetingWhen.Foreground = UI.Theme.Dim;
            _meetingJoin.Background = UI.Theme.Accent;
            _meetingJoin.CornerRadius = new CornerRadius(square ? 0 : size * 0.5);
            _meetingJoin.Padding = new Thickness(size * 1.1, size * 0.45);
            _meetingJoin.Margin = new Thickness(size * 1.2, 0, 0, 0);
            _meetingJoinText.FontSize = size;
            _meetingJoinText.Foreground = UI.Theme.Background;
            _meetingJoinText.Text = L.T("Join");
            _meetingClose.Width = _meetingClose.Height = size * 2;
            _meetingClose.Margin = new Thickness(size * 0.4, 0, 0, 0);
            _meetingCloseGlyph.FontSize = size * 1.1;
            _meetingCloseGlyph.Foreground = UI.Theme.Dim;
            ToolTip.SetTip(_meetingClose, L.T("Dismiss"));
        }
        _meetingKey = key;
        _meetingLink = e!.Link;
        _meetingJoin.IsVisible = e.Link != null;
        _meetingTitle.Text = e.Title;
        double seconds = (e.Start - now).TotalSeconds;
        _meetingWhen.Text = seconds > 0
            ? L.F("starts in {0}", $"{(int)seconds / 60}:{(int)seconds % 60:00}")
            : seconds > -60 ? L.T("starting now") : L.F("started {0} min ago", (int)(-seconds / 60));
        _meeting.IsVisible = true;
    }

    // ---------------------------------------------------------------- overlays

    void StyleOverlays()
    {
        _alert.Background = UI.Theme.Crit;
        _alertText.Foreground = Brushes.White;
        _alertText.FontFamily = UI.Theme.Font;
        _toast.Background = UI.Theme.Tile;
        _toast.BorderBrush = UI.Theme.Accent;
        _toast.BorderThickness = new Thickness(1);
        _toastText.Foreground = UI.Theme.Text;
        _toastText.FontFamily = UI.Theme.Font;
    }

    void ShowAlerts(IReadOnlyList<AlertEngine.Alert> alerts)
    {
        _activeAlerts = alerts;
        if (alerts.Count == 0)
        {
            _alert.IsVisible = false;
            _pulseTimer.Stop();
            return;
        }
        _alertText.Text = "⚠  " + alerts[0].Message + (alerts.Count > 1 ? L.F("   (+{0} more)", alerts.Count - 1) : "") + L.T("   · tap to hide");
        _alert.IsVisible = true;
        if (!_pulseTimer.IsEnabled && UI.Theme.Animate)
            _pulseTimer.Start();
    }

    void SnoozeAlerts()
    {
        _alerts.Snooze(_activeAlerts, TimeSpan.FromMinutes(10));
        ShowAlerts(Array.Empty<AlertEngine.Alert>());
        Toast(L.T("Alerts hidden for 10 minutes"));
    }

    public void Toast(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        _toastText.Text = text;
        _toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- night mode

    /// <summary>The motion stops while nobody can see it: minimized, idle a long time, black at night, or the PC locked.</summary>
    void UpdatePause() =>
        UI.FrameClock.Paused = !BenchAwake && (WindowState == WindowState.Minimized || (_last?.IdleSeconds ?? 0) > IdlePauseSeconds
                                               || _nightBlack || LinuxScreenLock.Active);

    /// <summary>SHOJI_BENCH_AWAKE=1 (test only): the motion keeps running whatever the idle time says, so its cost can be measured.</summary>
    static readonly bool BenchAwake = Environment.GetEnvironmentVariable("SHOJI_BENCH_AWAKE") == "1";

    /// <summary>
    /// Dims (or blacks out) the dashboard during the night hours with a sheet above everything. A tap wakes it for
    /// WakeSeconds. Checked every 30 s, after a wake runs out and on every config change.
    /// </summary>
    void UpdateNight()
    {
        var night = _config.Night;
        int brightness = Math.Clamp(night.Brightness, 0, 100);
        bool dim = SceneEngine.NightActive(night, DateTime.Now) && brightness < 100 && DateTime.Now >= _wakeUntil;
        bool wasBlack = _nightBlack;
        _nightDimmed = dim;
        _nightBlack = dim && brightness == 0;
        _night.IsVisible = night.Enabled;
        _night.IsHitTestVisible = dim;
        _night.Opacity = dim ? 1 - brightness / 100.0 : 0;
        if (wasBlack && !_nightBlack && _last != null)
            UpdateModules(_last); // they weren't updated while black
        UpdatePause();
    }

    void OnNightPointer(object? sender, PointerPressedEventArgs e)
    {
        if (!_config.Night.Enabled)
            return;
        if (_nightDimmed)
        {
            // Only wakes it: not a swipe, and not a press on the tile underneath.
            e.Handled = true;
            _swallowRelease = true;
            Wake();
        }
        else if (DateTime.Now < _wakeUntil)
        {
            Wake(); // awake at night: every touch keeps it awake a little longer
        }
    }

    void OnNightRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (!_swallowRelease)
            return;
        _swallowRelease = false;
        e.Handled = true;
    }

    void Wake()
    {
        var seconds = Math.Max(3, _config.Night.WakeSeconds);
        _wakeUntil = DateTime.Now.AddSeconds(seconds);
        _wakeTimer.Stop();
        _wakeTimer.Interval = TimeSpan.FromSeconds(seconds + 0.2);
        _wakeTimer.Start();
        UpdateNight();
    }

    // ---------------------------------------------------------------- IDashboardHost

    public LinuxMedia? Media => _collector?.Media;
    public string PageName => CurrentPage?.Name ?? "";
    public string ThemeName => UI.Theme.Current.Name;
    public bool ScenesEnabled => _config.ScenesEnabled;

    // ---------------------------------------------------------------- tile edit sheet (long press, UI/TileEditor.cs)

    /// <summary>Holding a tile may open its edit sheet now (switched on, and no sheet open yet).</summary>
    public bool LongPressEnabled => _config.LongPressEdit && !_editor.IsOpen && !_corner.IsHitTestVisible; // a hold in the top right corner brought the window buttons up instead

    /// <summary>A tile was held: open the edit sheet for it (its page and index in that page's list, hidden tiles included).</summary>
    public void EditTile(Tile tile)
    {
        foreach (var page in _pages)
            foreach (var m in page.Modules)
            {
                if (m.View != tile && !Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(m.View, tile))
                    continue;
                int index = _config.Pages[page.ConfigIndex].Modules.IndexOf(m.Config);
                if (index < 0)
                    return;
                // The held press is not a swipe.
                _swipeStart = null;
                _slide.X = 0;
                _editor.Open(_config, page.ConfigIndex, index);
                return;
            }
    }

    /// <summary>Columns of the screenful showing tile <paramref name="module"/> of configured page <paramref name="page"/>.</summary>
    public int TileColumns(int page, int module)
    {
        var config = page >= 0 && page < _config.Pages.Count && module >= 0 && module < _config.Pages[page].Modules.Count ? _config.Pages[page].Modules[module] : null;
        var screen = _pages.FirstOrDefault(p => p.ConfigIndex == page && p.Modules.Any(m => m.Config == config))
                     ?? _pages.FirstOrDefault(p => p.ConfigIndex == page);
        return Math.Max(1, screen?.Columns ?? 4);
    }

    // ---------------------------------------------------------------- web dashboard

    /// <summary>Starts, restarts or stops the MQTT publisher when its settings changed.</summary>
    void UpdateMqtt()
    {
        var mqtt = _config.Mqtt;
        string? signature = mqtt.Enabled && !string.IsNullOrWhiteSpace(mqtt.Host) ? mqtt.Signature : null;
        if (signature == _mqttSignature)
            return;
        _mqtt?.Dispose();
        _mqtt = signature == null ? null : new MqttPublisher(mqtt);
        _mqttSignature = signature;
    }

    void UpdateWebServer()
    {
        var web = _config.Web;
        if (web.Enabled && string.IsNullOrEmpty(web.Key))
        {
            web.Key = WebServer.NewKey();
            _config.Save();
        }
        string? signature = web.Enabled && web.Key != null ? $"{web.Port}:{web.Key}" : null;
        if (signature == _webSignature)
            return;
        _web?.Dispose();
        _web = null;
        _webSignature = null;
        if (signature == null)
            return;
        try
        {
            _web = new WebServer(web.Port, web.Key!);
            _webSignature = signature;
        }
        catch (Exception ex)
        {
            Log.Write($"Web dashboard could not start on port {web.Port}: {ex.Message}");
            Toast(L.F("Web dashboard: port {0} is in use", web.Port));
        }
    }

    /// <summary>The phone page's buttons: every enabled control deck, page by page (the server drops command / script buttons).</summary>
    void UpdateWebDeck()
    {
        if (_web == null)
            return;
        _web.SetDeck(_config.Web.AllowDeck
            ? _config.Pages.SelectMany(p => p.Modules)
                .Where(m => m.Enabled && m.Type == "deck" && m.Buttons != null)
                .SelectMany(m => m.Buttons!).ToList()
            : null);
        _web.Press = b => Dispatcher.UIThread.InvokeAsync(() =>
        {
            DeckActions.Run(b, this);
            return (string?)null;
        }).GetTask();
    }

    // ---------------------------------------------------------------- monitor placement

    /// <summary>Covers the configured monitor, or hides until it appears.</summary>
    public void Place()
    {
        if (_headless)
            return;
        var screen = ScreenPick.Find(Screens, _config.Monitor);
        if (screen == null)
        {
            if (IsVisible)
            {
                Log.Write($"Monitor '{_config.Monitor}' not found – hiding until it appears.");
                Hide();
            }
            _retryTimer.Start();
            return;
        }
        _retryTimer.Stop();
        var b = screen.Bounds;
        if (WindowState == WindowState.FullScreen && Position == b.Position)
            return;
        WindowState = WindowState.Normal;
        Position = b.Position;
        Width = b.Width / screen.Scaling;
        Height = b.Height / screen.Scaling;
        if (!IsVisible)
            Show();
        // Full screen on the monitor the window is on now (X11 / XWayland honour the position first).
        Dispatcher.UIThread.Post(() =>
        {
            Position = b.Position;
            WindowState = WindowState.FullScreen;
        }, DispatcherPriority.Background);
        // Window managers may apply full screen on the monitor the window was on before the move; check once and retry.
        DispatcherTimer.RunOnce(() =>
        {
            if (!IsVisible || Screens.ScreenFromWindow(this) is not { } now || now.Bounds == b)
                return;
            Log.Write($"Dashboard landed on {now.Bounds} instead of {b}; moving it again.");
            WindowState = WindowState.Normal;
            Position = b.Position;
            Dispatcher.UIThread.Post(() => WindowState = WindowState.FullScreen, DispatcherPriority.Background);
        }, TimeSpan.FromMilliseconds(700));
    }

    protected override void OnClosed(EventArgs e)
    {
        _retryTimer.Stop();
        _shiftTimer.Stop();
        _cycleTimer.Stop();
        _nightTimer.Stop();
        _wakeTimer.Stop();
        DisposeModules();
        _collector?.Dispose();
        ServiceStore.DisposeAll();
        _mqtt?.Dispose();
        _web?.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>Finds the monitor the config asks for ("auto" = smallest non-primary, "primary", a number, or a name).</summary>
public static class ScreenPick
{
    public static Screen? Find(Screens screens, string? monitor)
    {
        var all = screens.All.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        if (all.Count == 0)
            return null;
        monitor = (monitor ?? "auto").Trim();
        if (monitor.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return all.FirstOrDefault(s => s.IsPrimary) ?? all[0];
        if (int.TryParse(monitor, out int n))
            return n >= 1 && n <= all.Count ? all[n - 1] : null;
        if (!monitor.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return all.FirstOrDefault(s => Name(s).Equals(monitor, StringComparison.OrdinalIgnoreCase));
        // Auto: the smallest screen that isn't the main one; with one screen, that one.
        var others = all.Where(s => !s.IsPrimary).ToList();
        return others.Count == 0 ? all[0] : others.OrderBy(s => (long)s.Bounds.Width * s.Bounds.Height).First();
    }

    public static string Name(Screen s) => string.IsNullOrWhiteSpace(s.DisplayName) ? $"{s.Bounds.Width}x{s.Bounds.Height}+{s.Bounds.X}+{s.Bounds.Y}" : s.DisplayName!;

    public static string Describe(Screen s, int number) =>
        $"{number}: {Name(s)} – {s.Bounds.Width}×{s.Bounds.Height}{(s.IsPrimary ? L.T(" (main)") : "")}";
}

static class VisualExtensions
{
    public static Avalonia.Visual? GetVisualParentCompat(this Avalonia.Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
}
