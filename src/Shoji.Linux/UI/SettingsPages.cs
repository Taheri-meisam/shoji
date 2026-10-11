using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Pages & tiles and Control deck sections of Settings.</summary>
public sealed partial class SettingsWindow
{
    // ================================================================ Pages & tiles

    /// <summary>Opens Settings at Pages & tiles on a given page ("More settings…" on the dashboard's tile edit sheet).</summary>
    public static void OpenPage(Dashboard dashboard, int page)
    {
        Open(dashboard);
        if (_instance == null)
            return;
        _instance._page = Math.Max(0, page);
        _instance.Go("pages");
    }

    /// <summary>"Page N" with the first number from the page count up that no page uses yet.</summary>
    static string NewPageName(AppConfig c)
    {
        for (int n = c.Pages.Count + 1; ; n++)
        {
            string name = L.F("Page {0}", n);
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
        var pills = new WrapPanel { Margin = new Thickness(0, 16, 0, 10) };
        for (int i = 0; i < config.Pages.Count; i++)
        {
            int index = i;
            pills.Children.Add(Chip($"{i + 1}   {config.Pages[i].Name}", i == _page, () => { _page = index; Rebuild(); }));
        }
        var newPage = Chip("+   New page", false, () =>
        {
            if (Change(c => c.Pages.Add(new PageConfig { Name = NewPageName(c), Modules = ModuleCatalog.Preset("essentials") })))
                _page = config.Pages.Count;
            Rebuild();
        });
        ((TextBlock)newPage.Child!).Foreground = Look.AccentText;
        pills.Children.Add(newPage);
        _panel.Children.Add(pills);

        var page = config.Pages[_page];
        int p = _page;

        // Preview and page settings side by side, or one above the other in a narrow window.
        var top = new WrapPanel();
        var previewCard = Card(addToPanel: false);
        previewCard.Margin = new Thickness(0, 0, 12, 10);
        previewCard.Padding = new Thickness(18, 16, 18, 14);
        previewCard.VerticalAlignment = VerticalAlignment.Top;
        ((StackPanel)previewCard.Child!).Children.Add(new TextBlock { Text = L.T("ON THE SCREEN"), FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10), Foreground = Look.Text3 });
        ((StackPanel)previewCard.Child!).Children.Add(PagePreview(config, page));
        top.Children.Add(previewCard);

        var settings = Card(addToPanel: false);
        settings.Width = 400;
        settings.VerticalAlignment = VerticalAlignment.Top;
        Setting(settings, "Page name", null, TextField(page.Name, 170, v => Change(c => RenamePage(c, p, v ?? L.T("Page")))), icon: "edit");
        var columns = Combo(new[] { L.T("Same as Display"), "1", "2", "3", "4", "5", "6" }, Math.Clamp(page.Columns, 0, 6), i => Change(c => c.Pages[p].Columns = i, rebuild: true));
        columns.MinWidth = 170;
        Setting(settings, "Columns", null, columns, icon: "view_column");
        var cycle = new[] { 0, 15, 30, 60, 120, 300 };
        var rotate = Combo(new[] { "Off", "every 15 s", "every 30 s", "every minute", "every 2 minutes", "every 5 minutes" }.Select(L.T).ToList(),
            Math.Max(0, Array.IndexOf(cycle, config.PageCycleSeconds)), i => Change(c => c.PageCycleSeconds = cycle[i]));
        rotate.MinWidth = 170;
        Setting(settings, "Rotate pages", "All pages, when no scene is active", rotate, icon: "autorenew");
        Setting(settings, "Hold a tile to edit it on the dashboard", "Order, width, style, page, hide", Switch(config.LongPressEdit, v => Change(c => c.LongPressEdit = v)), icon: "touch_app");
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        var left = IconButton("arrow_back", "Move this page left", () => { if (Change(c => Swap(c.Pages, p, p - 1))) _page--; Rebuild(); });
        left.IsEnabled = p > 0;
        var right = IconButton("arrow_forward", "Move this page right", () => { if (Change(c => Swap(c.Pages, p, p + 1))) _page++; Rebuild(); });
        right.IsEnabled = p < config.Pages.Count - 1;
        var delete = IconButton("delete", "Delete this page", () => Message(L.F("Delete the page \"{0}\" and its tiles?", page.Name), "Delete", () =>
        {
            if (Change(c => c.Pages.RemoveAt(p)))
                _page = Math.Max(0, p - 1);
            Rebuild();
        }), danger: true);
        delete.IsEnabled = config.Pages.Count > 1;
        actions.Children.Add(new TextBlock { Text = L.T("Page"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Look.Text2 });
        actions.Children.Add(left);
        actions.Children.Add(right);
        actions.Children.Add(delete);
        ((StackPanel)settings.Child!).Children.Add(new Border { Height = 1, Background = Look.Divider });
        ((StackPanel)settings.Child!).Children.Add(actions);
        top.Children.Add(settings);
        _panel.Children.Add(top);

        AddShare(config); // export / import of the whole layout (SettingsShare.cs)

        Section("Layout presets", "Replaces this page's layout; tiles a preset doesn't use stay on the page, switched off.");
        var presets = new WrapPanel();
        foreach (var (id, name, description) in ModuleCatalog.Presets)
            presets.Children.Add(PresetCard(id, name, description, () => Change(c => ModuleCatalog.ApplyPreset(c.Pages[p], id), rebuild: true)));
        _panel.Children.Add(presets);

        Section(L.F("Tiles on \"{0}\"", page.Name), "Switch a tile off to hide it without losing its settings. Arrows change the order; tiles fill rows left to right.");
        for (int i = 0; i < page.Modules.Count; i++)
            _panel.Children.Add(TileRow(p, page.Modules[i], i, page.Modules.Count));
        if (page.Modules.Count == 0)
            _panel.Children.Add(Hint("No tiles yet: add some below, or pick a preset."));

        Section("Add a tile", null);
        var gallery = new WrapPanel();
        foreach (var info in ModuleCatalog.All.Where(i => !WindowsOnly.Contains(i.Type)))
        {
            var type = info.Type;
            var existing = page.Modules.FindIndex(m => m.Type == type && !m.Enabled);
            var icon = Icons.Tile(Icons.ForTile(type), 38);
            icon.Margin = new Thickness(0, 0, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var title = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Look.Text1, Inlines = new InlineCollection { new Run(L.T(info.Name)), new Run("  " + info.Kanji) { FontWeight = FontWeight.Normal, FontSize = 12, Foreground = Look.Text3 } } };
            var desc = new TextBlock { Text = L.T(info.Description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Height = 48, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 8), Foreground = Look.Text2 };
            var add = Button(existing >= 0 ? "Show again" : "+  Add", () => Change(c =>
            {
                var mods = c.Pages[p].Modules;
                int hidden = mods.FindIndex(m => m.Type == type && !m.Enabled);
                if (hidden >= 0) mods[hidden].Enabled = true;
                else mods.Add(ModuleCatalog.New(type));
            }, rebuild: true));
            add.Margin = new Thickness(0);
            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            dock.Children.Add(new StackPanel { Children = { title, desc, add } });
            gallery.Children.Add(HoverCard(dock, 246, new Thickness(14)));
        }
        _panel.Children.Add(gallery);
    }

    Border PresetCard(string id, string name, string description, Action apply)
    {
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var type in ModuleCatalog.Preset(id).Where(m => m.Enabled).Select(m => m.Type).Distinct().Take(6))
        {
            var t = Icons.Tile(Icons.ForTile(type), 22);
            t.Margin = new Thickness(0, 0, 4, 0);
            icons.Children.Add(t);
        }
        var title = new DockPanel();
        if (id == "main")
        {
            var pill = Pill("Best start", PillKind.Accent, small: true);
            DockPanel.SetDock(pill, Dock.Right);
            title.Children.Add(pill);
        }
        title.Children.Add(new TextBlock { Text = L.T(name), FontWeight = FontWeight.SemiBold, Foreground = Look.Text1 });
        var body = new StackPanel
        {
            Children =
            {
                icons, title,
                new TextBlock { Text = L.T(description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Height = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), Foreground = Look.Text2 },
            },
        };
        var card = HoverCard(body, 200, new Thickness(14, 12));
        ToolTip.SetTip(card, L.T("Use this layout on this page"));
        Clickable(card, apply);
        return card;
    }

    /// <summary>How the page's tiles will sit on the dashboard screen, using the dashboard's own layout rules.</summary>
    Control PagePreview(AppConfig config, PageConfig page)
    {
        var screen = ScreenPick.Find(Screens, config.Monitor);
        double sw = screen?.Bounds.Width ?? 1920, sh = screen?.Bounds.Height ?? 1080, aspect = sw / sh, scale = screen?.Scaling ?? 1;
        var all = page.Modules.Where(m => m.Enabled && ModuleCatalog.Find(m.Type) != null && !WindowsOnly.Contains(m.Type)).ToList();
        var plans = TileLayout.Plan(all.Select(m => m.Span).ToList(), sw / scale - 12, sh / scale - 12, page.Columns > 0 ? page.Columns : config.Columns, scale, config.Scale);
        var first = plans[0];
        int cols = first.Columns;
        var tiles = all.Skip(first.Start).Take(first.Count).ToList();
        var placement = TileLayout.Pack(tiles.Select(m => m.Span).ToList(), cols);
        double tileW = (sw / scale - 12) / first.Scale / cols;
        int rows = placement.Count == 0 ? 1 : placement.Max(x => x.Row) + 1;
        double tileH = (sh / scale - 12) / first.Scale / rows;
        var density = Responsive.For(tileW, tileH);

        double height = aspect >= 1 ? Math.Min(200, 340 / aspect) : 260, width = height * aspect;
        var grid = new Grid { Width = width, Height = height };
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        for (int i = 0; i < tiles.Count; i++)
        {
            var look = Icons.ForTile(tiles[i].Type);
            var c1 = Color.Parse(look.From);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) };
            label.Children.Add(new TextBlock { Text = look.Name, FontFamily = UI.Theme.Icons, FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.9 });
            label.Children.Add(new TextBlock { Text = tiles[i].Title ?? L.T(ModuleCatalog.Find(tiles[i].Type)?.Name ?? tiles[i].Type), FontSize = 10.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            var cell = new Border
            {
                Margin = new Thickness(2), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = label,
                Background = new SolidColorBrush(Color.FromArgb(0x48, c1.R, c1.G, c1.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, c1.R, c1.G, c1.B)),
            };
            Grid.SetRow(cell, placement[i].Row);
            Grid.SetColumn(cell, placement[i].Col);
            Grid.SetColumnSpan(cell, placement[i].Span);
            grid.Children.Add(cell);
        }
        var frame = new Border
        {
            Padding = new Thickness(5), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = grid, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x40)),
            BoxShadow = BoxShadows.Parse("0 3 14 0 #59000000"),
        };
        string how = density switch
        {
            Density.Compact => L.T("tiles show their compact view (main reading only)"),
            Density.Roomy => L.T("tiles have room for every detail"),
            _ => L.T("tiles show their usual detail"),
        };
        var info = new StackPanel { Margin = new Thickness(0, 10, 0, 0), MaxWidth = Math.Max(width + 12, 280), HorizontalAlignment = HorizontalAlignment.Left };
        info.Children.Add(new TextBlock { Text = L.F(cols == 1 ? "{0:0}×{1:0} at {2:0}% · {3} column · text {4:0} px · {5}" : "{0:0}×{1:0} at {2:0}% · {3} columns · text {4:0} px · {5}", sw, sh, scale * 100, cols, first.TextPx(scale), how), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text2 });
        if (plans.Count > 1)
            info.Children.Add(new TextBlock
            {
                Text = L.F("Too many tiles to read on this screen: the last {0} move to {1} (swipe to them).", all.Count - first.Count, plans.Count == 2 ? L.T("a second screen") : L.F("{0} more screens", plans.Count - 1)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Look.Warn,
            });
        return new StackPanel { Children = { frame, info } };
    }

    Control TileRow(int page, ModuleConfig m, int index, int count)
    {
        var info = ModuleCatalog.Find(m.Type);
        void Edit(Action<ModuleConfig> change, bool rebuild = false) => Change(c => change(c.Pages[page].Modules[index]), rebuild);
        bool windowsOnly = WindowsOnly.Contains(m.Type);

        var toggle = Switch(m.Enabled, on => Edit(x => x.Enabled = on, rebuild: true));
        toggle.Margin = new Thickness(14, 0, 2, 0);
        ToolTip.SetTip(toggle, L.T("Show this tile"));
        var icon = Icons.Tile(Icons.ForTile(m.Type), 36);
        icon.Margin = new Thickness(0, 0, 14, 0);
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Look.Text1, Inlines = new InlineCollection { new Run(L.T(info?.Name ?? m.Type)) } };
        if (!string.IsNullOrWhiteSpace(m.Title))
            title.Inlines.Add(new Run($"  “{m.Title}”") { FontWeight = FontWeight.Normal, Foreground = Look.AccentText });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(title);
        name.Children.Add(new TextBlock
        {
            Text = L.T(windowsOnly ? "Windows only: not shown on Linux" : info?.Description ?? "Unknown tile type – check config.json"),
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = windowsOnly ? Look.Warn : Look.Text2,
        });

        var controls = new WrapPanel { Margin = new Thickness(50, 10, 0, 0) };
        if (info is { Styles.Length: > 1 })
        {
            string themeDefault = UI.Theme.StyleFor(m.Type, null);
            var names = new List<string> { L.F("Theme ({0})", L.T(info.Styles.First(st => st.Id == themeDefault).Name)) };
            names.AddRange(info.Styles.Select(st => L.T(st.Name)));
            int current = m.Opt("Style") is { } chosen ? Array.FindIndex(info.Styles, st => st.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase)) + 1 : 0;
            var style = Combo(names, Math.Max(0, current), i => Edit(x => x.SetOpt("Style", i == 0 ? null : info.Styles[i - 1].Id)));
            style.MinWidth = 170;
            style.Margin = new Thickness(0, 0, 16, 6);
            controls.Children.Add(Labeled2("Style", style));
        }
        var width = Segmented(new[] { "1 col", "2 col", "Full" }, m.Span >= 3 ? 2 : Math.Max(0, m.Span - 1), i => Edit(x => x.Span = new[] { 1, 2, 9 }[i], rebuild: true));
        width.Margin = new Thickness(0, 0, 16, 6);
        width.HorizontalAlignment = HorizontalAlignment.Left;
        controls.Children.Add(Labeled2("Width", width));
        var up = IconButton("arrow_upward", "Move up", () => Change(c => Swap(c.Pages[page].Modules, index, index - 1), rebuild: true));
        var down = IconButton("arrow_downward", "Move down", () => Change(c => Swap(c.Pages[page].Modules, index, index + 1), rebuild: true));
        var remove = IconButton("delete", "Remove from this page (its settings are forgotten)", () => Change(c => c.Pages[page].Modules.RemoveAt(index), rebuild: true), danger: true);
        up.IsEnabled = index > 0;
        down.IsEnabled = index < count - 1;
        controls.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { up, down, remove } });

        var header = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(icon);
        header.Children.Add(toggle);
        header.Children.Add(name);
        if (!m.Enabled)
        {
            icon.Opacity = 0.4;
            name.Opacity = 0.55;
        }

        var ownEditor = m.Type switch
        {
            "worldclock" => new[] { "Zones" },
            "history" => new[] { "Series", "Hours" },
            _ => Array.Empty<string>(),
        };
        var options = new StackPanel { Margin = new Thickness(0, 4) };
        options.Children.Add(Field("Title", m.Title, "optional custom title", v => Edit(x => x.Title = v)));
        foreach (var option in (info?.OptionList ?? Array.Empty<OptionSpec>()).Where(o => !ownEditor.Contains(o.Key)))
        {
            if (option.Kind == OptionKind.Bool)
            {
                var box = Check(option.Hint, option.Get(m) is { } v && (v == "true" || v == "1"), on => Edit(x => option.Set(x, on ? "true" : null)));
                box.Margin = new Thickness(0, 8, 0, 2);
                options.Children.Add(box);
            }
            else if (option.Key == "City")
            {
                var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                row.Children.Add(new TextBlock { Text = L.T(option.Label), VerticalAlignment = VerticalAlignment.Center, Width = 120, Foreground = Look.Text1 });
                var picker = CityPicker(m.City, 300, v => Edit(x => x.City = v, rebuild: true));
                picker.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(picker);
                row.Children.Add(new TextBlock { Text = L.T("only to differ from Weather & time; clear it to use the shared location"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Foreground = Look.Text2 });
                options.Children.Add(row);
            }
            else
            {
                options.Children.Add(Field(option.Label, option.Get(m), option.Hint, v => Edit(x => option.Set(x, v)), option.Kind == OptionKind.Secret));
            }
        }
        if (m.Type == "deck")
        {
            var edit = Button("Edit buttons…", () => { _deck = (page, index); Go("deck"); });
            edit.Margin = new Thickness(0, 10, 0, 0);
            options.Children.Add(edit);
        }
        var expander = new Expander { Header = L.T("Options"), Content = options, Margin = new Thickness(50, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };

        var body = new StackPanel { Children = { header, controls } };
        if (m.Type == "websites")
            body.Children.Add(SitesEditor(m, Edit));
        if (m.Type == "worldclock")
            body.Children.Add(ZonesEditor(m, Edit));
        if (m.Type == "history")
            body.Children.Add(SeriesEditor(m, Edit));
        if (m.Type is "weather" or "forecast" || m.Type == "clock" && !m.OptBool("HideWeather"))
        {
            string where = !string.IsNullOrWhiteSpace(m.City) ? L.F("{0} (this tile's own city)", Sensors.WeatherService.Label(m.City))
                : !string.IsNullOrWhiteSpace(_config.City) ? Sensors.WeatherService.Label(_config.City) : L.T("no city set yet");
            var line = new WrapPanel { Margin = new Thickness(50, 8, 0, 0) };
            line.Children.Add(new TextBlock { Text = "location_on", FontFamily = UI.Theme.Icons, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Look.Text2 });
            line.Children.Add(new TextBlock { Text = L.F("Weather for {0}", where), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = Look.Text2 });
            line.Children.Add(Link("Change location", () => Go("weather")));
            body.Children.Add(line);
        }
        body.Children.Add(expander);
        return new Border
        {
            Padding = new Thickness(14, 12, 14, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            Child = body, Background = Look.Card, BorderBrush = Look.Stroke,
        };
    }

    Control SitesEditor(ModuleConfig m, Action<Action<ModuleConfig>, bool> edit)
    {
        var sites = m.Sites ?? new List<SiteRef>();
        var panel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
        for (int i = 0; i < sites.Count; i++)
        {
            int index = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2) };
            var url = TextField(sites[i].Url, 260, v => edit(x => { if (x.Sites != null && index < x.Sites.Count) x.Sites[index].Url = Sensors.WebsiteMonitor.Normalize(v)?.ToString() ?? v ?? ""; }, false));
            url.Watermark = "https://example.com";
            var name = TextField(sites[i].Name, 140, v => edit(x => { if (x.Sites != null && index < x.Sites.Count) x.Sites[index].Name = v; }, false));
            name.Watermark = L.T("Name (optional)");
            var contains = TextField(sites[i].Contains, 180, v => edit(x => { if (x.Sites != null && index < x.Sites.Count) x.Sites[index].Contains = v; }, false));
            contains.Watermark = L.T("Page must contain…");
            row.Children.Add(url);
            row.Children.Add(name);
            row.Children.Add(contains);
            row.Children.Add(IconButton("delete", "Remove this site", () => edit(x => x.Sites?.RemoveAt(index), true), danger: true));
            panel.Children.Add(row);
        }
        var add = sites.Count == 0 ? AccentButton("+  Add a site", () => edit(x => (x.Sites ??= new List<SiteRef>()).Add(new SiteRef { Url = "https://" }), true))
            : Button("+  Add a site", () => edit(x => (x.Sites ??= new List<SiteRef>()).Add(new SiteRef { Url = "https://" }), true));
        add.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(add);
        return panel;
    }

    Control ZonesEditor(ModuleConfig m, Action<Action<ModuleConfig>, bool> edit)
    {
        var zones = m.Zones is { Count: > 0 } z ? z : Modules.WorldClockModule.Defaults.ToList();
        var all = TimeZoneInfo.GetSystemTimeZones().OrderBy(t => t.BaseUtcOffset).ThenBy(t => t.Id).ToList();
        void EditZones(Action<List<ZoneRef>> change, bool rebuild) => edit(x =>
        {
            x.Zones = x.Zones is { Count: > 0 } ? x.Zones : Modules.WorldClockModule.Defaults.Select(d => new ZoneRef { Zone = d.Zone, Label = d.Label }).ToList();
            change(x.Zones);
        }, rebuild);
        string Show(TimeZoneInfo t) => $"(UTC{(t.BaseUtcOffset < TimeSpan.Zero ? "-" : "+")}{t.BaseUtcOffset:hh\\:mm}) {t.Id.Replace('_', ' ')}";

        var panel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
        for (int i = 0; i < zones.Count; i++)
        {
            int index = i;
            string id = zones[i].Zone;
            // Windows names ("Tokyo Standard Time") from a config made on Windows become IANA ids ("Asia/Tokyo").
            if (!all.Any(t => t.Id == id) && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
                id = iana;
            var names = all.Select(Show).ToList();
            int selected = all.FindIndex(t => t.Id == id);
            if (selected < 0)
            {
                names.Insert(0, zones[i].Zone + L.T(" (unknown)"));
                selected = 0;
            }
            int offset = names.Count - all.Count;
            var zone = Combo(names, selected, n => { if (n - offset >= 0) EditZones(list => { if (index < list.Count) list[index].Zone = all[n - offset].Id; }, false); });
            zone.MinWidth = 0;
            zone.Width = 300;
            var label = TextField(zones[i].Label, 140, v => EditZones(list => { if (index < list.Count) list[index].Label = v; }, false), "Name shown on the tile (empty = the zone's city)");
            label.Watermark = L.T("Name (optional)");
            label.Margin = new Thickness(8, 0, 0, 0);
            var remove = IconButton("delete", "Remove this city", () => EditZones(list => { if (index < list.Count) list.RemoveAt(index); }, true), danger: true);
            remove.IsEnabled = zones.Count > 1;
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2), Children = { zone, label, remove } });
        }
        var add = Button("+  Add a city", () => EditZones(list => list.Add(new ZoneRef { Zone = TimeZoneInfo.Local.Id }), true));
        add.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(add);
        return panel;
    }

    Control SeriesEditor(ModuleConfig m, Action<Action<ModuleConfig>, bool> edit)
    {
        var chosen = m.OptList("Series");
        if (chosen.Count == 0)
            chosen = new List<string> { "cpu.temp", "gpu.temp" };
        var panel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = L.T("Graphs"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6), Foreground = Look.Text2 });
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
        var span = Segmented(hours.Select(h => $"{h} h").ToArray(), current, i => edit(x => x.SetOpt("Hours", hours[i] == 24 ? null : hours[i].ToString()), false));
        span.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(Labeled2("Show the last", span));
        return panel;
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
        ("Screenshot of an area", new DeckButton { Icon = "camera", Label = "Snip", Action = "screenshot", Target = "snip" }),
        ("Steam", new DeckButton { Icon = "game", Label = "Steam", Action = "url", Target = "steam://open/main" }),
        ("Spotify", new DeckButton { Icon = "music", Label = "Spotify", Action = "launch", Target = "spotify" }),
        ("Calculator", new DeckButton { Icon = "calc", Label = "Calculator", Action = "launch", Target = "gnome-calculator" }),
        ("Terminal", new DeckButton { Icon = "terminal", Label = "Terminal", Action = "launch", Target = "gnome-terminal" }),
        ("Website", new DeckButton { Icon = "web", Label = "YouTube", Action = "url", Target = "https://youtube.com" }),
        ("Power profile: performance", new DeckButton { Icon = "bolt", Label = "Performance", Action = "powerplan", Target = "performance" }),
        ("Power profile: balanced", new DeckButton { Icon = "power", Label = "Balanced", Action = "powerplan", Target = "balanced" }),
        ("Show desktop (GNOME)", new DeckButton { Icon = "home", Label = "Desktop", Action = "keys", Target = "Super+D" }),
        ("Volume up", new DeckButton { Icon = "volume", Label = "Vol +", Action = "volume", Target = "+5" }),
        ("Lock screen", new DeckButton { Icon = "lock", Label = "Lock", Action = "lock" }),
        ("Sleep PC", new DeckButton { Icon = "sleep", Label = "Sleep", Action = "sleep" }),
        ("Run a command", new DeckButton { Icon = "code", Label = "Script", Action = "command", Target = "notify-send Shoji Hello" }),
    };

    void AddDeck(AppConfig config)
    {
        var decks = config.Pages.SelectMany((p, pi) => p.Modules.Select((m, mi) => (p, pi, m, mi))).Where(x => x.m.Type == "deck").ToList();
        if (decks.Count == 0)
        {
            _panel.Children.Add(Hint("There's no control deck yet."));
            _panel.Children.Add(AccentButton("Add a \"Deck\" page with a control deck", () => Change(c => c.Pages.Add(new PageConfig { Name = L.T("Deck"), Modules = ModuleCatalog.Preset("deck") }), rebuild: true)));
            return;
        }
        if (!decks.Any(d => (d.pi, d.mi) == _deck))
            _deck = (decks[0].pi, decks[0].mi);
        if (decks.Count > 1)
        {
            var pick = Combo(decks.Select(d => $"{d.p.Name}: {d.m.Title ?? L.T("Control deck")} #{d.mi + 1}").ToList(), decks.FindIndex(d => (d.pi, d.mi) == _deck), i =>
            {
                _deck = (decks[i].pi, decks[i].mi);
                Dispatcher.UIThread.Post(Rebuild);
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

        if (buttons.Count > 0)
        {
            var keys = new WrapPanel();
            foreach (var b in buttons)
                keys.Children.Add(DeckKey(b));
            var deckCard = Card();
            deckCard.Padding = new Thickness(18, 16, 12, 10);
            deckCard.Margin = new Thickness(0, 16, 0, 4);
            ((StackPanel)deckCard.Child!).Children.Add(new TextBlock { Text = L.T("ON THE DECK"), FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10), Foreground = Look.Text3 });
            ((StackPanel)deckCard.Child!).Children.Add(keys);
        }
        _panel.Children.Add(Hint("Keys go to the app in focus (needs xdotool on X11; on Wayland ydotool with ydotoold running, or wtype on Sway / Hyprland). Click a button's icon to pick another."));
        for (int i = 0; i < buttons.Count; i++)
            _panel.Children.Add(DeckRow(buttons[i], i, buttons.Count, EditButtons));

        var add2 = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        add2.Children.Add(AccentButton("+  Add button", () => EditButtons(b => b.Add(new DeckButton { Icon = "app", Label = L.T("New"), Action = "launch", Target = "gnome-text-editor" }), rebuild: true)));
        var examples = Combo(new[] { L.T("Add an example…") }.Concat(Examples.Select(e => L.T(e.Label))).ToList(), 0, i =>
        {
            if (i == 0) return;
            var e = Examples[i - 1].Button;
            EditButtons(b => b.Add(new DeckButton { Icon = e.Icon, Label = L.T(e.Label), Action = e.Action, Target = e.Target, Args = e.Args }), rebuild: true);
        });
        examples.Margin = new Thickness(0, 0, 8, 6);
        add2.Children.Add(examples);
        add2.Children.Add(Button("Reset to default buttons", () => Message("Replace these buttons with the default set?", "Replace",
            () => EditButtons(b => { b.Clear(); b.AddRange(DeckDefaults.Buttons()); }, rebuild: true))));
        _panel.Children.Add(add2);
    }

    Control DeckRow(DeckButton b, int i, int count, Action<Action<List<DeckButton>>, bool> edit)
    {
        void Set(Action<DeckButton> change, bool rebuild = false) => edit(list => change(list[i]), rebuild);
        var preview = UI.Icon.Make(b.Icon, 22, Look.AccentText);
        preview.VerticalAlignment = VerticalAlignment.Center;
        var key = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = preview, Margin = new Thickness(0, 0, 4, 4), VerticalAlignment = VerticalAlignment.Top, Background = Look.AccentSoft, BorderBrush = Look.Stroke };
        ToolTip.SetTip(key, L.T("Pick an icon"));
        Clickable(key, () => IconPicker(key, name => Set(x => x.Icon = name, rebuild: true)));
        var icon = TextField(b.Icon, 110, v => { Set(x => x.Icon = v ?? ""); UI.Icon.Set(preview, v); });
        var label = TextField(b.Label, 140, v => Set(x => x.Label = v ?? ""));
        var actions = DeckActions.All.ToList();
        int actionIndex = actions.FindIndex(a => a.Id.Equals(b.Action, StringComparison.OrdinalIgnoreCase));
        var action = Combo(actions.Select(a => L.T(a.Name)).ToList(), actionIndex, a => Set(x => x.Action = actions[a].Id, rebuild: true));
        action.MinWidth = 210;
        string hint = actionIndex >= 0 ? actions[actionIndex].TargetHint : "";
        Control Labeled(string text, Control control) => new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 4), Children = { new TextBlock { Text = L.T(text), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Look.Text2 }, control } };
        var top = new WrapPanel();
        top.Children.Add(Labeled("Icon", icon));
        top.Children.Add(Labeled("Label", label));
        top.Children.Add(Labeled("Does", action));
        var details = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        if (hint != "(nothing)")
            details.Children.Add(Field("Target", b.Target, hint, v => Set(x => x.Target = v)));
        if (b.Action == "launch")
            details.Children.Add(Field("Arguments", b.Args, "optional", v => Set(x => x.Args = v)));
        var up = IconButton("arrow_upward", "Move up", () => edit(list => Swap(list, i, i - 1), true));
        var down = IconButton("arrow_downward", "Move down", () => edit(list => Swap(list, i, i + 1), true));
        up.IsEnabled = i > 0;
        down.IsEnabled = i < count - 1;
        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Children = { up, down, IconButton("delete", "Delete button", () => edit(list => list.RemoveAt(i), true), danger: true) } };
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        DockPanel.SetDock(key, Dock.Left);
        dock.Children.Add(side);
        dock.Children.Add(key);
        dock.Children.Add(new StackPanel { Children = { top, details } });
        return new Border { Padding = new Thickness(14, 12, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = dock, Background = Look.Card, BorderBrush = Look.Stroke };
    }

    static Control DeckKey(DeckButton b)
    {
        var glyph = UI.Icon.Make(b.Icon, 24, Look.AccentText);
        var label = new TextBlock { Text = b.Label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 5, 4, 0), Foreground = Look.Text1 };
        var key = new Border
        {
            Width = 84, Height = 74, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8),
            Background = Look.Raised, BorderBrush = Look.Stroke, Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } },
        };
        if (!string.IsNullOrEmpty(b.Target))
            ToolTip.SetTip(key, b.Target);
        return key;
    }

    void IconPicker(Control anchor, Action<string> pick)
    {
        var grid = new WrapPanel { Width = 9 * 44 };
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true };
        ((ISetLogicalParent)popup).SetParent(anchor);
        foreach (var (name, ligature) in UI.Icon.Names.OrderBy(i => i.Key))
        {
            var cell = new Border
            {
                Width = 40, Height = 40, Margin = new Thickness(2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent,
                Child = new TextBlock { Text = ligature, FontFamily = UI.Theme.Icons, FontSize = 22, Foreground = Look.Text1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            ToolTip.SetTip(cell, name);
            cell.PointerEntered += (_, _) => cell.Background = Look.AccentSoft;
            cell.PointerExited += (_, _) => cell.Background = Brushes.Transparent;
            string chosen = name;
            Clickable(cell, () =>
            {
                popup.IsOpen = false;
                pick(chosen);
            });
            grid.Children.Add(cell);
        }
        popup.Child = new Border
        {
            Background = Look.Card, BorderBrush = Look.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 12, 12), BoxShadow = BoxShadows.Parse("0 4 16 0 #59000000"),
            Child = new StackPanel { Children = { grid, new TextBlock { Text = L.T("Or type any emoji or short text in the Icon box."), FontSize = 12, Foreground = Look.Text2, Margin = new Thickness(4, 8, 4, 2) } } },
        };
        popup.IsOpen = true;
    }
}
