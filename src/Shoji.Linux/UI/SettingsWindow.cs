using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Colours of the Settings window: warm washi paper (light) or ink (dark), vermilion accent — as on Windows.</summary>
static class Look
{
    public static bool Dark { get; private set; } = true;
    public static IBrush Bg = null!, Side = null!, Card = null!, Raised = null!, Stroke = null!, Divider = null!, Input = null!, Hover = null!,
        Text1 = null!, Text2 = null!, Text3 = null!, Accent = null!, AccentText = null!, AccentSoft = null!, Good = null!, GoodSoft = null!, Warn = null!, WarnSoft = null!;
    public static Color AccentColor;

    static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static void Apply(Window w, bool dark)
    {
        Dark = dark;
        Bg = B(dark ? "#14161B" : "#F5F3EF");
        Side = B(dark ? "#0F1115" : "#ECE8E1");
        Card = B(dark ? "#1B1E25" : "#FFFFFF");
        Raised = B(dark ? "#23272F" : "#FBFAF7");
        Stroke = B(dark ? "#2A2F39" : "#E3DDD3");
        Divider = B(dark ? "#262A33" : "#EEE9E1");
        Input = B(dark ? "#23272F" : "#F3F0EB");
        Hover = B(dark ? "#12FFFFFF" : "#0D000000");
        Text1 = B(dark ? "#ECEEF2" : "#1C1E23");
        Text2 = B(dark ? "#9AA3B2" : "#5F6673");
        Text3 = B(dark ? "#6B7383" : "#949AA5");
        AccentColor = Color.Parse(dark ? "#E8603F" : "#D9503A");
        Accent = new SolidColorBrush(AccentColor);
        AccentText = B(dark ? "#FF8A6B" : "#C2452F");
        AccentSoft = B(dark ? "#26E8603F" : "#1AD9503A");
        Good = B(dark ? "#3ECF7A" : "#1E9E55");
        GoodSoft = B(dark ? "#243ECF7A" : "#1F1E9E55");
        Warn = B(dark ? "#F0B43C" : "#A86B12");
        WarnSoft = B(dark ? "#26F0B43C" : "#26D99A2B");

        w.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        // Fluent controls (switches, combo boxes, buttons) pick the accent up from these.
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            w.Resources[key] = AccentColor;
        // The Fluent theme turns those colours into brushes once; point the controls' brushes at the accent directly.
        var hover = new SolidColorBrush(Color.Parse(dark ? "#F0714F" : "#E35E46"));
        var pressed = new SolidColorBrush(Color.Parse(dark ? "#D4573A" : "#C2452F"));
        foreach (var key in new[] { "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "AccentButtonBackground", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked",
                     "TextControlBorderBrushFocused", "ComboBoxBackgroundBorderBrushFocused", "SystemControlHighlightAccentBrush", "ExpanderHeaderBorderBrushPointerOver" })
            w.Resources[key] = Accent;
        foreach (var key in new[] { "ToggleSwitchFillOnPointerOver", "AccentButtonBackgroundPointerOver", "CheckBoxCheckBackgroundFillCheckedPointerOver" })
            w.Resources[key] = hover;
        foreach (var key in new[] { "ToggleSwitchFillOnPressed", "AccentButtonBackgroundPressed", "CheckBoxCheckBackgroundFillCheckedPressed" })
            w.Resources[key] = pressed;
        foreach (var key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" })
            w.Resources[key] = Brushes.White;
        w.Background = Bg;
        w.Foreground = Text1;
    }

    /// <summary>"auto" follows the desktop's light / dark preference (GNOME's color-scheme).</summary>
    public static bool IsDark(string? look) => (look ?? "auto").ToLowerInvariant() switch
    {
        "light" => false,
        "dark" => true,
        _ => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light,
    };
}

/// <summary>Coloured rounded-square icons for sections and tile types (Material Icons glyphs).</summary>
static class Icons
{
    public record Glyph(string Name, string From, string To);

    const string BlueA = "#5B9BFF", BlueB = "#2F5FE0", HeatA = "#FFA25C", HeatB = "#E8622A", NetA = "#3FE0C8", NetB = "#119C8C",
        MediaA = "#F57FD0", MediaB = "#B23FC4", TimeA = "#FFCB5C", TimeB = "#E89A1C", CtrlA = "#FF7A6B", CtrlB = "#D9503A",
        InfoA = "#6FE08A", InfoB = "#2C9A4D", SlateA = "#97A2B6", SlateB = "#5B6579";

    static readonly Dictionary<string, Glyph> Tiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = new("memory", BlueA, BlueB), ["gpu"] = new("developer_board", BlueA, BlueB), ["memory"] = new("dns", BlueA, BlueB),
        ["power"] = new("bolt", BlueA, BlueB), ["fps"] = new("speed", BlueA, BlueB), ["processes"] = new("list", BlueA, BlueB),
        ["overview"] = new("hub", BlueA, BlueB), ["history"] = new("show_chart", BlueA, BlueB),
        ["temps"] = new("thermostat", HeatA, HeatB), ["fans"] = new("toys", HeatA, HeatB), ["sensors"] = new("sensors", HeatA, HeatB),
        ["network"] = new("lan", NetA, NetB), ["ping"] = new("network_check", NetA, NetB), ["websites"] = new("public", NetA, NetB), ["webvalue"] = new("data_object", NetA, NetB),
        ["media"] = new("play_circle", MediaA, MediaB), ["lyrics"] = new("lyrics", MediaA, MediaB), ["mixer"] = new("tune", MediaA, MediaB),
        ["twitch"] = new("chat", MediaA, MediaB), ["notifications"] = new("notifications", MediaA, MediaB),
        ["clock"] = new("schedule", TimeA, TimeB), ["worldclock"] = new("language", TimeA, TimeB), ["calendar"] = new("calendar_month", TimeA, TimeB),
        ["agenda"] = new("event_note", TimeA, TimeB), ["weather"] = new("wb_cloudy", TimeA, TimeB), ["forecast"] = new("wb_sunny", TimeA, TimeB),
        ["timer"] = new("timer", TimeA, TimeB),
        ["deck"] = new("keyboard", CtrlA, CtrlB), ["homeassistant"] = new("home", CtrlA, CtrlB), ["mirror"] = new("cast", CtrlA, CtrlB),
        ["status"] = new("verified_user", InfoA, InfoB), ["recap"] = new("star", InfoA, InfoB), ["activity"] = new("insights", InfoA, InfoB),
        ["battery"] = new("battery_full", InfoA, InfoB), ["disks"] = new("storage", InfoA, InfoB), ["github"] = new("code", InfoA, InfoB),
        ["ticker"] = new("trending_up", InfoA, InfoB), ["system"] = new("computer", InfoA, InfoB),
        ["photos"] = new("photo_library", MediaA, MediaB),
    };

    public static Glyph ForTile(string type) => Tiles.TryGetValue(type, out var g) ? g : new("apps", SlateA, SlateB);

    public static Border Tile(Glyph g, double size, bool shadow = false)
    {
        var box = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
            Background = DrawCompat.Linear(new Point(0, 0), new Point(1, 1), false, (Color.Parse(g.From), 0), (Color.Parse(g.To), 1)),
            Child = new TextBlock
            {
                Text = g.Name, FontFamily = UI.Theme.Icons, FontSize = Math.Round(size * 0.56), Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (shadow)
            box.BoxShadow = BoxShadows.Parse($"0 4 16 0 #70{g.To[1..]}");
        return box;
    }
}

/// <summary>
/// Settings for the Linux dashboard, in the same design as on Windows: coloured sections in a sidebar with search,
/// a banner per section, cards with switches and segmented choices. Every change is saved to config.json at once
/// and the dashboard reloads itself.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    static SettingsWindow? _instance;

    readonly Dashboard _dashboard;
    readonly StackPanel _nav = new() { Margin = new Thickness(12, 0, 12, 8) };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Border _saved = new();
    readonly ContentControl _look = new();
    readonly TextBox _search = new();
    readonly DispatcherTimer _savedTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Grid _root = new();
    StackPanel _panel = new();
    AppConfig _config = new();
    string? _lastWritten;
    string _section = "look";
    int _page;
    (int Page, int Module) _deck = (-1, -1);
    string _themeGroup = "All";
    bool _resetScroll, _dark;

    static readonly (string Id, string Icon, string Name, string Subtitle, string From, string To, string Kanji, string Keywords)[] Sections =
    {
        ("look", "palette", "Appearance", "Theme, day and night, and how the dashboard fits the screen", "#FF7A6B", "#D9503A", "外観",
            "theme colour color accent background tile custom dark light day night display screen monitor refresh text size scale columns animations album art cover backdrop pixel shift burn-in on top"),
        ("weather", "wb_cloudy", "Weather & time", "Where the weather is for, units, and the clock", "#FFD95C", "#EBA414", "天気",
            "weather city location town place forecast temperature celsius fahrenheit units clock 24-hour time zone world calendar calendars ical ics webcal google outlook icloud agenda events"),
        ("pages", "dashboard", "Pages & tiles", "What each page shows, in which order and style", "#5B9BFF", "#2F5FE0", "頁",
            "page tile layout preset add widget style gauge columns rotate order share export import code photo frame websites weather"),
        ("scenes", "auto_awesome", "Scenes", "Switch page or theme by itself: a game starts, you step away, or at a time of day", "#B07CFF", "#6D3FDC", "場面",
            "scene automatic idle away time night app focus game fullscreen"),
        ("deck", "keyboard", "Control deck", "Touch buttons for apps, hotkeys, audio, power and more", "#FFC15C", "#F0892A", "操作盤",
            "deck button hotkey keys launch app volume power profile stream shortcut obs record scene websocket"),
        ("alerts", "notifications_active", "Alerts", "When the dashboard should get your attention", "#FF6B81", "#D42A46", "警報",
            "alert warning temperature hot warm orange red colour cpu gpu memory ram drive disk ping website down ssl certificate sound banner"),
        ("phone", "smartphone", "Phone dashboard", "See the dashboard on a phone or tablet on your network", "#3FE0C8", "#119C8C", "携帯",
            "phone web browser tablet qr network port key remote"),
        ("hardware", "memory", "Hardware sensors", "What Shoji can read on this PC, and how to unlock the rest", "#6FE08A", "#2C9A4D", "計器",
            "sensor cpu gpu graphics card nvidia amd temperature power rapl fans hwmon tools xdotool ydotool wtype"),
        ("about", "info", "About & install", "Version, app menu, start at login, files and reset", "#97A2B6", "#5B6579", "障子",
            "version install uninstall update upgrade github release app menu start startup login autostart config json reset folder files"),
    };

    static Icons.Glyph SectionGlyph(string id)
    {
        var s = Sections.First(x => x.Id == id);
        return new Icons.Glyph(s.Icon, s.From, s.To);
    }

    public static void Open(Dashboard dashboard)
    {
        if (_instance != null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(dashboard);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>For --screenshot: a Settings window on a given section, at a given size.</summary>
    public static Window ForScreenshot(Dashboard dashboard, string section, double w, double h)
    {
        var s = new SettingsWindow(dashboard) { Width = w, Height = h };
        s.Go(section);
        return s;
    }

    SettingsWindow(Dashboard dashboard)
    {
        _dashboard = dashboard;
        _page = dashboard.CurrentPageIndex;
        Icon = App.WindowIcon();
        Width = 1160;
        Height = 860;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Inter, Ubuntu, Noto Sans, Noto Sans CJK JP");
        FontSize = 14;
        _dark = Look.IsDark(AppConfig.Load().SettingsLook);
        Look.Apply(this, _dark);
        Styles.Add(InvalidBoxStyle());

        // Left: brand, search, sections, the light / dark switch.
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 16, 16) };
        brand.Children.Add(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://shoji/Assets/shoji.png"))), Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) });
        var name = new TextBlock { FontSize = 18, FontWeight = FontWeight.Bold, Inlines = new InlineCollection { new Run("Shoji"), new Run("  障子") { FontSize = 14, FontWeight = FontWeight.Normal } } };
        _version = new TextBlock { FontSize = 12 };
        brand.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, _version } });
        _brandKanji = (Run)name.Inlines![1];

        _search.Margin = new Thickness(16, 0, 16, 14);
        _search.TextChanged += (_, _) => BuildNav();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Matches().FirstOrDefault() is { } first)
                Go(first);
            else if (e.Key == Key.Escape)
                _search.Text = "";
        };

        _lookBox = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(14, 8, 14, 16) };
        _lookCaption = new TextBlock { FontSize = 12, Margin = new Thickness(2, 0, 0, 8) };
        _lookBox.Child = new StackPanel { Children = { _lookCaption, _look } };

        _left = new DockPanel { Width = 268 };
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_lookBox, Dock.Bottom);
        _left.Children.Add(brand);
        _left.Children.Add(_search);
        _left.Children.Add(_lookBox);
        _left.Children.Add(new ScrollViewer { Content = _nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // Right: the section (its banner scrolls with it) and the "Saved" toast in the corner.
        BuildSavedToast();
        var right = new Grid { Children = { _scroll, _saved } };
        _root.ColumnDefinitions = new ColumnDefinitions("Auto,*");
        Grid.SetColumn(right, 1);
        _root.Children.Add(_left);
        _root.Children.Add(right);
        Content = _root;
        _savedTimer.Tick += (_, _) =>
        {
            _savedTimer.Stop();
            _saved.Opacity = 0;
        };
        // Closing the window doesn't take focus from the box being typed in: save what's typed first.
        Closing += (_, _) => FlushPending();
        Rebuild();
    }

    readonly TextBlock _version;
    readonly Run _brandKanji;
    readonly Border _lookBox;
    readonly TextBlock _lookCaption;
    readonly TextBlock _savedText = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly DockPanel _left;

    /// <summary>The window's own texts, set again on every rebuild (the language may have changed).</summary>
    void TextChrome()
    {
        Title = L.T("Shoji settings");
        _version.Text = L.F("Version {0} · Linux", AppPaths.VersionText);
        _search.Watermark = L.T("Find a setting");
        ToolTip.SetTip(_search, L.T("Type a word like \"temperature\", \"monitor\" or \"hotkey\"; Enter opens the first match"));
        _lookCaption.Text = L.T("Settings look");
        _savedText.Text = L.T("Saved · the dashboard is updating");
    }

    void PaintChrome()
    {
        _left.Background = Look.Side;
        _version.Foreground = Look.Text2;
        _brandKanji.Foreground = Look.Text3;
        _lookBox.Background = Look.Card;
        _lookBox.BorderBrush = Look.Stroke;
        _lookCaption.Foreground = Look.Text2;
        _saved.Background = Look.Raised;
        _saved.BorderBrush = Look.Stroke;
        ((TextBlock)((StackPanel)_saved.Child!).Children[0]).Foreground = Look.Good;
    }

    void BuildSavedToast()
    {
        var check = new TextBlock { Text = "check_circle", FontFamily = UI.Theme.Icons, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _saved.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { check, _savedText } };
        _saved.CornerRadius = new CornerRadius(20);
        _saved.Padding = new Thickness(16, 9, 18, 9);
        _saved.BorderThickness = new Thickness(1);
        _saved.Margin = new Thickness(0, 0, 28, 22);
        _saved.HorizontalAlignment = HorizontalAlignment.Right;
        _saved.VerticalAlignment = VerticalAlignment.Bottom;
        _saved.IsHitTestVisible = false;
        _saved.Opacity = 0;
        _saved.BoxShadow = BoxShadows.Parse("0 4 18 0 #4D000000");
        _saved.Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) } };
    }

    // ================================================================ navigation and saving

    void Rebuild()
    {
        PendingCommits.Clear(); // the boxes are about to be replaced (half-typed text there is dropped)
        var config = _config = AppConfig.Load();
        L.Apply(config.Language); // before the dashboard's reload: this window is rebuilt first
        try { _lastWritten = System.IO.File.ReadAllText(AppConfig.FilePath); } catch { _lastWritten = null; }
        bool dark = Look.IsDark(config.SettingsLook);
        if (dark != _dark)
        {
            _dark = dark;
            Look.Apply(this, dark);
        }
        PaintChrome();
        TextChrome();
        _page = Math.Clamp(_page, 0, config.Pages.Count - 1);
        BuildNav();
        var looks = new[] { "light", "dark", "auto" };
        _look.Content = Segmented(new[] { "Light", "Dark", "Auto" }, Math.Max(0, Array.IndexOf(looks, (config.SettingsLook ?? "auto").ToLowerInvariant())),
            i => Change(c => c.SettingsLook = looks[i], rebuild: true), stretch: true);

        var offset = _resetScroll ? new Vector(0, 0) : _scroll.Offset;
        _resetScroll = false;
        _panel = new StackPanel { Margin = new Thickness(32, 24, 32, 72), MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Left };
        _panel.Children.Add(Hero(_section, config));
        if (config.IsFallback)
            Banner("config.json has an error, so Shoji is running on defaults and won't save anything over your file. " +
                   "Fix it (About & install → Edit config.json; a copy is in config.invalid.json) or reset to defaults.");
        switch (_section)
        {
            case "look": AddThemes(config); AddDayNight(config); AddDisplay(config); AddColors(config); break;
            case "weather": AddWeather(config); break;
            case "pages": AddPages(config); break;
            case "scenes": AddScenes(config); break;
            case "deck": AddDeck(config); AddObs(config); break;
            case "alerts": AddAlerts(config); break;
            case "phone": AddWeb(config); break;
            case "hardware": AddHardware(config); break;
            default: AddAbout(); break;
        }
        _scroll.Content = _panel;
        Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    void Go(string section)
    {
        _resetScroll = section != _section;
        _section = section;
        Rebuild();
    }

    IEnumerable<string> Matches()
    {
        var words = (_search.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in Sections)
        {
            string hay = $"{s.Name} {s.Subtitle} {s.Keywords} {L.T(s.Name)} {L.T(s.Subtitle)} {L.T(s.Keywords)}".ToLowerInvariant(); // English words find it too
            if (words.All(hay.Contains))
                yield return s.Id;
        }
    }

    void BuildNav()
    {
        _nav.Children.Clear();
        var shown = Matches().ToHashSet();
        foreach (var s in Sections)
        {
            if (!shown.Contains(s.Id))
                continue;
            bool selected = s.Id == _section;
            var bar = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-2, 0, 0, 0), Opacity = selected ? 1 : 0, Background = Look.Accent };
            var icon = Icons.Tile(SectionGlyph(s.Id), 30);
            icon.Margin = new Thickness(8, 0, 12, 0);
            var label = new TextBlock { Text = L.T(s.Name), VerticalAlignment = VerticalAlignment.Center, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Look.Text1 };
            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            if (NavBadge(s.Id, _config) is { } badge)
            {
                DockPanel.SetDock(badge, Dock.Right);
                badge.Margin = new Thickness(6, 0, 8, 0);
                row.Children.Add(badge);
            }
            row.Children.Add(label);
            var item = new Border { Child = new Grid { Children = { row, bar } }, Height = 44, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1), Background = selected ? Look.AccentSoft : Brushes.Transparent };
            if (!selected)
            {
                item.PointerEntered += (_, _) => item.Background = Look.Hover;
                item.PointerExited += (_, _) => item.Background = Brushes.Transparent;
            }
            string target = s.Id;
            Clickable(item, () => Go(target));
            _nav.Children.Add(item);
        }
        if (shown.Count == 0)
            _nav.Children.Add(new TextBlock { Text = L.T("Nothing matches. Try one word, like \"theme\" or \"fan\"."), TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(10, 8, 10, 0), Foreground = Look.Text2 });
    }

    static Control? NavBadge(string id, AppConfig config) => id switch
    {
        "pages" => Pill(config.Pages.Count.ToString(), PillKind.Neutral, small: true),
        "scenes" => config.ScenesEnabled && config.Scenes.Any(s => s.Enabled) ? Pill("On", PillKind.Good, small: true) : null,
        "alerts" => config.Alerts.Enabled ? Pill("On", PillKind.Good, small: true) : Pill("Off", PillKind.Neutral, small: true),
        "phone" => config.Web.Enabled ? Pill("On", PillKind.Good, small: true) : null,
        "hardware" => Sensors.LinuxSetup.Missing().Count > 0 ? Pill("Tips", PillKind.Warn, small: true) : null,
        _ => null,
    };

    /// <summary>The coloured banner at the top of each section: big icon, title, subtitle, kanji watermark.</summary>
    Control Hero(string id, AppConfig config)
    {
        var s = Sections.First(x => x.Id == id);
        var icon = Icons.Tile(SectionGlyph(id), 54, shadow: true);
        icon.Margin = new Thickness(0, 0, 18, 0);
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L.T(s.Name), FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Look.Text1 },
                new TextBlock { Text = L.T(s.Subtitle), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Foreground = Look.Text2 },
            },
        };
        var row = new DockPanel { Margin = new Thickness(22, 20) };
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
        var tint = Color.Parse(s.From);
        var wash = DrawCompat.Linear(new Point(0, 0.5), new Point(1, 0.5), false,
            (Color.FromArgb((byte)(_dark ? 0x38 : 0x30), tint.R, tint.G, tint.B), 0),
            (Color.FromArgb((byte)(_dark ? 0x10 : 0x0C), tint.R, tint.G, tint.B), 0.45),
            (Color.FromArgb(0, tint.R, tint.G, tint.B), 0.8));
        var kanji = new TextBlock
        {
            Text = s.Kanji, FontSize = 104, FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Noto Sans CJK JP"),
            Opacity = _dark ? 0.06 : 0.07, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -12, 24, -40), IsHitTestVisible = false, Foreground = Look.Text1,
        };
        return new Border
        {
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8), ClipToBounds = true,
            Background = Look.Card, BorderBrush = Look.Stroke,
            Child = new Grid { Children = { new Border { Background = wash }, kanji, row } },
        };
    }

    static Control? HeroStatus(string id, AppConfig config) => id switch
    {
        "scenes" => config.ScenesEnabled ? Pill(L.F("{0} active", config.Scenes.Count(s => s.Enabled)), PillKind.Good) : Pill("Paused", PillKind.Neutral),
        "alerts" => config.Alerts.Enabled ? Pill("On", PillKind.Good) : Pill("Off", PillKind.Neutral),
        "phone" => config.Web.Enabled ? Pill("Serving", PillKind.Good) : Pill("Off", PillKind.Neutral),
        "hardware" => Sensors.LinuxSetup.Missing().Count == 0 ? Pill("Everything readable", PillKind.Good) : Pill(L.F("{0} tips", Sensors.LinuxSetup.Missing().Count), PillKind.Warn),
        "about" => Pill(Autostart.Enabled ? "Starts at login" : "Manual start", Autostart.Enabled ? PillKind.Good : PillKind.Neutral),
        _ => null,
    };

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
            Message("config.json can't be read right now (probably a typo from editing it by hand), so nothing was saved. " +
                    "Fix the file, or use About & install → Reset everything.");
            return false;
        }
        try { _lastWritten = System.IO.File.ReadAllText(AppConfig.FilePath); } catch { _lastWritten = null; }
        _saved.Opacity = 1;
        _savedTimer.Stop();
        _savedTimer.Start();
        if (rebuild)
            Dispatcher.UIThread.Post(Rebuild);
        return true;
    }

    /// <summary>A small message box (Avalonia has none built in).</summary>
    void Message(string text, string? confirm = null, Action? onConfirm = null)
    {
        var dialog = new Window
        {
            Title = "Shoji", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Look.Card, Foreground = Look.Text1, RequestedThemeVariant = RequestedThemeVariant, FontFamily = FontFamily,
        };
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorDark1" })
            dialog.Resources[key] = Look.AccentColor;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
        if (confirm != null)
        {
            var ok = AccentButton(confirm, () => { dialog.Close(); onConfirm?.Invoke(); });
            buttons.Children.Add(ok);
            buttons.Children.Add(Button("Cancel", dialog.Close));
        }
        else
        {
            buttons.Children.Add(AccentButton("OK", dialog.Close));
        }
        dialog.Content = new StackPanel { Margin = new Thickness(22, 20), Children = { new TextBlock { Text = L.T(text), TextWrapping = TextWrapping.Wrap }, buttons } };
        dialog.ShowDialog(this);
    }
}
