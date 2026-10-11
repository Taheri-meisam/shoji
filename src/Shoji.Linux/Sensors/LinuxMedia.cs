using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// "Now playing" from MPRIS (Spotify, browsers, VLC, Rhythmbox… anything in GNOME's media controls), through the
/// busctl CLI so no D-Bus library is needed. To keep process spawns low it reads the player every 2 s (4 s when paused),
/// looks for players every 5 s (15 s while one is playing, as it then only matters for picking a playing player; 5 min
/// without busctl) and extrapolates the position in between; buttons trigger an immediate re-read.
/// </summary>
public sealed class LinuxMedia
{
    const string Prefix = "org.mpris.MediaPlayer2.";
    const string ObjectPath = "/org/mpris/MediaPlayer2";
    const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    readonly object _gate = new();
    readonly SemaphoreSlim _refreshing = new(1, 1);
    string? _player;                  // bus name of the player being shown
    // Only touched while holding _refreshing.
    readonly Dictionary<string, string> _identities = new(StringComparer.Ordinal);
    DateTime _discoveredAt = DateTime.MinValue, _readAt = DateTime.MinValue;
    bool _noBusctl;
    State? _state;
    bool _firstRead = true;

    // Cover art: the URL of the track being shown, its bytes once loaded, and whether a load was started for it.
    string? _artUrl;
    byte[]? _art;
    bool _artLoading;

    sealed record State(string Title, string Artist, string App, string Status, TimeSpan? Position, TimeSpan? Duration,
                        string? ArtUrl, DateTime ReadAt);

    /// <summary>What's playing, or null when no player is running. Cheap: returns the cached state and refreshes in the background. Never throws.</summary>
    public async Task<MediaInfo?> ReadAsync()
    {
        if (TestMedia.Value is { } fake)
            return fake;
        try
        {
            var refresh = RefreshIfDueAsync();
            // The very first call waits (briefly) so the tile doesn't start empty; later calls never wait.
            if (_firstRead)
            {
                _firstRead = false;
                await Task.WhenAny(refresh, Task.Delay(1500)).ConfigureAwait(false);
            }
            return Current();
        }
        catch (Exception ex)
        {
            Log.Write($"Media read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>SHOJI_FAKE_MEDIA="title|artist|cover file" (tests and screenshots only): pretend that track is playing.</summary>
    static readonly Lazy<MediaInfo?> TestMedia = new(() =>
    {
        if (Environment.GetEnvironmentVariable("SHOJI_FAKE_MEDIA") is not { Length: > 0 } spec)
            return null;
        var parts = spec.Split('|');
        byte[]? art = null;
        try { art = parts.Length > 2 ? File.ReadAllBytes(parts[2]) : null; } catch { /* no cover then */ }
        return new MediaInfo(parts[0], parts.Length > 1 ? parts[1] : "", "Test", true, TimeSpan.FromSeconds(42), TimeSpan.FromMinutes(3), art);
    });

    MediaInfo? Current()
    {
        lock (_gate)
        {
            if (_state is not { } s)
                return null;
            bool playing = s.Status == "Playing";
            TimeSpan? position = s.Position;
            if (position is TimeSpan p && playing)
            {
                // Players only report position when asked; between reads assume it kept playing.
                p += DateTime.UtcNow - s.ReadAt;
                position = s.Duration is TimeSpan d && d > TimeSpan.Zero && p > d ? d : p;
            }
            return new MediaInfo(s.Title, s.Artist, s.App, playing, position, s.Duration, s.ArtUrl != null && s.ArtUrl == _artUrl ? _art : null);
        }
    }

    Task RefreshIfDueAsync()
    {
        var now = DateTime.UtcNow;
        bool discover, read;
        lock (_gate)
        {
            var discoverEvery = TimeSpan.FromSeconds(_noBusctl ? 300 : _state?.Status == "Playing" ? 15 : 5);
            discover = now - _discoveredAt > discoverEvery;
            // While playing, every 2 s keeps the title and position honest; a paused player only changes when someone presses
            // play (our own buttons re-read at once), so it is checked half as often.
            var every = TimeSpan.FromSeconds(_state?.Status == "Playing" ? 2 : 4);
            read = _player != null && now - _readAt > every;
        }
        if (!discover && !read)
            return Task.CompletedTask;
        if (!_refreshing.Wait(0))
            return Task.CompletedTask; // one already running
        return Task.Run(async () =>
        {
            try
            {
                if (discover)
                    await DiscoverAsync().ConfigureAwait(false);
                await ReadPlayerAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write($"Media refresh failed: {ex.Message}");
            }
            finally
            {
                _refreshing.Release();
            }
        });
    }

    /// <summary>Finds MPRIS players; keeps the current one unless another is playing and it isn't.</summary>
    async Task DiscoverAsync()
    {
        lock (_gate)
            _discoveredAt = DateTime.UtcNow;
        var (code, output) = await LinuxCli.RunAsync("busctl",
            new[] { "--user", "--json=short", "call", "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames" }).ConfigureAwait(false);
        bool missing = code == -1 && !LinuxSetup.Which("busctl");
        lock (_gate)
        {
            if (missing && !_noBusctl)
                Log.Write("busctl not found: the media tile can't see players (sudo apt install systemd)");
            _noBusctl = missing;
        }
        if (code != 0)
            return;
        var players = new List<string>();
        using (var doc = JsonDocument.Parse(output))
        {
            // {"type":"as","data":[["org.freedesktop.DBus", ":1.5", …]]}
            foreach (var name in doc.RootElement.GetProperty("data")[0].EnumerateArray())
                if (name.GetString() is { } n && n.StartsWith(Prefix, StringComparison.Ordinal))
                    players.Add(n);
        }
        players.Sort(StringComparer.Ordinal);

        string? current;
        lock (_gate)
            current = _player;
        string? pick = players.Contains(current ?? "") ? current : players.FirstOrDefault();
        if (players.Count > 1)
        {
            // Only worth asking when there's a choice: switch to a playing player if the current one isn't playing.
            bool currentPlaying = current != null && players.Contains(current) && await StatusAsync(current).ConfigureAwait(false) == "Playing";
            if (!currentPlaying)
                foreach (var p in players.Where(p => p != current))
                    if (await StatusAsync(p).ConfigureAwait(false) == "Playing")
                    {
                        pick = p;
                        break;
                    }
        }

        lock (_gate)
        {
            if (pick != _player)
                _readAt = DateTime.MinValue;
            _player = pick;
            if (pick == null)
                _state = null;
        }
        if (pick != null && !_identities.ContainsKey(pick))
            _identities[pick] = await IdentityAsync(pick).ConfigureAwait(false);
    }

    static async Task<string?> StatusAsync(string player)
    {
        var (code, output) = await LinuxCli.RunAsync("busctl",
            new[] { "--user", "--json=short", "get-property", player, ObjectPath, PlayerInterface, "PlaybackStatus" }, 1000).ConfigureAwait(false);
        if (code != 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.GetProperty("data").GetString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The player's own name ("Spotify", "Chromium"), else a tidied bus name ("vlc", "firefox").</summary>
    static async Task<string> IdentityAsync(string player)
    {
        var (code, output) = await LinuxCli.RunAsync("busctl",
            new[] { "--user", "--json=short", "get-property", player, ObjectPath, "org.mpris.MediaPlayer2", "Identity" }, 1000).ConfigureAwait(false);
        if (code == 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(output);
                if (doc.RootElement.GetProperty("data").GetString() is { Length: > 0 } id)
                    return id.Length > 20 ? id[..20] : id;
            }
            catch
            {
                // fall back to the bus name
            }
        }
        // "org.mpris.MediaPlayer2.chromium.instance1234" → "chromium" → "Chromium"
        string shortName = player[Prefix.Length..].Split('.')[0];
        return shortName.Length == 0 ? L.T("Player") : char.ToUpperInvariant(shortName[0]) + shortName[1..];
    }

    /// <summary>Metadata, PlaybackStatus and Position in one busctl call (it prints one JSON value per line).</summary>
    async Task ReadPlayerAsync()
    {
        string? player;
        lock (_gate)
        {
            player = _player;
            if (player == null)
                return;
            _readAt = DateTime.UtcNow;
        }
        var (code, output) = await LinuxCli.RunAsync("busctl",
            new[] { "--user", "--json=short", "get-property", player, ObjectPath, PlayerInterface, "Metadata", "PlaybackStatus", "Position" }, 1500)
            .ConfigureAwait(false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            // Some players don't implement Position, which fails the whole call: ask again without it.
            (code, output) = await LinuxCli.RunAsync("busctl",
                new[] { "--user", "--json=short", "get-property", player, ObjectPath, PlayerInterface, "Metadata", "PlaybackStatus" }, 1500)
                .ConfigureAwait(false);
            lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                // The player went away; look for another one next time.
                lock (_gate)
                {
                    if (_player == player)
                    {
                        _player = null;
                        _state = null;
                        _discoveredAt = DateTime.MinValue;
                    }
                }
                return;
            }
        }

        string title = "", artist = "", album = "";
        string? artUrl = null;
        TimeSpan? duration = null, position = null;
        string status = "Stopped";
        using (var meta = JsonDocument.Parse(lines[0]))
        {
            // {"type":"a{sv}","data":{"xesam:title":{"type":"s","data":"…"},"xesam:artist":{"type":"as","data":["…"]},"mpris:length":{"type":"x","data":262521000},…}}
            if (meta.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in data.EnumerateObject())
                {
                    if (!entry.Value.TryGetProperty("data", out var v))
                        continue;
                    switch (entry.Name)
                    {
                        case "xesam:title": title = Text(v); break;
                        case "xesam:artist": artist = Text(v); break;
                        case "xesam:albumArtist" when artist.Length == 0: artist = Text(v); break;
                        case "xesam:album": album = Text(v); break;
                        case "mpris:artUrl": artUrl = Text(v) is { Length: > 0 } u ? u : null; break;
                        case "mpris:length" when v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var us) && us > 0:
                            duration = TimeSpan.FromMilliseconds(us / 1000);
                            break;
                    }
                }
            }
        }
        using (var s = JsonDocument.Parse(lines[1]))
            status = s.RootElement.TryGetProperty("data", out var v) ? v.GetString() ?? "Stopped" : "Stopped";
        if (lines.Length > 2)
        {
            using var pos = JsonDocument.Parse(lines[2]);
            if (pos.RootElement.TryGetProperty("data", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var us) && us >= 0)
                position = TimeSpan.FromMilliseconds(us / 1000);
        }
        if (artist.Length == 0)
            artist = album;
        if (duration == null)
            position = null; // live streams: a running position with no end isn't useful

        string app = _identities.TryGetValue(player, out var id) ? id : player[Prefix.Length..];
        lock (_gate)
        {
            if (_player != player)
                return; // switched meanwhile
            _state = new State(title, artist, app, status, position, duration, artUrl, DateTime.UtcNow);
            if (artUrl != _artUrl)
            {
                _artUrl = artUrl;
                _art = null;
                _artLoading = false;
            }
            if (artUrl != null && _art == null && !_artLoading)
            {
                _artLoading = true;
                _ = LoadArtAsync(artUrl);
            }
        }
    }

    /// <summary>A string, or an array of strings joined with ", " (xesam:artist is a list).</summary>
    static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
        _ => "",
    };

    const int MaxArtBytes = 8 * 1024 * 1024;

    /// <summary>Cover art from a local file (browsers write one to /tmp) or the web, loaded once per track (at most 8 MB).</summary>
    async Task LoadArtAsync(string url)
    {
        byte[]? bytes = null;
        try
        {
            var uri = new Uri(url);
            if (uri.IsFile)
            {
                var info = new FileInfo(uri.LocalPath);
                if (info.Exists && info.Length < MaxArtBytes)
                    bytes = await File.ReadAllBytesAsync(info.FullName).ConfigureAwait(false);
            }
            else if (uri.Scheme is "http" or "https")
            {
                // Covers are a few hundred KB; a URL that serves something huge isn't one.
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is not > MaxArtBytes)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[64 * 1024];
                    int n;
                    while ((n = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0 && buffer.Length <= MaxArtBytes)
                        buffer.Write(chunk, 0, n);
                    if (buffer.Length <= MaxArtBytes)
                        bytes = buffer.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Cover art not loaded ({url}): {ex.Message}");
        }
        lock (_gate)
        {
            if (_artUrl != url)
                return; // the track changed meanwhile
            _art = bytes is { Length: > 0 } ? bytes : null;
            // A failed load stays "loading" for this track so it isn't retried every tick.
        }
    }

    // ---------------------------------------------------------------- buttons

    public Task PlayPauseAsync() => CommandAsync("PlayPause");
    public Task NextAsync() => CommandAsync("Next");
    public Task PreviousAsync() => CommandAsync("Previous");

    async Task CommandAsync(string method)
    {
        try
        {
            string? player;
            lock (_gate)
                player = _player;
            if (player == null)
            {
                // Pressed before the first discovery finished (or after the player was closed): look once more.
                await _refreshing.WaitAsync().ConfigureAwait(false);
                try { await DiscoverAsync().ConfigureAwait(false); }
                finally { _refreshing.Release(); }
                lock (_gate)
                    player = _player;
                if (player == null)
                    return;
            }
            await LinuxCli.RunAsync("busctl", new[] { "--user", "call", player, ObjectPath, PlayerInterface, method }, 2000).ConfigureAwait(false);
            // Show the result straight away rather than at the next 2-second read.
            await Task.Delay(150).ConfigureAwait(false);
            await _refreshing.WaitAsync().ConfigureAwait(false);
            try { await ReadPlayerAsync().ConfigureAwait(false); }
            finally { _refreshing.Release(); }
        }
        catch (Exception ex)
        {
            Log.Write($"Media command failed: {ex.Message}");
        }
    }
}
