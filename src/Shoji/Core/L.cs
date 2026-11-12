using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Shoji.Core;

/// <summary>
/// Translations. Text is written in English in the code and looked up with <see cref="T"/>; a language is a JSON
/// file of "English": "translation" pairs, embedded in the app (Lang/nb.json, plus Lang/nb.linux.json on Linux) or
/// dropped into the settings folder by the user (lang/de.json).
/// Missing entries simply stay English, so a partly translated language still works.
/// Dates and day / month names use <see cref="Culture"/>; numbers keep being parsed and saved invariantly.
/// </summary>
public static class L
{
    static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();

    /// <summary>Key in a language file that holds the language's own name ("Deutsch"), shown in the language list.</summary>
    public const string NameKey = "@language";

    /// <summary>Where people drop their own translations: "&lt;settings folder&gt;/lang/de.json".</summary>
    public static string UserFolder => Path.Combine(AppPaths.DataFolder, "lang");

    static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Settings → Appearance → Language. "auto" follows the system's display language. After English come the
    /// languages built into the app and the ones found in <see cref="UserFolder"/>, by name.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> Languages
    {
        get
        {
            var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string code in Embedded().Select(n => n["Lang/".Length..].Split('.')[0]).Distinct())
                found[code] = NameOf(code, null);
            foreach (string file in UserFiles())
            {
                string code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                string? own = null;
                try
                {
                    using var stream = File.OpenRead(file);
                    own = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Json)?.GetValueOrDefault(NameKey);
                }
                catch (Exception)
                {
                    continue; // a broken file isn't offered
                }
                if (own is { Length: > 0 } || !found.ContainsKey(code))
                    found[code] = NameOf(code, own);
            }
            var list = new List<(string, string)> { ("auto", "Automatic"), ("en", "English") };
            list.AddRange(found.Where(f => f.Key != "en").OrderBy(f => f.Value, StringComparer.CurrentCultureIgnoreCase).Select(f => (f.Key, f.Value)));
            return list;
        }
    }

    static string NameOf(string code, string? own)
    {
        if (own is { Length: > 0 })
            return own;
        if (code == "nb")
            return "Norsk (bokmål)";
        try
        {
            string name = CultureInfo.GetCultureInfo(code).NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    static IEnumerable<string> Embedded() => Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .Where(n => n.StartsWith("Lang/", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));

    static IEnumerable<string> UserFiles()
    {
        try
        {
            // "de.json", "pt-br.json"; not the template, and not half-saved editor files.
            return Directory.Exists(UserFolder)
                ? Directory.GetFiles(UserFolder, "*.json").Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?\\.json$")).OrderBy(f => f, StringComparer.Ordinal).ToList()
                : Enumerable.Empty<string>();
        }
        catch (Exception)
        {
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>The language in use: "en", "nb", or the code of another language file.</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>For formatting dates and day / month names in the chosen language.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>Raised after <see cref="Apply"/> switched to another language (UI that caches text rebuilds then).</summary>
    public static event Action? Changed;

    /// <summary>Loads the language for a setting ("auto", "en", "nb"…). Cheap when nothing changes.</summary>
    public static void Apply(string? setting)
    {
        string lang = (setting ?? "auto").Trim().ToLowerInvariant();
        if (lang is "" or "auto")
        {
            // The system's language when there is a translation for it ("pt-br" before "pt"), else English.
            var ui = CultureInfo.CurrentUICulture;
            var have = Languages.Select(l => l.Id).ToHashSet();
            string two = ui.TwoLetterISOLanguageName is "nn" or "no" ? "nb" : ui.TwoLetterISOLanguageName;
            lang = have.Contains(ui.Name.ToLowerInvariant()) ? ui.Name.ToLowerInvariant() : have.Contains(two) ? two : "en";
        }
        if (lang == Language && (lang == "en" || _map.Count > 0))
            return;
        Language = lang;
        _map = lang == "en" ? new Dictionary<string, string>() : Load(lang);
        Culture = lang switch
        {
            "nb" => CultureInfo.GetCultureInfo("nb-NO"),
            "en" => CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "nb" ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.CurrentCulture,
            _ => CultureFor(lang),
        };
        Changed?.Invoke();
    }

    /// <summary>Dates in the chosen language; the system's own formats when that already is the system's language.</summary>
    static CultureInfo CultureFor(string lang)
    {
        try
        {
            var chosen = CultureInfo.GetCultureInfo(lang);
            var current = CultureInfo.CurrentCulture;
            if (current.TwoLetterISOLanguageName == chosen.TwoLetterISOLanguageName)
                return current;
            return chosen.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(lang) : chosen;
        }
        catch (Exception)
        {
            return CultureInfo.CurrentCulture;
        }
    }

    /// <summary>
    /// Writes "lang/template.json": every text of the app with an empty translation (filled in where the current
    /// language already has one), ready to be saved as "de.json" and translated. Returns the file's path.
    /// </summary>
    public static string WriteTemplate()
    {
        // The built-in Norwegian files list every text there is.
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Embedded())
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
            foreach (string key in (JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Json) ?? new()).Keys)
                keys.Add(key);
        }
        var template = new Dictionary<string, string> { [NameKey] = "" };
        foreach (string key in keys)
            if (key != NameKey)
                template[key] = Language == "en" || Language == "nb" ? "" : _map.GetValueOrDefault(key, "");
        Directory.CreateDirectory(UserFolder);
        string path = Path.Combine(UserFolder, "template.json");
        File.WriteAllText(path, JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return path;
    }

    /// <summary>The text in the current language (the English text itself when there's no translation).</summary>
    public static string T(string english) =>
        _map.TryGetValue(english, out var translated) && translated.Length > 0 ? translated : english;

    /// <summary>
    /// <see cref="T"/> for a word that needs different translations in different places (the weather "Clear" vs the
    /// "Clear" button): looks up "context|English" first.
    /// </summary>
    public static string T(string english, string context) =>
        _map.TryGetValue(context + "|" + english, out var translated) && translated.Length > 0 ? translated : T(english);

    /// <summary><see cref="T"/> for a format string, filled in with <see cref="Culture"/>.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(Culture, T(englishFormat), args);

    static Dictionary<string, string> Load(string lang)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();
        void Add(string name, Func<Stream> open)
        {
            try
            {
                using var stream = open();
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Json) ?? new())
                    map[key] = value;
            }
            catch (Exception ex)
            {
                Log.Write($"Language file {name} is broken: {ex.Message}");
            }
        }
        // Lang/nb.json (shared and Windows) and, on Linux, Lang/nb.linux.json; later files win…
        foreach (var name in Embedded().Where(n => n.StartsWith("Lang/" + lang + ".", StringComparison.Ordinal)).OrderBy(n => n.Length))
            Add(name, () => assembly.GetManifestResourceStream(name)!);
        // …and the user's own file wins over those, so anyone can add a language or fix a built-in one.
        foreach (string file in UserFiles().Where(f => Path.GetFileNameWithoutExtension(f).Equals(lang, StringComparison.OrdinalIgnoreCase)))
            Add(file, () => File.OpenRead(file));
        map.Remove(NameKey);
        return map;
    }
}
