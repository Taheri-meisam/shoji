using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>The sections of the Settings window (same content as on Windows, minus what Linux can't do).</summary>
public sealed partial class SettingsWindow
{
    /// <summary>Tile types that need Windows (per-app usage timing with window titles).</summary>
    static readonly HashSet<string> WindowsOnly = new(StringComparer.OrdinalIgnoreCase) { "activity" };

    // ================================================================ Appearance

    void AddThemes(AppConfig config)
    {
        var current = UI.Theme.Find(config.Theme).Id;
        Section("Theme", config.DayNight.Enabled
            ? "Day and night themes are on (below), so they decide what you see; this theme is used when they're switched off."
            : "Applies to the dashboard instantly. Also in the tray menu and on a deck button.");
        var groups = new[] { "All" }.Concat(Themes.All.Select(t => t.Group).Distinct()).ToList();
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var g in groups)
            chips.Children.Add(Chip(g == "All" ? L.F("All  {0}", Themes.All.Count) : g, g == _themeGroup, () => { _themeGroup = g; Rebuild(); }));
        _panel.Children.Add(chips);
        var wrap = new WrapPanel();
        foreach (var t in Themes.All.Where(t => _themeGroup == "All" || t.Group == _themeGroup))
            wrap.Children.Add(ThemeCard(t, t.Id == current));
        _panel.Children.Add(wrap);
    }

    Control ThemeCard(ThemeDef t, bool selected)
    {
        IBrush Solid(string c) => new SolidColorBrush(UI.Theme.Parse(c));
        IBrush bg = t.Background2 == null ? Solid(t.Background) : DrawCompat.Linear(new Point(0, 0), new Point(0, 1), false, (UI.Theme.Parse(t.Background), 0), (UI.Theme.Parse(t.Background2), 1));
        string title = t.UpperTitles ? "CPU" : "Cpu";
        title = t.Title switch { TitleStyle.Arrow => "▸ " + title, TitleStyle.Slash => "// " + title, TitleStyle.Prompt => "> cpu_", TitleStyle.Kanji => title + "  処理", _ => title };
        Border MiniTile(Control content) => new()
        {
            Background = Solid(t.Tile), BorderBrush = Solid(t.TileGlow ? t.Accent : t.TileBorder ?? t.Tile),
            BorderThickness = new Thickness(t.TileBorder != null || t.TileGlow ? 1 : 0),
            CornerRadius = new CornerRadius(t.Shape is TileShape.Hud or TileShape.Chamfer or TileShape.Drafting ? 0 : Math.Min(t.Radius, 9)),
            Padding = new Thickness(7, 5, 7, 6), Margin = new Thickness(3), Child = content,
        };
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0), ColumnDefinitions = new ColumnDefinitions("6*,4*") };
        var track = new Rectangle { Fill = Solid(t.Track) };
        Grid.SetColumn(track, 1);
        bar.Children.Add(new Rectangle { Fill = Solid(t.Accent) });
        bar.Children.Add(track);
        var numberTile = MiniTile(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = Solid(t.Accent), FontFamily = UI.Theme.Family(t.TitleFont ?? t.Font) },
                new TextBlock { Text = "42%", FontSize = 22, Foreground = Solid(t.Text), FontWeight = t.NumberWeight.ToAvalonia(), FontFamily = UI.Theme.Family(t.DisplayFont ?? t.Font) },
                bar,
            },
        });
        var arcTrack = new Path { Data = Geometry.Parse("M 6,30 A 20,20 0 1 1 46,30"), Stroke = Solid(t.Track), StrokeThickness = t.ThinGauges ? 2 : 5, StrokeLineCap = PenLineCap.Round };
        var arcValue = new Path { Data = Geometry.Parse("M 6,30 A 20,20 0 0 1 38,12"), Stroke = Solid(t.Accent2), StrokeThickness = t.ThinGauges ? 2 : 5, StrokeLineCap = PenLineCap.Round };
        var gaugeTile = MiniTile(new Grid
        {
            Children =
            {
                new Canvas { Width = 52, Height = 38, Children = { arcTrack, arcValue }, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "68°", FontSize = 11, Foreground = Solid(t.Text), FontFamily = UI.Theme.Family(t.DisplayFont ?? t.Font), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom },
            },
        });
        var minis = new Grid { Margin = new Thickness(8), ColumnDefinitions = new ColumnDefinitions("1.25*,*") };
        Grid.SetColumn(gaugeTile, 1);
        minis.Children.Add(numberTile);
        minis.Children.Add(gaugeTile);
        var preview = new Grid { Height = 118, Children = { new Border { CornerRadius = new CornerRadius(10), Background = bg, ClipToBounds = true, Child = minis } } };
        if (selected)
            preview.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 7, 0),
                Background = Look.Accent, BoxShadow = BoxShadows.Parse("0 1 6 0 #66000000"),
                Child = new TextBlock { Text = "check", FontFamily = UI.Theme.Icons, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        var card = new Border
        {
            Width = 226, Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(selected ? 2 : 1),
            Padding = new Thickness(selected ? 5 : 6), Background = Look.Card, BorderBrush = selected ? Look.Accent : Look.Stroke,
            Child = new StackPanel
            {
                Children =
                {
                    preview,
                    new TextBlock { Text = L.T(t.Name), FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 9, 6, 0), Foreground = Look.Text1 },
                    new TextBlock { Text = L.T(t.Description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 1, 6, 6), Foreground = Look.Text2 },
                },
            },
        };
        if (!selected)
        {
            card.PointerEntered += (_, _) => card.BorderBrush = Look.Text3;
            card.PointerExited += (_, _) => card.BorderBrush = Look.Stroke;
        }
        Clickable(card, () => Change(c => c.Theme = t.Id, rebuild: true));
        return card;
    }

    void AddDayNight(AppConfig config)
    {
        Section("Day and night", "Switch theme by the time of day. A scene with its own theme still wins.");
        var dn = config.DayNight;
        var names = Themes.All.Select(t => L.T(t.Name)).ToList();
        int IndexOf(string id) => Math.Max(0, Themes.All.ToList().FindIndex(t => t.Id == UI.Theme.Find(id).Id));
        var card = Card();
        Setting(card, "Use day and night themes", null, Switch(dn.Enabled, v => Change(c => c.DayNight.Enabled = v, rebuild: true)), icon: "contrast");
        Setting(card, "Day theme", L.F("from {0}", dn.DayStarts), Combo(names, IndexOf(dn.DayTheme), i => Change(c => c.DayNight.DayTheme = Themes.All[i].Id)), icon: "light_mode");
        Setting(card, "Night theme", L.F("from {0}", dn.NightStarts), Combo(names, IndexOf(dn.NightTheme), i => Change(c => c.DayNight.NightTheme = Themes.All[i].Id)), icon: "dark_mode");
        Setting(card, "Day starts", "24-hour time", ClockField(dn.DayStarts, 90, v => Change(c => c.DayNight.DayStarts = v ?? "07:00"), optional: true), icon: "schedule");
        Setting(card, "Night starts", "24-hour time", ClockField(dn.NightStarts, 90, v => Change(c => c.DayNight.NightStarts = v ?? "19:30"), optional: true), last: true, icon: "schedule");

        Section("Night mode", "Dim the dashboard while you sleep. A tap brings it back for a moment.");
        var night = config.Night;
        var nm = Card();
        Setting(nm, "Dim at night", null, Switch(night.Enabled, v => Change(c => c.Night.Enabled = v)), icon: "bedtime");
        Setting(nm, "From", "24-hour time", ClockField(night.From, 90, v => Change(c => c.Night.From = v ?? "23:00"), optional: true), icon: "schedule");
        Setting(nm, "To", "24-hour time", ClockField(night.To, 90, v => Change(c => c.Night.To = v ?? "07:00"), optional: true), icon: "schedule");
        Setting(nm, "Brightness", "0 % = black, like the screen is off", PercentSlider(Math.Clamp(night.Brightness, 0, 100), v => Change(c => c.Night.Brightness = v)), icon: "brightness_6");
        Setting(nm, "Awake after a tap", "seconds", Number(night.WakeSeconds, v => Change(c => c.Night.WakeSeconds = (int)Math.Clamp(Math.Round(v), 3, 3600))), last: true, icon: "touch_app");
    }

    void AddDisplay(AppConfig config)
    {
        Section("Display", null);
        var card = Card();
        var screens = Screens.All.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        var monitorValues = new List<string> { "auto" };
        var monitorNames = new List<string> { L.T("Auto (smallest secondary screen)") };
        for (int i = 0; i < screens.Count; i++)
        {
            monitorValues.Add((i + 1).ToString());
            monitorNames.Add(ScreenPick.Describe(screens[i], i + 1));
        }
        if (!monitorValues.Contains(config.Monitor, StringComparer.OrdinalIgnoreCase))
        {
            monitorValues.Add(config.Monitor);
            monitorNames.Add(config.Monitor);
        }
        int monitorIndex = monitorValues.FindIndex(v => v.Equals(config.Monitor, StringComparison.OrdinalIgnoreCase));
        var languages = L.Languages;
        Setting(card, "Language", "Of the dashboard and these settings", Combo(languages.Select(l => l.Id == "auto" ? L.T(l.Name) : l.Name).ToList(),
            Math.Max(0, languages.ToList().FindIndex(l => l.Id == (config.Language ?? "auto").Trim().ToLowerInvariant())), i => Change(c => c.Language = languages[i].Id, rebuild: true)), icon: "translate");
        Setting(card, "Show on", "Which screen the dashboard fills", Combo(monitorNames, monitorIndex, i => Change(c => c.Monitor = monitorValues[i])), icon: "monitor");
        var refresh = new[] { 500, 1000, 2000, 5000 };
        Setting(card, "Refresh every", null, Segmented(new[] { "0.5 s", "1 s", "2 s", "5 s" }, Nearest(refresh.Select(r => (double)r).ToList(), config.RefreshMs), i => Change(c => c.RefreshMs = refresh[i])), icon: "refresh");
        var scales = new[] { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5 };
        Setting(card, "Text size", "On top of the automatic sizing", Combo(scales.Select(s => $"{s * 100:0}%").ToArray(), Nearest(scales, config.Scale), i => Change(c => c.Scale = scales[i])), icon: "format_size");
        Setting(card, "Columns", "Pages can override this", Combo(new[] { L.T("Automatic"), "1", "2", "3", "4", "5", "6" }, Math.Clamp(config.Columns, 0, 6), i => Change(c => c.Columns = i)), icon: "view_column");
        Setting(card, "Animations", "Drifting petals, aurora, neon flicker, scan lines, weather", Switch(config.Animations, v => Change(c => c.Animations = v)), icon: "animation");
        Setting(card, "Album art backdrop", "The playing song's cover, blurred, behind a page with Now playing", Switch(config.AlbumArtBackdrop, v => Change(c => c.AlbumArtBackdrop = v)), icon: "album");
        Setting(card, "Pixel shift", "Moves the layout a few pixels now and then against OLED burn-in", Switch(config.PixelShift, v => Change(c => c.PixelShift = v)), icon: "grain");
        Setting(card, "Always on top", null, Switch(config.Topmost, v => Change(c => c.Topmost = v)), last: true, icon: "push_pin");
    }

    void AddColors(AppConfig config)
    {
        Section("Custom colours", "Change a colour of the theme. \"Theme\" keeps the theme's own; or type any #RRGGBB.");
        var card = Card();
        ColorRow(card, "Accent", "Gauges, bars and titles", "palette", config.AccentColor, (c, v) => c.AccentColor = v,
            new[] { "#4FC3F7", "#7C4DFF", "#FF4081", "#FF6E40", "#FFD740", "#69F0AE", "#18FFFF", "#FFFFFF" }, false);
        ColorRow(card, "Background", "Behind the tiles", "wallpaper", config.BackgroundColor, (c, v) => c.BackgroundColor = v,
            new[] { "#000000", "#0B0E14", "#101820", "#1A1423", "#14201A", "#201A14", "#F2EEE6", "#E8EEF5" }, false);
        ColorRow(card, "Tiles", "The tiles themselves (text adjusts to stay readable)", "dashboard", config.TileColor, (c, v) => c.TileColor = v,
            new[] { "#151A24", "#1E1E1E", "#0F2027", "#24182E", "#1B2A20", "#2A2118", "#FFFFFF", "#DDE5EE" }, true);
    }

    void ColorRow(Border card, string label, string description, string icon, string? current, Action<AppConfig, string?> set, string[] swatches, bool last)
    {
        var inner = (StackPanel)card.Child!;
        var head = new DockPanel { Margin = new Thickness(0, 12, 0, 6) };
        var glyph = new TextBlock { Text = icon, FontFamily = UI.Theme.Icons, FontSize = 20, Width = 24, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text2 };
        DockPanel.SetDock(glyph, Dock.Left);
        head.Children.Add(glyph);
        head.Children.Add(new StackPanel { Children = { new TextBlock { Text = L.T(label), Foreground = Look.Text1 }, new TextBlock { Text = L.T(description), FontSize = 12, Foreground = Look.Text2 } } });
        inner.Children.Add(head);
        var row = new WrapPanel { Margin = new Thickness(38, 0, 0, 12) };
        var themeChip = Chip("Theme", current == null, () => Change(c => set(c, null), rebuild: true));
        themeChip.Margin = new Thickness(0, 0, 10, 6);
        row.Children.Add(themeChip);
        foreach (var hex in swatches)
        {
            bool on = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.Parse(hex)), BorderThickness = new Thickness(on ? 3 : 1), BorderBrush = on ? Look.Accent : Look.Stroke,
            };
            ToolTip.SetTip(dot, hex);
            string pick = hex;
            Clickable(dot, () => Change(c => set(c, pick), rebuild: true));
            row.Children.Add(dot);
        }
        var box = TextField(current, 96, v =>
        {
            if (v == null) Change(c => set(c, null), rebuild: true);
            else if (Color.TryParse(v.StartsWith('#') ? v : "#" + v, out var col)) Change(c => set(c, $"#{col.R:X2}{col.G:X2}{col.B:X2}"), rebuild: true);
            else Dispatcher.UIThread.Post(Rebuild);
        }, "#RRGGBB");
        box.Watermark = "#RRGGBB";
        box.Margin = new Thickness(6, 0, 0, 6);
        row.Children.Add(box);
        inner.Children.Add(row);
        if (!last)
            inner.Children.Add(new Border { Height = 1, Background = Look.Divider });
    }

    // ================================================================ Weather & time

    static readonly Dictionary<string, (string Text, bool Ok)> PlaceCache = new(StringComparer.OrdinalIgnoreCase);
    int _lookup;

    void AddWeather(AppConfig config)
    {
        Section("Location", "Every weather tile, and clocks that show the weather, use this place.");
        var card = Card();
        var resultGlyph = new TextBlock { FontFamily = UI.Theme.Icons, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var picker = CityPicker(config.City, 280, v =>
        {
            if (Change(c => c.City = v))
                Lookup(v, result, resultGlyph);
        });
        Setting(card, "City", "Start typing and pick your place from the list.", picker, icon: "location_on");
        var status = new DockPanel { Margin = new Thickness(38, 0, 0, 14) };
        DockPanel.SetDock(resultGlyph, Dock.Left);
        status.Children.Add(resultGlyph);
        status.Children.Add(result);
        ((StackPanel)card.Child!).Children.Add(status);
        Lookup(config.City, result, resultGlyph);

        var own = config.Pages.SelectMany((pg, pi) => pg.Modules.Select((m, mi) => (pg, pi, m, mi)))
            .Where(x => x.m.Type is "weather" or "forecast" or "clock" && !string.IsNullOrWhiteSpace(x.m.City)).ToList();
        if (own.Count > 0)
        {
            Section("Tiles with their own city", "These win over the location above. One forecast is fetched, so the first of them sets the weather for all tiles.");
            var tiles = Card();
            for (int i = 0; i < own.Count; i++)
            {
                var (pg, pi, m, mi) = own[i];
                Setting(tiles, L.F("{0} on “{1}”", L.T(ModuleCatalog.Find(m.Type)?.Name ?? m.Type), pg.Name), L.F("uses {0}", Sensors.WeatherService.Label(m.City)),
                    Button("Use the location above", () => Change(c => c.Pages[pi].Modules[mi].City = null, rebuild: true)), last: i == own.Count - 1, icon: Icons.ForTile(m.Type).Name);
            }
        }

        Section("Units and clock", null);
        var units = Card();
        Setting(units, "Temperatures", "Weather and PC sensors", Segmented(new[] { "°C", "°F" }, config.Fahrenheit ? 1 : 0, i => Change(c => c.TemperatureUnit = i == 1 ? "F" : "C")), icon: "thermostat");
        Setting(units, "24-hour clock", null, Switch(config.Use24HourClock, v => Change(c => c.Use24HourClock = v)), last: true, icon: "schedule");
        AddCalendars(config);
    }

    async void Lookup(string? city, TextBlock result, TextBlock glyph)
    {
        string key = (city ?? "").Trim(), cacheKey = L.Language + ":" + key; // the result is cached as text, in a language
        int ticket = ++_lookup;
        void Show(string text, string? state)
        {
            result.Text = text;
            glyph.Text = state switch { "ok" => "check_circle", "warn" => "warning", _ => "search" };
            glyph.Foreground = state switch { "ok" => Look.Good, "warn" => Look.Warn, _ => Look.Text2 };
            result.Foreground = state == null ? Look.Text2 : Look.Text1;
        }
        if (key.Length == 0)
        {
            Show(L.T("No city set yet: type one above to see the weather."), "warn");
            return;
        }
        if (PlaceCache.TryGetValue(cacheKey, out var cached))
        {
            Show(cached.Text, cached.Ok ? "ok" : "warn");
            return;
        }
        Show(L.F("Looking up “{0}”…", Sensors.WeatherService.Label(key)), null);
        string text;
        bool ok;
        try
        {
            var place = await Sensors.WeatherService.GeocodeAsync(key);
            ok = place != null;
            text = place != null ? L.F("Weather for {0}", place.FullName) : L.F("Couldn't find “{0}”. Check the spelling, or add the country (“{0}, US”).", key);
            PlaceCache[cacheKey] = (text, ok);
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

    /// <summary>A city box that searches as you type and lists matching places; picking one saves its name and position.</summary>
    Control CityPicker(string? value, double width, Action<string?> save)
    {
        var box = new TextBox { Text = Sensors.WeatherService.Label(value), Width = width, Watermark = L.T("Type a city…") };
        string committed = box.Text ?? "";
        var list = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = box, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = Look.Card, BorderBrush = Look.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4), Margin = new Thickness(0, 4, 12, 12), MinWidth = width, MaxWidth = 480, Child = list,
                BoxShadow = BoxShadows.Parse("0 4 16 0 #59000000"),
            },
        };
        ((ISetLogicalParent)popup).SetParent(box);
        var results = new List<Sensors.WeatherService.Place>();
        var rows = new List<Border>();
        int highlighted = -1;
        bool picking = false;
        System.Threading.CancellationTokenSource? cts = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

        void Paint()
        {
            for (int i = 0; i < rows.Count; i++)
                rows[i].Background = i == highlighted ? Look.AccentSoft : Brushes.Transparent;
        }
        void Pick(Sensors.WeatherService.Place p)
        {
            timer.Stop();
            cts?.Cancel();
            popup.IsOpen = false;
            picking = true;
            box.Text = p.FullName;
            box.CaretIndex = box.Text.Length;
            picking = false;
            committed = box.Text;
            PendingCommits.Remove(CommitTyped);
            save(Sensors.WeatherService.Store(p));
        }
        void Message(string text)
        {
            list.Children.Clear();
            rows.Clear();
            results.Clear();
            highlighted = -1;
            list.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Look.Text2, Margin = new Thickness(10, 8), TextWrapping = TextWrapping.Wrap });
            popup.IsOpen = true;
        }
        async void Search()
        {
            timer.Stop();
            string query = (box.Text ?? "").Trim();
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
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"City search failed: {ex.Message}");
                if (mine == cts && box.IsFocused)
                    Message(L.T("Couldn't search right now (offline?). You can still type the name and press Enter."));
                return;
            }
            if (mine != cts || !box.IsFocused)
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
                var line = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Look.Text1, Inlines = new InlineCollection { new Run(p.Name) { FontWeight = FontWeight.SemiBold } } };
                if (where.Length > 0)
                    line.Inlines.Add(new Run("  " + where) { Foreground = Look.Text2 });
                var coords = new TextBlock { Text = FormattableString.Invariant($"{p.Lat:0.00}, {p.Lon:0.00}"), FontSize = 11, Foreground = Look.Text3, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var dock = new DockPanel();
                DockPanel.SetDock(coords, Dock.Right);
                dock.Children.Add(coords);
                dock.Children.Add(line);
                var row = new Border { Child = dock, Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(7), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
                int index = rows.Count;
                var place = p;
                row.PointerEntered += (_, _) => { highlighted = index; Paint(); };
                row.PointerPressed += (_, e) => { e.Handled = true; Pick(place); };
                rows.Add(row);
                list.Children.Add(row);
            }
            Paint();
            popup.IsOpen = true;
        }

        timer.Tick += (_, _) => Search();
        box.DetachedFromVisualTree += (_, _) => { timer.Stop(); cts?.Cancel(); };
        box.TextChanged += (_, _) =>
        {
            if (picking || (box.Text ?? "") == committed)
                return;
            PendingCommits.Add(CommitTyped);
            popup.IsOpen = false;
            results.Clear();
            highlighted = -1;
            timer.Stop();
            timer.Start();
        };
        box.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
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
                else if ((box.Text ?? "") != committed)
                {
                    popup.IsOpen = false;
                    CommitTyped();
                }
            }
            else if (e.Key == Key.Escape && popup.IsOpen)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        void CommitTyped()
        {
            PendingCommits.Remove(CommitTyped);
            if ((box.Text ?? "") == committed)
                return;
            committed = box.Text ?? "";
            save(string.IsNullOrWhiteSpace(committed) ? null : committed.Trim());
        }
        box.LostFocus += (_, _) =>
        {
            // Clicking a place in the list doesn't take focus from the box; anything else commits what's typed.
            // (The list lives in the popup's own root, so ask its content: the Popup control itself is never "over".)
            if (popup.IsOpen && popup.Child?.IsPointerOver == true)
                return;
            timer.Stop();
            popup.IsOpen = false;
            CommitTyped();
        };
        return box;
    }

    // ================================================================ Scenes

    void AddScenes(AppConfig config)
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 10);
        Setting(card, "Automatic scenes", "Also in the tray menu and on a deck button", Switch(config.ScenesEnabled, v => Change(c => c.ScenesEnabled = v, rebuild: true)), last: true, icon: "auto_awesome");
        // App and full-screen conditions work for X11 / XWayland windows; native Wayland ones only with GNOME's Window Calls extension.
        var recent = Sensors.LinuxFocus.Shared.RecentApps;
        bool nativeWayland = Sensors.LinuxFocus.IsWayland && Sensors.LinuxFocus.Shared.WindowCalls != true;
        _panel.Children.Add(Hint((recent.Count == 0
            ? L.T("All conditions you fill in must match; the first matching scene wins. When none matches, you're back on the page you were on.")
            : L.F("Recently used apps (for the app fields): {0}.", string.Join(", ", recent))) +
            (nativeWayland ? L.T(" App and full-screen conditions work for X11 / XWayland apps (most games). Native Wayland apps on GNOME need the 'Window Calls' extension.") : "")));
        var pageNames = new List<string> { L.T("(stay on current page)") };
        pageNames.AddRange(config.Pages.Select(p => p.Name));
        var themeNames = new List<string> { L.T("(keep theme)") };
        themeNames.AddRange(Themes.All.Select(t => L.T(t.Name)));
        for (int i = 0; i < config.Scenes.Count; i++)
            _panel.Children.Add(SceneCard(config, config.Scenes[i], i, pageNames, themeNames));
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(AccentButton("+  New scene", () => Change(c => c.Scenes.Add(new SceneConfig { Name = L.F("Scene {0}", c.Scenes.Count + 1) }), rebuild: true)));
        row.Children.Add(Button("Add the example scenes", () => Change(c => c.Scenes.AddRange(AppConfig.DefaultScenes().Where(d => !c.Scenes.Any(s => s.Name == d.Name))), rebuild: true)));
        _panel.Children.Add(row);
    }

    Control SceneCard(AppConfig config, SceneConfig sc, int i, List<string> pageNames, List<string> themeNames)
    {
        void Edit(Action<SceneConfig> change, bool rebuild = false) => Change(c => change(c.Scenes[i]), rebuild);
        var card = Card(addToPanel: false);
        card.Padding = new Thickness(20, 14, 20, 6);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var up = IconButton("arrow_upward", "Move up (earlier scenes win)", () => Change(c => Swap(c.Scenes, i, i - 1), rebuild: true));
        var down = IconButton("arrow_downward", "Move down", () => Change(c => Swap(c.Scenes, i, i + 1), rebuild: true));
        up.IsEnabled = i > 0;
        down.IsEnabled = i < config.Scenes.Count - 1;
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        buttons.Children.Add(IconButton("delete", "Delete scene", () => Change(c => c.Scenes.RemoveAt(i), rebuild: true), danger: true));
        var enabled = Switch(sc.Enabled, v => Edit(x => x.Enabled = v, rebuild: true));
        enabled.Margin = new Thickness(14, 0, 2, 0);
        buttons.Children.Add(enabled);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var icon = Icons.Tile(SectionGlyph("scenes"), 34);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.Opacity = sc.Enabled ? 1 : 0.4;
        head.Children.Add(icon);
        var name = TextField(sc.Name, 240, v => Edit(x => x.Name = v ?? L.T("Scene")));
        name.FontWeight = FontWeight.SemiBold;
        name.HorizontalAlignment = HorizontalAlignment.Left;
        head.Children.Add(name);
        ((StackPanel)card.Child!).Children.Add(head);

        static string? List(List<string>? l) => l == null || l.Count == 0 ? null : string.Join(", ", l);
        static List<string>? Parse(string? s) => s?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() is { Count: > 0 } l ? l : null;
        Setting(card, "When the app in focus is", "firefox, *launcher*, /steamapps/", TextField(List(sc.Apps), 300, v => Edit(x => x.Apps = Parse(v))), icon: "apps");
        Setting(card, "…or it's full screen", "games, full-screen video", Switch(sc.Fullscreen, v => Edit(x => x.Fullscreen = v)), icon: "fullscreen");
        Setting(card, "Except these apps", "never trigger for them", TextField(List(sc.ExceptApps), 300, v => Edit(x => x.ExceptApps = Parse(v))), icon: "block");
        Setting(card, "Away for (minutes)", "no keyboard or mouse", TextField(sc.IdleMinutes?.ToString(), 90, v => Edit(x => x.IdleMinutes = int.TryParse(v, out int n) && n > 0 ? n : null)), icon: "bedtime");
        var time = new StackPanel { Orientation = Orientation.Horizontal };
        time.Children.Add(ClockField(sc.From, 80, v => Edit(x => x.From = v), optional: true));
        time.Children.Add(new TextBlock { Text = L.T("  to  "), VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text2 });
        time.Children.Add(ClockField(sc.To, 80, v => Edit(x => x.To = v), optional: true));
        Setting(card, "Between", "e.g. 22:00 to 06:00", time, icon: "schedule");
        int pageIndex = string.IsNullOrWhiteSpace(sc.Page) ? 0 : config.PageIndex(sc.Page) + 1;
        Setting(card, "Then show page", null, Combo(pageNames, Math.Max(0, pageIndex), x => Edit(s => s.Page = x == 0 ? null : pageNames[x])), icon: "dashboard");
        int themeIndex = string.IsNullOrWhiteSpace(sc.Theme) ? 0 : Themes.All.ToList().FindIndex(t => t.Id == UI.Theme.Find(sc.Theme).Id) + 1;
        Setting(card, "…and use theme", null, Combo(themeNames, themeIndex, t => Edit(s => s.Theme = t == 0 ? null : Themes.All[t - 1].Id)), last: true, icon: "palette");
        return card;
    }

    // ================================================================ Alerts

    void AddAlerts(AppConfig config)
    {
        var a = config.Alerts;
        var main = Card();
        main.Margin = new Thickness(0, 16, 0, 10);
        Setting(main, "Alerts", "A banner flashes when a problem lasts 5 seconds; tap it to hide for 10 minutes", Switch(a.Enabled, v => Change(c => c.Alerts.Enabled = v, rebuild: true)), icon: "notifications_active");
        Setting(main, "Sound", null, Switch(a.Sound, v => Change(c => c.Alerts.Sound = v)), last: true, icon: "volume_up");
        Section("Limits", "The Status board tile lights up from the same limits. 0 turns a check off.");
        var card = Card();
        Setting(card, "CPU hotter than", "°C", Number(a.CpuTemp, v => Change(c => c.Alerts.CpuTemp = v)), icon: "memory");
        Setting(card, "GPU hotter than", "°C", Number(a.GpuTemp, v => Change(c => c.Alerts.GpuTemp = v)), icon: "developer_board");
        Setting(card, "Memory above", "%", Number(a.RamPercent, v => Change(c => c.Alerts.RamPercent = v)), icon: "dns");
        Setting(card, "Drive free below", "% (needs a Drives or Status tile)", Number(a.DiskFreePercent, v => Change(c => c.Alerts.DiskFreePercent = v)), icon: "storage");
        Setting(card, "Ping above", "ms (needs a Ping tile)", Number(a.PingMs, v => Change(c => c.Alerts.PingMs = v)), last: true, icon: "network_check");
        Section("Value colours", "When temperatures on the tiles turn orange, then red (always in °C).");
        var colours = Card();
        Setting(colours, "Warm from", "°C, shown in orange", Number(config.TempWarn, v => Change(c => c.TempWarn = v)), icon: "thermostat");
        Setting(colours, "Hot from", "°C, shown in red", Number(config.TempCrit, v => Change(c => c.TempCrit = v)), last: true, icon: "local_fire_department");
        Section("Websites", null);
        var sites = Card();
        Setting(sites, "Website down", "Sites on a Websites tile, after two failed checks in a row", Switch(a.SiteDown, v => Change(c => c.Alerts.SiteDown = v)), icon: "public");
        Setting(sites, "SSL certificate expires within", "days (domains: twice this)", Number(a.CertDays, v => Change(c => c.Alerts.CertDays = v)), last: true, icon: "lock");
    }

    // ================================================================ Phone dashboard

    void AddWeb(AppConfig config)
    {
        var web = config.Web;
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 10);
        Setting(card, "Serve the web dashboard", web.AllowDeck ? "Phone buttons are on (below)" : "Read-only: it can't control the PC", Switch(web.Enabled, v => Change(c =>
        {
            c.Web.Enabled = v;
            if (v && string.IsNullOrEmpty(c.Web.Key))
                c.Web.Key = WebServer.NewKey();
        }, rebuild: true)), icon: "smartphone");
        Setting(card, "Port", null, TextField(web.Port.ToString(), 90, v => Change(c => c.Web.Port = int.TryParse(v, out int n) && n is > 0 and < 65536 ? n : 8787, rebuild: true)), icon: "lan");
        var deck = Switch(web.AllowDeck, v => Change(c => c.Web.AllowDeck = v, rebuild: true));
        deck.IsEnabled = web.Enabled;
        Setting(card, "Let the phone press deck buttons", "Anyone with the link can press them. Buttons that run commands or scripts are never shown.", deck, last: true, icon: "touch_app");
        _panel.Children.Add(Hint("Open the dashboard in a browser on any device on your home network. The link contains a secret key; anyone with it on your network can see your stats."));
        if (!web.Enabled || string.IsNullOrEmpty(web.Key))
            return;
        string url = WebServer.Url(web);
        var connect = Card();
        connect.Padding = new Thickness(20, 20, 20, 18);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
            grid.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(10), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 22, 0), Child = new Image { Source = new Bitmap(new System.IO.MemoryStream(png)), Width = 168, Height = 168 } });
        }
        catch (Exception ex)
        {
            Log.Write($"QR code failed: {ex.Message}");
        }
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = L.T("Scan with your phone"), FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Look.Text1 });
        right.Children.Add(new TextBlock { Text = L.F("Point the camera at the code, or type the link below. If it doesn't load, allow the port through the firewall (sudo ufw allow {0}/tcp).", web.Port), TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 4, 0, 14), Foreground = Look.Text2 });
        right.Children.Add(new TextBox { Text = url, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 10) });
        var row = new WrapPanel();
        row.Children.Add(AccentButton("Copy link", () => Clipboard?.SetTextAsync(url)));
        row.Children.Add(Button("Open", () => App.Open(url)));
        row.Children.Add(Button("New key", () => Change(c => c.Web.Key = WebServer.NewKey(), rebuild: true)));
        right.Children.Add(row);
        grid.Children.Add(right);
        ((StackPanel)connect.Child!).Children.Add(grid);
    }

    // ================================================================ Hardware

    void AddHardware(AppConfig config)
    {
        var missing = Sensors.LinuxSetup.Missing();
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 10);
        card.Padding = new Thickness(20, 18, 20, 16);
        bool ready = missing.Count == 0;
        var badge = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = ready ? Look.GoodSoft : Look.WarnSoft,
            Child = new TextBlock { Text = ready ? "check" : "tips_and_updates", FontFamily = UI.Theme.Icons, FontSize = 24, Foreground = ready ? Look.Good : Look.Warn, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = L.T(ready ? "Everything Shoji can read is readable" : "A few things can be unlocked"), FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Look.Text1 });
        text.Children.Add(new TextBlock { Text = L.T(ready ? "CPU, GPU, memory, drives, network, temperatures, fans, media and audio all work." : "Most tiles already work. The list below says what each missing piece adds and the one command that enables it."), TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 4, 0, 4), Foreground = Look.Text2 });
        var dock = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        dock.Children.Add(text);
        ((StackPanel)card.Child!).Children.Add(dock);

        var gpus = Sensors.HardwareSensors.GpuNames;
        if (gpus.Count > 1)
        {
            Section("Graphics card", null);
            var gpuCard = Card();
            Setting(gpuCard, "GPU tiles show", "Power adds up every graphics card either way", Combo(gpus, Math.Clamp(config.GpuIndex, 0, gpus.Count - 1), i => Change(c => c.GpuIndex = i)), last: true, icon: "developer_board");
        }

        Section("What Shoji can read", null);
        var list = Card();
        var items = Sensors.LinuxSetup.Capabilities();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            Control right = it.Ok ? Pill("Working", PillKind.Good)
                : it.Setup is { } setup ? AccentButton("Set up", () => { try { setup(); } catch (Exception ex) { Log.Write($"{it.Name} setup failed: {ex.Message}"); } Rebuild(); })
                : it.Fix == null ? Pill("Not available", PillKind.Neutral) : Button("Copy fix", () => Clipboard?.SetTextAsync(it.Fix));
            Setting(list, it.Name, it.Ok ? it.Detail : it.Fix == null ? it.Detail : it.Detail + "\n" + it.Fix, right, last: i == items.Count - 1, icon: it.Icon);
        }
    }

    // ================================================================ About & install

    void AddAbout()
    {
        var brand = Card();
        brand.Margin = new Thickness(0, 16, 0, 10);
        brand.Padding = new Thickness(22, 20);
        var logo = new Image { Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://shoji/Assets/shoji.png"))), Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0) };
        var name = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Look.Text1, Inlines = new InlineCollection { new Run("Shoji"), new Run("  障子") { FontWeight = FontWeight.Normal, Foreground = Look.Text3 } } };
        var about = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                name,
                new TextBlock { Text = L.F("Version {0} for Linux · a dashboard and touch deck for your small screen", AppPaths.VersionText), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Foreground = Look.Text2 },
                new TextBlock { Text = L.T("A shoji is the paper screen of a Japanese house: light comes through it, softly."), FontStyle = FontStyle.Italic, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Look.Text3 },
            },
        };
        var head = new DockPanel();
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        head.Children.Add(about);
        ((StackPanel)brand.Child!).Children.Add(head);

        Section("On this PC", null);
        var card = Card();
        Setting(card, "Start at login", "Opens the dashboard when you sign in", Switch(Autostart.Enabled, v => { Autostart.Set(v); Dispatcher.UIThread.Post(Rebuild); }), icon: "power_settings_new");
        bool inMenu = Linux.Installer.InAppMenu;
        Setting(card, "App menu", inMenu ? "Shoji is in your app grid; opening it again shows these settings" : "Add Shoji to the app grid / launcher with its icon",
            Linux.Installer.FromPackage && inMenu ? Pill("From the package", PillKind.Neutral) // its entry is in /usr/share, owned by the .deb
            : inMenu ? Button("Remove", () => { Linux.Installer.RemoveFromAppMenu(); Rebuild(); }) : AccentButton("Add to app menu", () => { Linux.Installer.AddToAppMenu(); Rebuild(); }), icon: "apps");
        Setting(card, "Program", AppPaths.ExePath, Button("Show folder", () => App.Open(AppPaths.ExeDir)), last: true, icon: "folder");

        AddUpdates(_config);

        Section("Files", "World clock zones, colour overrides and more can also be set in config.json.");
        var files = Card();
        Setting(files, "config.json", "Every setting, as text", Button("Edit…", () => App.Open(AppConfig.FilePath)), icon: "description");
        Setting(files, "Data folder", AppConfig.Folder, Button("Open", () => App.Open(AppConfig.Folder)), icon: "folder_open");
        Setting(files, "Reset everything", "Theme, pages, tiles, scenes and deck buttons go back to defaults", Button("Reset…", () =>
            Message("Reset all Shoji settings (theme, pages, tiles, scenes, deck buttons) to defaults?", "Reset", () =>
            {
                new AppConfig { LayoutVersion = AppConfig.CurrentLayoutVersion }.Save();
                _page = 0;
                Dispatcher.UIThread.Post(Rebuild);
            })), last: true, icon: "restart_alt");
    }
}
