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
/// file of "English": "translation" pairs embedded in the app (Lang/nb.json, plus Lang/nb.linux.json on Linux).
/// Missing entries simply stay English, so a partly translated language still works.
/// Dates and day / month names use <see cref="Culture"/>; numbers keep being parsed and saved invariantly.
/// </summary>
public static class L
{
    static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();

    /// <summary>Settings → Appearance → Language. "auto" follows the system's display language.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Languages = new[]
    {
        ("auto", "Automatic"), ("en", "English"), ("nb", "Norsk (bokmål)"),
    };

    /// <summary>The language in use: "en" or "nb".</summary>
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
            lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
            {
                "nb" or "nn" or "no" => "nb",
                _ => "en",
            };
        if (lang == Language && (lang == "en" || _map.Count > 0))
            return;
        Language = lang;
        _map = lang == "en" ? new Dictionary<string, string>() : Load(lang);
        Culture = lang switch
        {
            "nb" => CultureInfo.GetCultureInfo("nb-NO"),
            _ => CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "nb" ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.CurrentCulture,
        };
        Changed?.Invoke();
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
        // Lang/nb.json (shared and Windows) and, on Linux, Lang/nb.linux.json; later files win.
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("Lang/" + lang + ".", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(n => n.Length))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var (key, value) in entries ?? new())
                    map[key] = value;
            }
            catch (Exception ex)
            {
                Log.Write($"Language file {name} is broken: {ex.Message}");
            }
        }
        return map;
    }
}
