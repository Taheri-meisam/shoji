using System;
using System.IO;
using Shoji.Core;

namespace Shoji;

/// <summary>
/// One Shoji per user: the first start holds a lock file; later starts pass their request ("settings") to it
/// through a small file it watches, then exit.
/// </summary>
public static class SingleInstance
{
    static FileStream? _lock;
    static FileSystemWatcher? _watcher;

    static string LockPath => Path.Combine(AppConfig.Folder, "shoji.lock");
    static string RequestPath => Path.Combine(AppConfig.Folder, "request");

    /// <summary>A request from a later start, on a background thread.</summary>
    public static event Action<string>? Requests;

    /// <summary>True when this is the only running Shoji (it then keeps the lock until it exits).</summary>
    public static bool TryTake()
    {
        Directory.CreateDirectory(AppConfig.Folder);
        try
        {
            // FileShare.None is an exclusive advisory lock (flock) on Linux; it goes away when the process ends.
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }
        _watcher = new FileSystemWatcher(AppConfig.Folder, "request") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        FileSystemEventHandler read = (_, _) =>
        {
            try
            {
                string text = File.ReadAllText(RequestPath).Trim();
                if (text.Length == 0)
                    return; // created but not written yet: the write's own event reads it
                File.Delete(RequestPath);
                Requests?.Invoke(text);
            }
            catch
            {
                // being written; the next event reads it
            }
        };
        _watcher.Created += read;
        _watcher.Changed += read;
        _watcher.Renamed += (sender, e) => read(sender, e);
        _watcher.EnableRaisingEvents = true;
        return true;
    }

    /// <summary>Asks the running Shoji to do something.</summary>
    public static void Send(string request)
    {
        try
        {
            // Written aside and moved into place, so the running Shoji never reads a half-written (empty) file.
            string temp = RequestPath + "." + Environment.ProcessId;
            File.WriteAllText(temp, request);
            File.Move(temp, RequestPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not reach the running Shoji: {ex.Message}");
        }
    }
}
