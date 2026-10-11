using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// The pictures behind a Photo frame tile: finds them in a folder (on a worker thread, again every 10 minutes while
/// the tile is showing) and hands them out one by one, shuffled or by name. Shared by both editions; the tiles do the
/// decoding and drawing.
/// </summary>
public sealed class PhotoLibrary
{
    public const int MaxPhotos = 2000;
    static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(10);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

    readonly bool _subfolders, _shuffle;
    readonly Random _random = new();
    readonly object _gate = new();
    List<string> _files = new();
    int _position;          // index in _files of the next photo to hand out
    string? _last;          // the photo handed out last (not repeated straight away after a reshuffle)
    DateTime _scannedAt = DateTime.MinValue;
    bool _scanning, _scannedOnce, _missing;

    public PhotoLibrary(string? folder, bool subfolders, bool shuffle)
    {
        Folder = ResolveFolder(folder);
        _subfolders = subfolders;
        _shuffle = shuffle;
    }

    public string Folder { get; }

    /// <summary>The folder for a tile's "Folder" option: empty means the user's Pictures folder; "~/…" is the home folder.</summary>
    public static string ResolveFolder(string? folder)
    {
        folder = folder?.Trim().Trim('"');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(folder))
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return string.IsNullOrEmpty(pictures) ? Path.Combine(home, "Pictures") : pictures;
        }
        if (folder == "~")
            return home;
        if (folder.StartsWith("~/", StringComparison.Ordinal) || folder.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, folder[2..]);
        return Environment.ExpandEnvironmentVariables(folder);
    }

    /// <summary>True once the first scan has finished.</summary>
    public bool Ready { get { lock (_gate) return _scannedOnce; } }

    public int Count { get { lock (_gate) return _files.Count; } }

    /// <summary>Why there is nothing to show (after the first scan), or null.</summary>
    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                if (!_scannedOnce || _files.Count > 0)
                    return null;
                return _missing
                    ? L.F("The folder {0} wasn't found. Pick another one in this tile's settings (hold the tile, or Settings → Pages & tiles).", Folder)
                    : L.F("No photos in {0} yet. Put some there (JPEG, PNG, WebP, GIF, BMP), or pick another folder in this tile's settings.", Folder);
            }
        }
    }

    /// <summary>Starts a scan on a worker thread when there was none yet or the last one is 10 minutes old. Cheap to call often.</summary>
    public void ScanIfDue(bool force = false)
    {
        lock (_gate)
        {
            if (_scanning || (!force && DateTime.UtcNow - _scannedAt < RescanEvery))
                return;
            _scanning = true;
        }
        Task.Run(Scan);
    }

    void Scan()
    {
        var found = new List<string>();
        bool missing = false;
        try
        {
            if (!Directory.Exists(Folder))
            {
                missing = true;
            }
            else
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = _subfolders, IgnoreInaccessible = true, MaxRecursionDepth = 12,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, // also skips .thumbnails and the like
                };
                foreach (var file in Directory.EnumerateFiles(Folder, "*", options))
                {
                    if (!Extensions.Contains(Path.GetExtension(file)))
                        continue;
                    found.Add(file);
                    if (found.Count >= MaxPhotos)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("photos-scan:" + Folder, $"Photo frame: couldn't read {Folder}: {ex.Message}", TimeSpan.FromHours(1));
        }

        if (!_shuffle)
            found.Sort(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            // Carry on from the photo shown last rather than starting over (in order), or deal a new shuffled round.
            string? last = _last;
            _files = found;
            if (_shuffle)
            {
                Shuffle(_files);
                _position = 0;
            }
            else
            {
                int at = last == null ? -1 : _files.FindIndex(f => string.Equals(f, last, StringComparison.OrdinalIgnoreCase));
                _position = at + 1;
            }
            _missing = missing;
            _scannedOnce = true;
            _scannedAt = DateTime.UtcNow;
            _scanning = false;
        }
    }

    void Shuffle(List<string> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>The next photo's path (round and round; a shuffled library is reshuffled each round), or null when there are none.</summary>
    public string? Next()
    {
        lock (_gate)
        {
            if (_files.Count == 0)
                return null;
            if (_position >= _files.Count)
            {
                _position = 0;
                if (_shuffle)
                {
                    Shuffle(_files);
                    // Not the same photo twice in a row across the reshuffle.
                    if (_files.Count > 1 && string.Equals(_files[0], _last, StringComparison.OrdinalIgnoreCase))
                        (_files[0], _files[^1]) = (_files[^1], _files[0]);
                }
            }
            _last = _files[_position++];
            return _last;
        }
    }

    /// <summary>A photo that couldn't be opened is left out until the next scan.</summary>
    public void Drop(string path)
    {
        lock (_gate)
        {
            int i = _files.IndexOf(path);
            if (i < 0)
                return;
            _files.RemoveAt(i);
            if (i < _position)
                _position--;
        }
    }
}

/// <summary>
/// What a picture file says about itself, read from its first bytes only: size, EXIF orientation and the date it was
/// taken. Enough to decode it at the size it is shown and turn it the right way up.
/// </summary>
public readonly record struct PhotoInfo(int Width, int Height, int Orientation, DateTime? Taken)
{
    /// <summary>Orientations 5–8 are stored on their side: width and height swap on screen.</summary>
    public bool Sideways => Orientation is >= 5 and <= 8;

    /// <summary>
    /// How to show the stored picture the right way up: turn it clockwise by Angle, then mirror it left-right when
    /// Mirror (EXIF orientations 1–8).
    /// </summary>
    public (double Angle, bool Mirror) Turn => Orientation switch
    {
        2 => (0, true),
        3 => (180, false),
        4 => (180, true),
        5 => (90, true),
        6 => (90, false),
        7 => (270, true),
        8 => (270, false),
        _ => (0, false),
    };

    /// <summary>
    /// The width (in stored pixels, before turning) to decode at so the picture fills or fits a box of the given
    /// device pixels without wasting memory; 0 when the size isn't known (decode to the box width then).
    /// </summary>
    public int DecodeWidth(double boxWidth, double boxHeight, bool fill)
    {
        if (Width <= 0 || Height <= 0 || boxWidth <= 0 || boxHeight <= 0)
            return 0;
        double shownW = Sideways ? Height : Width, shownH = Sideways ? Width : Height;
        double scale = fill ? Math.Max(boxWidth / shownW, boxHeight / shownH) : Math.Min(boxWidth / shownW, boxHeight / shownH);
        scale = Math.Min(1, scale); // never larger than the file
        return Math.Max(1, (int)Math.Round(Width * scale));
    }

    /// <summary>Caption text: "name" (the file name) or "date" (taken, else the file's date); null for none.</summary>
    public static string? Caption(string path, string? mode, DateTime? taken)
    {
        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            case "name":
            case "filename":
                return Path.GetFileNameWithoutExtension(path);
            case "date":
            case "date taken":
            case "taken":
                DateTime when = taken ?? SafeFileDate(path);
                return when == DateTime.MinValue ? null : when.ToString(L.T("d MMMM yyyy"), L.Culture);
            default:
                return null;
        }
    }

    static DateTime SafeFileDate(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Reads the header of a JPEG, PNG, GIF, BMP or WebP file. Never throws (unknown values stay 0 / null).</summary>
    public static PhotoInfo Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            // EXIF sits near the start of a JPEG (APP1 must fit in 64 KB); camera files put the size after it.
            var head = new byte[(int)Math.Min(stream.Length, 192 * 1024)];
            int read = 0;
            while (read < head.Length)
            {
                int n = stream.Read(head, read, head.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return Parse(head.AsSpan(0, read));
        }
        catch
        {
            return default;
        }
    }

    public static PhotoInfo Parse(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
                return Jpeg(b);
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G')
                return new PhotoInfo(Be32(b, 16), Be32(b, 20), 1, null);
            if (b.Length >= 10 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
                return new PhotoInfo(b[6] | b[7] << 8, b[8] | b[9] << 8, 1, null);
            if (b.Length >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M')
                return new PhotoInfo(Math.Abs(Le32(b, 18)), Math.Abs(Le32(b, 22)), 1, null);
            if (b.Length >= 30 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
                return WebP(b);
        }
        catch (IndexOutOfRangeException)
        {
            // A truncated or odd header: decode it without hints.
        }
        return default;
    }

    static PhotoInfo Jpeg(ReadOnlySpan<byte> b)
    {
        int width = 0, height = 0, orientation = 1;
        DateTime? taken = null;
        int i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF)
                break;
            byte marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; } // padding
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker is 0xD9 or 0xDA)
                break; // end, or the picture data starts: the header is over
            int length = b[i + 2] << 8 | b[i + 3];
            if (length < 2)
                break;
            int data = i + 4, end = Math.Min(b.Length, i + 2 + length);
            if (marker == 0xE1 && end - data > 14 && b[data] == (byte)'E' && b[data + 1] == (byte)'x' && b[data + 2] == (byte)'i' && b[data + 3] == (byte)'f')
                Exif(b[(data + 6)..end], ref orientation, ref taken);
            else if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && end - data >= 5)
            {
                height = b[data + 1] << 8 | b[data + 2];
                width = b[data + 3] << 8 | b[data + 4];
                break;
            }
            i += 2 + length;
        }
        return new PhotoInfo(width, height, orientation, taken);
    }

    /// <summary>The TIFF block inside APP1: orientation (0x0112) from IFD0, DateTimeOriginal (0x9003) from the Exif IFD.</summary>
    static void Exif(ReadOnlySpan<byte> t, ref int orientation, ref DateTime? taken)
    {
        bool little = t[0] == (byte)'I';

        int ifd0 = U32(t, 4, little), exifIfd = 0;
        if (ifd0 <= 0 || ifd0 + 2 > t.Length)
            return;
        int count = U16(t, ifd0, little);
        for (int e = 0; e < count; e++)
        {
            int at = ifd0 + 2 + e * 12;
            if (at + 12 > t.Length)
                break;
            int tag = U16(t, at, little);
            if (tag == 0x0112)
            {
                int o = U16(t, at + 8, little);
                if (o is >= 1 and <= 8)
                    orientation = o;
            }
            else if (tag == 0x8769)
            {
                exifIfd = U32(t, at + 8, little);
            }
        }
        if (exifIfd <= 0 || exifIfd + 2 > t.Length)
            return;
        count = U16(t, exifIfd, little);
        for (int e = 0; e < count; e++)
        {
            int at = exifIfd + 2 + e * 12;
            if (at + 12 > t.Length)
                break;
            if (U16(t, at, little) != 0x9003 && U16(t, at, little) != 0x9004)
                continue;
            int len = U32(t, at + 4, little), offset = U32(t, at + 8, little);
            if (len < 19 || offset <= 0 || offset + 19 > t.Length)
                continue;
            // "YYYY:MM:DD HH:MM:SS"
            var text = System.Text.Encoding.ASCII.GetString(t.Slice(offset, 19));
            if (DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            {
                taken = when;
                if (U16(t, at, little) == 0x9003)
                    return; // the original wins over "digitized"
            }
        }
    }

    static PhotoInfo WebP(ReadOnlySpan<byte> b)
    {
        // RIFF header (12 bytes), then the first chunk: "VP8 " (lossy), "VP8L" (lossless) or "VP8X" (extended).
        if (b[12] == (byte)'V' && b[13] == (byte)'P' && b[14] == (byte)'8')
        {
            switch ((char)b[15])
            {
                case ' ':
                    return new PhotoInfo((b[26] | b[27] << 8) & 0x3FFF, (b[28] | b[29] << 8) & 0x3FFF, 1, null);
                case 'L':
                    return new PhotoInfo(1 + (b[21] | (b[22] & 0x3F) << 8), 1 + (b[22] >> 6 | b[23] << 2 | (b[24] & 0x0F) << 10), 1, null);
                case 'X':
                    return new PhotoInfo(1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16), 1, null);
            }
        }
        return default;
    }

    static int U16(ReadOnlySpan<byte> t, int at, bool little) => little ? t[at] | t[at + 1] << 8 : t[at] << 8 | t[at + 1];
    static int U32(ReadOnlySpan<byte> t, int at, bool little) =>
        little ? t[at] | t[at + 1] << 8 | t[at + 2] << 16 | t[at + 3] << 24 : t[at] << 24 | t[at + 1] << 16 | t[at + 2] << 8 | t[at + 3];
    static int Be32(ReadOnlySpan<byte> b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];
    static int Le32(ReadOnlySpan<byte> b, int at) => b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24;
}
