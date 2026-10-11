using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Shoji.Core;

/// <summary>Where updates come from (Settings → About & install). Nothing is fetched until a feed is set.</summary>
public sealed class UpdatesConfig
{
    /// <summary>Look for a new version in the background (at most every 12 hours). It is never installed without a tap.</summary>
    public bool Check { get; set; } = true;

    /// <summary>
    /// A GitHub repository ("owner/repo"), or the https link of a manifest (the latest.json that scripts/make-release.sh writes).
    /// Defaults to the official repository; a cleared box comes back as the default (nulls aren't saved), so "Check" turns checking off.
    /// </summary>
    public string? Feed { get; set; } = DefaultFeed;

    public const string DefaultFeed = "Taheri-meisam/Shoji";

    /// <summary>"Skip this version": no toast or tray item for it ("Check now" still shows it).</summary>
    public string? Skipped { get; set; }
}

/// <summary>
/// What the background check remembers between runs. Kept in its own file, not config.json: writing config.json
/// reloads the dashboard, which a check twice a day shouldn't do.
/// </summary>
public sealed class UpdateState
{
    /// <summary>When the feed was last asked (UTC).</summary>
    public DateTime? LastChecked { get; set; }

    /// <summary>The last version the dashboard announced, so each one gets a single toast.</summary>
    public string? Notified { get; set; }

    static readonly object Gate = new();
    static string FilePath => Path.Combine(AppConfig.Folder, "update-state.json");

    public static UpdateState Load()
    {
        try
        {
            lock (Gate)
                return File.Exists(FilePath) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch
        {
            return new(); // unreadable: it only means one extra check
        }
    }

    public static void Edit(Action<UpdateState> change)
    {
        try
        {
            lock (Gate)
            {
                var state = Load();
                change(state);
                Sensors.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(state));
            }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("update-state", $"Couldn't save the update state: {ex.Message}", TimeSpan.FromHours(6));
        }
    }
}

/// <summary>One file of a release, with the SHA-256 it must have.</summary>
public sealed class UpdateAsset
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Lower-case hex, or null when the release doesn't say (then it isn't installed).</summary>
    public string? Sha256 { get; set; }
}

/// <summary>The newest release a feed offers.</summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string? Notes { get; set; }
    /// <summary>Web page of the release (for "Open download page").</summary>
    public string? Page { get; set; }
    /// <summary>Shoji-Setup.exe (installed copies).</summary>
    public UpdateAsset? Windows { get; set; }
    /// <summary>Shoji-portable.zip (portable and loose copies).</summary>
    public UpdateAsset? WindowsPortable { get; set; }
    /// <summary>Shoji-linux-x64.tar.gz.</summary>
    public UpdateAsset? Linux { get; set; }
    /// <summary>shoji_&lt;version&gt;_amd64.deb: only offered as a download (the package manager installs it).</summary>
    public UpdateAsset? LinuxDeb { get; set; }
}

public enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>
/// Looks for a newer Shoji (GitHub releases or a manifest), downloads it on request, checks its SHA-256 and hands
/// it to <see cref="SelfUpdate"/> (one per edition) to install. Shared by both editions; the UI shows
/// <see cref="Stage"/> and listens to <see cref="Changed"/>.
/// </summary>
public static class UpdateChecker
{
    static readonly TimeSpan Every = TimeSpan.FromHours(12);
    static readonly HttpClient Http = CreateClient();
    static readonly object Gate = new();
    static Timer? _timer;
    static Action<Action> _ui = a => a();
    static Action<UpdateInfo>? _announce;
    static Action? _exit;
    static string? _skipped, _pendingShown;
    static bool _checked;
    static DateTime _lastProgress;

    public static UpdateStage Stage { get; private set; }
    /// <summary>What went wrong (Failed), or a note for the current stage.</summary>
    public static string? Message { get; private set; }
    /// <summary>The newer release the last check found (null when up to date).</summary>
    public static UpdateInfo? Latest { get; private set; }
    /// <summary>Download progress 0–1 (−1 while the size is unknown).</summary>
    public static double Progress { get; private set; }

    /// <summary>Any change of stage, result or progress. Raised on a background thread.</summary>
    public static event Action? Changed;
    /// <summary>The update the tray offers appeared or went away. Raised on a background thread.</summary>
    public static event Action? PendingChanged;

    public static string CurrentVersion => AppPaths.VersionText;

    /// <summary>A newer version that wasn't skipped: what the tray menu offers.</summary>
    public static UpdateInfo? Pending =>
        Latest is { } l && !string.Equals(l.Version, _skipped, StringComparison.OrdinalIgnoreCase) ? l : null;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // downloads are ~45–90 MB
        // GitHub's API refuses requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Shoji/{AppPaths.VersionText} (update check)");
        return c;
    }

    // ---------------------------------------------------------------- background check

    /// <summary>
    /// Starts the background check. <paramref name="ui"/> runs an action on the UI thread, <paramref name="announce"/>
    /// tells the user about a new version (once per version), <paramref name="exit"/> quits the app so the update can
    /// replace it.
    /// </summary>
    public static void Start(Action<Action> ui, Action<UpdateInfo> announce, Action exit)
    {
        _ui = ui;
        _announce = announce;
        _exit = exit;
        _skipped = AppConfig.Load().Updates.Skipped;
        var state = UpdateState.Load();
        // An update was announced before the last restart and isn't installed yet: ask again soon, so the tray and
        // Settings can offer it, instead of waiting out the 12 hours.
        bool known = state.Notified != null && IsNewer(state.Notified, CurrentVersion);
        _timer = new Timer(_ => _ = Tick(), null, known ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
        Task.Run(CleanDownloads);
    }

    /// <summary>Downloads of earlier updates (the Windows setup runs from there, so it can't be removed right away).</summary>
    static void CleanDownloads()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "shoji-update-*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* in use or not ours */ }
            }
        }
        catch
        {
            // no temp folder
        }
    }

    static async Task Tick()
    {
        try
        {
            var u = AppConfig.Load().Updates;
            if (!u.Check || string.IsNullOrWhiteSpace(u.Feed))
                return;
            var state = UpdateState.Load();
            bool known = !_checked && state.Notified != null && IsNewer(state.Notified, CurrentVersion);
            if (!known && state.LastChecked is { } last && DateTime.UtcNow - last < Every && DateTime.UtcNow >= last)
                return;
            await CheckAsync(manual: false);
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("update-tick", $"Update check failed: {ex.Message}", TimeSpan.FromHours(6));
        }
    }

    static bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing;

    /// <summary>Asks the feed for the newest release ("Check now", or the background check).</summary>
    public static async Task CheckAsync(bool manual = true)
    {
        string? feed;
        lock (Gate)
        {
            if (Busy)
                return;
            var u = AppConfig.Load().Updates;
            feed = u.Feed?.Trim();
            _skipped = u.Skipped;
            if (string.IsNullOrEmpty(feed))
            {
                Set(UpdateStage.Failed, L.T("No update source is set: type owner/repo on GitHub, or a manifest URL."));
                return;
            }
            Set(UpdateStage.Checking, null);
        }

        UpdateInfo info;
        try
        {
            info = await FetchAsync(feed);
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("update-check", $"Update check ({feed}) failed: {ex.Message}", manual ? TimeSpan.Zero : TimeSpan.FromHours(6));
            Set(UpdateStage.Failed, Friendly(ex));
            return;
        }

        bool newer = IsNewer(info.Version, CurrentVersion);
        _checked = true;
        Latest = newer ? info : null;
        Set(newer ? UpdateStage.Available : UpdateStage.UpToDate, null);
        if (newer)
            Log.Write($"Update check: Shoji {info.Version} is available (running {CurrentVersion})");

        // Remember the check, and announce a new version once (a manual check counts as seen).
        bool announce = newer && !manual && !string.Equals(info.Version, _skipped, StringComparison.OrdinalIgnoreCase);
        _ui(() =>
        {
            string? notified = null;
            UpdateState.Edit(st =>
            {
                notified = st.Notified;
                st.LastChecked = DateTime.UtcNow;
                if (newer)
                    st.Notified = info.Version;
            });
            if (announce && !string.Equals(notified, info.Version, StringComparison.OrdinalIgnoreCase))
                _announce?.Invoke(info);
        });
    }

    /// <summary>"Skip this version".</summary>
    public static void Skip(string version)
    {
        _skipped = version;
        AppConfig.Edit(c => c.Updates.Skipped = version);
        Raise();
    }

    // ---------------------------------------------------------------- download and install

    /// <summary>Downloads the update for this copy, checks its SHA-256 and installs it (the user tapped "Install").</summary>
    public static async Task InstallAsync()
    {
        UpdateInfo? info;
        lock (Gate)
        {
            info = Latest;
            if (Busy || info == null)
                return;
            if (SelfUpdate.Blocker is { } why)
            {
                Set(UpdateStage.Failed, why);
                return;
            }
            Progress = 0;
            Set(UpdateStage.Downloading, null);
        }

        var asset = SelfUpdate.AssetFor(info);
        try
        {
            if (asset == null)
                throw new UpdateException(L.T("This release has no download for this edition of Shoji."));
            if (string.IsNullOrEmpty(asset.Sha256))
                throw new UpdateException(L.F("The release doesn't publish a SHA-256 checksum for {0}, so Shoji won't install it.", asset.Name));
            string folder = Path.Combine(Path.GetTempPath(), "shoji-update-" + SafeName(info.Version));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, SafeName(asset.Name));
            await DownloadAsync(asset, file, p =>
            {
                Progress = p;
                // A few updates a second are plenty for a progress bar.
                if (DateTime.UtcNow - _lastProgress > TimeSpan.FromMilliseconds(250))
                {
                    _lastProgress = DateTime.UtcNow;
                    Raise();
                }
            });

            Set(UpdateStage.Installing, null);
            Log.Write($"Installing Shoji {info.Version} from {asset.Url} (SHA-256 verified)");
            string? error = await Task.Run(() => SelfUpdate.Install(file, info));
            if (error != null)
                throw new UpdateException(error);
            _ui(() => _exit?.Invoke()); // the new version starts as soon as this one is gone
        }
        catch (Exception ex)
        {
            Log.Write($"Update to {info.Version} failed: {ex}");
            Set(UpdateStage.Failed, Friendly(ex));
        }
    }

    /// <summary>Saves <paramref name="asset"/> to <paramref name="file"/>, hashing on the way; throws unless the SHA-256 matches.</summary>
    public static async Task DownloadAsync(UpdateAsset asset, string file, Action<double>? progress = null, CancellationToken cancel = default)
    {
        var uri = SafeUri(asset.Url) ?? throw new UpdateException(L.F("Refusing to download {0} from a non-https link.", asset.Name));
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string temp = file + ".part";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    progress?.Invoke(total > 0 ? Math.Min(1, done / (double)total.Value) : -1);
                }
            }
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!HashEquals(actual, asset.Sha256))
                throw new UpdateException(L.F("{0} doesn't match its SHA-256 checksum (the download is damaged or was changed), so it wasn't installed.", asset.Name));
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>True when the file's SHA-256 is <paramref name="expected"/> (hex, any case, optionally "sha256:…"). Missing = false.</summary>
    public static bool VerifyFile(string file, string? expected)
    {
        using var stream = File.OpenRead(file);
        return HashEquals(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), expected);
    }

    static bool HashEquals(string actual, string? expected)
    {
        string? e = NormalizeHash(expected);
        return e != null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(e));
    }

    /// <summary>"sha256:ABC…" / "ABC…" → "abc…"; null unless it's 64 hex digits.</summary>
    public static string? NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;
        string h = hash.Trim();
        if (h.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            h = h[7..];
        h = h.ToLowerInvariant();
        return h.Length == 64 && h.All(Uri.IsHexDigit) ? h : null;
    }

    // ---------------------------------------------------------------- feeds

    /// <summary>"owner/repo" (or a github.com link) → the repository; null for a manifest URL or nonsense.</summary>
    public static string? GitHubRepo(string? feed)
    {
        if (string.IsNullOrWhiteSpace(feed))
            return null;
        var m = Regex.Match(feed.Trim(), @"^(?:https?://(?:www\.)?github\.com/)?([A-Za-z0-9-]+/[A-Za-z0-9._-]+?)(?:\.git)?/?$");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Whether a feed setting can be used ("owner/repo" or an https link), for the Settings box.</summary>
    public static bool ValidFeed(string? feed) => GitHubRepo(feed) != null || SafeUri(feed) != null;

    /// <summary>
    /// https links only: the SHA-256 the feed gives is only worth something if nobody could change it on the way.
    /// Plain http is allowed to this PC itself (testing a release from a local folder).
    /// </summary>
    static Uri? SafeUri(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
            return null;
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
    }

    static async Task<UpdateInfo> FetchAsync(string feed)
    {
        if (GitHubRepo(feed) is { } repo)
        {
            string json = await GetText($"https://api.github.com/repos/{repo}/releases/latest", github: true);
            var (info, sums) = ParseGitHubRelease(json);
            // Assets uploaded before GitHub started computing digests (2025) have none: fall back to a SHA256SUMS file.
            if (sums != null && new[] { info.Windows, info.WindowsPortable, info.Linux }.Any(a => a != null && a.Sha256 == null))
                ApplySums(info, await GetText(sums, github: false));
            return info;
        }
        var uri = SafeUri(feed) ?? throw new UpdateException(L.T("The update source must be owner/repo on GitHub, or an https link to a manifest."));
        return ParseManifest(await GetText(uri.ToString(), github: false), uri);
    }

    static async Task<string> GetText(string url, bool github)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (github)
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await Http.SendAsync(request, cts.Token);
        if (github && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new UpdateException(L.T("GitHub has no release for that repository (or it's private)."));
        if (github && (int)response.StatusCode is 403 or 429)
            throw new UpdateException(L.T("GitHub's rate limit was reached; try again in an hour."));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>A GitHub "latest release" answer → the update, plus the link of its SHA256SUMS file (if it has one).</summary>
    public static (UpdateInfo Info, string? SumsUrl) ParseGitHubRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new UpdateInfo
        {
            Version = CleanVersion(Str(root, "tag_name") ?? Str(root, "name") ?? throw new UpdateException(L.T("The release has no version tag."))),
            Notes = Str(root, "body"),
            Page = Str(root, "html_url"),
        };
        string? sums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string? name = Str(a, "name"), url = Str(a, "browser_download_url");
                if (name == null || url == null)
                    continue;
                var asset = new UpdateAsset { Name = name, Url = url, Sha256 = NormalizeHash(Str(a, "digest")) };
                switch (name.ToLowerInvariant())
                {
                    case "shoji-setup.exe": info.Windows = asset; break;
                    case "shoji-portable.zip": info.WindowsPortable = asset; break;
                    case "shoji-linux-x64.tar.gz": info.Linux = asset; break;
                    case "sha256sums" or "sha256sums.txt": sums = url; break;
                    case var deb when deb.StartsWith("shoji_") && deb.EndsWith("_amd64.deb"): info.LinuxDeb = asset; break;
                }
            }
        }
        return (info, sums);
    }

    /// <summary>
    /// A manifest (latest.json):
    /// { "version": "1.16.0", "notes": "…", "page": "https://…",
    ///   "windows": { "url": "…/Shoji-Setup.exe", "sha256": "…" }, "windowsPortable": { … }, "linux": { … }, "linuxDeb": { … } }.
    /// Relative URLs are taken from the manifest's own folder.
    /// </summary>
    public static UpdateInfo ParseManifest(string json, Uri manifestUri)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        UpdateAsset? Asset(string key, string fallbackName)
        {
            if (!TryGet(root, key, out var e) || e.ValueKind != JsonValueKind.Object || Str(e, "url") is not { } url)
                return null;
            var full = Uri.TryCreate(manifestUri, url, out var u) ? u.ToString() : url;
            string name = Uri.TryCreate(full, UriKind.Absolute, out var abs) && Path.GetFileName(abs.LocalPath) is { Length: > 0 } n ? Uri.UnescapeDataString(n) : fallbackName;
            return new UpdateAsset { Name = name, Url = full, Sha256 = NormalizeHash(Str(e, "sha256")) };
        }
        return new UpdateInfo
        {
            Version = CleanVersion(Str(root, "version") ?? throw new UpdateException(L.T("The manifest has no \"version\"."))),
            Notes = Str(root, "notes"),
            Page = Str(root, "page"),
            Windows = Asset("windows", "Shoji-Setup.exe"),
            WindowsPortable = Asset("windowsPortable", "Shoji-portable.zip"),
            Linux = Asset("linux", "Shoji-linux-x64.tar.gz"),
            LinuxDeb = Asset("linuxDeb", "shoji_amd64.deb"),
        };
    }

    /// <summary>Fills missing checksums from a SHA256SUMS file ("&lt;hex&gt;  &lt;name&gt;" or "&lt;hex&gt; *&lt;name&gt;" per line).</summary>
    public static void ApplySums(UpdateInfo info, string sums)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sums.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9A-Fa-f]{64})\s+\*?(.+)$");
            if (m.Success)
                map[Path.GetFileName(m.Groups[2].Value.Trim())] = m.Groups[1].Value.ToLowerInvariant();
        }
        foreach (var a in new[] { info.Windows, info.WindowsPortable, info.Linux })
            if (a != null && a.Sha256 == null && map.TryGetValue(a.Name, out var h))
                a.Sha256 = h;
    }

    static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    static string? Str(JsonElement e, string name) =>
        TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // ---------------------------------------------------------------- versions

    /// <summary>"v1.16.0" → "1.16.0".</summary>
    public static string CleanVersion(string v)
    {
        v = v.Trim();
        return v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1]) ? v[1..] : v;
    }

    /// <summary>True when <paramref name="candidate"/> is a later version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) => CompareVersions(candidate, current) > 0;

    /// <summary>
    /// Semantic version order: 1.9.0 &lt; 1.10.0, 1.16 = 1.16.0, 1.16.0-beta.2 &lt; 1.16.0-beta.10 &lt; 1.16.0; build
    /// metadata ("+abc") is ignored. Something that isn't a version sorts below every real one.
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        if (na == null || nb == null)
            return (na == null ? 0 : 1) - (nb == null ? 0 : 1);
        for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            long x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
            if (x != y)
                return x < y ? -1 : 1;
        }
        // A pre-release comes before its release.
        if (pa == null || pb == null)
            return (pa == null ? 1 : 0) - (pb == null ? 1 : 0);
        var ia = pa.Split('.');
        var ib = pb.Split('.');
        for (int i = 0; i < Math.Min(ia.Length, ib.Length); i++)
        {
            bool da = long.TryParse(ia[i], NumberStyles.None, CultureInfo.InvariantCulture, out long xa);
            bool db = long.TryParse(ib[i], NumberStyles.None, CultureInfo.InvariantCulture, out long xb);
            int c = da && db ? xa.CompareTo(xb) : da ? -1 : db ? 1 : string.CompareOrdinal(ia[i], ib[i]);
            if (c != 0)
                return Math.Sign(c);
        }
        return Math.Sign(ia.Length.CompareTo(ib.Length));
    }

    static (long[]? Numbers, string? Pre) Split(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return (null, null);
        v = CleanVersion(v);
        int plus = v.IndexOf('+');
        if (plus >= 0)
            v = v[..plus];
        string? pre = null;
        int dash = v.IndexOf('-');
        if (dash >= 0)
        {
            pre = v[(dash + 1)..];
            v = v[..dash];
        }
        var parts = v.Split('.');
        var numbers = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return (null, null);
        return (numbers, string.IsNullOrEmpty(pre) ? null : pre);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The start of the release notes, for the Settings card.</summary>
    public static string? Excerpt(string? notes, int max = 320)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;
        // Markdown headings and emphasis read fine without their marks.
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"^#+\s*|\*\*|__", ""))
            .Where(l => l.Length > 0);
        string text = string.Join("\n", lines);
        if (text.Length <= max)
            return text;
        int cut = text.LastIndexOf('\n', max);
        return (cut > max / 2 ? text[..cut] : text[..max].TrimEnd()) + " …";
    }

    static string SafeName(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

    static string Friendly(Exception ex) => ex switch
    {
        UpdateException => ex.Message,
        HttpRequestException h when h.StatusCode != null => L.F("The update source answered {0} ({1}).", (int)h.StatusCode.Value, h.StatusCode),
        HttpRequestException => L.T("Couldn't reach the update source (offline?)."),
        TaskCanceledException or OperationCanceledException => L.T("The update source didn't answer in time."),
        JsonException => L.T("The update source didn't send a valid release (check the link)."),
        IOException or UnauthorizedAccessException => L.F("Couldn't write the update: {0}", ex.Message),
        _ => ex.Message,
    };

    static void Set(UpdateStage stage, string? message)
    {
        Stage = stage;
        Message = message;
        Raise();
    }

    static void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Write($"Update UI error: {ex.Message}"); }
        string? pending = Pending?.Version;
        if (pending != _pendingShown)
        {
            _pendingShown = pending;
            try { PendingChanged?.Invoke(); }
            catch (Exception ex) { Log.Write($"Update UI error: {ex.Message}"); }
        }
    }
}

/// <summary>A problem with a message meant for the user.</summary>
public sealed class UpdateException(string message) : Exception(message);
