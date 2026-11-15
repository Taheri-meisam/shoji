using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>A model a local server knows about. <see cref="GpuBytes"/> below <see cref="Bytes"/> means part of it runs from normal memory (slow).</summary>
/// <param name="Detail">"9.7B · Q4_K_M": what the server says about its size and quantization.</param>
/// <param name="Unloads">When the server will drop it from memory; null when it stays (or the server doesn't say).</param>
public sealed record LocalModel(string Name, string? Detail, long Bytes, long GpuBytes, bool Loaded, int? Context, DateTime? Unloads);

/// <param name="Server">"Ollama", "LM Studio" or "Local server".</param>
public sealed record LocalModels(bool Reachable, string Server, IReadOnlyList<LocalModel> Models)
{
    public IEnumerable<LocalModel> Loaded => Models.Where(m => m.Loaded);
    public IEnumerable<LocalModel> OnDisk => Models.Where(m => !m.Loaded);
}

public static class LocalModelsText
{
    /// <summary>"6.2 GB", "323 MB".</summary>
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", L.Culture) + " GB" : (bytes / (double)(1L << 20)).ToString("0", L.Culture) + " MB";

    /// <summary>"16K" for a context of 16384 tokens.</summary>
    public static string Context(int tokens) => tokens >= 1024 ? (tokens / 1024.0).ToString("0.#", L.Culture) + "K" : tokens.ToString(L.Culture);

    /// <summary>"unloads in 4 min", "stays loaded".</summary>
    public static string Unloads(DateTime? at, DateTime nowUtc)
    {
        if (at is not DateTime t || t - nowUtc > TimeSpan.FromDays(30))
            return L.T("stays loaded");
        var left = t - nowUtc;
        return left.TotalMinutes >= 60 ? L.F("unloads in {0} h {1} min", (int)left.TotalHours, left.Minutes)
            : left.TotalSeconds >= 60 ? L.F("unloads in {0} min", (int)left.TotalMinutes)
            : L.T("unloads soon");
    }

    /// <summary>"qwen3.5:latest" → "qwen3.5"; other tags stay.</summary>
    public static string Name(string name) => name.EndsWith(":latest", StringComparison.Ordinal) ? name[..^7] : name;
}

/// <summary>
/// Asks a model server on this PC (or the address given) which models it has loaded: Ollama's own API first, then
/// LM Studio's, then the plain OpenAI-style list that llama.cpp and others answer. Only reads; nothing is loaded,
/// unloaded or sent to a model.
/// </summary>
public sealed class LocalModelsService : Poller<LocalModels>
{
    public static string KeyFor(string? server) => "localmodels:" + Address(server);

    readonly string _base;
    IReadOnlyList<LocalModel> _installed = Array.Empty<LocalModel>();
    DateTime _installedAt;
    /// <summary>Which API answered last time, so the others aren't tried every few seconds.</summary>
    int _kind = -1;

    public LocalModelsService(string? server) : base(TimeSpan.FromSeconds(5))
    {
        _base = Address(server);
        Start();
    }

    /// <summary>"localhost:1234", "http://nas:11434/" → "http://nas:11434". Empty = Ollama here (OLLAMA_HOST when set).</summary>
    public static string Address(string? server)
    {
        string s = (string.IsNullOrWhiteSpace(server) ? Environment.GetEnvironmentVariable("OLLAMA_HOST") : server)?.Trim() ?? "";
        if (s.Length == 0 || s.StartsWith("0.0.0.0", StringComparison.Ordinal))
            return "http://127.0.0.1:11434";
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "http://" + s;
        return s.TrimEnd('/');
    }

    protected override async Task<LocalModels> FetchAsync(CancellationToken ct)
    {
#if SHOJI_LINUX
        if (Demo.Enabled)
            return DemoModels();
#endif
        // The one that answered before first; all three only when nothing is known (or it stopped answering).
        var order = _kind >= 0 ? new[] { _kind } : new[] { 0, 1, 2 };
        foreach (int kind in order)
        {
            try
            {
                var found = kind switch { 0 => await Ollama(ct), 1 => await LmStudio(ct), _ => await OpenAiStyle(ct) };
                if (found != null)
                {
                    _kind = kind;
                    return found;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // not running, or not this kind of server
            }
        }
        _kind = -1;
        return new LocalModels(false, "", Array.Empty<LocalModel>());
    }

    async Task<JsonDocument?> Get(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var response = await Http.GetAsync(_base + path, timeout.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
    }

    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long Num(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    static IEnumerable<JsonElement> List(JsonDocument doc, string name) =>
        doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

    static string? Join(params string?[] parts)
    {
        string s = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return s.Length > 0 ? s : null;
    }

    /// <summary>/api/ps = what is in memory now; /api/tags = what is on disk (asked once a minute).</summary>
    async Task<LocalModels?> Ollama(CancellationToken ct)
    {
        using var ps = await Get("/api/ps", ct);
        if (ps == null || !ps.RootElement.TryGetProperty("models", out _))
            return null;
        var models = new List<LocalModel>();
        foreach (var m in List(ps, "models"))
        {
            string name = Str(m, "name") ?? Str(m, "model") ?? "";
            if (name.Length == 0)
                continue;
            var details = m.TryGetProperty("details", out var d) ? d : default;
            DateTime? unloads = Str(m, "expires_at") is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                ? at.UtcDateTime : null;
            long size = Num(m, "size");
            models.Add(new LocalModel(name, Join(Str(details, "parameter_size"), Str(details, "quantization_level")), size, Math.Min(size, Num(m, "size_vram")), true,
                Num(m, "context_length") is > 0 and var c ? (int)c : null, unloads));
        }

        if (DateTime.UtcNow - _installedAt > TimeSpan.FromSeconds(60))
        {
            try
            {
                using var tags = await Get("/api/tags", ct);
                if (tags != null)
                    _installed = List(tags, "models").Select(m =>
                    {
                        var details = m.TryGetProperty("details", out var d) ? d : default;
                        return new LocalModel(Str(m, "name") ?? "", Join(Str(details, "parameter_size"), Str(details, "quantization_level")), Num(m, "size"), 0, false, null, null);
                    }).Where(m => m.Name.Length > 0).ToList();
                _installedAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // the loaded ones are still worth showing
            }
        }
        models.AddRange(_installed.Where(i => !models.Any(m => m.Name == i.Name)));
        return new LocalModels(true, "Ollama", models);
    }

    /// <summary>LM Studio's own list says which models are loaded ("state") next to what is downloaded.</summary>
    async Task<LocalModels?> LmStudio(CancellationToken ct)
    {
        using var doc = await Get("/api/v0/models", ct);
        if (doc == null)
            return null;
        var models = new List<LocalModel>();
        foreach (var m in List(doc, "data"))
        {
            string name = Str(m, "id") ?? "";
            if (name.Length == 0 || Str(m, "type") == "embeddings" && Str(m, "state") != "loaded")
                continue;
            bool loaded = Str(m, "state") == "loaded";
            long context = Num(m, "loaded_context_length") is > 0 and var c ? c : Num(m, "max_context_length");
            models.Add(new LocalModel(name, Join(Str(m, "arch"), Str(m, "quantization")), 0, 0, loaded, loaded && context > 0 ? (int)context : null, null));
        }
        return new LocalModels(true, "LM Studio", models);
    }

    /// <summary>llama.cpp's server, vLLM and the like: the models they list are the ones they serve.</summary>
    async Task<LocalModels?> OpenAiStyle(CancellationToken ct)
    {
        using var doc = await Get("/v1/models", ct);
        if (doc == null)
            return null;
        var models = new List<LocalModel>();
        foreach (var m in List(doc, "data"))
        {
            string name = Str(m, "id") ?? "";
            if (name.Length == 0)
                continue;
            // llama.cpp gives the file's path as the name, and its size and context under "meta".
            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            var meta = m.TryGetProperty("meta", out var me) ? me : default;
            long size = Num(meta, "size");
            models.Add(new LocalModel(slash >= 0 ? name[(slash + 1)..] : name, null, size, size, true, Num(meta, "n_ctx_train") is > 0 and var c ? (int)c : null, null));
        }
        return new LocalModels(true, L.T("Local server"), models);
    }

#if SHOJI_LINUX
    static LocalModels DemoModels()
    {
        var now = Demo.Now.ToUniversalTime();
        return new LocalModels(true, "Ollama", new[]
        {
            new LocalModel("qwen3.5:latest", "9.7B · Q4_K_M", 6_165_354_249, 6_165_354_249, true, 16384, now.AddSeconds(262)),
            new LocalModel("nomic-embed-text:latest", "137M · F16", 323_150_151, 323_150_151, true, 8192, now.AddSeconds(262)),
            new LocalModel("qwen2.5-coder:1.5b", "1.5B · Q4_K_M", 986_062_089, 0, false, null, null),
            new LocalModel("llama3.2:3b", "3.2B · Q4_K_M", 2_019_393_189, 0, false, null, null),
            new LocalModel("gemma3:12b", "12.2B · Q4_K_M", 8_149_190_253, 0, false, null, null),
        });
    }
#endif
}
