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
        layers.Children.Add(_cover);
        layers.Children.Add(_animBack);
        layers.Children.Add(root);
        layers.Children.Add(_animFront);
        layers.Children.Add(_crt);
        layers.Children.Add(_dots);
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
        _crt.InvalidateVisual();

        int showing = CurrentPageIndex;
        DisposeModules();
        ServiceStore.BeginBuild();
        _built = _config.Pages.Select(p => (p, p.Modules.Where(m => m.Enabled)
                .Select(m => ModuleFactory.SafeCreate(m, _config, _collector!, this))
                .Where(m => m != null).Select(m => m!).ToList())).ToList();
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
        if (!IsVisible)
            return;

        UpdateModules(s);
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
        for (int r = 0; r <= placement.Max(p => p.Row); r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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
        _swiping = false;
    }

    void OnSwipeMove(object sender, MouseEventArgs e)
    {
        if (_swipeStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _pages.Count < 2)
            return;
        var p = e.GetPosition(this);
        double dx = p.X - start.X, dy = p.Y - start.Y;
        if (!_swiping && Math.Abs(dx) > 24 && Math.Abs(dx) > Math.Abs(dy) * 1.3)
            _swiping = true;
        if (_swiping)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = dx * 0.35; // follow the finger a little
        }
    }

    void OnSwipeEnd(object sender, MouseButtonEventArgs e)
    {
        if (_swipeStart is Point start && _swiping)
        {
            e.Handled = true; // it was a swipe, not a tap on whatever is underneath
            double dx = e.GetPosition(this).X - start.X;
            if (Math.Abs(dx) > 70)
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
        _web?.Dispose();
        base.OnClosed(e);
    }
}
