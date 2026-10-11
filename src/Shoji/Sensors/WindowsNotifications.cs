using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Feeds <see cref="NotificationHub"/> with the toasts Windows shows (Discord, WhatsApp, Outlook…).
///
/// Why not UserNotificationListener: that WinRT API only works for apps with package identity (MSIX or a sparse
/// package) that declare the userNotificationListener capability; from a plain .exe, RequestAccessAsync fails
/// ("Element not found") or reports Denied. So Shoji reads what Windows itself stores for the Action Center:
/// %LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db (SQLite, WAL), read-only, every 3 s while a
/// Notifications tile is on screen. Rows already gone from the database (dismissed in the Action Center) are
/// simply missed; that's fine for a "what just came in" tile.
/// </summary>
public sealed class WindowsNotifications : IDisposable
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    readonly string _db;
    readonly Timer _timer;
    long _lastArrival;          // FILETIME of the newest row seen
    long _wantedUntil;          // UTC ticks; a visible tile keeps pushing it forward
    int _busy;
    bool _copyMode, _failed;
    volatile bool _disposed;
    WpnDatabase.CopyStamp _copied;

    public WindowsNotifications(string? dbPath = null)
    {
        _db = dbPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications", "wpndatabase.db");
        // The first read also picks up what arrived in the last hour, so the tile isn't empty right after it's added.
        _lastArrival = DateTime.UtcNow.AddHours(-1).ToFileTimeUtc();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), Interval);
    }

    /// <summary>Called by a visible tile on every refresh: reading stops a few seconds after the last call.</summary>
    public void Want() => Interlocked.Exchange(ref _wantedUntil, (DateTime.UtcNow + Interval + Interval).Ticks);

    void Tick()
    {
        if (_disposed || DateTime.UtcNow.Ticks > Interlocked.Read(ref _wantedUntil) || Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            Poll();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Reads the new rows into the hub. Public for tests; never throws.</summary>
    public void Poll()
    {
        if (!File.Exists(_db))
        {
            Fail(L.T("Windows' notification database wasn't found, so this tile can't show notifications on this PC."),
                $"Notifications: {_db} not found");
            return;
        }
        List<WpnDatabase.Row> rows;
        try
        {
            rows = _copyMode ? ReadCopy() : WpnDatabase.Read(_db, _lastArrival);
        }
        catch (SqliteException ex) when (!_copyMode && ex.SqliteErrorCode is 5 or 6 or 8 or 10 or 14) // busy, locked, read-only, I/O (shm), can't open
        {
            // The notification service holds the file in a way SQLite can't share: read a private copy instead.
            Log.Write($"Notifications: reading a copy of the database ({ex.Message})");
            _copyMode = true;
            try { rows = ReadCopy(); }
            catch (Exception ex2) { Fail(L.T("Couldn't read Windows' notifications."), $"Notifications: {ex2.Message}"); return; }
        }
        catch (Exception ex)
        {
            // Most likely a Windows update changed the tables.
            Fail(L.T("Couldn't read Windows' notifications (the database looks different on this Windows version)."), $"Notifications: {ex.Message}");
            return;
        }

        if (_failed)
        {
            _failed = false;
            NotificationHub.Hint = null;
        }
        foreach (var r in rows)
        {
            _lastArrival = Math.Max(_lastArrival, r.Arrival);
            if (_disposed || WpnDatabase.ParseToast(r.Payload) is not { } toast)
                continue;
            DateTime time;
            try { time = DateTime.FromFileTimeUtc(r.Arrival).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { time = DateTime.Now; }
            NotificationHub.Add(new NotificationItem(WpnDatabase.FriendlyApp(r.App), toast.Title, toast.Body, time, toast.Icon));
        }
    }

    List<WpnDatabase.Row> ReadCopy()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Shoji-wpn");
        // Copy only when Windows wrote something; the database can be a few MB.
        var stamp = WpnDatabase.Stamp(_db);
        if (stamp == _copied)
            return new List<WpnDatabase.Row>();
        string copy = WpnDatabase.CopyTo(_db, dir);
        try
        {
            var rows = WpnDatabase.Read(copy, _lastArrival, readOnly: false);
            _copied = stamp;
            return rows;
        }
        finally
        {
            // Notification texts are private: don't leave them lying around in %TEMP%.
            WpnDatabase.DeleteCopy(dir);
        }
    }

    void Fail(string hint, string log)
    {
        _failed = true;
        NotificationHub.Hint = hint;
        Log.WriteThrottled("notifications-db", log, TimeSpan.FromHours(1));
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        NotificationHub.Clear();
        NotificationHub.Hint = null;
    }
}

/// <summary>Reading wpndatabase.db: the rows, the toast XML inside them and readable app names.</summary>
public static class WpnDatabase
{
    public sealed record Row(long Arrival, string App, string Payload);
    public sealed record Toast(string Title, string Body, string? Icon);
    public readonly record struct CopyStamp(long DbLength, DateTime DbWritten, long WalLength, DateTime WalWritten);

    /// <summary>Toasts that arrived after <paramref name="sinceFileTime"/>, oldest first.</summary>
    public static List<Row> Read(string path, long sinceFileTime, bool readOnly = true)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, // don't keep Windows' file open between reads
            DefaultTimeout = 2,
        };
        using var db = new SqliteConnection(cs.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT n.ArrivalTime, h.PrimaryId, n.Payload FROM Notification n " +
            "JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
            "WHERE n.Type = 'toast' AND n.ArrivalTime > $since ORDER BY n.ArrivalTime LIMIT 100";
        cmd.Parameters.AddWithValue("$since", sinceFileTime);
        var rows = new List<Row>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(2))
                continue;
            string payload = r.GetValue(2) switch
            {
                byte[] b => Encoding.UTF8.GetString(b),
                var v => v.ToString() ?? "",
            };
            rows.Add(new Row(r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), payload.Trim('﻿', '\0', ' ', '\r', '\n')));
        }
        return rows;
    }

    /// <summary>The first &lt;text&gt; is the title, the others the body; the app logo is used as the icon if it's a local file.</summary>
    public static Toast? ParseToast(string xml)
    {
        XElement root;
        try { root = XElement.Parse(xml); }
        catch (Exception) { return null; }
        if (root.Name.LocalName != "toast")
            return null;
        var bindings = root.Descendants().Where(e => e.Name.LocalName == "binding").ToList();
        var binding = bindings.FirstOrDefault(b => string.Equals((string?)b.Attribute("template"), "ToastGeneric", StringComparison.OrdinalIgnoreCase))
                      ?? bindings.FirstOrDefault();
        if (binding == null)
            return null;
        // Direct children only: grouped / adaptive sub-texts repeat what the main ones say.
        var texts = binding.Elements().Where(e => e.Name.LocalName == "text")
            .Select(e => NotificationText.Tidy(e.Value)).Where(t => t.Length > 0).ToList();
        if (texts.Count == 0)
            return null;
        string? icon = binding.Elements().Where(e => e.Name.LocalName == "image")
            .OrderByDescending(e => (string?)e.Attribute("placement") == "appLogoOverride")
            .Select(e => LocalFile((string?)e.Attribute("src")))
            .FirstOrDefault(p => p != null);
        return new Toast(texts[0], string.Join("\n", texts.Skip(1)), icon);
    }

    static string? LocalFile(string? src)
    {
        if (string.IsNullOrEmpty(src))
            return null;
        try
        {
            string path = src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(src).LocalPath : src;
            return Path.IsPathRooted(path) && File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null; // ms-appx:, http:, odd paths
        }
    }

    static readonly (string Part, string Name)[] Known =
    {
        ("discord", "Discord"), ("whatsapp", "WhatsApp"), ("telegram", "Telegram"), ("signal", "Signal"), ("slack", "Slack"),
        ("teams", "Teams"), ("outlook", "Outlook"), ("windowscommunicationsapps", "Mail"), ("thunderbird", "Thunderbird"),
        ("spotify", "Spotify"), ("steam", "Steam"), ("epicgames", "Epic Games"), ("chrome", "Chrome"), ("msedge", "Edge"),
        ("firefox", "Firefox"), ("308046B0AF4A39CB", "Firefox"), ("brave", "Brave"),
        ("windowsterminal", "Terminal"), ("skydrive", "OneDrive"), ("onedrive", "OneDrive"), ("sechealthui", "Windows Security"),
        ("windows.defender", "Windows Security"), ("immersivecontrolpanel", "Settings"), ("windows.explorer", "File Explorer"),
        ("windowsstore", "Microsoft Store"), ("xboxapp", "Xbox"), ("gamingapp", "Xbox"), ("zoom", "Zoom"), ("skype", "Skype"),
        ("phonelink", "Phone Link"), ("yourphone", "Phone Link"), ("calendar", "Calendar"),
    };

    /// <summary>
    /// "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" → "WhatsApp", "Microsoft.Office.OUTLOOK.EXE.15" → "Outlook",
    /// "{6D809377-…}\Foo\Bar.exe" → "Bar"; anything unknown keeps its id, tidied.
    /// </summary>
    public static string FriendlyApp(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "Windows";
        foreach (var (part, name) in Known)
            if (id.Contains(part, StringComparison.OrdinalIgnoreCase))
                return name;
        string s = id;
        int bang = s.IndexOf('!');
        if (bang > 0)
            s = s[..bang];
        // A path to an exe (desktop apps without an app id).
        if (s.Contains('\\') || s.Contains('/'))
            return Path.GetFileNameWithoutExtension(s.Replace('/', '\\').Split('\\')[^1]) is { Length: > 0 } file ? file : id;
        // Package family name: "Publisher.App_hash".
        int us = s.LastIndexOf('_');
        if (us > 0 && s.Length - us - 1 == 13)
            s = s[..us];
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Equals("exe", StringComparison.OrdinalIgnoreCase) && !p.All(char.IsDigit)
                        && !(p.Length >= 6 && p.All(Uri.IsHexDigit) && p.Any(char.IsDigit)) // "5319275A" publisher ids
                        && !p.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && !p.Equals("com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (parts.Count == 0)
            return id;
        string last = parts[^1];
        // "EXCEL" (Office ids) reads better as "Excel".
        return last.Length > 3 && last.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? last[0] + last[1..].ToLowerInvariant() : last;
    }

    public static CopyStamp Stamp(string db)
    {
        static (long, DateTime) Of(string p)
        {
            var f = new FileInfo(p);
            return f.Exists ? (f.Length, f.LastWriteTimeUtc) : (0, DateTime.MinValue);
        }
        var (dl, dw) = Of(db);
        var (wl, ww) = Of(db + "-wal");
        return new CopyStamp(dl, dw, wl, ww);
    }

    /// <summary>Copies the database and its WAL (sharing the files with Windows, which keeps writing them). Returns the copy.</summary>
    public static string CopyTo(string db, string dir)
    {
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "wpn.db");
        foreach (var suffix in new[] { "", "-wal" })
        {
            string from = db + suffix, to = target + suffix;
            if (!File.Exists(from))
            {
                File.Delete(to);
                continue;
            }
            using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }
        File.Delete(target + "-shm"); // rebuilt from the WAL by SQLite
        return target;
    }

    public static void DeleteCopy(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* in use or already gone; next copy overwrites it */ }
    }
}
