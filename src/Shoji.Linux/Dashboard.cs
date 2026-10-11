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
        layers.Children.Add(_cover);
        layers.Children.Add(_animBack);
        layers.Children.Add(root);
        layers.Children.Add(_animFront);
        layers.Children.Add(_crt);
        layers.Children.Add(_dots);
        _editor = new TileEditor(this); // long-press edit sheet (UI/TileEditor.cs), below the alert, toast and night layers
        layers.Children.Add(_editor);
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
        _crt.InvalidateVisual();

        int showing = CurrentPageIndex;
        DisposeModules();
        ServiceStore.BeginBuild();
        _built = _config.Pages.Select(p => (p, p.Modules.Where(m => m.Enabled)
                .Select(m => ModuleFactory.SafeCreate(m, _config, _collector!, this))
                .Where(m => m != null).Select(m => m!).ToList())).ToList();
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
        // Nobody at the PC for a while (the screen is probably blanked) or the dashboard minimized: stop the motion,
        // which is most of what the dashboard costs. The first input brings it back within a second.
        // Also while night mode has it black, or the desktop's lock screen / screensaver is up.
        LinuxScreenLock.Update(s.IdleSeconds);
        UpdatePause();
        if (!IsVisible)
            return;
        if (!_nightBlack) // nobody can see the tiles; they catch up when it wakes
            UpdateModules(s);
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
        for (int r = 0; r <= placement.Max(p => p.Row); r++)
            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
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
        SwipeInProgress = false;
    }

    void OnSwipeMove(object? sender, PointerEventArgs e)
    {
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
            _slide.X = dx * 0.35; // follow the finger a little
            _slide.Transitions = transitions;
        }
    }

    void OnSwipeEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (_swipeStart is Point start && SwipeInProgress)
        {
            e.Handled = true; // a swipe, not a tap on whatever is underneath
            double dx = e.GetPosition(this).X - start.X;
            if (Math.Abs(dx) > 70)
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
        UI.FrameClock.Paused = WindowState == WindowState.Minimized || (_last?.IdleSeconds ?? 0) > IdlePauseSeconds
                               || _nightBlack || LinuxScreenLock.Active;

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
    public bool LongPressEnabled => _config.LongPressEdit && !_editor.IsOpen;

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
