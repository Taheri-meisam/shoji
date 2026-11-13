using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shoji.Core;
using Shoji.Modules;
using Shoji.Sensors;
using Shoji.UI;
using Screen = System.Windows.Forms.Screen;

namespace Shoji;

/// <summary>
/// Borderless full-screen dashboard that lives on the configured monitor. Holds the pages (swipe between them),
/// runs scenes, the day/night theme, alerts and the optional web dashboard.
/// </summary>
public sealed class MainWindow : Window, IDashboardHost
{
    /// <summary>One screenful. A configured page becomes several when its tiles don't fit readably on this screen.</summary>
    sealed class Page
    {
        public required PageConfig Config { get; init; }
        public required int ConfigIndex { get; init; }
        /// <summary>0 for the page itself, 1, 2… for its continuation screens.</summary>
        public required int Part { get; init; }
        public required List<Module> Modules { get; init; }
        public required int Columns { get; init; }
        public required double ScaleValue { get; init; }
        public Grid Grid { get; } = new();
        public ScaleTransform Scale { get; } = new(1, 1);
        public string Name => Part == 0 ? Config.Name : $"{Config.Name} ({Part + 1})";
    }

    /// <summary>The tiles of each configured page, in config order (they're arranged into screenfuls by Arrange).</summary>
    List<(PageConfig Config, List<Module> Modules)> _built = new();

    readonly Grid _pagesHost = new();
    readonly Backdrop _backdrop = new();
    readonly ArtLayer _art = new();
    readonly Wallpaper _wallpaper = new(); // the user's own background picture (UI/Wallpaper.cs)
    readonly AlbumArtBackdrop _cover = new(); // the playing track's cover behind a media page (UI/AlbumArtBackdrop.cs)
    readonly CrtOverlay _crt = new();
    readonly Canvas _animBack = new() { IsHitTestVisible = false, ClipToBounds = true };
    readonly Canvas _animFront = new() { IsHitTestVisible = false, ClipToBounds = true };
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    readonly Border _alert = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _alertText = new() { FontWeight = FontWeights.SemiBold, FontSize = 15 };
    readonly Border _toast = new() { Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22) };
    readonly TextBlock _toastText = new() { FontSize = 15 };
    // Night mode: a black layer over everything, as dark as the night brightness asks; while dimmed it takes the taps.
    readonly Border _night = new() { Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
    readonly DispatcherTimer _nightTimer = new() { Interval = TimeSpan.FromSeconds(30) }, _wakeTimer = new();
    bool _nightDimmed, _nightWoken, _swallowUp;
    IntPtr _displayNotify;
    bool _sessionNotify;
    string? _pressToast;
    bool _pressing;
    readonly TranslateTransform _shift = new(), _slide = new();
    readonly DispatcherTimer _retryTimer, _shiftTimer, _cycleTimer, _taskbarTimer;
    readonly Random _random = new();
    readonly SceneEngine _scenes = new();
    readonly AlertEngine _alerts = new();
    readonly TileEditor _editor;

    AppConfig _config = new();
    Collector? _collector;
    string? _collectorKey;
    List<Page> _pages = new();
    int _current;
    (int ConfigIndex, int Part)? _homePage; // where to return when a scene ends (survives re-layouts)
    bool _movedDuringScene;
    string _appliedTheme = "";
    IReadOnlyList<AlertEngine.Alert> _activeAlerts = Array.Empty<AlertEngine.Alert>();
    WebServer? _web;
    string? _webSignature;
    MqttPublisher? _mqtt;
    string? _mqttSignature;
    Snapshot? _last;
    Point? _swipeStart;
    bool _swiping;
    IntPtr _hwnd;

    public Screen? CurrentScreen { get; private set; }
    /// <summary>The configured page that's showing (continuation screens count as their page).</summary>
    public int CurrentPageIndex => CurrentPage?.ConfigIndex ?? 0;

    int RuntimeIndex(int configIndex) => _pages.FindIndex(p => p.ConfigIndex == configIndex);

    readonly List<string> _recentApps = new();

    /// <summary>Process names of recently focused apps, newest first (Settings shows them to help write scenes).</summary>
    public IReadOnlyList<string> RecentApps => _recentApps;

    public MainWindow()
    {
        Title = "Shoji";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var root = new Grid { Margin = new Thickness(6), RenderTransform = _shift };
        _pagesHost.RenderTransform = _slide;
        root.Children.Add(_pagesHost);

        _alert.Child = _alertText;
        _alert.Padding = new Thickness(16, 8, 16, 8);
        _alert.CornerRadius = new CornerRadius(8);
        Tap.On(_alert, SnoozeAlerts);
        _toast.Child = _toastText;
        _toast.Padding = new Thickness(16, 7, 16, 7);
        _toast.CornerRadius = new CornerRadius(8);

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
        layers.Children.Add(_meeting);
        layers.Children.Add(_alert);
        layers.Children.Add(_toast);
        // The tile edit sheet (long press on a tile) sits above the tiles and below the night-mode dimmer.
        _editor = new TileEditor(ColumnsOf, Toast, OpenSettingsAt);
        layers.Children.Add(_editor);
        layers.Children.Add(_night);
        Content = layers;

        _nightTimer.Tick += (_, _) => UpdateNight();
        _wakeTimer.Tick += (_, _) =>
        {
            _wakeTimer.Stop();
            _nightWoken = false;
            UpdateNight();
        };
        Motion.Changed += OnMotionChanged;

        // While the target monitor is missing, look for it now and then (it may still be waking up).
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _retryTimer.Tick += (_, _) => Place();

        _taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _taskbarTimer.Tick += (_, _) => CoverTaskbar();

        _shiftTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _shiftTimer.Tick += (_, _) =>
        {
            _shift.X = _random.Next(-3, 4);
            _shift.Y = _random.Next(-3, 4);
        };

        _cycleTimer = new DispatcherTimer();
        _cycleTimer.Tick += (_, _) =>
        {
            if (_scenes.Active == null && _pages.Count > 1 && !_editor.IsOpen)
                ShowPageIndex((_current + 1) % _pages.Count, fromScene: true);
        };

        SizeChanged += (_, _) =>
        {
            Arrange(); // a different size can mean a different number of screenfuls
            Animations.Build(_animBack, _animFront, ActualWidth, ActualHeight);
        };
        SourceInitialized += OnSourceInitialized;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

        // First, so a tap on the dimmed screen only wakes it and never reaches a swipe or a tile.
        PreviewMouseLeftButtonDown += OnNightTap;
        PreviewMouseLeftButtonUp += OnNightTapEnd;
        PreviewMouseLeftButtonDown += OnSwipeStart;
        PreviewMouseMove += OnSwipeMove;
        PreviewMouseLeftButtonUp += OnSwipeEnd;
        // Mouse and keyboard ways to change page (the wheel only where no list takes it).
        MouseWheel += OnPageWheel;
        PreviewMouseDown += OnPageButtons;
        KeyDown += OnPageKey;
        MouseLeave += (_, _) => ShowEdges(null);
        BuildEdge(_edgeLeft, "\u2039", -1);
        BuildEdge(_edgeRight, "\u203A", +1);
        BuildCorner();
        BuildMeeting();
    }

    public Collector? Collector => _collector;
    Page? CurrentPage => _current >= 0 && _current < _pages.Count ? _pages[_current] : null;

    /// <summary>(Re)builds everything from config. Safe to call again after the config file changes.</summary>
    public void Apply(AppConfig config)
    {
        L.Apply(config.Language); // before the tiles are built: they take their text when created
        _config = config;
        Topmost = config.Topmost;

        // Theme / layout changes keep the running collector (no LibreHardwareMonitor reopen, no blank second).
        var key = Collector.KeyFor(config);
        bool newCollector = _collector == null || key != _collectorKey;
        if (newCollector)
        {
            DisposeModules();
            _collector?.Stop(); // no wait: the new collector's loop waits for the old one to let go of the sensors
            Collector? created = null;
            // A replaced collector may still deliver one last snapshot while it winds down: ignore it.
            created = new Collector(config, snapshot => Dispatcher.BeginInvoke(() =>
            {
                if (created == _collector)
                    Render(snapshot);
            }));
            _collector = created;
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
        UpdateNight();
        TileHold.Handler = config.LongPressEdit ? OnTileHold : null;
        Build(EffectiveTheme());
        Place();
        if (newCollector)
            _collector!.Start();
    }

    /// <summary>Creates all pages and tiles in the given theme. The collector (and its services) stay.</summary>
    void Build(string themeId)
    {
        Theme.Apply(_config, themeId);
        _appliedTheme = themeId;
        Background = Theme.WindowBackground;
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
        ServiceStore.EndBuild(); // frees helpers (chat connections, pollers…) no tile uses any more
        _pages = new List<Page>();
        Arrange(keepConfigPage: showing);
        Animations.Build(_animBack, _animFront, ActualWidth, ActualHeight);
        StyleOverlays();

        // Fill the new tiles straight away instead of waiting for the next refresh.
        if (_last != null)
            UpdateModules(_last);

        // An open tile edit sheet shows the new state, and its tile stays in view (it may be on a continuation screen now).
        if (_editor.Reload(_config) is { } target)
        {
            var edited = _config.Pages[target.Page].Modules[target.Index];
            int screen = _pages.FindIndex(p => p.Modules.Any(m => m.Config == edited));
            if (screen >= 0 && screen != _current)
                ShowPageIndex(screen, animate: false, fromScene: true);
        }
    }

    void DisposeModules()
    {
        foreach (var m in _built.SelectMany(p => p.Modules))
        {
            try { m.Dispose(); } catch { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------- refresh

    void Render(Snapshot s)
    {
        _last = s;
        var app = s.Foreground.ProcessName;
        if (app.Length > 0 && (_recentApps.Count == 0 || _recentApps[0] != app))
        {
            _recentApps.Remove(app);
            _recentApps.Insert(0, app);
            if (_recentApps.Count > 10)
                _recentApps.RemoveAt(10);
        }
        // The phone dashboard keeps going even while the small screen is off or unplugged.
        _web?.Publish(s, _appliedTheme);
        _mqtt?.Publish(s);
        if (!IsVisible)
            return;

        UpdateModules(s);
        UpdateMeeting(s.Time);
        UpdateCover();

        if (_scenes.Update(_config, s))
            OnSceneChanged();

        // Scene or day/night may want a different theme.
        var theme = EffectiveTheme();
        if (theme != _appliedTheme)
            Build(theme);

        var alerts = _alerts.Evaluate(_config.Alerts, s, out bool isNew);
        ShowAlerts(alerts);
        if (isNew && _config.Alerts.Sound)
            System.Media.SystemSounds.Exclamation.Play();
    }

    void UpdateModules(Snapshot s)
    {
        // Hidden pages update too, so their tiles are current the moment you swipe to them.
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
            if (index >= 0 && !_editor.IsOpen) // not away from a tile being edited
                ShowPageIndex(index, fromScene: true);
            Toast(scene.Name);
        }
        else
        {
            // Back to where you were, unless you swiped somewhere else yourself meanwhile.
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

    void ShowPageIndex(int index, bool animate = true, bool fromScene = false)
    {
        if (_pages.Count == 0)
            return;
        index = (index % _pages.Count + _pages.Count) % _pages.Count;
        int direction = _current < 0 ? 0 : index > _current ? 1 : index < _current ? -1 : 0;
        if (!fromScene && _scenes.Active != null)
            _movedDuringScene = true;

        _current = index;
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Grid.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        BuildDots();
        UpdateCover();

        if (animate && direction != 0)
        {
            var slide = new DoubleAnimation(direction * 60, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fade = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260));
            _slide.BeginAnimation(TranslateTransform.XProperty, slide);
            _pagesHost.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = 0;
        }
    }

    /// <summary>The album-art backdrop follows the track, the page and the setting.</summary>
    void UpdateCover() => _cover.Update(_config, CurrentPage?.Config, _last?.Media);

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
                Fill = i == _current ? Theme.Accent : new SolidColorBrush(Theme.WithAlpha(Theme.TextColor, 0x50)),
            };
            // A bigger invisible target around each dot so it can be tapped.
            var target = new Border { Width = 22, Height = 13, Background = Brushes.Transparent, Child = dot, ToolTip = _pages[i].Name };
            Tap.On(target, () => ShowPageIndex(index));
            _dots.Children.Add(target);
        }
    }

    /// <summary>
    /// Splits every configured page into screenfuls that fit this screen readably (see <see cref="TileLayout.Plan"/>)
    /// and lays them out. Runs after a rebuild and whenever the window size changes; the tiles themselves are reused.
    /// </summary>
    void Arrange(int? keepConfigPage = null)
    {
        var showing = CurrentPage;
        int keepConfig = keepConfigPage ?? showing?.ConfigIndex ?? 0, keepPart = keepConfigPage == null ? showing?.Part ?? 0 : 0;

        foreach (var page in _pages)
            page.Grid.Children.Clear(); // release the tiles so they can move to a new grid
        _pagesHost.Children.Clear();

        double w = ActualWidth - 12, h = ActualHeight - 12;
        bool sized = w > 50 && h > 50;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pages = new List<Page>();
        for (int c = 0; c < _built.Count; c++)
        {
            var (config, modules) = _built[c];
            var spans = modules.Select(m => m.Config.Span).ToList();
            int fixedCols = config.Columns > 0 ? config.Columns : _config.Columns;
            var plans = sized
                ? TileLayout.Plan(spans, w, h, fixedCols, dpi, _config.Scale)
                : new List<TileLayout.ScreenPlan> { new(0, modules.Count, Math.Max(1, fixedCols), 1) };
            for (int part = 0; part < plans.Count; part++)
            {
                var plan = plans[part];
                var page = new Page
                {
                    Config = config, ConfigIndex = c, Part = part, Columns = plan.Columns, ScaleValue = plan.Scale,
                    Modules = modules.Skip(plan.Start).Take(plan.Count).ToList(),
                };
                page.Grid.LayoutTransform = page.Scale;
                page.Grid.Visibility = Visibility.Collapsed;
                foreach (var m in page.Modules)
                    page.Grid.Children.Add(m.View);
                LayoutPage(page);
                _pagesHost.Children.Add(page.Grid);
                pages.Add(page);
            }
        }
        _pages = pages;

        int index = _pages.FindIndex(p => p.ConfigIndex == keepConfig && p.Part == keepPart);
        if (index < 0) index = Math.Max(0, RuntimeIndex(keepConfig));
        _current = -1; // force the switch below to show it
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
        return page.Columns > 1 || config.Span >= 9 ? TileLayout.ShowpieceWeight(config.Type.ToLowerInvariant()) : 0;
    }

    /// <summary>Puts a screenful's tiles in its grid at the planned column count and scale.</summary>
    static void LayoutPage(Page page)
    {
        var grid = page.Grid;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (page.Modules.Count == 0)
            return;
        var placement = TileLayout.Pack(page.Modules.Select(m => m.Config.Span).ToList(), page.Columns);
        for (int c = 0; c < page.Columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = placement.Max(p => p.Row) + 1;
        bool tall = TileLayout.Tall(rows, page.Columns);
        for (int r = 0; r < rows; r++)
            {
            double showpiece = rows > 1 ? ShowpieceRow(page, placement, r) : 0;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(showpiece > 0 ? showpiece : tall ? RowWeight(page, placement, r) : 1, GridUnitType.Star) });
        }
        for (int i = 0; i < page.Modules.Count; i++)
        {
            var view = page.Modules[i].View;
            Grid.SetRow(view, placement[i].Row);
            Grid.SetColumn(view, placement[i].Col);
            Grid.SetColumnSpan(view, placement[i].Span);
        }
        page.Scale.ScaleX = page.Scale.ScaleY = page.ScaleValue;
    }

    // ---------------------------------------------------------------- tile edit sheet

    /// <summary>Opens Settings the way the tray does (set by Program). "More settings…" on the tile edit sheet uses it.</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>A long press on a tile: open its edit sheet. The press must not also become a page swipe.</summary>
    void OnTileHold(Tile tile)
    {
        if (_cornerShown)
            return; // a hold in the top right corner brought the window buttons up instead
        _swipeStart = null;
        _swiping = false;
        for (int p = 0; p < _built.Count; p++)
            foreach (var m in _built[p].Modules)
                if (m.View == tile || m.View.IsAncestorOf(tile))
                {
                    _editor.Open(_config, p, _built[p].Config.Modules.IndexOf(m.Config));
                    return;
                }
    }

    /// <summary>The columns a configured page is laid out in on this screen right now.</summary>
    int ColumnsOf(int configPage) =>
        _pages.Where(p => p.ConfigIndex == configPage).Select(p => p.Columns).DefaultIfEmpty(1).Max();

    void OpenSettingsAt(int configPage)
    {
        if (!SettingsWindow.OpenAt("pages", configPage, OpenSettings))
            Toast(L.T("Open Settings from the tray icon → Pages & tiles"));
    }

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

    void OnPageWheel(object sender, MouseWheelEventArgs e)
    {
        // One page per gesture: a touchpad sends a burst of small steps.
        long now = Environment.TickCount64;
        if (e.Delta == 0 || now - _lastFlip < 350 || _pages.Count < 2 || _editor.IsOpen)
            return;
        _lastFlip = now;
        e.Handled = true;
        Flip(e.Delta < 0 ? +1 : -1);
    }

    /// <summary>The mouse's back / forward thumb buttons.</summary>
    void OnPageButtons(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
            Flip(-1);
        else if (e.ChangedButton == MouseButton.XButton2)
            Flip(+1);
        else
            return;
        e.Handled = true;
    }

    void OnPageKey(object sender, KeyEventArgs e)
    {
        if (_pages.Count < 2 || _editor.IsOpen || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
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
        edge.BorderThickness = new Thickness(1);
        edge.Child = new TextBlock { Text = glyph, FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -8, 0, 0) };
        Tap.On(edge, () => Flip(delta));
    }

    /// <summary>Shows the arrow of the edge the mouse is near (null = none). A finger never brings them up: it swipes.</summary>
    void ShowEdges(Point? mouse)
    {
        _cornerNear = mouse is { } at && InCorner(at) && !_swiping;
        // Only real movement counts: WPF repeats MouseMove under a resting pointer whenever the tiles redraw.
        if (mouse is { } now && (Math.Abs(now.X - _cornerMouse.X) > 2 || Math.Abs(now.Y - _cornerMouse.Y) > 2))
        {
            _cornerMouse = now;
            PinCorner(3.5);
        }
        UpdateCorner();
        bool can = mouse != null && _pages.Count > 1 && !_editor.IsOpen && !_swiping;
        double reach = Math.Min(90, ActualWidth * 0.12);
        Set(_edgeLeft, can && mouse!.Value.X < reach);
        Set(_edgeRight, can && mouse!.Value.X > ActualWidth - reach);

        static void Set(Border edge, bool show)
        {
            if (edge.IsHitTestVisible == show)
                return;
            edge.IsHitTestVisible = show;
            if (show)
            {
                edge.Background = Theme.Tile;
                edge.BorderBrush = new SolidColorBrush(Theme.WithAlpha(Theme.TextColor, 0x30));
                ((TextBlock)edge.Child).Foreground = Theme.Text;
            }
            edge.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 0.92 : 0, TimeSpan.FromMilliseconds(160)));
        }
    }

    // ---------------------------------------------------------------- the window's own buttons: Settings and Close

    // The dashboard has no title bar, and the tray icon is easy to miss (Windows tucks it into the overflow): these
    // are the visible way to reach Settings and to close Shoji. They show for a few seconds when the dashboard starts
    // and whenever the mouse moves over it (as a video player's controls do), on a right click, and on a long press
    // in that corner (touch screens).
    readonly Border _corner = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) };
    readonly DispatcherTimer _cornerTimer = new(), _cornerHold = new() { Interval = TimeSpan.FromMilliseconds(650) };
    bool _cornerNear, _cornerPinned, _cornerShown;
    Point _cornerMouse;

    /// <summary>Closes Shoji the way the tray icon's Exit does (set by Program).</summary>
    public Action? Quit { get; set; }

    void BuildCorner()
    {
        _corner.Padding = new Thickness(3);
        _corner.BorderThickness = new Thickness(1);
        _corner.Opacity = 0;
        _corner.IsHitTestVisible = false;
        _corner.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                CornerButton("\uE713", L.T("Settings"), () => OpenSettings?.Invoke()),
                CornerButton("\uE8BB", L.T("Exit"), () => Quit?.Invoke()),
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
            if (!_swiping)
                PinCorner();
        };
        PreviewMouseRightButtonDown += (_, _) => PinCorner();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (InCorner(e.GetPosition(this)) && !_cornerShown)
                _cornerHold.Start();
        };
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _cornerHold.Stop()), handledEventsToo: true);
        Loaded += (_, _) => PinCorner(8); // so they are seen once, right where they will be
    }

    Border CornerButton(string glyph, string name, Action action)
    {
        var b = new Border
        {
            Background = Brushes.Transparent, ToolTip = name,
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        // Quiet until pointed at: dim glyphs, the one under the mouse lights up.
        b.MouseEnter += (_, _) =>
        {
            b.Background = new SolidColorBrush(Theme.WithAlpha(Theme.TextColor, 0x24));
            ((TextBlock)b.Child).Foreground = Theme.Text;
        };
        b.MouseLeave += (_, _) =>
        {
            b.Background = Brushes.Transparent;
            ((TextBlock)b.Child).Foreground = Theme.Dim;
        };
        return Tap.On(b, action);
    }

    bool InCorner(Point p) => p.Y < 110 && p.X > ActualWidth - Math.Max(220, ActualWidth * 0.2);

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
        if (_cornerShown == show)
            return;
        _cornerShown = show;
        _corner.IsHitTestVisible = show;
        if (show)
        {
            _corner.Background = Theme.Tile;
            _corner.BorderBrush = new SolidColorBrush(Theme.WithAlpha(Theme.TextColor, 0x30));
            // A small pill in the theme's own shape (square for the HUD themes), a little bigger on a big monitor.
            double size = Math.Round(Math.Clamp(Math.Min(ActualWidth, ActualHeight) / 30, 30, 40));
            bool square = Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
            _corner.CornerRadius = new CornerRadius(square ? 0 : size / 2 + 3);
            foreach (Border button in ((StackPanel)_corner.Child).Children)
            {
                button.Width = button.Height = size;
                button.CornerRadius = new CornerRadius(square ? 0 : size / 2);
                var glyph = (TextBlock)button.Child;
                glyph.FontSize = Math.Round(size * 0.42);
                glyph.Foreground = Theme.Dim;
            }
        }
        _corner.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(160)));
    }

    // ---------------------------------------------------------------- swipe

    void OnSwipeStart(object sender, MouseButtonEventArgs e)
    {
        if (_editor.IsOpen)
            return; // the tile edit sheet is up: no swiping the page under it
        // Dragging a slider is not a page swipe.
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TouchSlider)
                return;
            if (d is not Visual)
                break;
        }
        _swipeStart = e.GetPosition(this);
        _swipeStartedAt = Environment.TickCount64;
        _swiping = false;
    }

    void OnSwipeMove(object sender, MouseEventArgs e)
    {
        // A real mouse (not a finger or pen promoted to mouse events) near an edge brings up that edge's arrow.
        if (e.StylusDevice == null && e.LeftButton != MouseButtonState.Pressed)
            ShowEdges(e.GetPosition(this));
        if (_swipeStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _pages.Count < 2)
            return;
        var p = e.GetPosition(this);
        double dx = p.X - start.X, dy = p.Y - start.Y;
        if (!_swiping && Math.Abs(dx) > 24 && Math.Abs(dx) > Math.Abs(dy) * 1.3)
            _swiping = true;
        if (_swiping)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = dx * 0.6; // follow the finger (or the dragging mouse)
        }
    }

    void OnSwipeEnd(object sender, MouseButtonEventArgs e)
    {
        if (_swipeStart is Point start && _swiping)
        {
            e.Handled = true; // it was a swipe, not a tap on whatever is underneath
            double dx = e.GetPosition(this).X - start.X;
            // Far enough for this screen (a narrow one needs less), or a quick flick.
            double far = Math.Clamp(ActualWidth * 0.12, 36, 70);
            double speed = Math.Abs(dx) / Math.Max(1, Environment.TickCount64 - _swipeStartedAt); // px per ms
            if (Math.Abs(dx) > far || speed > 0.6 && Math.Abs(dx) > 36)
            {
                ShowPageIndex(dx < 0 ? _current + 1 : _current - 1);
                Toast(CurrentPage?.Name ?? "");
            }
            else
            {
                _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
            }
        }
        _swipeStart = null;
        _swiping = false;
    }

    // ---------------------------------------------------------------- meeting reminder

    // Two minutes before a calendar event starts (and until five minutes after) a card comes up at the top with a
    // countdown and, when the event carries a meeting link, a Join button. The calendars are the ones from
    // Settings → Weather & time; Settings → Weather & time → Calendars switches the reminder off.
    AgendaService? _meetings;
    readonly Border _meeting = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _meetingTitle = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, _meetingWhen = new(), _meetingIcon = new() { Text = "\uE787", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _meetingJoinText = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _meetingCloseGlyph = new() { Text = "\uE8BB", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _meetingJoin = new() { VerticalAlignment = VerticalAlignment.Center }, _meetingClose = new() { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    string? _meetingDismissed, _meetingLink, _meetingKey;

    void BuildMeeting()
    {
        _meetingJoin.Child = _meetingJoinText;
        _meetingClose.Child = _meetingCloseGlyph;
        Tap.On(_meetingJoin, () =>
        {
            if (_meetingLink is { } link)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { Log.Write($"Could not open the meeting link: {ex.Message}"); }
            }
            _meetingDismissed = _meetingKey;
            _meeting.Visibility = Visibility.Collapsed;
        });
        Tap.On(_meetingClose, () =>
        {
            _meetingDismissed = _meetingKey;
            _meeting.Visibility = Visibility.Collapsed;
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
            _meeting.Visibility = Visibility.Collapsed;
            return;
        }
        if (_meeting.Visibility != Visibility.Visible || key != _meetingKey)
        {
            // Sized for the screen and in the theme's colours, like the tiles under it.
            double size = Math.Round(Math.Clamp(Math.Min(ActualWidth, ActualHeight) / 34, 14, 28));
            bool square = Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
            _meeting.Margin = new Thickness(16, _alert.Visibility == Visibility.Visible ? 64 : 14, 16, 0);
            _meeting.Padding = new Thickness(size, size * 0.6, size * 0.6, size * 0.6);
            _meeting.CornerRadius = new CornerRadius(square ? 0 : size * 0.8);
            _meeting.Background = Theme.Tile;
            _meeting.BorderBrush = Theme.Accent;
            _meeting.BorderThickness = new Thickness(1.5);
            _meetingIcon.FontFamily = _meetingCloseGlyph.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            _meetingIcon.FontSize = size * 1.4;
            _meetingIcon.Foreground = Theme.Accent;
            _meetingIcon.Margin = new Thickness(0, 0, size * 0.7, 0);
            _meetingTitle.FontFamily = _meetingWhen.FontFamily = _meetingJoinText.FontFamily = Theme.Font;
            _meetingTitle.FontSize = size * 1.15;
            _meetingTitle.Foreground = Theme.Text;
            _meetingTitle.MaxWidth = Math.Max(160, ActualWidth * 0.5);
            _meetingWhen.FontSize = size * 0.9;
            _meetingWhen.Foreground = Theme.Dim;
            _meetingJoin.Background = Theme.Accent;
            _meetingJoin.CornerRadius = new CornerRadius(square ? 0 : size * 0.5);
            _meetingJoin.Padding = new Thickness(size * 1.1, size * 0.45, size * 1.1, size * 0.45);
            _meetingJoin.Margin = new Thickness(size * 1.2, 0, 0, 0);
            _meetingJoinText.FontSize = size;
            _meetingJoinText.Foreground = Theme.Background;
            _meetingJoinText.Text = L.T("Join");
            _meetingClose.Width = _meetingClose.Height = size * 2;
            _meetingClose.Margin = new Thickness(size * 0.4, 0, 0, 0);
            _meetingCloseGlyph.FontSize = size * 0.8;
            _meetingCloseGlyph.Foreground = Theme.Dim;
            _meetingClose.ToolTip = L.T("Dismiss");
        }
        _meetingKey = key;
        _meetingLink = e!.Link;
        _meetingJoin.Visibility = e.Link != null ? Visibility.Visible : Visibility.Collapsed;
        _meetingTitle.Text = e.Title;
        double seconds = (e.Start - now).TotalSeconds;
        _meetingWhen.Text = seconds > 0
            ? L.F("starts in {0}", $"{(int)seconds / 60}:{(int)seconds % 60:00}")
            : seconds > -60 ? L.T("starting now") : L.F("started {0} min ago", (int)(-seconds / 60));
        _meeting.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- overlays

    void StyleOverlays()
    {
        _alert.Background = Theme.Crit;
        _alertText.Foreground = Brushes.White;
        _alertText.FontFamily = Theme.Font;
        _toast.Background = Theme.Tile;
        _toast.BorderBrush = Theme.Accent;
        _toast.BorderThickness = new Thickness(1);
        _toastText.Foreground = Theme.Text;
        _toastText.FontFamily = Theme.Font;
    }

    void ShowAlerts(IReadOnlyList<AlertEngine.Alert> alerts)
    {
        bool was = _activeAlerts.Count > 0;
        _activeAlerts = alerts;
        if (alerts.Count == 0)
        {
            _alert.Visibility = Visibility.Collapsed;
            _alert.BeginAnimation(OpacityProperty, null);
            return;
        }
        _alertText.Text = "⚠  " + alerts[0].Message + (alerts.Count > 1 ? "   " + L.F("(+{0} more)", alerts.Count - 1) : "") + "   · " + L.T("tap to hide");
        _alert.Visibility = Visibility.Visible;
        if (!was && !Motion.Paused)
            PulseAlert();
    }

    void PulseAlert()
    {
        var pulse = new DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 20);
        _alert.BeginAnimation(OpacityProperty, pulse);
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
        if (_pressing)
            _pressToast = text;
        _toastText.Text = text;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.6) };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6))));
        _toast.BeginAnimation(OpacityProperty, a);
    }

    // ---------------------------------------------------------------- night mode & pausing

    double _nightOpacity;

    /// <summary>
    /// Dims (or blacks out) the dashboard during the night hours unless a tap woke it a moment ago. Runs on every
    /// config apply, every 30 s while night mode is on, and when a wake runs out.
    /// </summary>
    void UpdateNight(bool fast = false)
    {
        var night = _config.Night;
        if (night.Enabled) _nightTimer.Start();
        else _nightTimer.Stop();

        bool active = SceneEngine.NightActive(night, DateTime.Now);
        if (!active && _nightWoken)
        {
            _nightWoken = false;
            _wakeTimer.Stop();
        }
        int brightness = Math.Clamp(night.Brightness, 0, 100);
        _nightDimmed = active && !_nightWoken && brightness < 100;
        _night.IsHitTestVisible = _nightDimmed;
        Motion.Set("night", _nightDimmed && brightness == 0); // black: nobody sees anything move

        double target = _nightDimmed ? 1 - brightness / 100.0 : 0;
        if (target == _nightOpacity)
            return;
        _nightOpacity = target;
        _night.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(fast ? 0.3 : 1.5)));
    }

    /// <summary>A tap on the dimmed screen wakes it for a while and goes no further (not a swipe, not a button).</summary>
    void OnNightTap(object sender, MouseButtonEventArgs e)
    {
        _swallowUp = false;
        if (_nightDimmed)
        {
            e.Handled = true;
            _swallowUp = true;
            _nightWoken = true;
            RestartWake();
            UpdateNight(fast: true);
        }
        else if (_nightWoken)
        {
            RestartWake(); // still using it: stay awake
        }
    }

    void OnNightTapEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_swallowUp)
            return;
        _swallowUp = false;
        e.Handled = true;
    }

    void RestartWake()
    {
        _wakeTimer.Stop();
        _wakeTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_config.Night.WakeSeconds, 5, 3600));
        _wakeTimer.Start();
    }

    /// <summary>Animations stop while nobody can see them (see <see cref="Motion"/>); the layers here follow.</summary>
    void OnMotionChanged()
    {
        Animations.Build(_animBack, _animFront, ActualWidth, ActualHeight);
        if (_activeAlerts.Count == 0)
            return;
        if (Motion.Paused)
            _alert.BeginAnimation(OpacityProperty, null);
        else
            PulseAlert();
    }

    // ---------------------------------------------------------------- IDashboardHost

    public MediaService? Media => _collector?.Media;
    public string PageName => CurrentPage?.Name ?? "";
    public string ThemeName => Theme.Current.Name;
    public bool ScenesEnabled => _config.ScenesEnabled;

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
            _config.Save(); // the reload that follows starts the server with the new key
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
            _webSignature = signature; // only once it really runs, so a busy port is retried on the next reload
        }
        catch (Exception ex)
        {
            Log.Write($"Web dashboard could not start on port {web.Port}: {ex.Message}");
            Toast(L.F("Web dashboard: port {0} is in use", web.Port));
        }
    }

    /// <summary>Hands the phone page the deck buttons of every switched-on control deck (page order), or none.</summary>
    void UpdateWebDeck()
    {
        if (_web == null)
            return;
        _web.SetDeck(_config.Web.AllowDeck
            ? _config.Pages.SelectMany(p => p.Modules)
                .Where(m => m.Enabled && m.Type.Equals("deck", StringComparison.OrdinalIgnoreCase))
                .SelectMany(m => m.Buttons ?? DeckDefaults.Buttons()).ToList()
            : null);
        // Same path as a tap on the dashboard; a toast the button shows ("Microphone muted") goes back to the phone too.
        _web.Press = b => Dispatcher.InvokeAsync(() =>
        {
            _pressToast = null;
            _pressing = true;
            try
            {
                DeckActions.Run(b, this);
            }
            finally
            {
                _pressing = false;
            }
            if (_last != null)
                foreach (var deck in _built.SelectMany(p => p.Modules).OfType<DeckModule>())
                    deck.Update(_last); // the dashboard's deck shows the new state (muted mic…) right away
            return _pressToast;
        }).Task;
    }

    // ---------------------------------------------------------------- monitor placement

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: not in Alt+Tab / taskbar. No-activate: clicking it never steals focus from your game/app.
        long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex = (ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        // Explorer announces a restart (new taskbars) with this; as admin we have to allow it in from non-admin Explorer.
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated != 0)
            NativeMethods.ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // Pause animations while the display is off or the PC is locked (Windows reports the current display state right away).
        var display = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
        _displayNotify = NativeMethods.RegisterPowerSettingNotification(_hwnd, ref display, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_displayNotify == IntPtr.Zero)
            Log.Write($"Display on/off notifications unavailable (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        _sessionNotify = NativeMethods.WTSRegisterSessionNotification(_hwnd, NativeMethods.NOTIFY_FOR_THIS_SESSION);
        if (!_sessionNotify)
            Log.Write($"Lock notifications unavailable (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    uint _taskbarCreated;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // After a DPI change WPF resizes the window by its own rule; snap back to the exact monitor bounds.
        if (msg == NativeMethods.WM_DPICHANGED || msg == NativeMethods.WM_DISPLAYCHANGE)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, Place);
        // Explorer restarted: hide the new taskbar now rather than at the next timer tick.
        else if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated && _config.CoverTaskbar)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, CoverTaskbar);
        // Display off (0) / on (1) / dimmed (2, still visible).
        else if (msg == NativeMethods.WM_POWERBROADCAST && wParam.ToInt64() == NativeMethods.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE && setting.DataLength >= 4)
            {
                bool off = setting.Data == 0;
                Dispatcher.BeginInvoke(() => Motion.Set("display off", off));
            }
        }
        else if (msg == NativeMethods.WM_WTSSESSION_CHANGE && wParam.ToInt64() is NativeMethods.WTS_SESSION_LOCK or NativeMethods.WTS_SESSION_UNLOCK)
        {
            bool locked = wParam.ToInt64() == NativeMethods.WTS_SESSION_LOCK;
            Dispatcher.BeginInvoke(() => Motion.Set("locked", locked));
        }
        return IntPtr.Zero;
    }

    void OnDisplaysChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Place);

    /// <summary>Moves the window to cover the configured monitor exactly, or hides it if that monitor is absent.</summary>
    public void Place()
    {
        var screen = ScreenPlacer.Find(_config.Monitor);
        CurrentScreen = screen;

        if (screen == null)
        {
            if (IsVisible)
            {
                Log.Write($"Monitor '{_config.Monitor}' not found – hiding until it appears.");
                Hide();
            }
            TaskbarHider.Apply(null);
            _retryTimer.Start();
            return;
        }

        _retryTimer.Stop();

        // Create the native window first so it can be moved before it is ever shown (no flash on the main screen).
        if (_hwnd == IntPtr.Zero)
            new WindowInteropHelper(this).EnsureHandle();

        MoveTo(screen);
        if (!IsVisible)
        {
            Show();
            // Showing can apply a DPI change on the new monitor; snap to the exact bounds once more.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MoveTo(screen));
        }
        CoverTaskbar();
    }

    /// <summary>Hides the taskbar on the dashboard's screen (Windows 11 puts one on every screen), or shows it again.</summary>
    void CoverTaskbar()
    {
        TaskbarHider.Apply(_config.CoverTaskbar && IsVisible && CurrentScreen != null ? CurrentScreen.Bounds : null);
        // Explorer recreates its taskbars when it restarts; check now and then while covering.
        if (_config.CoverTaskbar)
            _taskbarTimer.Start();
        else
            _taskbarTimer.Stop();
    }

    void MoveTo(Screen screen)
    {
        // Screen.Bounds are physical pixels (we're per-monitor DPI aware), so use SetWindowPos rather
        // than WPF's Left/Top, which are in DIPs relative to whichever monitor the window was on.
        var b = screen.Bounds;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _retryTimer.Stop();
        _shiftTimer.Stop();
        _cycleTimer.Stop();
        _taskbarTimer.Stop();
        _nightTimer.Stop();
        _wakeTimer.Stop();
        Motion.Changed -= OnMotionChanged;
        if (_displayNotify != IntPtr.Zero)
            NativeMethods.UnregisterPowerSettingNotification(_displayNotify);
        if (_sessionNotify)
            NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
        TaskbarHider.RestoreAll();
        DisposeModules();
        _collector?.Dispose();
        ServiceStore.DisposeAll();
        _mqtt?.Dispose();
        _web?.Dispose();
        base.OnClosed(e);
    }
}
