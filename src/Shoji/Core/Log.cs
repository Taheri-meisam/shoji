using System;
using System.Collections.Generic;
using System.IO;

namespace Shoji.Core;

/// <summary>Tiny append-only log in %APPDATA%\Shoji\log.txt (moved to log.old.txt when it gets large).</summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly Dictionary<string, DateTime> LastWritten = new();
    static string FilePath => Path.Combine(AppConfig.Folder, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                    info.MoveTo(Path.Combine(AppConfig.Folder, "log.old.txt"), overwrite: true); // keep the previous stretch for bug reports
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>Writes at most once per <paramref name="every"/> for the same <paramref name="key"/>, for errors that repeat every refresh.</summary>
    public static void WriteThrottled(string key, string message, TimeSpan every)
    {
        var now = DateTime.UtcNow;
        lock (LastWritten)
        {
            if (LastWritten.TryGetValue(key, out var last) && now - last < every && now >= last)
                return;
            LastWritten[key] = now;
        }
        Write(message);
    }
}
