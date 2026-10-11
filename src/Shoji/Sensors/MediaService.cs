using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// "Now playing" from Windows' global media controls – works with Spotify, browsers (YouTube etc.),
/// Windows Media Player and anything else that shows up in the volume flyout.
/// </summary>
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    string? _thumbKey;
    byte[]? _thumb;

    async Task<GlobalSystemMediaTransportControlsSession?> CurrentSessionAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return _manager.GetCurrentSession();
    }

    public async Task<MediaInfo?> ReadAsync()
    {
        try
        {
            var session = await CurrentSessionAsync();
            if (session == null)
                return null;

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            TimeSpan? duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime - timeline.StartTime : null;
            TimeSpan? position = null;
            if (duration != null)
            {
                // Apps only report position occasionally; extrapolate while playing.
                var pos = timeline.Position;
                if (playing)
                    pos += DateTimeOffset.Now - timeline.LastUpdatedTime;
                position = pos < TimeSpan.Zero ? TimeSpan.Zero : pos > duration ? duration : pos;
            }

            string key = props.Title + "|" + props.Artist;
            if (key != _thumbKey)
            {
                // Apps often publish the title first and the cover a moment later: only remember the track once it has one.
                _thumb = await ReadThumbnailAsync(props);
                if (_thumb != null)
                    _thumbKey = key;
            }

            return new MediaInfo(
                Title: props.Title ?? "",
                Artist: string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist,
                App: FriendlyApp(session.SourceAppUserModelId),
                IsPlaying: playing,
                Position: position,
                Duration: duration,
                Thumbnail: _thumb);
        }
        catch (Exception ex)
        {
            // The media session API is flaky when apps open/close; just try again next tick.
            Log.Write($"Media read failed: {ex.Message}");
            _manager = null;
            return null;
        }
    }

    static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null)
            return null;
        try
        {
            using var winrtStream = await props.Thumbnail.OpenReadAsync();
            using var stream = winrtStream.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    static string FriendlyApp(string appId)
    {
        if (string.IsNullOrEmpty(appId))
            return "";
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("zunemusic") || id.Contains("mediaplayer")) return "Media Player";
        if (id.Contains("vlc")) return "VLC";
        var name = Path.GetFileNameWithoutExtension(appId.Split('!')[0]);
        return name.Length > 20 ? name[..20] : name;
    }

    public Task PlayPauseAsync() => Do(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Do(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Do(s => s.TrySkipPreviousAsync().AsTask());

    async Task Do(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        try
        {
            var session = await CurrentSessionAsync();
            if (session != null)
                await action(session);
        }
        catch (Exception ex)
        {
            Log.Write($"Media command failed: {ex.Message}");
        }
    }
}
