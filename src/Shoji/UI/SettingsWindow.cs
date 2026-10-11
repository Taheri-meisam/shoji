using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// Settings window (opens on the main screen), in the Windows 11 style: a section list on the left, cards on the right,
/// light / dark following Windows. Every change is written to config.json straight away and the dashboard reloads
/// itself, so what you click is what you see on the small screen.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    static SettingsWindow? _instance;

    // Shoji's own palette (see SettingsPalette); the Fluent controls are pointed at the same colours.
    const string TextPrimary = SettingsPalette.Text1, TextSecondary = SettingsPalette.Text2, TextTertiary = SettingsPalette.Text3;
    const string CardFill = SettingsPalette.Card, CardStroke = SettingsPalette.Stroke;
    const string Subtle = SettingsPalette.Hover, AccentFill = SettingsPalette.Accent, OnAccent = "TextOnAccentFillColorPrimaryBrush";
    static readonly FontFamily IconFont = SettingsIcons.Font;

    readonly Action _exportSensors, _enableCpuSensors;
    readonly Func<IReadOnlyList<string>> _recentApps;
    readonly StackPanel _nav = new() { Margin = new Thickness(12, 0, 12, 8) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Border _saved = new();
    readonly ContentControl _look = new();
    readonly TextBox _search = new();
    StackPanel _panel = new();
    AppConfig _config = new();
    string? _lastWritten;
    string _section = "look";
    int _page;
    (int Page, int Module) _deck = (-1, -1);
    string _themeGroup = "All";
    bool _resetScroll, _dark;

    static readonly (string Id, string Icon, string Name, string Subtitle)[] Sections =
    {
        ("look", "\uE790", "Appearance", "Theme, day and night, and how the dashboard fits the screen"),
        ("weather", "\uE753", "Weather & time", "Where the weather is for, units, and the clock"),
        ("pages", "\uE8A9", "Pages & tiles", "What each page shows, in which order and style"),
        ("scenes", "\uE945", "Scenes", "Switch page or theme by itself: a game starts, music plays, you step away"),
        ("deck", "\uE765", "Control deck", "Touch buttons for apps, hotkeys, audio, power and more"),
        ("alerts", "\uE7BA", "Alerts", "When the dashboard should get your attention"),
        ("phone", "\uE8EA", "Phone dashboard", "See the dashboard on a phone or tablet on your network"),
        ("hardware", "\uE9D9", "Hardware sensors", "CPU temperature, power and motherboard sensors"),
        ("about", "\uE946", "About & install", "Version, installation, files and reset"),
    };

    /// <summary>Per section: icon colours, a kanji for the header, and words the search box finds it by.</summary>
    static readonly Dictionary<string, (string From, string To, string Kanji, string Keywords)> SectionLooks = new()
    {
        ["look"] = ("#FF7A6B", "#D9503A", "外観", "theme colour color accent background tile custom dark light day night display screen monitor refresh text size scale columns animations album art cover backdrop pixel shift burn-in on top"),
        ["weather"] = ("#FFD95C", "#EBA414", "天気", "weather city location town place forecast temperature celsius fahrenheit units clock 24-hour time zone world calendar calendars ical ics webcal google outlook icloud agenda events"),
        ["pages"] = ("#5B9BFF", "#2F5FE0", "頁", "page tile layout preset add widget style gauge columns rotate order share export import code photo frame mirror websites weather"),
        ["scenes"] = ("#B07CFF", "#6D3FDC", "場面", "scene automatic game fullscreen app idle away time music"),
        ["deck"] = ("#FFC15C", "#F0892A", "操作盤", "deck button hotkey keys launch app volume power plan stream shortcut"),
        ["alerts"] = ("#FF6B81", "#D42A46", "警報", "alert warning temperature hot warm orange red colour cpu gpu memory ram drive disk ping website down ssl certificate sound banner"),
        ["phone"] = ("#3FE0C8", "#119C8C", "携帯", "phone web browser tablet qr network port key remote"),
        ["hardware"] = ("#6FE08A", "#2C9A4D", "計器", "sensor cpu gpu graphics card temperature pawnio admin driver fps motherboard fan export"),
        ["about"] = ("#97A2B6", "#5B6579", "障子", "version install uninstall portable update start startup windows boot login config json reset folder files"),
    };

    static SettingsIcons.Look SectionIcon(string id) =>
        new(Sections.First(s => s.Id == id).Icon, SectionLooks[id].From, SectionLooks[id].To);

    /// <summary>Where the next <see cref="Open"/> goes (see <see cref="OpenAt"/>).</summary>
    static (string Section, int Page)? _goTo;
    static Action? _reopen;

    /// <summary>
    /// Opens Settings at a section and page (the dashboard's tile edit sheet: "More settings…"). <paramref name="open"/>
    /// opens it the way the tray does; without it, the way it was last opened. False when neither is known yet.
    /// </summary>
    public static bool OpenAt(string section, int page, Action? open)
    {
        open ??= _reopen;
        if (_instance == null && open == null)
            return false;
        _goTo = (section, page);
        if (open != null)
            open();
        else
            _instance!.TakeGoTo();
        if (_instance is { } w)
        {
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            // Asked for from the dashboard, which never takes focus: at least bring it in front of other windows.
            w.Topmost = true;
            w.Topmost = false;
        }
        return true;
    }

    void TakeGoTo()
    {
        if (_goTo is not { } go)
            return;
        _goTo = null;
        FlushPending();
        _page = go.Page;
        Go(go.Section);
    }

    public static void Open(Action exportSensors, Action enableCpuSensors, Func<IReadOnlyList<string>> recentApps, Func<int> currentPage)
    {
        _reopen = () => Open(exportSensors, enableCpuSensors, recentApps, currentPage);
        if (_instance != null)
        {
            _instance.TakeGoTo();
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(exportSensors, enableCpuSensors, recentApps, currentPage());
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    SettingsWindow(Action exportSensors, Action enableCpuSensors, Func<IReadOnlyList<string>> recentApps, int currentPage)
    {
        _exportSensors = exportSensors;
        _enableCpuSensors = enableCpuSensors;
        _recentApps = recentApps;
        _page = currentPage;
        if (_goTo is { } go)
        {
            _goTo = null;
            (_section, _page) = go;
        }

        _dark = SettingsPalette.IsDark(AppConfig.Load().SettingsLook);
        SettingsPalette.Apply(this, _dark);
        Title = L.T("Shoji settings");
        Icon = SetupWindow.AppIcon();
        // Fit the screen it opens on (a 1366×768 laptop at 125 % has only ~1090×580 to spare).
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1160, area.Width - 40);
        Height = Math.Min(880, area.Height - 40);
        MinWidth = Math.Min(800, Width);
        MinHeight = Math.Min(520, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, SettingsPalette.Bg);
        SetResourceReference(ForegroundProperty, TextPrimary);

        // Left: brand, search, the section list and the light / dark switch.
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 16, 16) };
        brand.Children.Add(new Image { Source = SetupWindow.AppIcon(), Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) });
        var name = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold };
        name.Inlines.Add(new System.Windows.Documents.Run("Shoji"));
        var kanji = new System.Windows.Documents.Run("  障子") { FontSize = 14, FontWeight = FontWeights.Normal };
        kanji.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, TextTertiary);
        name.Inlines.Add(kanji);
        brand.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, Ref(new TextBlock { Text = L.F("Version {0}", AppPaths.VersionText), FontSize = 12 }, TextBlock.ForegroundProperty, TextSecondary) },
        });

        var lookBox = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(14, 8, 14, 16) };
        lookBox.SetResourceReference(Border.BackgroundProperty, CardFill);
        lookBox.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        lookBox.Child = new StackPanel
        {
            Children = { Ref(new TextBlock { Text = L.T("Settings look"), FontSize = 12, Margin = new Thickness(2, 0, 0, 8) }, TextBlock.ForegroundProperty, TextSecondary), _look },
        };

        var navScroll = new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var left = new DockPanel { Width = 268 };
        left.SetResourceReference(Panel.BackgroundProperty, SettingsPalette.Side);
        var search = SearchBox();
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(lookBox, Dock.Bottom);
        left.Children.Add(brand);
        left.Children.Add(search);
        left.Children.Add(lookBox);
        left.Children.Add(navScroll);

        // Right: the section (its header scrolls with it) and the "Saved" toast floating in the corner.
        BuildSavedToast();
        var right = new Grid { Children = { _scroll, _saved } };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        root.Children.Add(left);
        root.Children.Add(right);
        Content = root;

        Rebuild();
        // "Auto" follows Windows when it switches between light and dark.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemColors;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemColors;
        // Closing with X / Alt+F4 doesn't take focus from the box being typed in: save what's typed first.
        Closing += (_, _) => FlushPending();
    }

    void OnSystemColors(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // Fires for many unrelated changes; rebuild only when the look actually flips (a rebuild drops half-typed text).
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)
            Dispatcher.BeginInvoke(() =>
            {
                if (_instance == this && SettingsPalette.IsDark(_config.SettingsLook) != _dark)
                    Rebuild();
            });
    }

    FrameworkElement SearchBox()
    {
        var glyph = Ref(new TextBlock { Text = "\uE721", FontFamily = IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false }, TextBlock.ForegroundProperty, TextTertiary);
        var placeholder = Ref(new TextBlock { Text = L.T("Find a setting"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 0, 1), IsHitTestVisible = false }, TextBlock.ForegroundProperty, TextTertiary);
        _search.Padding = new Thickness(30, 0, 8, 0);
        _search.MinHeight = 34;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = L.T("Type a word like \"temperature\", \"monitor\" or \"hotkey\"; Enter opens the first match");
        _search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildNav(_config);
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Matches().FirstOrDefault() is { } first)
                Go(first);
            else if (e.Key == Key.Escape)
                _search.Text = "";
        };
        return new Grid { Margin = new Thickness(16, 0, 16, 14), Children = { _search, glyph, placeholder } };
    }

    /// <summary>Sections the search box matches (all of them when it's empty): every typed word must appear somewhere.</summary>
    IEnumerable<string> Matches()
    {
        var words = _search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (id, _, name, subtitle) in Sections)
        {
            // English words always work too, whatever the language.
            string keywords = SectionLooks[id].Keywords;
            string hay = $"{name} {subtitle} {keywords} {L.T(name)} {L.T(subtitle)} {L.T(keywords)}".ToLowerInvariant();
            if (words.All(hay.Contains))
                yield return id;
        }
    }

    void BuildSavedToast()
    {
        var check = Ref(new TextBlock { Text = "\uE73E", FontFamily = IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, TextBlock.ForegroundProperty, SettingsPalette.Good);
        var text = new TextBlock { Text = L.T("Saved · the dashboard is updating"), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _saved.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { check, text } };
        _saved.CornerRadius = new CornerRadius(20);
        _saved.Padding = new Thickness(16, 9, 18, 9);
        _saved.BorderThickness = new Thickness(1);
        _saved.Margin = new Thickness(0, 0, 28, 22);
        _saved.HorizontalAlignment = HorizontalAlignment.Right;
        _saved.VerticalAlignment = VerticalAlignment.Bottom;
        _saved.IsHitTestVisible = false;
        _saved.Opacity = 0;
        _saved.RenderTransform = new TranslateTransform();
        _saved.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.3 };
        _saved.SetResourceReference(Border.BackgroundProperty, SettingsPalette.Raised);
        _saved.SetResourceReference(Border.BorderBrushProperty, CardStroke);
    }

    // ================================================================ navigation and saving

    void Rebuild()
    {
        PendingCommits.Clear(); // the boxes are about to be replaced (half-typed text there is dropped, as before)
        var config = _config = AppConfig.Load();
        try { _lastWritten = System.IO.File.ReadAllText(AppConfig.FilePath); } catch { _lastWritten = null; }
        bool dark = SettingsPalette.IsDark(config.SettingsLook);
        if (dark != _dark)
        {
            _dark = dark;
            SettingsPalette.Apply(this, dark);
        }
        _page = Math.Clamp(_page, 0, config.Pages.Count - 1);
        BuildNav(config);
        var looks = new[] { "light", "dark", "auto" };
        _look.Content = Segmented(new[] { "\u2600  " + L.T("Light"), "\u263E  " + L.T("Dark"), L.T("Auto") }, Math.Max(0, Array.IndexOf(looks, (config.SettingsLook ?? "auto").ToLowerInvariant())),
            i => Change(c => c.SettingsLook = looks[i], rebuild: true), stretch: true);

        double offset = _resetScroll ? 0 : _scroll.VerticalOffset;
        _resetScroll = false;
        _panel = new StackPanel { Margin = new Thickness(32, 24, 32, 72), MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Left };
        _panel.Children.Add(Hero(_section, config));
        if (config.IsFallback)
            Banner(L.T("config.json has an error, so Shoji is running on defaults and won't save anything over your file. " +
                   "Fix it (About & install → Edit config.json; a copy is in config.invalid.json) or reset to defaults."));
        switch (_section)
        {
            case "look": AddThemes(config); AddDayNight(config); AddNight(config); AddDisplay(config); AddColors(config); break;
            case "weather": AddWeather(config); break;
            case "pages": AddPages(config); break;
            case "scenes": AddScenes(config); break;
            case "deck": AddDeck(config); AddObs(config); break;
            case "alerts": AddAlerts(config); break;
            case "phone": AddWeb(config); break;
            case "hardware": AddSensors(); break;
            default: AddAbout(); break;
        }
        _scroll.Content = _panel;
        Dispatcher.BeginInvoke(() => _scroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The coloured banner at the top of each section: big icon, title, subtitle and a kanji watermark.</summary>
    FrameworkElement Hero(string id, AppConfig config)
    {
        var (_, _, title, subtitle) = Sections.First(s => s.Id == id);
        var look = SectionLooks[id];
        var icon = SettingsIcons.Tile(SectionIcon(id), 54, shadow: true);
        icon.Margin = new Thickness(0, 0, 18, 0);
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L.T(title), FontSize = 26, FontWeight = FontWeights.Bold },
                Ref(new TextBlock { Text = L.T(subtitle), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }, TextBlock.ForegroundProperty, TextSecondary),
            },
        };
        var row = new DockPanel { Margin = new Thickness(22, 20, 22, 20) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (HeroStatus(id, config) is { } status)
        {
            DockPanel.SetDock(status, Dock.Right);
            status.VerticalAlignment = VerticalAlignment.Center;
            status.Margin = new Thickness(16, 0, 0, 0);
            row.Children.Add(status);
        }
        row.Children.Add(text);

        // A wash of the section colour from the left, fading into the card.
        var tint = Theme.Parse(look.From);
        var wash = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        wash.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(_dark ? 0x38 : 0x30), tint.R, tint.G, tint.B), 0));
        wash.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(_dark ? 0x10 : 0x0C), tint.R, tint.G, tint.B), 0.45));
        wash.GradientStops.Add(new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 0.8));
        var kanji = new TextBlock
        {
            Text = look.Kanji, FontSize = 104, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Yu Gothic UI, Meiryo, Segoe UI"),
            Opacity = _dark ? 0.06 : 0.07, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -12, 24, -40), IsHitTestVisible = false,
        };
        kanji.SetResourceReference(TextBlock.ForegroundProperty, TextPrimary);
        var layers = new Grid { Children = { new Border { Background = Theme.Freeze(wash) }, kanji, row } };
        var hero = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8), Child = layers, ClipToBounds = true };
        hero.SetResourceReference(Border.BackgroundProperty, CardFill);
        hero.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        // Rounded clip so the wash and kanji don't poke out of the corners.
        hero.SizeChanged += (_, e) => layers.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width - 2, e.NewSize.Height - 2), 15, 15);
        return hero;
    }

    /// <summary>A status pill for the section header, where there's something worth knowing at a glance.</summary>
    static FrameworkElement? HeroStatus(string id, AppConfig config) => id switch
    {
        "scenes" => config.ScenesEnabled ? Pill(L.F("{0} active", config.Scenes.Count(s => s.Enabled)), PillKind.Good) : Pill(L.T("Paused"), PillKind.Neutral),
        "alerts" => config.Alerts.Enabled ? Pill(L.T("On"), PillKind.Good) : Pill(L.T("Off"), PillKind.Neutral),
        "phone" => config.Web.Enabled ? Pill(L.T("Serving"), PillKind.Good) : Pill(L.T("Off"), PillKind.Neutral),
        "hardware" => CpuSensorSetup.Ready ? Pill(L.T("All sensors working"), PillKind.Good) : Pill(L.T("Needs setup"), PillKind.Warn),
        "about" => Pill(AppPaths.IsInstalledCopy ? L.T("Installed") : AppPaths.IsPortable ? L.T("Portable") : L.T("Not installed"), AppPaths.IsInstalledCopy ? PillKind.Good : PillKind.Neutral),
        _ => null,
    };

    enum PillKind { Neutral, Good, Warn, Accent }

    static Border Pill(string text, PillKind kind, bool small = false)
    {
        var t = new TextBlock { Text = text, FontSize = small ? 11 : 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border { CornerRadius = new CornerRadius(20), Padding = small ? new Thickness(8, 1, 8, 2) : new Thickness(12, 4, 12, 5), Child = t, VerticalAlignment = VerticalAlignment.Center };
        var (bg, fg) = kind switch
        {
            PillKind.Good => (SettingsPalette.GoodSoft, SettingsPalette.Good),
            PillKind.Warn => (SettingsPalette.WarnSoft, SettingsPalette.Warn),
            PillKind.Accent => (SettingsPalette.AccentSoft, SettingsPalette.AccentText),
            _ => (SettingsPalette.Hover, TextSecondary),
        };
        pill.SetResourceReference(Border.BackgroundProperty, bg);
        t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        return pill;
    }

    void Go(string section)
    {
        _resetScroll = section != _section;
        _section = section;
        Rebuild();
    }

    void BuildNav(AppConfig config)
    {
        _nav.Children.Clear();
        var shown = Matches().ToHashSet();
        foreach (var (id, _, name, _) in Sections)
        {
            if (!shown.Contains(id))
                continue;
            bool selected = id == _section;
            var bar = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-2, 0, 0, 0), Opacity = selected ? 1 : 0 };
            bar.SetResourceReference(Border.BackgroundProperty, AccentFill);
            var icon = SettingsIcons.Tile(SectionIcon(id), 30);
            icon.Margin = new Thickness(8, 0, 12, 0);
            var label = new TextBlock { Text = L.T(name), VerticalAlignment = VerticalAlignment.Center, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            if (NavBadge(id, config) is { } badge)
            {
                DockPanel.SetDock(badge, Dock.Right);
                badge.Margin = new Thickness(6, 0, 8, 0);
                row.Children.Add(badge);
            }
            row.Children.Add(label);
            var item = new Border { Child = new Grid { Children = { row, bar } }, Height = 44, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            if (selected)
                item.SetResourceReference(Border.BackgroundProperty, SettingsPalette.AccentSoft);
            else
            {
                item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, Subtle);
                item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            }
            string target = id;
            Clickable(item, () => Go(target));
            _nav.Children.Add(item);
        }
        if (shown.Count == 0)
            _nav.Children.Add(Ref(new TextBlock { Text = L.T("Nothing matches. Try one word, like \"theme\" or \"fan\"."), TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(10, 8, 10, 0) }, TextBlock.ForegroundProperty, TextSecondary));
    }

    /// <summary>Small state pills in the section list (page count, on / off, needs setup).</summary>
    static FrameworkElement? NavBadge(string id, AppConfig config) => id switch
    {
        "pages" => Pill(config.Pages.Count.ToString(), PillKind.Neutral, small: true),
        "scenes" => config.ScenesEnabled && config.Scenes.Any(s => s.Enabled) ? Pill(L.T("On"), PillKind.Good, small: true) : null,
        "alerts" => config.Alerts.Enabled ? Pill(L.T("On"), PillKind.Good, small: true) : Pill(L.T("Off"), PillKind.Neutral, small: true),
        "phone" => config.Web.Enabled ? Pill(L.T("On"), PillKind.Good, small: true) : null,
        "hardware" => CpuSensorSetup.Ready ? null : Pill(L.T("Setup"), PillKind.Warn, small: true),
        _ => null,
    };

    /// <summary>
    /// The language changed (<see cref="L.Changed"/>): reopen in place, since the window's frame (side bar, search box)
    /// is built once and every section caches its text.
    /// </summary>
    public static void LanguageChanged()
    {
        if (_instance is not { } w || _reopen is not { } reopen)
            return;
        FlushPending();
        PlaceCache.Clear(); // its texts are in the old language
        _goTo = (w._section, w._page);
        var bounds = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : (Rect?)null;
        w.Close();
        reopen();
        if (_instance is { } fresh && bounds is { } b)
        {
            fresh.Left = b.Left;
            fresh.Top = b.Top;
            fresh.Width = b.Width;
            fresh.Height = b.Height;
        }
    }

    /// <summary>Called after config.json changed on disk: rebuild if something other than this window changed it.</summary>
    public static void ConfigChanged()
    {
        var w = _instance;
        if (w == null)
            return;
        string? now = null;
        try { now = System.IO.File.ReadAllText(AppConfig.FilePath); } catch { /* being written */ }
        if (now != w._lastWritten)
            w.Rebuild();
    }

    /// <summary>Saves a change (the dashboard reloads by itself); rebuilds this window when its own layout changes.</summary>
    bool Change(Action<AppConfig> change, bool rebuild = false)
    {
        if (!AppConfig.Edit(change))
        {
            MessageBox.Show(this, L.T("config.json can't be read right now (probably a typo from editing it by hand), so nothing was saved. " +
                                  "Fix the file, or use About & install → Reset everything."), "Shoji", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try { _lastWritten = System.IO.File.ReadAllText(AppConfig.FilePath); } catch { _lastWritten = null; }
        // The toast slides up, stays a moment and fades.
        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.4) };
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18))));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8))));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
        _saved.BeginAnimation(OpacityProperty, flash);
        var slide = new DoubleAnimation(10, 0, TimeSpan.FromSeconds(0.22)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        ((TranslateTransform)_saved.RenderTransform).BeginAnimation(TranslateTransform.YProperty, slide);
        if (rebuild)
            Dispatcher.BeginInvoke(Rebuild);
        return true;
    }

    // ================================================================ Appearance

    void AddThemes(AppConfig config)
    {
        var current = Theme.Find(config.Theme).Id;
        Section(L.T("Theme"), config.DayNight.Enabled
            ? L.T("Day and night themes are on (below), so they decide what you see; this theme is used when they're switched off.")
            : L.T("Applies to the dashboard instantly. Also in the tray menu and on a deck button."));

        var groups = new[] { "All" }.Concat(Themes.All.Select(t => t.Group).Distinct()).ToList();
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var g in groups)
            chips.Children.Add(Chip(g == "All" ? L.F("All  {0}", Themes.All.Count) : L.T(g), g == _themeGroup, () => { _themeGroup = g; Rebuild(); }));
        _panel.Children.Add(chips);

        var wrap = new WrapPanel();
        foreach (var t in Themes.All.Where(t => _themeGroup == "All" || t.Group == _themeGroup))
            wrap.Children.Add(ThemeCard(t, t.Id == current));
        _panel.Children.Add(wrap);
    }

    FrameworkElement ThemeCard(ThemeDef t, bool selected)
    {
        Brush Solid(string c) => Theme.Freeze(new SolidColorBrush(Theme.Parse(c)));
        Brush bg = t.Background2 == null ? Solid(t.Background) : new LinearGradientBrush(Theme.Parse(t.Background), Theme.Parse(t.Background2), 90);

        // A miniature dashboard in the theme's own colours and fonts: a number tile and a gauge tile.
        string title = t.UpperTitles ? "CPU" : "Cpu";
        title = t.Title switch { TitleStyle.Arrow => "▸ " + title, TitleStyle.Slash => "// " + title, TitleStyle.Prompt => "> cpu_", TitleStyle.Kanji => title + "  処理", _ => title };
        Border MiniTile(UIElement content) => new()
        {
            Background = Solid(t.Tile),
            BorderBrush = Solid(t.TileGlow ? t.Accent : t.TileBorder ?? t.Tile),
            BorderThickness = new Thickness(t.TileBorder != null || t.TileGlow ? 1 : 0),
            CornerRadius = new CornerRadius(t.Shape is TileShape.Hud or TileShape.Chamfer or TileShape.Drafting ? 0 : Math.Min(t.Radius, 9)),
            Padding = new Thickness(7, 5, 7, 6),
            Margin = new Thickness(3),
            Child = content,
        };
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        var track = new Rectangle { Fill = Solid(t.Track) };
        Grid.SetColumn(track, 1);
        bar.Children.Add(new Rectangle { Fill = Solid(t.Accent) });
        bar.Children.Add(track);
        var numberTile = MiniTile(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Solid(t.Accent), FontFamily = Theme.Family(t.TitleFont ?? t.Font) },
                new TextBlock { Text = "42%", FontSize = 22, Foreground = Solid(t.Text), FontWeight = t.NumberWeight, FontFamily = Theme.Family(t.DisplayFont ?? t.Font) },
                bar,
            },
        });
        var arcTrack = new Path { Data = Geometry.Parse("M 6,30 A 20,20 0 1 1 46,30"), Stroke = Solid(t.Track), StrokeThickness = t.ThinGauges ? 2 : 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var arcValue = new Path { Data = Geometry.Parse("M 6,30 A 20,20 0 0 1 38,12"), Stroke = Solid(t.Accent2), StrokeThickness = t.ThinGauges ? 2 : 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var gaugeTile = MiniTile(new Grid
        {
            Children =
            {
                new Canvas { Width = 52, Height = 38, Children = { arcTrack, arcValue }, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "68°", FontSize = 11, Foreground = Solid(t.Text), FontFamily = Theme.Family(t.DisplayFont ?? t.Font), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom },
            },
        });
        var minis = new Grid { Margin = new Thickness(8) };
        minis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        minis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(gaugeTile, 1);
        minis.Children.Add(numberTile);
        minis.Children.Add(gaugeTile);

        var preview = new Grid { Height = 118, Children = { new Border { CornerRadius = new CornerRadius(10), Background = bg, ClipToBounds = true, Child = minis } } };
        if (selected)
        {
            // A check badge in the corner of the chosen theme.
            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 7, 0),
                Child = new TextBlock { Text = "\uE73E", FontFamily = IconFont, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = 0.4 },
            };
            badge.SetResourceReference(Border.BackgroundProperty, AccentFill);
            preview.Children.Add(badge);
        }

        var name = new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 9, 6, 0) };
        var desc = Ref(new TextBlock { Text = L.T(t.Description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 1, 6, 6) }, TextBlock.ForegroundProperty, TextSecondary);
        var card = new Border
        {
            Width = 226, Margin = new Thickness(0, 0, 14, 14), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(selected ? 2 : 1), Padding = new Thickness(selected ? 5 : 6),
            Child = new StackPanel { Children = { preview, name, desc } }, RenderTransform = new TranslateTransform(),
        };
        card.SetResourceReference(Border.BackgroundProperty, CardFill);
        card.SetResourceReference(Border.BorderBrushProperty, selected ? AccentFill : CardStroke);
        // Lifts a little under the mouse.
        void Lift(double y) => ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(120)));
        card.MouseEnter += (_, _) =>
        {
            Lift(-3);
            if (!selected) card.SetResourceReference(Border.BorderBrushProperty, TextTertiary);
        };
        card.MouseLeave += (_, _) =>
        {
            Lift(0);
            if (!selected) card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        };
        Clickable(card, () => Change(c => c.Theme = t.Id, rebuild: true));
        return card;
    }

    void AddDayNight(AppConfig config)
    {
        Section(L.T("Day and night"), L.T("Switch theme by the time of day. A scene with its own theme still wins."));
        var dn = config.DayNight;
        var names = Themes.All.Select(t => t.Name).ToList();
        int IndexOf(string id) => Math.Max(0, Themes.All.ToList().FindIndex(t => t.Id == Theme.Find(id).Id));
        var card = Card();
        Setting(card, L.T("Use day and night themes"), null, Switch(dn.Enabled, v => Change(c => c.DayNight.Enabled = v, rebuild: true)), icon: "\uE793");
        Setting(card, L.T("Day theme"), L.F("from {0}", dn.DayStarts), Combo(names, IndexOf(dn.DayTheme), i => Change(c => c.DayNight.DayTheme = Themes.All[i].Id)), icon: "\uE706");
        Setting(card, L.T("Night theme"), L.F("from {0}", dn.NightStarts), Combo(names, IndexOf(dn.NightTheme), i => Change(c => c.DayNight.NightTheme = Themes.All[i].Id)), icon: "\uE708");
        Setting(card, L.T("Day starts"), L.T("24-hour time"), ClockField(dn.DayStarts, 90, v => Change(c => c.DayNight.DayStarts = v ?? "07:00"), optional: true), icon: "\uE823");
        Setting(card, L.T("Night starts"), L.T("24-hour time"), ClockField(dn.NightStarts, 90, v => Change(c => c.DayNight.NightStarts = v ?? "19:30"), optional: true), last: true, icon: "\uE823");
    }

    void AddNight(AppConfig config)
    {
        Section(L.T("Night mode"), L.T("Dims the dashboard at night. A tap brings it back to full brightness for a while; that first tap only wakes it."));
        var night = config.Night;
        var card = Card();
        Setting(card, L.T("Dim at night"), null, Switch(night.Enabled, v => Change(c => c.Night.Enabled = v)), icon: "\uE708");
        Setting(card, L.T("From"), L.T("24-hour time"), ClockField(night.From, 90, v => Change(c => c.Night.From = v ?? "23:00"), optional: true), icon: "\uE823");
        Setting(card, L.T("To"), L.T("24-hour time"), ClockField(night.To, 90, v => Change(c => c.Night.To = v ?? "07:00"), optional: true), icon: "\uE823");
        Setting(card, L.T("Brightness"), L.T("0 % = black, like the screen is off (animations pause too)"),
            PercentSlider(night.Brightness, v => Change(c => c.Night.Brightness = v)), icon: "\uE706");
        Setting(card, L.T("Wake for"), L.T("seconds after a tap"), TextField(night.WakeSeconds.ToString(CultureInfo.CurrentCulture), 90, v => Change(c => c.Night.WakeSeconds = int.Parse(v!, CultureInfo.CurrentCulture)),
            check: v => int.TryParse(v ?? "", NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) && n is >= 5 and <= 3600
                ? (true, n.ToString(CultureInfo.CurrentCulture))
                : (false, null),
            invalidHint: L.T("Type a number of seconds from 5 to 3600: this wasn't saved")), last: true, icon: "\uE916");
    }

    /// <summary>A 0–100 % slider with its value beside it; saves once you stop moving it (every save reloads the dashboard).</summary>
    static FrameworkElement PercentSlider(int value, Action<int> save)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Clamp(value, 0, 100), Width = 180, TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        int saved = (int)slider.Value;
        void Show() => label.Text = $"{slider.Value:0} %";
        void Commit()
        {
            wait.Stop();
            PendingCommits.Remove(Commit);
            if ((int)slider.Value != saved)
                save(saved = (int)slider.Value);
        }
        wait.Tick += (_, _) => Commit();
        slider.ValueChanged += (_, _) =>
        {
            Show();
            PendingCommits.Add(Commit); // closing the window before the pause still saves it
            wait.Stop();
            wait.Start();
        };
        Show();
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { label, slider } };
    }

    void AddDisplay(AppConfig config)
    {
        Section(L.T("Display"), null);
        var card = Card();
        var monitorValues = new List<string> { "auto" };
        var monitorNames = new List<string> { L.T("Auto (smallest secondary screen)") };
        foreach (var s in ScreenPlacer.Ordered())
        {
            monitorValues.Add(s.DeviceName);
            monitorNames.Add(ScreenPlacer.Describe(s));
        }
        if (!monitorValues.Contains(config.Monitor, StringComparer.OrdinalIgnoreCase))
        {
            monitorValues.Add(config.Monitor);
            monitorNames.Add(config.Monitor);
        }
        int monitorIndex = monitorValues.FindIndex(v => v.Equals(config.Monitor, StringComparison.OrdinalIgnoreCase));
        Setting(card, L.T("Show on"), L.T("Which screen the dashboard fills"), Combo(monitorNames, monitorIndex, i => Change(c => c.Monitor = monitorValues[i])), icon: "\uE7F4");
        var refresh = new[] { 500, 1000, 2000, 5000 };
        Setting(card, L.T("Refresh every"), null, Segmented(new[] { "0.5 s", "1 s", "2 s", "5 s" },
            Nearest(refresh.Select(r => (double)r).ToList(), config.RefreshMs), i => Change(c => c.RefreshMs = refresh[i])), icon: "\uE895");
        var scales = new[] { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5 };
        Setting(card, L.T("Text size"), L.T("On top of the automatic sizing"), Combo(scales.Select(s => $"{s * 100:0}%").ToArray(), Nearest(scales, config.Scale), i => Change(c => c.Scale = scales[i])), icon: "\uE8D2");
        Setting(card, L.T("Columns"), L.T("Pages can override this"), Combo(new[] { L.T("Automatic"), "1", "2", "3", "4", "5", "6" }, Math.Clamp(config.Columns, 0, 6), i => Change(c => c.Columns = i)), icon: "\uE8A9");
        Setting(card, L.T("Animations"), L.T("Drifting petals, aurora, neon flicker, scan lines"), Switch(config.Animations, v => Change(c => c.Animations = v)), icon: "\uE945");
        Setting(card, L.T("Album art backdrop"), L.T("The playing song's cover, blurred, behind a page with Now playing"), Switch(config.AlbumArtBackdrop, v => Change(c => c.AlbumArtBackdrop = v)), icon: "\uE93C");
        Setting(card, L.T("Pixel shift"), L.T("Moves the layout a few pixels now and then against OLED burn-in"), Switch(config.PixelShift, v => Change(c => c.PixelShift = v)), icon: "\uE7A1");
        Setting(card, L.T("Cover the taskbar"), L.T("Hides the taskbar on the dashboard's screen so it fills it completely (your main taskbar stays)"),
            Switch(config.CoverTaskbar, v => Change(c => c.CoverTaskbar = v)), icon: "\uE740");
        Setting(card, L.T("Always on top"), null, Switch(config.Topmost, v => Change(c => c.Topmost = v)), icon: "\uE718");
        // Language names stay in their own language, so they can be found whatever the current one is.
        int language = Math.Max(0, L.Languages.ToList().FindIndex(x => x.Id.Equals(config.Language, StringComparison.OrdinalIgnoreCase)));
        Setting(card, L.T("Language"), L.T("Dashboard, Settings and tray menu. Automatic follows Windows."),
            Combo(L.Languages.Select(x => x.Id == "auto" ? L.T(x.Name) : x.Name).ToList(), language, i =>
            {
                if (Change(c => c.Language = L.Languages[i].Id))
                    L.Apply(L.Languages[i].Id); // reopens this window in the new language (LanguageChanged)
            }), last: true, icon: "\uE774");
    }

    /// <summary>Theme colour overrides (AccentColor, BackgroundColor, TileColor in config.json).</summary>
    void AddColors(AppConfig config)
    {
        Section(L.T("Custom colours"), L.T("Change a colour of the theme. \"Theme\" keeps the theme's own; or type any #RRGGBB."));
        var card = Card();
        ColorRow(card, L.T("Accent"), L.T("Gauges, bars and titles"), "\uE790", config.AccentColor, (c, v) => c.AccentColor = v,
            new[] { "#4FC3F7", "#7C4DFF", "#FF4081", "#FF6E40", "#FFD740", "#69F0AE", "#18FFFF", "#FFFFFF" }, last: false);
        ColorRow(card, L.T("Background"), L.T("Behind the tiles"), "\uE8B9", config.BackgroundColor, (c, v) => c.BackgroundColor = v,
            new[] { "#000000", "#0B0E14", "#101820", "#1A1423", "#14201A", "#201A14", "#F2EEE6", "#E8EEF5" }, last: false);
        ColorRow(card, L.T("Tiles"), L.T("The tiles themselves"), "\uECA5", config.TileColor, (c, v) => c.TileColor = v,
            new[] { "#151A24", "#1E1E1E", "#0F2027", "#24182E", "#1B2A20", "#2A2118", "#FFFFFF", "#DDE5EE" }, last: true);
    }

    /// <summary>A colour setting: label, then swatches, "Theme" (no override) and a hex box.</summary>
    void ColorRow(Border card, string label, string description, string icon, string? current, Action<AppConfig, string?> set, string[] swatches, bool last)
    {
        var inner = (StackPanel)card.Child;
        var head = new DockPanel { Margin = new Thickness(0, 12, 0, 6) };
        var glyph = Ref(new TextBlock { Text = icon, FontFamily = IconFont, FontSize = 17, Width = 22, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }, TextBlock.ForegroundProperty, TextSecondary);
        DockPanel.SetDock(glyph, Dock.Left);
        head.Children.Add(glyph);
        head.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label },
                Ref(new TextBlock { Text = description, FontSize = 12 }, TextBlock.ForegroundProperty, TextSecondary),
            },
        });
        inner.Children.Add(head);

        var row = new WrapPanel { Margin = new Thickness(36, 0, 0, 12) };
        bool IsCurrent(string? hex) => string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
        var themeChip = Chip(L.T("Theme"), current == null, () => Change(c => set(c, null), rebuild: true));
        themeChip.Margin = new Thickness(0, 0, 10, 6);
        row.Children.Add(themeChip);
        foreach (var hex in swatches)
        {
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = hex,
                Background = Theme.Freeze(new SolidColorBrush(Theme.Parse(hex))), BorderThickness = new Thickness(IsCurrent(hex) ? 3 : 1),
            };
            dot.SetResourceReference(Border.BorderBrushProperty, IsCurrent(hex) ? AccentFill : CardStroke);
            string pick = hex;
            Clickable(dot, () => Change(c => set(c, pick), rebuild: true));
            row.Children.Add(dot);
        }
        var box = TextField(current, 92, v =>
        {
            // Only valid colours are saved; anything else puts the box back.
            if (v == null) Change(c => set(c, null), rebuild: true);
            else if (TryColor(v) is { } ok) Change(c => set(c, ok), rebuild: true);
            else Dispatcher.BeginInvoke(Rebuild);
        }, "#RRGGBB");
        box.Margin = new Thickness(6, 0, 0, 6);
        row.Children.Add(box);
        inner.Children.Add(row);
        if (!last)
            inner.Children.Add(Ref(new Border { Height = 1 }, Border.BackgroundProperty, SettingsPalette.Divider));
    }

    /// <summary>"#abc", "aabbcc" or "#AABBCC" → "#AABBCC"; null if it isn't a colour.</summary>
    static string? TryColor(string text)
    {
        text = text.Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(text);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch
        {
            return null;
        }
    }

    // ================================================================ Weather & time

    static readonly Dictionary<string, (string Text, bool Ok)> PlaceCache = new(StringComparer.OrdinalIgnoreCase);
    int _lookup;

    void AddWeather(AppConfig config)
    {
        Section(L.T("Location"), L.T("Every weather tile, and clocks that show the weather, use this place."));
        var card = Card();
        var resultGlyph = new TextBlock { FontFamily = IconFont, FontSize = 13, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var picker = CityPicker(config.City, 260, v =>
        {
            if (Change(c => c.City = v))
                Lookup(v, result, resultGlyph);
        });
        var here = Link(L.T("Use my location"), () => Change(c => c.City = null, rebuild: true));
        here.Margin = new Thickness(12, 0, 0, 0);
        here.Visibility = string.IsNullOrWhiteSpace(config.City) ? Visibility.Collapsed : Visibility.Visible;
        Setting(card, L.T("City"), L.T("Start typing and pick your place from the list. Empty = Windows location."),
            new StackPanel { Orientation = Orientation.Horizontal, Children = { picker, here } }, icon: "\uE707");
        var status = new DockPanel { Margin = new Thickness(36, 0, 0, 14) };
        DockPanel.SetDock(resultGlyph, Dock.Left);
        status.Children.Add(resultGlyph);
        status.Children.Add(result);
        ((StackPanel)card.Child).Children.Add(status);
        Lookup(config.City, result, resultGlyph);

        // Tiles that set a city of their own win over the shared one.
        var own = config.Pages.SelectMany((pg, pi) => pg.Modules.Select((m, mi) => (pg, pi, m, mi)))
            .Where(x => x.m.Type is "weather" or "forecast" or "clock" && !string.IsNullOrWhiteSpace(x.m.City)).ToList();
        if (own.Count > 0)
        {
            Section(L.T("Tiles with their own city"), L.T("These win over the location above. The dashboard fetches one forecast, so the first of them sets the weather for all tiles."));
            var tiles = Card();
            for (int i = 0; i < own.Count; i++)
            {
                var (pg, pi, m, mi) = own[i];
                string name = ModuleCatalog.Find(m.Type)?.Name ?? m.Type;
                string uses = Sensors.WeatherService.Label(m.City);
                Setting(tiles, L.F("{0} on “{1}”", L.T(name), pg.Name), m.Enabled ? L.F("uses {0}", uses) : L.F("uses {0} (tile is switched off)", uses),
                    Button(L.T("Use the location above"), () => Change(c => c.Pages[pi].Modules[mi].City = null, rebuild: true)),
                    last: i == own.Count - 1, icon: SettingsIcons.ForTile(m.Type).Glyph);
            }
        }

        Section(L.T("Units and clock"), null);
        var units = Card();
        Setting(units, L.T("Temperatures"), L.T("Weather and PC sensors"), Segmented(new[] { "°C", "°F" }, config.Fahrenheit ? 1 : 0, i => Change(c => c.TemperatureUnit = i == 1 ? "F" : "C")), icon: "\uE9CA");
        Setting(units, L.T("24-hour clock"), null, Switch(config.Use24HourClock, v => Change(c => c.Use24HourClock = v)), last: true, icon: "\uE823");
        AddCalendars(config);

        // World clock cities are edited on the tile; offer a shortcut.
        var world = config.Pages.SelectMany((pg, pi) => pg.Modules.Select(m => (pi, m))).FirstOrDefault(x => x.m.Type == "worldclock");
        if (world.m != null)
        {
            Section(L.T("World clock"), null);
            var wc = Card();
            var zones = world.m.Zones is { Count: > 0 } z ? z : Modules.WorldClockModule.Defaults.ToList();
            Setting(wc, L.T("Cities"), string.Join(" · ", zones.Select(x => x.Label ?? ZoneName(x.Zone))),
                Button(L.T("Edit…"), () => { _page = world.pi; Go("pages"); }), last: true, icon: "\uE909");
        }
    }

    /// <summary>
    /// A city box that searches as you type and lists matching places ("Paris – Île-de-France, France", "Paris – Texas,
    /// United States"); picking one saves its name and exact position. Typed text that isn't picked is saved as is
    /// (looked up by name later). Empty = null.
    /// </summary>
    FrameworkElement CityPicker(string? value, double width, Action<string?> save)
    {
        var box = new TextBox { Text = Sensors.WeatherService.Label(value), Width = width, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("Type a city, then pick it from the list (or use lat,lon)") };
        string committed = box.Text;
        // The list is a popup (its own window), so it gets its colours directly.
        Brush Res(string key) => (Brush)FindResource(key);
        var list = new StackPanel();
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = box, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = Res(SettingsPalette.Card), BorderBrush = Res(SettingsPalette.Stroke), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4), Margin = new Thickness(0, 4, 12, 12), MinWidth = width, MaxWidth = 480, Child = list,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.35 },
            },
        };
        var results = new List<Sensors.WeatherService.Place>();
        var rows = new List<Border>();
        int highlighted = -1;
        bool picking = false;
        System.Threading.CancellationTokenSource? cts = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

        void Paint()
        {
            for (int i = 0; i < rows.Count; i++)
                rows[i].Background = i == highlighted ? Res(SettingsPalette.AccentSoft) : Brushes.Transparent;
        }
        void Pick(Sensors.WeatherService.Place p)
        {
            // Stop a pending or running search, so it can't reopen the list over the finished box.
            timer.Stop();
            cts?.Cancel();
            popup.IsOpen = false;
            picking = true;
            box.Text = p.FullName;
            box.CaretIndex = box.Text.Length;
            picking = false;
            committed = box.Text;
            save(Sensors.WeatherService.Store(p));
        }
        void Message(string text)
        {
            list.Children.Clear();
            rows.Clear();
            results.Clear();
            highlighted = -1;
            list.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Res(SettingsPalette.Text2), Margin = new Thickness(10, 8, 10, 8), TextWrapping = TextWrapping.Wrap });
            popup.IsOpen = true;
        }
        async void Search()
        {
            timer.Stop();
            string query = box.Text.Trim();
            cts?.Cancel();
            if (query.Length < 2)
            {
                popup.IsOpen = false;
                return;
            }
            var mine = cts = new System.Threading.CancellationTokenSource();
            IReadOnlyList<Sensors.WeatherService.Place> found;
            try
            {
                found = await Sensors.WeatherService.SearchAsync(query, mine.Token);
            }
            catch (OperationCanceledException) when (mine.IsCancellationRequested)
            {
                return; // replaced by a newer search
            }
            catch (Exception ex)
            {
                // Includes HttpClient's timeout, which also shows up as a cancellation.
                Log.Write($"City search failed: {ex.Message}");
                if (mine == cts && box.IsKeyboardFocused)
                    Message(L.T("Couldn't search right now (offline?). You can still type the name and press Enter."));
                return;
            }
            if (mine != cts || !box.IsKeyboardFocused)
                return;
            if (found.Count == 0)
            {
                Message(L.F("No place called “{0}”. Check the spelling, or try a bigger town nearby.", query));
                return;
            }
            list.Children.Clear();
            rows.Clear();
            results = found.ToList();
            highlighted = 0;
            foreach (var p in results)
            {
                string where = p.FullName.Length > p.Name.Length ? p.FullName[(p.Name.Length + 2)..] : "";
                var line = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res(SettingsPalette.Text1) };
                line.Inlines.Add(new System.Windows.Documents.Run(p.Name) { FontWeight = FontWeights.SemiBold });
                if (where.Length > 0)
                    line.Inlines.Add(new System.Windows.Documents.Run("  " + where) { Foreground = Res(SettingsPalette.Text2) });
                var coords = new TextBlock { Text = FormattableString.Invariant($"{p.Lat:0.00}, {p.Lon:0.00}"), FontSize = 11, Foreground = Res(SettingsPalette.Text3), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var dock = new DockPanel();
                DockPanel.SetDock(coords, Dock.Right);
                dock.Children.Add(coords);
                dock.Children.Add(line);
                var row = new Border { Child = dock, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                int index = rows.Count;
                var place = p;
                row.MouseEnter += (_, _) => { highlighted = index; Paint(); };
                // Picks on press, like a dropdown, without taking focus from the text box.
                row.MouseLeftButtonDown += (_, e) => { e.Handled = true; Pick(place); };
                rows.Add(row);
                list.Children.Add(row);
            }
            Paint();
            popup.IsOpen = true;
        }

        timer.Tick += (_, _) => Search();
        box.Unloaded += (_, _) => { timer.Stop(); cts?.Cancel(); };
        box.TextChanged += (_, _) =>
        {
            if (picking || box.Text == committed)
                return;
            // The list belongs to the old text: Enter now saves what's typed unless a new list arrives.
            popup.IsOpen = false;
            results.Clear();
            highlighted = -1;
            timer.Stop();
            timer.Start();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (popup.IsOpen && rows.Count > 0 && e.Key is Key.Down or Key.Up)
            {
                highlighted = Math.Clamp(highlighted + (e.Key == Key.Down ? 1 : -1), 0, rows.Count - 1);
                Paint();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (popup.IsOpen && highlighted >= 0 && highlighted < results.Count)
                    Pick(results[highlighted]);
                else if (box.Text != committed)
                {
                    committed = box.Text;
                    popup.IsOpen = false;
                    save(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
                }
            }
            else if (e.Key == Key.Escape && popup.IsOpen)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };
        void CommitTyped()
        {
            PendingCommits.Remove(CommitTyped);
            if (box.Text == committed)
                return;
            committed = box.Text;
            save(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        }
        box.TextChanged += (_, _) =>
        {
            if (!picking && box.Text != committed)
                PendingCommits.Add(CommitTyped);
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            timer.Stop();
            popup.IsOpen = false;
            CommitTyped();
        };
        return box;
    }

    /// <summary>Checks a city (or Windows location when empty) and shows what the weather will be for.</summary>
    async void Lookup(string? city, TextBlock result, TextBlock glyph)
    {
        string key = (city ?? "").Trim();
        int ticket = ++_lookup;
        void Show(string text, string? state)
        {
            result.Text = text;
            glyph.Text = state switch { "ok" => "\uE73E", "warn" => "\uE7BA", _ => "\uE721" };
            string brush = state switch { "ok" => SettingsPalette.Good, "warn" => SettingsPalette.Warn, _ => TextSecondary };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
            result.SetResourceReference(TextBlock.ForegroundProperty, state == null ? TextSecondary : TextPrimary);
        }
        if (PlaceCache.TryGetValue(key, out var cached))
        {
            Show(cached.Text, cached.Ok ? "ok" : "warn");
            return;
        }
        Show(key.Length == 0 ? L.T("Checking Windows location…") : L.F("Looking up “{0}”…", key), null);
        string text;
        bool ok;
        try
        {
            var place = key.Length == 0 ? await Sensors.WeatherService.WindowsPlaceAsync() : await Sensors.WeatherService.GeocodeAsync(key);
            ok = place != null;
            text = place != null ? L.F("Weather for {0}", place.FullName)
                : key.Length == 0 ? L.T("Windows location is off or not allowed for desktop apps. Type a city above, or allow it in Windows Settings → Privacy & security → Location.")
                : L.F("Couldn't find “{0}”. Check the spelling, or add the country (“{0}, US”).", key);
            if (ok || key.Length > 0)
                PlaceCache[key] = (text, ok);
        }
        catch (Exception ex)
        {
            Log.Write($"Place lookup failed: {ex.Message}");
            ok = false;
            text = L.T("Couldn't check right now (offline?). The dashboard keeps trying by itself.");
        }
        if (ticket == _lookup)
            Show(text, ok ? "ok" : "warn");
    }

    static string ZoneName(string id) =>
        id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ') : id.Replace(" Standard Time", "");

    static int Nearest(IReadOnlyList<double> options, double value) =>
        options.Select((o, i) => (d: Math.Abs(o - value), i)).OrderBy(x => x.d).First().i;

    // ================================================================ Pages & tiles

    /// <summary>"Page N" with the first number from the page count up that no page uses yet.</summary>
    static string NewPageName(AppConfig c)
    {
        for (int n = c.Pages.Count + 1; ; n++)
        {
            string name = $"Page {n}";
            if (!c.Pages.Any(x => x.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    /// <summary>Renames a page and the scenes and deck "page" buttons that point at it by its old name.</summary>
    static void RenamePage(AppConfig c, int index, string name)
    {
        string old = c.Pages[index].Name;
        // Only references that actually lead to this page (a duplicate name resolves to the first page with it).
        bool referenced = old.Trim().Length > 0 && c.PageIndex(old) == index;
        c.Pages[index].Name = name;
        if (!referenced || old.Trim().Equals(name.Trim(), StringComparison.Ordinal))
            return;
        bool Same(string? target) => target != null && !int.TryParse(target, out _) && target.Trim().Equals(old.Trim(), StringComparison.OrdinalIgnoreCase);
        foreach (var scene in c.Scenes)
            if (Same(scene.Page))
                scene.Page = name;
        foreach (var m in c.AllModules)
            foreach (var b in m.Buttons ?? new List<DeckButton>())
                if (b != null && string.Equals(b.Action, "page", StringComparison.OrdinalIgnoreCase) && Same(b.Target))
                    b.Target = name;
    }

    void AddPages(AppConfig config)
    {
        // Page picker: one pill per page, plus "New page".
        var pills = new WrapPanel { Margin = new Thickness(0, 16, 0, 10) };
        for (int i = 0; i < config.Pages.Count; i++)
        {
            int index = i;
            pills.Children.Add(Chip($"{i + 1}   {config.Pages[i].Name}", i == _page, () => { _page = index; Rebuild(); }));
        }
        var newPage = Chip("+   " + L.T("New page"), false, () =>
        {
            if (Change(c => c.Pages.Add(new PageConfig { Name = NewPageName(c), Modules = ModuleCatalog.Preset("essentials") })))
                _page = config.Pages.Count;
            Rebuild();
        });
        ((TextBlock)newPage.Child).SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.AccentText);
        pills.Children.Add(newPage);
        _panel.Children.Add(pills);

        var page = config.Pages[_page];
        int p = _page;

        // Preview and page settings side by side.
        // Side by side when there's room, one above the other in a narrow window.
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
        var previewCard = Card(addToPanel: false);
        previewCard.Margin = new Thickness(0, 0, 12, 10);
        previewCard.VerticalAlignment = VerticalAlignment.Top;
        previewCard.Padding = new Thickness(18, 16, 18, 14);
        ((StackPanel)previewCard.Child).Children.Add(Ref(new TextBlock { Text = L.T("ON THE SCREEN"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }, TextBlock.ForegroundProperty, TextTertiary));
        ((StackPanel)previewCard.Child).Children.Add(PagePreview(config, page));
        top.Children.Add(previewCard);

        var settings = Card(addToPanel: false);
        settings.Width = 400; // with the preview (at most ~390) it fits side by side in the default window
        settings.VerticalAlignment = VerticalAlignment.Top;
        // No rebuild here: it would run on focus loss and swallow the click that caused it.
        Setting(settings, L.T("Page name"), null, TextField(page.Name, 170, v => Change(c => RenamePage(c, p, v ?? "Page"))), icon: "\uE70F");
        var columns = Combo(new[] { L.T("Same as Display"), "1", "2", "3", "4", "5", "6" }, Math.Clamp(page.Columns, 0, 6), i => Change(c => c.Pages[p].Columns = i, rebuild: true));
        columns.MinWidth = 170;
        Setting(settings, L.T("Columns"), null, columns, icon: "\uE8A9");
        var cycle = new[] { 0, 15, 30, 60, 120, 300 };
        var rotate = Combo(new[] { L.T("Off"), L.T("every 15 s"), L.T("every 30 s"), L.T("every minute"), L.T("every 2 minutes"), L.T("every 5 minutes") },
            Math.Max(0, Array.IndexOf(cycle, config.PageCycleSeconds)), i => Change(c => c.PageCycleSeconds = cycle[i]));
        rotate.MinWidth = 170;
        Setting(settings, L.T("Rotate pages"), L.T("All pages, when no scene is active"), rotate, icon: "\uE895");
        Setting(settings, L.T("Hold a tile to edit it on the dashboard"), L.T("Move, resize, restyle or hide it right there"),
            Switch(config.LongPressEdit, v => Change(c => c.LongPressEdit = v)), icon: "\uE7C9");
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        var left = IconButton("\uE72B", L.T("Move this page left"), () => { if (Change(c => Swap(c.Pages, p, p - 1))) _page--; Rebuild(); });
        left.IsEnabled = p > 0;
        var right = IconButton("\uE72A", L.T("Move this page right"), () => { if (Change(c => Swap(c.Pages, p, p + 1))) _page++; Rebuild(); });
        right.IsEnabled = p < config.Pages.Count - 1;
        var delete = IconButton("\uE74D", L.T("Delete this page"), () =>
        {
            if (MessageBox.Show(this, L.F("Delete the page \"{0}\" and its tiles?", page.Name), "Shoji", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            if (Change(c => c.Pages.RemoveAt(p)))
                _page = Math.Max(0, p - 1);
            Rebuild();
        }, danger: true);
        delete.IsEnabled = config.Pages.Count > 1;
        actions.Children.Add(Ref(new TextBlock { Text = L.T("Page"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, TextBlock.ForegroundProperty, TextSecondary));
        actions.Children.Add(left);
        actions.Children.Add(right);
        actions.Children.Add(delete);
        ((StackPanel)settings.Child).Children.Add(Ref(new Border { Height = 1 }, Border.BackgroundProperty, SettingsPalette.Divider));
        ((StackPanel)settings.Child).Children.Add(actions);
        top.Children.Add(settings);
        _panel.Children.Add(top);

        // Presets.
        AddShare(config); // export / import of the whole layout (SettingsShare.cs)

        Section(L.T("Layout presets"), L.T("Replaces this page's layout; tiles a preset doesn't use stay on the page, switched off."));
        var presets = new WrapPanel();
        foreach (var (id, name, description) in ModuleCatalog.Presets)
            presets.Children.Add(PresetCard(id, name, description, () => Change(c => ModuleCatalog.ApplyPreset(c.Pages[p], id), rebuild: true)));
        _panel.Children.Add(presets);

        // Tiles on this page.
        Section(L.F("Tiles on \"{0}\"", page.Name), L.T("Switch a tile off to hide it without losing its settings. Arrows change the order; tiles fill rows left to right."));
        for (int i = 0; i < page.Modules.Count; i++)
            _panel.Children.Add(TileRow(p, page.Modules[i], i, page.Modules.Count));
        if (page.Modules.Count == 0)
            _panel.Children.Add(Hint(L.T("No tiles yet: add some below, or pick a preset.")));

        // Add a tile.
        Section(L.T("Add a tile"), null);
        var gallery = new WrapPanel();
        foreach (var info in ModuleCatalog.All)
        {
            var type = info.Type;
            var existing = page.Modules.FindIndex(m => m.Type == type && !m.Enabled);
            var icon = SettingsIcons.Tile(SettingsIcons.ForTile(type), 38);
            icon.Margin = new Thickness(0, 0, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.Inlines.Add(L.T(info.Name));
            var kanji = new System.Windows.Documents.Run("  " + info.Kanji) { FontWeight = FontWeights.Normal, FontSize = 12 };
            kanji.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, TextTertiary);
            title.Inlines.Add(kanji);
            var desc = Ref(new TextBlock { Text = L.T(info.Description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Height = 48, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 8) },
                TextBlock.ForegroundProperty, TextSecondary);
            var add = Button(existing >= 0 ? L.T("Show again") : "+  " + L.T("Add"), () =>
            {
                int added = existing >= 0 ? existing : page.Modules.Count;
                bool ok = Change(c =>
                {
                    var mods = c.Pages[p].Modules;
                    int hidden = mods.FindIndex(m => m.Type == type && !m.Enabled);
                    if (hidden >= 0) mods[hidden].Enabled = true;
                    else mods.Add(ModuleCatalog.New(type));
                }, rebuild: true);
                // A new mirror tile needs something to show: pick it right away.
                if (ok && type == "mirror" && existing < 0)
                    Dispatcher.BeginInvoke(() => PickMirror(p, added), System.Windows.Threading.DispatcherPriority.Background);
            });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0);
            var text = new StackPanel { Children = { title, desc, add } };
            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            dock.Children.Add(text);
            gallery.Children.Add(HoverCard(dock, 246, new Thickness(14, 14, 14, 14)));
        }
        _panel.Children.Add(gallery);
    }

    /// <summary>A card that outlines itself under the mouse.</summary>
    static Border HoverCard(UIElement child, double width, Thickness padding)
    {
        var card = new Border { Width = width, Padding = padding, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, CardFill);
        card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, TextTertiary);
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        return card;
    }

    /// <summary>A layout preset: the tile icons it brings, its name and what's in it. Click to apply.</summary>
    static Border PresetCard(string id, string name, string description, Action apply)
    {
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var type in ModuleCatalog.Preset(id).Where(m => m.Enabled).Select(m => m.Type).Distinct().Take(6))
        {
            var t = SettingsIcons.Tile(SettingsIcons.ForTile(type), 22);
            t.Margin = new Thickness(0, 0, 4, 0);
            icons.Children.Add(t);
        }
        var title = new DockPanel();
        if (id == "main")
        {
            var pill = Pill(L.T("Best start"), PillKind.Accent, small: true);
            DockPanel.SetDock(pill, Dock.Right);
            title.Children.Add(pill);
        }
        title.Children.Add(new TextBlock { Text = L.T(name), FontWeight = FontWeights.SemiBold });
        var body = new StackPanel
        {
            Children =
            {
                icons, title,
                Ref(new TextBlock { Text = L.T(description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Height = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) }, TextBlock.ForegroundProperty, TextSecondary),
            },
        };
        var card = HoverCard(body, 200, new Thickness(14, 12, 14, 12));
        card.Cursor = Cursors.Hand;
        card.ToolTip = L.T("Use this layout on this page");
        Clickable(card, apply);
        return card;
    }

    /// <summary>How the page's tiles will sit on the dashboard screen, using the dashboard's own layout rules.</summary>
    static FrameworkElement PagePreview(AppConfig config, PageConfig page)
    {
        var screen = ScreenPlacer.Find(config.Monitor) ?? System.Windows.Forms.Screen.PrimaryScreen;
        double sw = screen?.Bounds.Width ?? 1920, sh = screen?.Bounds.Height ?? 1080, aspect = sw / sh;
        // Same rules as the dashboard: DIPs (physical pixels / display scale) minus its margins, readable-text floor, overflow screens.
        double scale = screen == null ? 1 : NativeMethods.MonitorScale(screen.Bounds);
        var all = page.Modules.Where(m => m.Enabled && ModuleCatalog.Find(m.Type) != null).ToList();
        var plans = TileLayout.Plan(all.Select(m => m.Span).ToList(), sw / scale - 12, sh / scale - 12,
            page.Columns > 0 ? page.Columns : config.Columns, scale, config.Scale);
        var first = plans[0];
        int cols = first.Columns;
        var tiles = all.Skip(first.Start).Take(first.Count).ToList();
        var spans = tiles.Select(m => m.Span).ToList();
        var placement = TileLayout.Pack(spans, cols);
        double tileW = (sw / scale - 12) / first.Scale / cols;
        int rowsN = placement.Count == 0 ? 1 : placement.Max(x => x.Row) + 1;
        double tileH = (sh / scale - 12) / first.Scale / rowsN;
        var density = Responsive.For(tileW, tileH);

        double height = aspect >= 1 ? Math.Min(200, 340 / aspect) : 260, width = height * aspect;
        var grid = new Grid { Width = width, Height = height };
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = placement.Count == 0 ? 1 : placement.Max(x => x.Row) + 1;
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < tiles.Count; i++)
        {
            var name = L.T(ModuleCatalog.Find(tiles[i].Type)?.Name ?? tiles[i].Type);
            // Each tile in its family colour, with its icon, like the gallery below.
            var look = SettingsIcons.ForTile(tiles[i].Type);
            var c1 = Theme.Parse(look.From);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
            label.Children.Add(new TextBlock { Text = look.Glyph, FontFamily = IconFont, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.9 });
            label.Children.Add(new TextBlock { Text = tiles[i].Title ?? name, FontSize = 10.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            var cell = new Border
            {
                Margin = new Thickness(2), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = label,
                Background = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x48, c1.R, c1.G, c1.B))),
                BorderBrush = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0xA0, c1.R, c1.G, c1.B))),
            };
            Grid.SetRow(cell, placement[i].Row);
            Grid.SetColumn(cell, placement[i].Col);
            Grid.SetColumnSpan(cell, placement[i].Span);
            grid.Children.Add(cell);
        }
        // A monitor bezel around it.
        var screenFrame = new Border
        {
            Padding = new Thickness(5), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = grid, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x40)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.35 },
        };

        string how = density switch
        {
            Density.Compact => L.T("tiles show their compact view (main reading only)"),
            Density.Roomy => L.T("tiles have room for every detail"),
            _ => L.T("tiles show their usual detail"),
        };
        var info = new StackPanel { Margin = new Thickness(0, 10, 0, 0), MaxWidth = Math.Max(width + 12, 280), HorizontalAlignment = HorizontalAlignment.Left };
        info.Children.Add(Ref(new TextBlock { Text = L.F("{0:0}×{1:0} at {2:0}% · {3} · text {4:0} px · {5}", sw, sh, scale * 100, cols == 1 ? L.T("1 column") : L.F("{0} columns", cols), first.TextPx(scale), how), FontSize = 12, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, TextSecondary));
        if (plans.Count > 1)
        {
            int moved = all.Count - first.Count;
            var warn = new TextBlock
            {
                Text = L.F("⚠  Too many tiles to read on this screen: the last {0} move to {1} (swipe to them). " +
                           "Use fewer tiles, or put some on their own page, to see them all at once.",
                    moved, plans.Count == 2 ? L.T("a second screen") : L.F("{0} more screens", plans.Count - 1)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.Warn);
            info.Children.Add(warn);
        }
        return new StackPanel { Children = { screenFrame, info } };
    }

    FrameworkElement TileRow(int page, ModuleConfig m, int index, int count)
    {
        var info = ModuleCatalog.Find(m.Type);
        void Edit(Action<ModuleConfig> change, bool rebuild = false) => Change(c => change(c.Pages[page].Modules[index]), rebuild);

        var toggle = Switch(m.Enabled, on => Edit(x => x.Enabled = on, rebuild: true));
        toggle.ToolTip = L.T("Show this tile");
        toggle.Margin = new Thickness(14, 0, 2, 0);

        var icon = SettingsIcons.Tile(SettingsIcons.ForTile(m.Type), 36);
        icon.Margin = new Thickness(0, 0, 14, 0);
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(L.T(info?.Name ?? m.Type));
        if (!string.IsNullOrWhiteSpace(m.Title))
        {
            var custom = new System.Windows.Documents.Run($"  “{m.Title}”") { FontWeight = FontWeights.Normal };
            custom.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, SettingsPalette.AccentText);
            title.Inlines.Add(custom);
        }
        name.Children.Add(title);
        name.Children.Add(Ref(new TextBlock { Text = info != null ? L.T(info.Description) : L.T("Unknown tile type – check config.json"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, TextBlock.ForegroundProperty, TextSecondary));

        // Second line: style, width and order. It wraps, so nothing is cut off in a narrow window.
        var controls = new WrapPanel { Margin = new Thickness(50, 10, 0, 0) };
        if (info is { Styles.Length: > 1 })
        {
            string themeDefault = Theme.StyleFor(m.Type, null);
            var names = new List<string> { L.F("Theme ({0})", L.T(info.Styles.First(st => st.Id == themeDefault).Name)) };
            names.AddRange(info.Styles.Select(st => L.T(st.Name)));
            int current = m.Opt("Style") is { } chosen ? Array.FindIndex(info.Styles, st => st.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase)) + 1 : 0;
            var style = Combo(names, Math.Max(0, current), i => Edit(x => x.SetOpt("Style", i == 0 ? null : info.Styles[i - 1].Id)));
            style.MinWidth = 170;
            style.ToolTip = L.T("Style");
            style.Margin = new Thickness(0, 0, 16, 6);
            controls.Children.Add(Labeled2(L.T("Style"), style));
        }
        var width = Segmented(new[] { L.T("1 col"), L.T("2 col"), L.T("Full") }, m.Span >= 3 ? 2 : Math.Max(0, m.Span - 1), i => Edit(x => x.Span = new[] { 1, 2, 9 }[i], rebuild: true));
        width.ToolTip = L.T("Width: 1 column, 2 columns or the full row");
        width.Margin = new Thickness(0, 0, 16, 6);
        controls.Children.Add(Labeled2(L.T("Width"), width));
        var up = IconButton("\uE70E", L.T("Move up"), () => Change(c => Swap(c.Pages[page].Modules, index, index - 1), rebuild: true));
        var down = IconButton("\uE70D", L.T("Move down"), () => Change(c => Swap(c.Pages[page].Modules, index, index + 1), rebuild: true));
        var remove = IconButton("\uE74D", L.T("Remove from this page (its settings are forgotten)"), () => Change(c => c.Pages[page].Modules.RemoveAt(index), rebuild: true), danger: true);
        up.IsEnabled = index > 0;
        down.IsEnabled = index < count - 1;
        var order = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { up, down, remove } };
        controls.Children.Add(order);

        var header = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);
        header.Children.Add(toggle);
        header.Children.Add(name);
        if (!m.Enabled)
        {
            icon.Opacity = 0.4;
            name.Opacity = 0.55;
        }

        // Everything else folds away under "Options".
        var options = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        options.Children.Add(Field(L.T("Title"), m.Title, L.T("optional custom title"), v => Edit(x => x.Title = v)));
        // Options with their own editor on the tile (below) aren't repeated as text boxes.
        var ownEditor = m.Type switch
        {
            "worldclock" => new[] { "Zones" },
            "history" => new[] { "Series", "Hours" },
            _ => Array.Empty<string>(),
        };
        foreach (var option in (info?.OptionList ?? Array.Empty<OptionSpec>()).Where(o => !ownEditor.Contains(o.Key)))
        {
            if (option.Kind == OptionKind.Bool)
            {
                var box = Check(L.T(option.Hint), option.Get(m) is { } v && (v == "true" || v == "1"), on => Edit(x => option.Set(x, on ? "true" : null)));
                box.Margin = new Thickness(0, 8, 0, 2);
                options.Children.Add(box);
            }
            else if (option.Key == "City")
            {
                var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                row.Children.Add(new TextBlock { Text = L.T(option.Label), VerticalAlignment = VerticalAlignment.Center, Width = 120 });
                var picker = CityPicker(m.City, 300, v => Edit(x => x.City = v, rebuild: true));
                picker.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(picker);
                row.Children.Add(Ref(new TextBlock { Text = L.T("only to differ from Weather & time; clear it to use the shared location"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 }, TextBlock.ForegroundProperty, TextSecondary));
                options.Children.Add(row);
            }
            else
            {
                options.Children.Add(Field(L.T(option.Label), option.Get(m), L.T(option.Hint), v => Edit(x => option.Set(x, v)), option.Kind == OptionKind.Secret));
            }
        }
        if (m.Type == "deck")
        {
            var edit = Button(L.T("Edit buttons…"), () => { _deck = (page, index); Go("deck"); });
            edit.HorizontalAlignment = HorizontalAlignment.Left;
            edit.Margin = new Thickness(0, 10, 0, 0);
            options.Children.Add(edit);
        }
        var expander = new Expander { Header = L.T("Options"), Content = options, Margin = new Thickness(50, 2, 0, 0) };

        var body = new StackPanel { Children = { header, controls } };
        if (m.Type == "mirror")
        {
            // What it shows, and a picker instead of typing names or coordinates.
            string current = m.Opt("Screen") is { } scr ? (scr == "main" ? L.T("the main screen") : L.F("screen {0}", scr))
                : m.Opt("Window") is { } win ? (m.Opt("Crop") != null ? L.F("“{0}” (cropped)", win) : $"“{win}”")
                : m.Opt("Region") is { } reg ? L.F("a screen area ({0})", reg) : L.T("nothing yet");
            var pick = Button(L.T("Choose what to mirror…"), () => PickMirror(page, index));
            pick.SetResourceReference(StyleProperty, "AccentButtonStyle");
            var mirrorRow = new WrapPanel { Margin = new Thickness(50, 10, 0, 0) };
            mirrorRow.Children.Add(pick);
            mirrorRow.Children.Add(Ref(new TextBlock { Text = L.F("Showing {0}", current), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 6) }, TextBlock.ForegroundProperty, TextSecondary));
            body.Children.Add(mirrorRow);
        }
        if (m.Type == "websites")
            body.Children.Add(SitesEditor(m, Edit));
        if (m.Type == "worldclock")
            body.Children.Add(ZonesEditor(m, Edit));
        if (m.Type == "history")
            body.Children.Add(SeriesEditor(m, Edit));
        if (m.Type is "weather" or "forecast" || m.Type == "clock" && !m.OptBool("HideWeather"))
        {
            // Where its weather comes from, with a way to change it.
            var cfg = _config;
            string where = !string.IsNullOrWhiteSpace(m.City) ? L.F("{0} (this tile's own city)", Sensors.WeatherService.Label(m.City))
                : !string.IsNullOrWhiteSpace(cfg.City) ? Sensors.WeatherService.Label(cfg.City) : L.T("Windows location");
            var line = new WrapPanel { Margin = new Thickness(50, 8, 0, 0) };
            line.Children.Add(Ref(new TextBlock { Text = "\uE707", FontFamily = IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, TextBlock.ForegroundProperty, TextSecondary));
            line.Children.Add(Ref(new TextBlock { Text = L.F("Weather for {0}", where), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, TextBlock.ForegroundProperty, TextSecondary));
            line.Children.Add(Link(L.T("Change location"), () => Go("weather")));
            body.Children.Add(line);
        }
        body.Children.Add(expander);
        var card = new Border { Padding = new Thickness(14, 12, 14, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, CardFill);
        card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        return card;
    }

    /// <summary>The sites of a Websites tile: one row each (address, name, optional text the page must contain).</summary>
    FrameworkElement SitesEditor(ModuleConfig m, Action<Action<ModuleConfig>, bool> edit)
    {
        var sites = m.Sites ?? new List<SiteRef>();
        var panel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var (text, col) in new[] { ("Address", 0), ("Name (optional)", 1), ("Page must contain (optional)", 2) })
        {
            var t = Ref(new TextBlock { Text = L.T(text), FontSize = 12 }, TextBlock.ForegroundProperty, TextSecondary);
            Grid.SetColumn(t, col);
            head.Children.Add(t);
        }
        foreach (var w in new[] { 270.0, 150, 190, 40 })
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        if (sites.Count > 0)
            panel.Children.Add(head);

        for (int i = 0; i < sites.Count; i++)
        {
            int index = i;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var w in new[] { 270.0, 150, 190, 40 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            var url = TextField(sites[i].Url, 260, v => edit(x =>
            {
                // "example.com" is fine; it becomes https://example.com/
                if (x.Sites != null && index < x.Sites.Count)
                    x.Sites[index].Url = Sensors.WebsiteMonitor.Normalize(v)?.ToString() ?? v ?? "";
            }, false), "https://example.com");
            var name = TextField(sites[i].Name, 140, v => edit(x => { if (x.Sites != null && index < x.Sites.Count) x.Sites[index].Name = v; }, false));
            var contains = TextField(sites[i].Contains, 180, v => edit(x => { if (x.Sites != null && index < x.Sites.Count) x.Sites[index].Contains = v; }, false));
            var remove = IconButton("\uE74D", L.T("Remove this site"), () => edit(x => x.Sites?.RemoveAt(index), true));
            Grid.SetColumn(name, 1);
            Grid.SetColumn(contains, 2);
            Grid.SetColumn(remove, 3);
            row.Children.Add(url);
            row.Children.Add(name);
            row.Children.Add(contains);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }
        var add = Button("+  " + L.T("Add a site"), () => edit(x => (x.Sites ??= new List<SiteRef>()).Add(new SiteRef { Url = "https://" }), true));
        add.Padding = new Thickness(12, 5, 12, 6);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 6, 0, 0);
        if (sites.Count == 0)
            add.SetResourceReference(StyleProperty, "AccentButtonStyle");
        panel.Children.Add(add);
        return panel;
    }

    /// <summary>The cities of a World clock tile: a time zone list and an optional name per row.</summary>
    FrameworkElement ZonesEditor(ModuleConfig m, Action<Action<ModuleConfig>, bool> edit)
    {
        var zones = m.Zones is { Count: > 0 } z ? z : Modules.WorldClockModule.Defaults.ToList();
        var all = TimeZoneInfo.GetSystemTimeZones();
        // Saving the first change also saves the default cities, which were only implied before.
        void EditZones(Action<List<ZoneRef>> change, bool rebuild) => edit(x =>
        {
            x.Zones = x.Zones is { Count: > 0 } ? x.Zones : Modules.WorldClockModule.Defaults.Select(d => new ZoneRef { Zone = d.Zone, Label = d.Label }).ToList();
            change(x.Zones);
        }, rebuild);

        var panel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
        for (int i = 0; i < zones.Count; i++)
        {
            int index = i;
            string id = zones[i].Zone;
            if (!all.Any(t => t.Id == id) && TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win))
                id = win;
            var names = all.Select(t => t.DisplayName).ToList();
            int selected = all.ToList().FindIndex(t => t.Id == id);
            if (selected < 0)
            {
                names.Insert(0, L.F("{0} (unknown)", zones[i].Zone));
                selected = 0;
            }
            int offset = names.Count - all.Count;
            var zone = Combo(names, selected, n =>
            {
                if (n - offset >= 0)
                    EditZones(list => { if (index < list.Count) list[index].Zone = all[n - offset].Id; }, false);
            });
            zone.MinWidth = 0;
            zone.Width = 300;
            var label = TextField(zones[i].Label, 140, v => EditZones(list => { if (index < list.Count) list[index].Label = v; }, false),
                L.T("Name shown on the tile (empty = the zone's city)"));
            label.Margin = new Thickness(8, 0, 0, 0);
            var remove = IconButton("\uE74D", L.T("Remove this city"), () => EditZones(list => { if (index < list.Count) list.RemoveAt(index); }, true), danger: true);
            remove.IsEnabled = zones.Count > 1;
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Children = { zone, label, remove } });
        }
        var add = Button("+  " + L.T("Add a city"), () => EditZones(list => list.Add(new ZoneRef { Zone = TimeZoneInfo.Local.Id }), true));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(add);
        return panel;
    }

    /// <summary>History graphs: one chip per series, and how many hours back.</summary>
    FrameworkElement SeriesEditor(ModuleConfig m, Action<Action<ModuleConfig>, bool> edit)
    {
        var chosen = m.OptList("Series");
        if (chosen.Count == 0)
            chosen = new List<string> { "cpu.temp", "gpu.temp" };
        var panel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
        panel.Children.Add(Ref(new TextBlock { Text = L.T("Graphs"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) }, TextBlock.ForegroundProperty, TextSecondary));
        var chips = new WrapPanel();
        foreach (var (key, name) in Sensors.HistoryStore.Names)
        {
            bool on = chosen.Contains(key);
            string k = key;
            chips.Children.Add(Chip((on ? "✓  " : "") + L.T(name), on, () =>
            {
                var next = on ? chosen.Where(x => x != k).ToList() : chosen.Append(k).ToList();
                if (next.Count > 0)
                    edit(x => x.SetOpt("Series", string.Join(", ", next)), true);
            }));
        }
        panel.Children.Add(chips);
        var hours = new[] { 1, 3, 6, 12, 24 };
        int current = Nearest(hours.Select(h => (double)h).ToList(), Math.Clamp(m.OptInt("Hours", 24), 1, 24));
        var span = Segmented(hours.Select(h => L.F("{0} h", h)).ToArray(), current, i => edit(x => x.SetOpt("Hours", hours[i] == 24 ? null : hours[i].ToString()), false));
        span.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(Labeled2(L.T("Show the last"), span));
        return panel;
    }

    /// <summary>Text that acts like a link (accent colour, underlined under the mouse).</summary>
    static TextBlock Link(string text, Action click)
    {
        var t = new TextBlock { Text = text, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.AccentText);
        t.MouseEnter += (_, _) => t.TextDecorations = TextDecorations.Underline;
        t.MouseLeave += (_, _) => t.TextDecorations = null;
        Clickable(t, click);
        return t;
    }

    /// <summary>Opens the mirror picker for a mirror tile and saves the choice (replacing whatever it showed before).</summary>
    void PickMirror(int page, int index)
    {
        var own = ScreenPlacer.Find(AppConfig.Load().Monitor)?.DeviceName;
        var choice = MirrorPicker.Ask(this, own);
        if (choice == null)
            return;
        Change(c =>
        {
            var x = c.Pages[page].Modules[index];
            x.SetOpt("Screen", choice.Screen);
            x.SetOpt("Window", choice.Window);
            x.SetOpt("Region", choice.Region);
            x.SetOpt("Crop", choice.Crop);
        }, rebuild: true);
    }

    static void Swap<T>(List<T> list, int a, int b)
    {
        if (a < 0 || b < 0 || a >= list.Count || b >= list.Count)
            return;
        (list[a], list[b]) = (list[b], list[a]);
    }

    // ================================================================ Scenes

    void AddScenes(AppConfig config)
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 10);
        Setting(card, L.T("Automatic scenes"), L.T("Also in the tray menu and on a deck button"), Switch(config.ScenesEnabled, v => Change(c => c.ScenesEnabled = v, rebuild: true)), last: true, icon: "\uE945");

        var recent = _recentApps();
        _panel.Children.Add(Hint(recent.Count == 0
            ? L.T("All conditions you fill in must match; the first matching scene wins. When none matches, you're back on the page you were on.")
            : L.F("Recently used apps (for the app fields): {0}", string.Join(", ", recent))));

        var pageNames = new List<string> { L.T("(stay on current page)") };
        pageNames.AddRange(config.Pages.Select(p => p.Name));
        var themeNames = new List<string> { L.T("(keep theme)") };
        themeNames.AddRange(Themes.All.Select(t => t.Name));
        for (int i = 0; i < config.Scenes.Count; i++)
            _panel.Children.Add(SceneCard(config, config.Scenes[i], i, pageNames, themeNames));

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var add = AccentButton("+  " + L.T("New scene"), () => Change(c => c.Scenes.Add(new SceneConfig { Name = $"Scene {c.Scenes.Count + 1}" }), rebuild: true));
        row.Children.Add(add);
        row.Children.Add(Button(L.T("Add the example scenes"), () => Change(c => c.Scenes.AddRange(AppConfig.DefaultScenes().Where(d => !c.Scenes.Any(s => s.Name == d.Name))), rebuild: true)));
        _panel.Children.Add(row);
    }

    FrameworkElement SceneCard(AppConfig config, SceneConfig sc, int i, List<string> pageNames, List<string> themeNames)
    {
        void Edit(Action<SceneConfig> change, bool rebuild = false) => Change(c => change(c.Scenes[i]), rebuild);
        static string? List(List<string>? l) => l == null || l.Count == 0 ? null : string.Join(", ", l);
        static List<string>? Parse(string? s) => s?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() is { Count: > 0 } l ? l : null;

        var card = Card(addToPanel: false); // AddScenes adds it
        card.Padding = new Thickness(20, 14, 20, 6);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var up = IconButton("\uE70E", L.T("Move up (earlier scenes win)"), () => Change(c => Swap(c.Scenes, i, i - 1), rebuild: true));
        var down = IconButton("\uE70D", L.T("Move down"), () => Change(c => Swap(c.Scenes, i, i + 1), rebuild: true));
        up.IsEnabled = i > 0;
        down.IsEnabled = i < config.Scenes.Count - 1;
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        buttons.Children.Add(IconButton("\uE74D", L.T("Delete scene"), () => Change(c => c.Scenes.RemoveAt(i), rebuild: true), danger: true));
        var enabled = Switch(sc.Enabled, v => Edit(x => x.Enabled = v, rebuild: true));
        enabled.ToolTip = L.T("Use this scene");
        enabled.Margin = new Thickness(14, 0, 2, 0);
        buttons.Children.Add(enabled);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var icon = SettingsIcons.Tile(SectionIcon("scenes"), 34);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.Opacity = sc.Enabled ? 1 : 0.4;
        head.Children.Add(icon);
        var name = TextField(sc.Name, 240, v => Edit(x => x.Name = v ?? "Scene"));
        name.FontWeight = FontWeights.SemiBold;
        name.HorizontalAlignment = HorizontalAlignment.Left;
        name.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(name);
        ((StackPanel)card.Child).Children.Add(head);

        Setting(card, L.T("When the app in focus is"), "spotify, *launcher*, \\steamapps\\", TextField(List(sc.Apps), 300, v => Edit(x => x.Apps = Parse(v))), icon: "\uE71D");
        Setting(card, L.T("…or it's full screen"), L.T("games, full-screen video"), Switch(sc.Fullscreen, v => Edit(x => x.Fullscreen = v)), icon: "\uE740");
        Setting(card, L.T("Except these apps"), L.T("never trigger for them"), TextField(List(sc.ExceptApps), 300, v => Edit(x => x.ExceptApps = Parse(v))), icon: "\uE711");
        Setting(card, L.T("Away for (minutes)"), L.T("no keyboard or mouse"), TextField(sc.IdleMinutes?.ToString(), 90, v => Edit(x => x.IdleMinutes = int.TryParse(v, out int n) && n > 0 ? n : null)), icon: "\uE708");
        var time = new StackPanel { Orientation = Orientation.Horizontal };
        time.Children.Add(ClockField(sc.From, 80, v => Edit(x => x.From = v), optional: true));
        time.Children.Add(new TextBlock { Text = "  " + L.T("to") + "  ", VerticalAlignment = VerticalAlignment.Center });
        time.Children.Add(ClockField(sc.To, 80, v => Edit(x => x.To = v), optional: true));
        Setting(card, L.T("Between"), L.T("e.g. 22:00 to 06:00"), time, icon: "\uE823");
        int pageIndex = string.IsNullOrWhiteSpace(sc.Page) ? 0 : config.PageIndex(sc.Page) + 1;
        Setting(card, L.T("Then show page"), null, Combo(pageNames, Math.Max(0, pageIndex), x => Edit(s => s.Page = x == 0 ? null : pageNames[x])), icon: "\uE8A9");
        int themeIndex = string.IsNullOrWhiteSpace(sc.Theme) ? 0 : Themes.All.ToList().FindIndex(t => t.Id == Theme.Find(sc.Theme).Id) + 1;
        Setting(card, L.T("…and use theme"), null, Combo(themeNames, themeIndex, t => Edit(s => s.Theme = t == 0 ? null : Themes.All[t - 1].Id)), last: true, icon: "\uE790");
        return card;
    }

    // ================================================================ Control deck

    static readonly (string Label, DeckButton Button)[] Examples =
    {
        ("Discord: mute (set the hotkey in Discord)", new DeckButton { Icon = "mic", Label = "Discord mute", Action = "keys", Target = "Ctrl+Shift+M" }),
        ("OBS: start / stop recording", new DeckButton { Icon = "record", Label = "Record", Action = "obs", Target = "record" }),
        ("OBS: go live / end stream", new DeckButton { Icon = "live", Label = "Stream", Action = "obs", Target = "stream" }),
        ("OBS: switch to a scene", new DeckButton { Icon = "video", Label = "Gaming", Action = "obs", Target = "scene:Gaming" }),
        ("OBS: mute the microphone input", new DeckButton { Icon = "mic", Label = "OBS mic", Action = "obs", Target = "mute:Mic/Aux" }),
        ("OBS: save a replay", new DeckButton { Icon = "download", Label = "Replay", Action = "obs", Target = "replay" }),
        ("OBS: virtual camera on / off", new DeckButton { Icon = "camera", Label = "Virtual cam", Action = "obs", Target = "virtualcam" }),
        ("Snipping Tool", new DeckButton { Icon = "camera", Label = "Snip", Action = "screenshot", Target = "snip" }),
        ("Steam", new DeckButton { Icon = "game", Label = "Steam", Action = "url", Target = "steam://open/main" }),
        ("Spotify", new DeckButton { Icon = "music", Label = "Spotify", Action = "url", Target = "spotify:" }),
        ("Calculator", new DeckButton { Icon = "calc", Label = "Calculator", Action = "launch", Target = "calc.exe" }),
        ("Terminal", new DeckButton { Icon = "terminal", Label = "Terminal", Action = "launch", Target = "wt.exe" }),
        ("Website", new DeckButton { Icon = "web", Label = "YouTube", Action = "url", Target = "https://youtube.com" }),
        ("Power plan: high performance", new DeckButton { Icon = "bolt", Label = "Performance", Action = "powerplan", Target = "high" }),
        ("Power plan: balanced", new DeckButton { Icon = "power", Label = "Balanced", Action = "powerplan", Target = "balanced" }),
        ("Show desktop", new DeckButton { Icon = "home", Label = "Desktop", Action = "keys", Target = "Win+D" }),
        ("Go to Gaming page", new DeckButton { Icon = "game", Label = "Gaming", Action = "page", Target = "Gaming" }),
        ("Volume up", new DeckButton { Icon = "volume", Label = "Vol +", Action = "volume", Target = "+5" }),
        ("Sleep PC", new DeckButton { Icon = "sleep", Label = "Sleep", Action = "sleep" }),
        ("Run a PowerShell script", new DeckButton { Icon = "code", Label = "Script", Action = "powershell", Target = "C:\\Scripts\\example.ps1" }),
    };

    void AddDeck(AppConfig config)
    {
        var decks = config.Pages.SelectMany((p, pi) => p.Modules.Select((m, mi) => (p, pi, m, mi))).Where(x => x.m.Type == "deck").ToList();
        if (decks.Count == 0)
        {
            _panel.Children.Add(Hint(L.T("There's no control deck yet.")));
            var add = AccentButton(L.T("Add a \"Deck\" page with a control deck"), () => Change(c => c.Pages.Add(new PageConfig { Name = "Deck", Modules = ModuleCatalog.Preset("deck") }), rebuild: true));
            add.HorizontalAlignment = HorizontalAlignment.Left;
            _panel.Children.Add(add);
            return;
        }
        if (!decks.Any(d => (d.pi, d.mi) == _deck))
            _deck = (decks[0].pi, decks[0].mi);
        if (decks.Count > 1)
        {
            var pick = Combo(decks.Select(d => $"{d.p.Name}: {d.m.Title ?? L.T("Control deck")} #{d.mi + 1}").ToList(), decks.FindIndex(d => (d.pi, d.mi) == _deck), i =>
            {
                _deck = (decks[i].pi, decks[i].mi);
                Dispatcher.BeginInvoke(Rebuild);
            });
            pick.Margin = new Thickness(0, 16, 0, 4);
            pick.HorizontalAlignment = HorizontalAlignment.Left;
            _panel.Children.Add(pick);
        }

        var (pageIndex, moduleIndex) = _deck;
        var buttons = config.Pages[pageIndex].Modules[moduleIndex].Buttons ?? new List<DeckButton>();
        void EditButtons(Action<List<DeckButton>> change, bool rebuild = false) => Change(c =>
        {
            var m = c.Pages[pageIndex].Modules[moduleIndex];
            m.Buttons ??= new List<DeckButton>();
            change(m.Buttons);
        }, rebuild);

        // What the deck looks like: its buttons as touch keys.
        if (buttons.Count > 0)
        {
            var keys = new WrapPanel();
            foreach (var b in buttons)
                keys.Children.Add(DeckKey(b));
            var deckCard = Card();
            deckCard.Padding = new Thickness(18, 16, 12, 10);
            ((StackPanel)deckCard.Child).Children.Add(Ref(new TextBlock { Text = L.T("ON THE DECK"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }, TextBlock.ForegroundProperty, TextTertiary));
            ((StackPanel)deckCard.Child).Children.Add(keys);
            deckCard.Margin = new Thickness(0, 16, 0, 4);
        }
        _panel.Children.Add(Hint(L.T("Keys go to the app in focus: the dashboard never takes focus, so it works mid-game. " +
                                 "A game running as admin only gets hotkeys if Shoji runs as admin too.")));
        for (int i = 0; i < buttons.Count; i++)
            _panel.Children.Add(DeckRow(buttons[i], i, buttons.Count, EditButtons));

        var add2 = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var plus = AccentButton("+  " + L.T("Add button"), () => EditButtons(b => b.Add(new DeckButton { Icon = "app", Label = "New", Action = "launch", Target = "notepad.exe" }), rebuild: true));
        add2.Children.Add(plus);
        var examples = Combo(new[] { L.T("Add an example…") }.Concat(Examples.Select(e => L.T(e.Label))).ToList(), 0, i =>
        {
            if (i == 0) return;
            var e = Examples[i - 1].Button;
            EditButtons(b => b.Add(new DeckButton { Icon = e.Icon, Label = e.Label, Action = e.Action, Target = e.Target, Args = e.Args }), rebuild: true);
        });
        examples.Margin = new Thickness(0, 0, 8, 6);
        add2.Children.Add(examples);
        add2.Children.Add(Button(L.T("Reset to default buttons"), () =>
        {
            if (MessageBox.Show(this, L.T("Replace these buttons with the default set?"), "Shoji", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                EditButtons(b => { b.Clear(); b.AddRange(DeckDefaults.Buttons()); }, rebuild: true);
        }));
        _panel.Children.Add(add2);
        _panel.Children.Add(Hint(L.F("Icon names: {0}. Or any emoji / short text.", string.Join(", ", DeckActions.Icons.Keys.OrderBy(k => k)))));
    }

    FrameworkElement DeckRow(DeckButton b, int i, int count, Action<Action<List<DeckButton>>, bool> edit)
    {
        void Set(Action<DeckButton> change, bool rebuild = false) => edit(list => change(list[i]), rebuild);

        var preview = new TextBlock { FontSize = 20, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        preview.SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.AccentText);
        SetPreview(preview, b.Icon);
        var key = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = preview, Margin = new Thickness(0, 0, 4, 4), VerticalAlignment = VerticalAlignment.Top };
        key.SetResourceReference(Border.BackgroundProperty, SettingsPalette.AccentSoft);
        key.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        key.Cursor = Cursors.Hand;
        key.ToolTip = L.T("Pick an icon");
        Clickable(key, () => IconPicker(key, name => Set(x => x.Icon = name, rebuild: true)));
        var icon = TextField(b.Icon, 96, v => { Set(x => x.Icon = v ?? ""); SetPreview(preview, v); });
        var label = TextField(b.Label, 140, v => Set(x => x.Label = v ?? ""));
        var actions = DeckActions.All.ToList();
        int actionIndex = actions.FindIndex(a => a.Id.Equals(b.Action, StringComparison.OrdinalIgnoreCase));
        var action = Combo(actions.Select(a => L.T(a.Name)).ToList(), actionIndex, a => Set(x => x.Action = actions[a].Id, rebuild: true));
        action.MinWidth = 210;
        string hint = actionIndex >= 0 ? actions[actionIndex].TargetHint : "";

        var top = new WrapPanel();
        top.Children.Add(Labeled(L.T("Icon"), icon));
        top.Children.Add(Labeled(L.T("Label"), label));
        top.Children.Add(Labeled(L.T("Does"), action));
        var details = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        if (hint != "(nothing)")
            details.Children.Add(Field(L.T("Target"), b.Target, L.T(hint), v => Set(x => x.Target = v)));
        if (b.Action == "launch")
            details.Children.Add(Field(L.T("Arguments"), b.Args, L.T("optional"), v => Set(x => x.Args = v)));

        var up = IconButton("\uE70E", L.T("Move up"), () => edit(list => Swap(list, i, i - 1), true));
        var down = IconButton("\uE70D", L.T("Move down"), () => edit(list => Swap(list, i, i + 1), true));
        up.IsEnabled = i > 0;
        down.IsEnabled = i < count - 1;
        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Children = { up, down, IconButton("\uE74D", L.T("Delete button"), () => edit(list => list.RemoveAt(i), true), danger: true) } };
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        DockPanel.SetDock(key, Dock.Left);
        dock.Children.Add(side);
        dock.Children.Add(key);
        dock.Children.Add(new StackPanel { Children = { top, details } });

        var card = new Border { Padding = new Thickness(14, 12, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = dock };
        card.SetResourceReference(Border.BackgroundProperty, CardFill);
        card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        return card;
    }

    /// <summary>A grid of the built-in icons under <paramref name="anchor"/>; picking one calls <paramref name="pick"/> with its name.</summary>
    void IconPicker(FrameworkElement anchor, Action<string> pick)
    {
        // The popup is its own window, so it gets the colours directly rather than through resource references.
        Brush Res(string key) => (Brush)FindResource(key);
        var grid = new WrapPanel { Width = 9 * 44 };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
        foreach (var (name, glyph) in DeckActions.Icons.OrderBy(i => i.Key))
        {
            var cell = new Border
            {
                Width = 40, Height = 40, Margin = new Thickness(2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = name,
                Child = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 18, Foreground = Res(SettingsPalette.Text1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            cell.MouseEnter += (_, _) => cell.Background = Res(SettingsPalette.AccentSoft);
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            string chosen = name;
            Clickable(cell, () =>
            {
                popup.IsOpen = false;
                pick(chosen);
            });
            grid.Children.Add(cell);
        }
        var hint = new TextBlock { Text = L.T("Or type any emoji or short text in the Icon box."), FontSize = 12, Foreground = Res(SettingsPalette.Text2), Margin = new Thickness(4, 8, 4, 2) };
        popup.Child = new Border
        {
            Background = Res(SettingsPalette.Card), BorderBrush = Res(SettingsPalette.Stroke), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8), Margin = new Thickness(0, 4, 12, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.35 },
            Child = new StackPanel { Children = { grid, hint } },
        };
        popup.IsOpen = true;
    }

    /// <summary>One deck button as it looks on the touch screen: icon over label.</summary>
    static FrameworkElement DeckKey(DeckButton b)
    {
        var glyph = new TextBlock { FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.AccentText);
        SetPreview(glyph, b.Icon);
        var label = new TextBlock { Text = b.Label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 5, 4, 0) };
        var key = new Border
        {
            Width = 84, Height = 74, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8),
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } }, ToolTip = b.Target,
        };
        key.SetResourceReference(Border.BackgroundProperty, SettingsPalette.Raised);
        key.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        return key;
    }

    static void SetPreview(TextBlock t, string? icon)
    {
        icon = (icon ?? "").Trim();
        if (DeckActions.Icons.TryGetValue(icon, out var glyph))
        {
            t.Text = glyph;
            t.FontFamily = IconFont;
        }
        else
        {
            t.Text = icon;
            t.FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI");
        }
    }

    /// <summary>A small grey caption, then the control (tile row tools).</summary>
    static FrameworkElement Labeled2(string label, FrameworkElement control)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        var caption = Ref(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) }, TextBlock.ForegroundProperty, TextSecondary);
        p.Children.Add(caption);
        p.Children.Add(control);
        return p;
    }

    static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 4) };
        p.Children.Add(Ref(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, TextBlock.ForegroundProperty, TextSecondary));
        p.Children.Add(control);
        return p;
    }

    // ================================================================ Alerts, phone, hardware, about

    void AddAlerts(AppConfig config)
    {
        var a = config.Alerts;
        var main = Card();
        main.Margin = new Thickness(0, 16, 0, 10);
        Setting(main, L.T("Alerts"), L.T("A banner flashes when a problem lasts 5 seconds; tap it to hide for 10 minutes"), Switch(a.Enabled, v => Change(c => c.Alerts.Enabled = v, rebuild: true)), icon: "\uE7BA");
        Setting(main, L.T("Sound"), null, Switch(a.Sound, v => Change(c => c.Alerts.Sound = v)), last: true, icon: "\uE767");

        Section(L.T("Limits"), L.T("The Status board tile lights up from the same limits. 0 turns a check off."));
        var card = Card();
        Setting(card, L.T("CPU hotter than"), "°C", Number(a.CpuTemp, v => Change(c => c.Alerts.CpuTemp = v)), icon: "\uE950");
        Setting(card, L.T("GPU hotter than"), "°C", Number(a.GpuTemp, v => Change(c => c.Alerts.GpuTemp = v)), icon: "\uE7F4");
        Setting(card, L.T("Memory above"), "%", Number(a.RamPercent, v => Change(c => c.Alerts.RamPercent = v)), icon: "\uECA5");
        Setting(card, L.T("Drive free below"), L.T("% (needs a Drives or Status tile)"), Number(a.DiskFreePercent, v => Change(c => c.Alerts.DiskFreePercent = v)), icon: "\uEDA2");
        Setting(card, L.T("Ping above"), L.T("ms (needs a Ping tile)"), Number(a.PingMs, v => Change(c => c.Alerts.PingMs = v)), last: true, icon: "\uEC05");

        Section(L.T("Value colours"), L.T("When temperatures on the tiles turn orange, then red (always in °C)."));
        var colours = Card();
        Setting(colours, L.T("Warm from"), L.T("°C, shown in orange"), Number(config.TempWarn, v => Change(c => c.TempWarn = v)), icon: "\uE9CA");
        Setting(colours, L.T("Hot from"), L.T("°C, shown in red"), Number(config.TempCrit, v => Change(c => c.TempCrit = v)), last: true, icon: "\uE95E");

        Section(L.T("Websites"), null);
        var sites = Card();
        Setting(sites, L.T("Website down"), L.T("Sites on a Websites tile, after two failed checks in a row"), Switch(a.SiteDown, v => Change(c => c.Alerts.SiteDown = v)), icon: "\uE774");
        Setting(sites, L.T("SSL certificate expires within"), L.T("days (domains: twice this)"), Number(a.CertDays, v => Change(c => c.Alerts.CertDays = v)), last: true, icon: "\uE72E");
    }

    void AddWeb(AppConfig config)
    {
        var web = config.Web;
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 10);
        Setting(card, L.T("Serve the web dashboard"), web.AllowDeck ? L.T("Shows your stats, and the phone can press deck buttons (below)") : L.T("Read-only: it can't control the PC"), Switch(web.Enabled, v => Change(c =>
        {
            c.Web.Enabled = v;
            if (v && string.IsNullOrEmpty(c.Web.Key))
                c.Web.Key = WebServer.NewKey();
        }, rebuild: true)), icon: "\uE8EA");
        Setting(card, L.T("Port"), null, TextField(web.Port.ToString(), 90, v => Change(c => c.Web.Port = int.TryParse(v, out int n) && n is > 0 and < 65536 ? n : 8787, rebuild: true)), icon: "\uE839");
        var deck = Switch(web.AllowDeck, v => Change(c => c.Web.AllowDeck = v, rebuild: true));
        deck.IsEnabled = web.Enabled;
        Setting(card, L.T("Let the phone press deck buttons"), L.T("Anyone with the link can press them. Buttons that run commands or scripts are never shown."),
            deck, last: true, icon: "\uE765");
        _panel.Children.Add(Hint(L.T("Open the dashboard in a browser on any device on your home network. " +
                                 "The link contains a secret key; anyone with it on your network can see your stats.")));
        if (!web.Enabled || string.IsNullOrEmpty(web.Key))
            return;

        // QR code on the left, the link and what to do on the right.
        string url = WebServer.Url(web);
        var connect = Card();
        connect.Padding = new Thickness(20, 20, 20, 18);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new System.IO.MemoryStream(png);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            grid.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(10), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 22, 0), Child = new Image { Source = img, Width = 168, Height = 168 } });
        }
        catch (Exception ex)
        {
            Log.Write($"QR code failed: {ex.Message}");
        }
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = L.T("Scan with your phone"), FontSize = 17, FontWeight = FontWeights.SemiBold });
        right.Children.Add(Ref(new TextBlock { Text = L.T("Point the camera at the code, or type the link below. If it doesn't load, allow Shoji through Windows Firewall on private networks."), TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 4, 0, 14) }, TextBlock.ForegroundProperty, TextSecondary));
        right.Children.Add(new TextBox { Text = url, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 10) });
        var row = new WrapPanel();
        row.Children.Add(AccentButton(L.T("Copy link"), () => Clipboard.SetText(url)));
        row.Children.Add(Button(L.T("Open"), () => Shell(url, null)));
        row.Children.Add(Button(L.T("New key"), () => Change(c => c.Web.Key = WebServer.NewKey(), rebuild: true)));
        right.Children.Add(row);
        grid.Children.Add(right);
        ((StackPanel)connect.Child).Children.Add(grid);
    }

    void AddSensors()
    {
        bool ready = CpuSensorSetup.Ready, admin = Startup.IsElevated, pawn = CpuSensorSetup.PawnIoInstalled;
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 10);
        card.Padding = new Thickness(20, 18, 20, 16);
        var badgeGlyph = Ref(new TextBlock { Text = ready ? "\uE73E" : "\uE7BA", FontFamily = IconFont, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, ready ? SettingsPalette.Good : SettingsPalette.Warn);
        var badge = Ref(new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Child = badgeGlyph, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top }, Border.BackgroundProperty, ready ? SettingsPalette.GoodSoft : SettingsPalette.WarnSoft);
        string title = ready ? L.T("Every sensor is working") : pawn ? L.T("Almost there: run as administrator") : L.T("CPU and motherboard sensors need a driver");
        string status = ready
            ? L.T("CPU temperature, power, motherboard sensors and the FPS counter are working (running as admin, PawnIO installed).")
            : !pawn
                ? L.T("CPU temperature, CPU power and motherboard fans / temps need the free PawnIO driver and admin rights; the FPS counter needs admin rights. GPU, load, memory, network and everything else already work.")
                : L.T("PawnIO is installed. Shoji just needs to run as administrator to read the CPU, motherboard and FPS.");
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Ref(new TextBlock { Text = status, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 4, 0, 14) }, TextBlock.ForegroundProperty, TextSecondary));
        var buttons = new WrapPanel();
        if (!ready)
            buttons.Children.Add(AccentButton(pawn ? L.T("Restart as admin") : L.T("Install PawnIO and restart as admin"), _enableCpuSensors));
        buttons.Children.Add(Button(L.T("Export sensor list…"), _exportSensors));
        text.Children.Add(buttons);
        var dock = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        dock.Children.Add(text);
        ((StackPanel)card.Child).Children.Add(dock);

        // Which graphics card the GPU tiles show (names come from the sensors the dashboard opened).
        var gpus = Sensors.HardwareSensors.GpuNames;
        if (gpus.Count > 0)
        {
            Section(L.T("Graphics card"), null);
            var gpuCard = Card();
            var cfg = _config;
            if (gpus.Count == 1)
                Setting(gpuCard, L.T("GPU tiles show"), null, new TextBlock { Text = gpus[0], VerticalAlignment = VerticalAlignment.Center }, last: true, icon: "\uE7F4");
            else
                Setting(gpuCard, L.T("GPU tiles show"), L.T("Power adds up every graphics card either way"), Combo(gpus, Math.Clamp(cfg.GpuIndex, 0, gpus.Count - 1), i => Change(c => c.GpuIndex = i)), last: true, icon: "\uE7F4");
        }

        // What works right now, one line each.
        Section(L.T("What Shoji can read"), null);
        var list = Card();
        var items = new (string Icon, string Name, string Needs, bool Ok)[]
        {
            ("\uE7F4", "GPU load, temperature, power, VRAM, fan", "works without setup", true),
            ("\uE950", "CPU load and clock", "works without setup", true),
            ("\uECA5", "Memory, drives, network, ping, battery", "works without setup", true),
            ("\uE95E", "CPU temperature and power", "needs PawnIO and admin", ready),
            ("\uE895", "Motherboard fans and temperatures", "needs PawnIO and admin", ready),
            ("\uE7FC", "FPS counter", "needs admin", admin),
        };
        for (int i = 0; i < items.Length; i++)
        {
            var (icon, name, needs, ok) = items[i];
            Setting(list, L.T(name), L.T(needs), ok ? Pill(L.T("Working"), PillKind.Good) : Pill(L.T("Not yet"), PillKind.Warn), last: i == items.Length - 1, icon: icon);
        }
    }

    void AddAbout()
    {
        // The app itself: icon, name and version.
        var brand = Card();
        brand.Margin = new Thickness(0, 16, 0, 10);
        brand.Padding = new Thickness(22, 20, 22, 20);
        var logo = new Image { Source = SetupWindow.AppIcon(), Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0) };
        var name = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold };
        name.Inlines.Add("Shoji");
        var kanji = new System.Windows.Documents.Run("  障子") { FontWeight = FontWeights.Normal };
        kanji.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, TextTertiary);
        name.Inlines.Add(kanji);
        var about = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                name,
                Ref(new TextBlock { Text = L.F("Version {0} · a dashboard and touch deck for your small screen", AppPaths.VersionText), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }, TextBlock.ForegroundProperty, TextSecondary),
                Ref(new TextBlock { Text = L.T("A shoji is the paper screen of a Japanese house: light comes through it, softly."), FontStyle = FontStyles.Italic, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, TextBlock.ForegroundProperty, TextTertiary),
            },
        };
        var head = new DockPanel();
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        head.Children.Add(about);
        ((StackPanel)brand.Child).Children.Add(head);

        Section(L.T("Installation"), null);
        var card = Card();
        card.Padding = new Thickness(20, 16, 20, 12);
        var inner = (StackPanel)card.Child;
        string where = AppPaths.IsInstalledCopy
            ? L.F("Installed (version {0}) in {1}. Settings are in {2}.", AppPaths.VersionText, AppPaths.InstallDir, AppPaths.DataFolder)
            : AppPaths.IsPortable
                ? L.F("Portable (version {0}): running from {1}, settings in its \"data\" folder.", AppPaths.VersionText, AppPaths.ExeDir)
                : L.F("Not installed (version {0}): running from {1}.", AppPaths.VersionText, AppPaths.ExePath);
        if (!AppPaths.IsInstalledCopy && Installer.InstalledVersion is { } v)
            where += L.F(" Version {0}.{1}.{2} is also installed.", v.Major, v.Minor, v.Build);
        inner.Children.Add(new TextBlock { Text = where, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var row = new WrapPanel();
        if (AppPaths.IsInstalledCopy)
            row.Children.Add(Button(L.T("Uninstall…"), () => Shell(AppPaths.InstalledExe, "--uninstall")));
        else
        {
            row.Children.Add(AccentButton(Installer.IsInstalled ? L.T("Update the installed app") : L.T("Install on this PC"), () => Shell(AppPaths.ExePath, "--install")));
            if (!AppPaths.IsPortable)
                row.Children.Add(Button(L.T("Make this copy portable"), () => Shell(AppPaths.ExePath, "--portable")));
        }
        inner.Children.Add(row);

        AddUpdates(_config);

        Section(L.T("Start with Windows"), null);
        var start = Card();
        var modes = new[] { StartupMode.Off, StartupMode.Normal, StartupMode.Elevated };
        int mode = Math.Max(0, Array.IndexOf(modes, Startup.Current));
        Setting(start, L.T("Start Shoji when you sign in"), L.T("\"As admin\" reads every sensor without a prompt (asks for permission once)"),
            Segmented(new[] { L.T("Off"), L.T("On"), L.T("On, as admin") }, mode, async i =>
            {
                // Off the UI thread: registering the admin task waits for the UAC prompt and PowerShell.
                try
                {
                    await System.Threading.Tasks.Task.Run(() => Startup.Set(modes[i]));
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show(this, L.T("Cancelled: Windows needs admin permission for that option."), "Shoji", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Log.Write($"Changing startup mode failed: {ex}");
                    MessageBox.Show(this, L.T("Couldn't change the startup setting (see log.txt)."), "Shoji", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Rebuild(); // show what actually happened
            }), last: true, icon: "\uE7E8");

        Section(L.T("Files"), L.T("World clock zones, sensor IDs, colour overrides and temperature thresholds can also be set in config.json."));
        var files = Card();
        Setting(files, "config.json", L.T("Every setting, as text"), Button(L.T("Edit…"), () => Shell("notepad.exe", $"\"{AppConfig.FilePath}\"")), icon: "\uE8A5");
        Setting(files, L.T("Data folder"), L.T("Settings, history, logs"), Button(L.T("Open"), () => Shell(AppConfig.Folder, null)), icon: "\uE8B7");
        Setting(files, L.T("Reset everything"), L.T("Theme, pages, tiles, scenes and deck buttons go back to defaults"), Button(L.T("Reset…"), () =>
        {
            if (MessageBox.Show(this, L.T("Reset all Shoji settings (theme, pages, tiles, scenes, deck buttons) to defaults?"), "Shoji", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                new AppConfig { LayoutVersion = AppConfig.CurrentLayoutVersion }.Save();
                _page = 0;
                Dispatcher.BeginInvoke(Rebuild);
            }
        }), last: true, icon: "\uE72C");
    }

    static void Shell(string file, string? args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { Arguments = args ?? "", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open {file}: {ex.Message}");
        }
    }

    // ================================================================ building blocks

    static T Ref<T>(T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    /// <summary>
    /// Makes a custom element clickable like a button: it takes focus on mouse-down (so a text box being edited saves
    /// first, with the right row), and only acts when the press both started and ended on it. That way closing a
    /// dropdown over it, or a drag that ends on it, doesn't click it. Enter / Space click it too.
    /// </summary>
    static void Clickable(FrameworkElement el, Action click)
    {
        el.Focusable = true;
        el.FocusVisualStyle = null;
        el.MouseLeftButtonDown += (_, e) =>
        {
            el.Focus();
            el.CaptureMouse();
            e.Handled = true;
        };
        el.MouseLeftButtonUp += (_, e) =>
        {
            if (!el.IsMouseCaptured)
                return;
            el.ReleaseMouseCapture();
            e.Handled = true;
            var p = e.GetPosition(el);
            if (p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight)
                click();
        };
        el.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && !e.IsRepeat)
            {
                e.Handled = true;
                click();
            }
        };
    }

    void Section(string title, string? subtitle)
    {
        _panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, _panel.Children.Count <= 1 ? 18 : 30, 0, subtitle == null ? 10 : 2),
        });
        if (subtitle != null)
            _panel.Children.Add(Ref(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(2, 0, 0, 12) }, TextBlock.ForegroundProperty, TextSecondary));
    }

    /// <summary>A note with an info icon.</summary>
    FrameworkElement Hint(string text)
    {
        var icon = Ref(new TextBlock { Text = "\uE946", FontFamily = IconFont, FontSize = 13, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top }, TextBlock.ForegroundProperty, TextTertiary);
        var body = Ref(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 }, TextBlock.ForegroundProperty, TextSecondary);
        var dock = new DockPanel { Margin = new Thickness(4, 6, 0, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(body);
        return dock;
    }

    void Banner(string text)
    {
        var icon = Ref(new TextBlock { Text = "\uE7BA", FontFamily = IconFont, FontSize = 16, Margin = new Thickness(0, 1, 12, 0) }, TextBlock.ForegroundProperty, SettingsPalette.Warn);
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 8, 0, 8), Child = dock };
        b.SetResourceReference(Border.BackgroundProperty, SettingsPalette.WarnSoft);
        _panel.Children.Add(b);
    }

    /// <summary>A rounded card; settings rows go inside its StackPanel.</summary>
    Border Card(bool addToPanel = true)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(20, 6, 20, 6),
            Margin = new Thickness(0, 0, 0, 10), Child = new StackPanel(),
        };
        card.SetResourceReference(Border.BackgroundProperty, CardFill);
        card.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        if (addToPanel)
            _panel.Children.Add(card);
        return card;
    }

    /// <summary>One row in a card: optional icon, label (and description) on the left, the control on the right, a divider below.</summary>
    static void Setting(Border card, string label, string? description, FrameworkElement control, bool last = false, string? icon = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description != null)
            text.Children.Add(Ref(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) }, TextBlock.ForegroundProperty, TextSecondary));
        control.VerticalAlignment = VerticalAlignment.Center;
        if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control)))
            System.Windows.Automation.AutomationProperties.SetName(control, label); // what screen readers announce
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 12), LastChildFill = true };
        if (icon != null)
        {
            var glyph = Ref(new TextBlock { Text = icon, FontFamily = IconFont, FontSize = 17, Width = 22, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }, TextBlock.ForegroundProperty, TextSecondary);
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
        }
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(text);
        var inner = (StackPanel)card.Child;
        inner.Children.Add(row);
        if (!last)
            inner.Children.Add(Ref(new Border { Height = 1 }, Border.BackgroundProperty, SettingsPalette.Divider));
    }

    static Border Chip(string text, bool selected, Action click)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(16, 7, 16, 7), FontSize = 13 };
        var chip = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8), Child = t, Cursor = Cursors.Hand };
        if (selected)
        {
            chip.SetResourceReference(Border.BackgroundProperty, AccentFill);
            chip.SetResourceReference(Border.BorderBrushProperty, AccentFill);
            t.SetResourceReference(TextBlock.ForegroundProperty, OnAccent);
            t.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            chip.SetResourceReference(Border.BackgroundProperty, CardFill);
            chip.SetResourceReference(Border.BorderBrushProperty, CardStroke);
            chip.MouseEnter += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, TextTertiary);
            chip.MouseLeave += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        }
        Clickable(chip, click);
        return chip;
    }

    /// <summary>
    /// A row of joined buttons for a few short choices (°C / °F, 1 s / 2 s…). Shows the new choice at once, then calls
    /// <paramref name="changed"/>.
    /// </summary>
    static FrameworkElement Segmented(IReadOnlyList<string> items, int selected, Action<int> changed, bool stretch = false)
    {
        var row = new UniformGrid { Rows = 1 };
        var outer = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(3), Child = row };
        outer.SetResourceReference(Border.BackgroundProperty, SettingsPalette.Input);
        outer.SetResourceReference(Border.BorderBrushProperty, CardStroke);
        if (!stretch)
        {
            row.HorizontalAlignment = HorizontalAlignment.Left;
            outer.HorizontalAlignment = HorizontalAlignment.Right;
        }
        int current = selected;
        var parts = new List<(Border Box, TextBlock Text)>();
        void Paint()
        {
            for (int i = 0; i < parts.Count; i++)
            {
                var (box, text) = parts[i];
                bool on = i == current;
                if (on) box.SetResourceReference(Border.BackgroundProperty, CardFill);
                else box.Background = Brushes.Transparent;
                box.Effect = on ? SegmentShadow : null;
                text.SetResourceReference(TextBlock.ForegroundProperty, on ? TextPrimary : TextSecondary);
                text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var text = new TextBlock { Text = items[i], FontSize = 13, Margin = new Thickness(12, 5, 12, 6), HorizontalAlignment = HorizontalAlignment.Center };
            var box = new Border { CornerRadius = new CornerRadius(7), Child = text, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            box.MouseEnter += (_, _) => { if (index != current) box.SetResourceReference(Border.BackgroundProperty, Subtle); };
            box.MouseLeave += (_, _) => Paint();
            Clickable(box, () =>
            {
                if (index == current)
                    return;
                current = index;
                Paint();
                changed(index);
            });
            parts.Add((box, text));
            row.Children.Add(box);
        }
        Paint();
        return outer;
    }

    static readonly System.Windows.Media.Effects.DropShadowEffect SegmentShadow = Frozen(new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Direction = 270, Opacity = 0.22 });

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    static ComboBox Combo(IReadOnlyList<string> items, int selected, Action<int> changed)
    {
        var box = new ComboBox { MinWidth = 240 };
        foreach (var i in items)
            box.Items.Add(i);
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
                changed(box.SelectedIndex);
        };
        return box;
    }

    static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = value, VerticalContentAlignment = VerticalAlignment.Center };
        if (text.Length > 0)
            box.Content = text;
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    static ToggleSwitch Switch(bool value, Action<bool> changed)
    {
        var s = new ToggleSwitch(value);
        s.Toggled += changed;
        return s;
    }

    /// <summary>
    /// Saves of typed text that hasn't been saved yet. Boxes save on Enter or when focus leaves them; closing the
    /// window (X, Alt+F4) doesn't move focus, so <see cref="FlushPending"/> saves these when it closes.
    /// </summary>
    static readonly HashSet<Action> PendingCommits = new();

    static void FlushPending()
    {
        foreach (var commit in PendingCommits.ToArray())
            commit();
        PendingCommits.Clear();
    }

    /// <summary>
    /// A text box that saves when you press Enter, click or Alt+Tab elsewhere, or close the window (empty = null).
    /// <paramref name="check"/> (optional) turns the text into the value to save, or returns false: then the box
    /// is marked invalid with the reason in its tooltip and nothing is saved.
    /// </summary>
    static TextBox TextField(string? value, double width, Action<string?> save, string? tooltip = null, Func<string?, (bool Ok, string? Value)>? check = null, string? invalidHint = null)
    {
        var box = new TextBox { Text = value ?? "", Width = width, ToolTip = tooltip, VerticalContentAlignment = VerticalAlignment.Center };
        string last = box.Text;
        void Commit()
        {
            PendingCommits.Remove(Commit);
            if (box.Text == last)
                return;
            string? text = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            if (check != null)
            {
                var (ok, normal) = check(text);
                MarkInvalid(box, ok ? null : invalidHint ?? L.T("Not a valid value: it wasn't saved"), tooltip);
                if (!ok)
                    return;
                text = normal;
                if (normal != null && box.Text != normal)
                    box.Text = normal; // show what was saved ("7pm" → "19:00")
            }
            last = box.Text;
            PendingCommits.Remove(Commit);
            save(text);
        }
        box.TextChanged += (_, _) =>
        {
            if (box.Text != last)
                PendingCommits.Add(Commit);
        };
        box.LostFocus += (_, _) => Commit();
        box.LostKeyboardFocus += (_, _) => Commit(); // also when the window loses focus (Alt+Tab)
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
        };
        return box;
    }

    /// <summary>Red border and the reason as tooltip (error = null puts the box back to normal).</summary>
    static void MarkInvalid(Control box, string? error, string? normalTooltip)
    {
        if (error == null)
        {
            box.ClearValue(BorderBrushProperty);
            box.ClearValue(BorderThicknessProperty);
            box.ToolTip = normalTooltip;
            return;
        }
        box.BorderBrush = InvalidBrush;
        box.BorderThickness = new Thickness(2);
        box.ToolTip = error;
    }

    static readonly Brush InvalidBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x3E, 0x3E)));

    static TextBox Number(double value, Action<double> save) =>
        TextField(value.ToString(CultureInfo.CurrentCulture), 90, v => save(double.Parse(v!, NumberStyles.Float, CultureInfo.CurrentCulture)),
            check: v => double.TryParse(v ?? "0", NumberStyles.Float, CultureInfo.CurrentCulture, out double d) && d >= 0
                ? (true, d.ToString(CultureInfo.CurrentCulture))
                : (false, null),
            invalidHint: L.T("Type a number (0 or more): this wasn't saved"));

    /// <summary>A 24-hour time box: "7", "7pm", "19.30" are saved as "HH:mm"; anything else is marked and not saved.</summary>
    static TextBox ClockField(string? value, double width, Action<string?> save, bool optional = false) =>
        TextField(value, width, save, L.T("24-hour time, e.g. 07:30"), v =>
        {
            if (v == null)
                return (optional, null);
            return SceneEngine.TryParseClock(v, out var t) ? (true, $"{(int)t.TotalHours:00}:{t.Minutes:00}") : (false, null);
        }, optional ? L.T("Use a time like 07:30 or 22:00 (or leave empty): this wasn't saved") : L.T("Use a time like 07:30 or 22:00: this wasn't saved"));

    /// <summary>"Label  [text box]  hint" (wraps on narrow windows). Secrets use a masked box.</summary>
    static FrameworkElement Field(string label, string? value, string hint, Action<string?> save, bool secret = false)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 120 });
        if (secret)
        {
            var box = new PasswordBox { Password = value ?? "", Width = 300, Margin = new Thickness(0, 0, 10, 0) };
            string last = box.Password;
            void Commit()
            {
                if (box.Password == last)
                    return;
                last = box.Password;
                PendingCommits.Remove(Commit);
                save(string.IsNullOrWhiteSpace(box.Password) ? null : box.Password.Trim());
            }
            box.PasswordChanged += (_, _) =>
            {
                if (box.Password != last)
                    PendingCommits.Add(Commit);
            };
            box.LostFocus += (_, _) => Commit();
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            row.Children.Add(box);
        }
        else
        {
            var box = TextField(value, 300, save);
            box.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(box);
        }
        row.Children.Add(Ref(new TextBlock { Text = hint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }, TextBlock.ForegroundProperty, TextSecondary));
        return row;
    }

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), MinWidth = 36, Padding = new Thickness(14, 6, 14, 7) };
        b.Click += (_, _) => click();
        return b;
    }

    static Button AccentButton(string text, Action click)
    {
        var b = Button(text, click);
        b.SetResourceReference(StyleProperty, "AccentButtonStyle");
        return b;
    }

    /// <summary>A flat round icon button (move up / down, delete) that only shows a background under the mouse.</summary>
    static Border IconButton(string glyph, string tooltip, Action click, bool danger = false)
    {
        var t = Ref(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, TextSecondary);
        var b = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Child = t, ToolTip = tooltip, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0) };
        ToolTipService.SetShowOnDisabled(b, true);
        b.MouseEnter += (_, _) =>
        {
            b.SetResourceReference(Border.BackgroundProperty, danger ? SettingsPalette.AccentSoft : Subtle);
            t.SetResourceReference(TextBlock.ForegroundProperty, danger ? SettingsPalette.AccentText : TextPrimary);
        };
        b.MouseLeave += (_, _) =>
        {
            b.Background = Brushes.Transparent;
            t.SetResourceReference(TextBlock.ForegroundProperty, TextSecondary);
        };
        Clickable(b, click);
        b.IsEnabledChanged += (_, _) => b.Opacity = b.IsEnabled ? 1 : 0.3;
        return b;
    }
}
