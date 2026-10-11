using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Shoji.Core;

/// <summary>
/// A layout someone shares: the pages with their tiles and deck buttons, the theme and colours, day / night and night
/// mode, optionally the scenes. Never any secret (see <see cref="LayoutShare.Sanitize"/>), and nothing about this PC
/// (monitor, phone dashboard, sensors setup).
/// </summary>
public sealed class SharedLayout
{
    /// <summary>Format version; a newer Shoji may write a higher one.</summary>
    public int Shoji { get; set; } = 1;
    /// <summary>The Shoji version that wrote it (informational).</summary>
    public string? App { get; set; }
    public List<PageConfig> Pages { get; set; } = new();
    public string? Theme { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? TileColor { get; set; }
    public DayNightConfig? DayNight { get; set; }
    public NightConfig? Night { get; set; }
    public List<SceneConfig>? Scenes { get; set; }
}

/// <summary>What the import preview says about a layout.</summary>
public sealed record LayoutSummary(int Pages, int Tiles, string? ThemeName, int DeckButtons, int Scenes, IReadOnlyList<string> RunsPrograms)
{
    /// <summary>"3 pages, 14 tiles, theme Matcha, 12 deck buttons".</summary>
    public string Text
    {
        get
        {
            var parts = new List<string> { Count(Pages, L.T("1 page"), L.T("{0} pages")), Count(Tiles, L.T("1 tile"), L.T("{0} tiles")) };
            if (ThemeName != null)
                parts.Add(L.F("theme {0}", ThemeName));
            if (DeckButtons > 0)
                parts.Add(Count(DeckButtons, L.T("1 deck button"), L.T("{0} deck buttons")));
            if (Scenes > 0)
                parts.Add(Count(Scenes, L.T("1 scene"), L.T("{0} scenes")));
            return string.Join(", ", parts);
        }
    }

    /// <summary>A warning when some of its deck buttons start programs or run commands, else null.</summary>
    public string? Warning => RunsPrograms.Count == 0 ? null
        : RunsPrograms.Count == 1
            ? L.F("1 deck button starts a program or runs a command ({0}). Only import layouts from people you trust, and check those buttons in Settings → Control deck.", Programs)
            : L.F("{0} deck buttons start programs or run commands ({1}). Only import layouts from people you trust, and check those buttons in Settings → Control deck.", RunsPrograms.Count, Programs);

    string Programs => string.Join(", ", RunsPrograms.Take(4)) + (RunsPrograms.Count > 4 ? "…" : "");

    /// <summary>"1 page" / "3 pages" (both already translated; <paramref name="many"/> has a {0}).</summary>
    static string Count(int n, string one, string many) => n == 1 ? one : string.Format(L.Culture, many, n);
}

/// <summary>
/// Export and import of layouts: a .shoji.json file, or a short code ("shoji1:" + base64url of the gzipped JSON) to
/// paste in a chat. Shared by both editions.
/// </summary>
public static class LayoutShare
{
    public const string CodePrefix = "shoji1:";
    public const string FileExtension = ".shoji.json";

    // Limits for what an import may hold: generous for real layouts, small enough that a bad paste can't hurt.
    const int MaxCodeChars = 512 * 1024, MaxJsonBytes = 4 * 1024 * 1024, MaxPages = 50, MaxTiles = 600, MaxButtons = 1000;

    static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static readonly JsonSerializerOptions CompactOptions = new(FileOptions) { WriteIndented = false };

    /// <summary>Option keys that hold something private whatever tile they're on (tokens, API keys, passwords, headers, calendar links).</summary>
    static readonly string[] SecretWords = { "token", "key", "password", "passwd", "secret", "apikey", "header", "calendars", "cookie", "auth" };

    /// <summary>URL query parameters that carry credentials (?token=…, &amp;api_key=…, &amp;sig=…).</summary>
    static readonly Regex SecretQuery = new("token|key|secret|password|passwd|auth|sig|signature|session|cookie|credential", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex HexColour = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------- export

    /// <summary>A copy of the config's layout, with every secret taken out.</summary>
    public static SharedLayout Export(AppConfig config, bool includeScenes)
    {
        var layout = new SharedLayout
        {
            App = AppPaths.VersionText,
            Pages = config.Pages,
            Theme = config.Theme,
            AccentColor = config.AccentColor,
            BackgroundColor = config.BackgroundColor,
            TileColor = config.TileColor,
            DayNight = config.DayNight,
            Night = config.Night,
            Scenes = includeScenes && config.Scenes.Count > 0 ? config.Scenes : null,
        };
        // A deep copy (through JSON), so sanitizing never touches the user's own settings.
        var copy = JsonSerializer.Deserialize<SharedLayout>(JsonSerializer.Serialize(layout, CompactOptions), CompactOptions)!;
        Sanitize(copy);
        return copy;
    }

    public static string ToJson(SharedLayout layout) => JsonSerializer.Serialize(layout, FileOptions);

    /// <summary>"shoji1:" + base64url(gzip(compact JSON)).</summary>
    public static string ToCode(SharedLayout layout)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(layout, CompactOptions));
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json);
        return CodePrefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // ---------------------------------------------------------------- import

    /// <summary>
    /// Reads a layout from a code or the contents of a .shoji.json file, checks it and takes out anything secret.
    /// Throws <see cref="FormatException"/> with a message for the user when it isn't a usable layout.
    /// </summary>
    public static SharedLayout Parse(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            throw new FormatException(L.T("Paste a layout code (it starts with \"shoji1:\") or open a .shoji.json file."));
        string json;
        if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            json = DecodeCode(text);
        else if (text.StartsWith('{'))
            json = text.Length <= MaxJsonBytes ? text : throw new FormatException(L.T("That layout is too large to be a Shoji layout."));
        else
            throw new FormatException(L.T("That isn't a Shoji layout. A layout code starts with \"shoji1:\"."));

        SharedLayout? layout;
        try
        {
            layout = JsonSerializer.Deserialize<SharedLayout>(json, FileOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException(L.F("The layout couldn't be read ({0}). Was the code copied completely?", ex.Message));
        }
        if (layout == null)
            throw new FormatException(L.T("The layout is empty."));
        Validate(layout);
        Sanitize(layout); // also on import: a hand-made file might carry something it shouldn't
        return layout;
    }

    static string DecodeCode(string text)
    {
        // Chat apps like to wrap long lines or add spaces; none of that is part of the code.
        var body = new string(text.AsSpan(CodePrefix.Length).ToArray().Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (body.Length > MaxCodeChars)
            throw new FormatException(L.T("That code is too long to be a Shoji layout."));
        byte[] packed;
        try
        {
            string b64 = body.Replace('-', '+').Replace('_', '/');
            packed = Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
        }
        catch (FormatException)
        {
            throw new FormatException(L.T("That code is damaged (it has characters a layout code can't have). Copy it again, completely."));
        }
        try
        {
            using var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > MaxJsonBytes)
                    throw new FormatException(L.T("That layout is too large to be a Shoji layout."));
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            throw new FormatException(L.T("That code is incomplete or damaged. Copy it again, completely."));
        }
    }

    /// <summary>Sizes, then the same clean-up a hand-edited config.json gets (<see cref="AppConfig.Normalize"/>).</summary>
    static void Validate(SharedLayout layout)
    {
        layout.Pages ??= new List<PageConfig>();
        layout.Pages.RemoveAll(p => p == null);
        if (layout.Pages.Count == 0)
            throw new FormatException(L.T("This layout has no pages."));
        if (layout.Pages.Count > MaxPages || layout.Pages.Sum(p => p.Modules?.Count ?? 0) > MaxTiles
            || layout.Pages.Sum(p => p.Modules?.Sum(m => m?.Buttons?.Count ?? 0) ?? 0) > MaxButtons)
            throw new FormatException(L.T("This layout is larger than Shoji allows (pages, tiles or buttons)."));

        // Normalize works on these very objects: nulls become defaults, empty entries go.
        var probe = new AppConfig
        {
            Pages = layout.Pages,
            Scenes = layout.Scenes ?? new List<SceneConfig>(),
            DayNight = layout.DayNight ?? new DayNightConfig(),
            Night = layout.Night ?? new NightConfig(),
        };
        probe.Normalize();
        foreach (var page in layout.Pages)
            foreach (var m in page.Modules)
                m.Span = Math.Clamp(m.Span, 1, 12);
        if (layout.Theme != null && !UI.Themes.All.Any(t => t.Id.Equals(layout.Theme, StringComparison.OrdinalIgnoreCase)))
            layout.Theme = null; // a theme this version doesn't have
        if (layout.DayNight is { } dn && (!UI.Themes.All.Any(t => t.Id == dn.DayTheme) || !UI.Themes.All.Any(t => t.Id == dn.NightTheme)))
            layout.DayNight = null;
        layout.AccentColor = Colour(layout.AccentColor);
        layout.BackgroundColor = Colour(layout.BackgroundColor);
        layout.TileColor = Colour(layout.TileColor);
    }

    static string? Colour(string? hex) => hex != null && HexColour.IsMatch(hex.Trim()) ? hex.Trim().ToUpperInvariant() : null;

    // ---------------------------------------------------------------- secrets

    /// <summary>True for a tile option that must never leave this PC: the catalog marks it secret, or its name says so.</summary>
    public static bool IsSecret(string tileType, string optionKey)
    {
        if (ModuleCatalog.Find(tileType)?.OptionList.Any(o => o.Kind == OptionKind.Secret && o.Key.Equals(optionKey, StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        return SecretWords.Any(w => optionKey.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Takes out tokens, keys, passwords, headers and calendar links, and credentials inside URLs.</summary>
    public static void Sanitize(SharedLayout layout)
    {
        foreach (var m in layout.Pages.SelectMany(p => p.Modules))
        {
            if (m.Options != null)
            {
                var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in m.Options)
                    if (key != null && value != null && !IsSecret(m.Type, key))
                        clean[key] = StripUrlSecrets(value);
                m.Options = clean.Count > 0 ? clean : null;
            }
            m.Sites?.ForEach(s => s.Url = StripUrlSecrets(s.Url));
            m.Buttons?.ForEach(b =>
            {
                if (b.Target != null) b.Target = StripUrlSecrets(b.Target);
                if (b.Args != null) b.Args = StripUrlSecrets(b.Args);
            });
        }
    }

    /// <summary>A URL loses its user:password@ part and query parameters like ?token=…; anything else comes back unchanged.</summary>
    public static string StripUrlSecrets(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Contains(' ') || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.IsFile || string.IsNullOrEmpty(uri.Host))
            return value;
        if (string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query))
            return value;
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        if (uri.Query.Length > 1)
        {
            var kept = uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => !SecretQuery.IsMatch(Uri.UnescapeDataString(pair.Split('=', 2)[0])));
            builder.Query = string.Join("&", kept);
        }
        return builder.Uri.AbsoluteUri;
    }

    // ---------------------------------------------------------------- preview and apply

    public static LayoutSummary Summarize(SharedLayout layout)
    {
        var buttons = layout.Pages.SelectMany(p => p.Modules).Where(m => m.Buttons != null).SelectMany(m => m.Buttons!).ToList();
        var runs = buttons.Where(b => (b.Action ?? "").ToLowerInvariant() is "launch" or "command" or "powershell")
            .Select(b => string.IsNullOrWhiteSpace(b.Label) ? b.Target ?? b.Action : b.Label).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        string? theme = layout.Theme == null ? null : UI.Themes.All.FirstOrDefault(t => t.Id.Equals(layout.Theme, StringComparison.OrdinalIgnoreCase))?.Name;
        return new LayoutSummary(layout.Pages.Count, layout.Pages.Sum(p => p.Modules.Count), theme, buttons.Count, layout.Scenes?.Count ?? 0, runs);
    }

    /// <summary>
    /// Puts an imported layout into the config. <paramref name="replace"/> swaps the pages (and the scenes, if the layout
    /// has any); otherwise its pages are added after yours, renamed where a name is taken. <paramref name="useTheme"/>
    /// also takes its theme, colours, day / night themes and night mode.
    /// </summary>
    public static void Apply(AppConfig config, SharedLayout layout, bool replace, bool useTheme)
    {
        // Work on a copy, so the same preview can be applied again (or differently) without sharing objects.
        var copy = JsonSerializer.Deserialize<SharedLayout>(JsonSerializer.Serialize(layout, CompactOptions), CompactOptions)!;
        if (replace)
        {
            config.Pages = copy.Pages;
            if (copy.Scenes != null)
                config.Scenes = copy.Scenes;
        }
        else
        {
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in copy.Pages)
            {
                string name = UniqueName(config, page.Name);
                if (name != page.Name)
                    renamed[page.Name.Trim()] = name;
                page.Name = name;
                config.Pages.Add(page);
            }
            // Deck "page" buttons on the added pages follow their page's new name.
            foreach (var b in copy.Pages.SelectMany(p => p.Modules).Where(m => m.Buttons != null).SelectMany(m => m.Buttons!))
                if (string.Equals(b.Action, "page", StringComparison.OrdinalIgnoreCase) && b.Target != null && renamed.TryGetValue(b.Target.Trim(), out var to))
                    b.Target = to;
        }
        if (useTheme)
        {
            if (copy.Theme != null)
                config.Theme = copy.Theme;
            config.AccentColor = copy.AccentColor;
            config.BackgroundColor = copy.BackgroundColor;
            config.TileColor = copy.TileColor;
            if (copy.DayNight != null)
                config.DayNight = copy.DayNight;
            if (copy.Night != null)
                config.Night = copy.Night;
        }
        config.Normalize();
    }

    static string UniqueName(AppConfig config, string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Page" : name.Trim();
        if (!config.Pages.Any(p => p.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
            return name;
        for (int n = 2; ; n++)
        {
            string candidate = $"{name} {n}";
            if (!config.Pages.Any(p => p.Name.Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    /// <summary>A file name for an export: "shoji-layout-2026-10-08.shoji.json".</summary>
    public static string SuggestedFileName() => $"shoji-layout-{DateTime.Now:yyyy-MM-dd}{FileExtension}";
}
