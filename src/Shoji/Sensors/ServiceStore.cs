using System;
using System.Collections.Generic;
using System.Linq;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Long-lived helpers shared by tiles (a Home Assistant poller, a Twitch connection, the focus timer…), keyed by
/// their settings. They outlive tile rebuilds (theme / page changes) and collector rebuilds (tiles switched on or off),
/// so a running timer keeps running and chats don't reconnect. After each dashboard rebuild, helpers that no tile
/// asked for any more are disposed.
/// </summary>
public static class ServiceStore
{
    static readonly object Gate = new();
    static readonly Dictionary<string, IDisposable> Services = new();
    static readonly HashSet<string> Used = new();
    static bool _building;

    public static T Get<T>(string key, Func<T> create) where T : class, IDisposable
    {
        IDisposable? replaced;
        T created;
        lock (Gate)
        {
            if (_building)
                Used.Add(key);
            if (Services.TryGetValue(key, out replaced) && replaced is T t)
                return t;
            // A different kind of helper under the same key gets replaced, so it has to be shut down too.
            created = create();
            Services[key] = created;
        }
        if (replaced != null)
            Dispose(replaced);
        return created;
    }

    /// <summary>Call before creating the tiles…</summary>
    public static void BeginBuild()
    {
        lock (Gate)
        {
            _building = true;
            Used.Clear();
        }
    }

    /// <summary>…and after: disposes helpers that none of the new tiles use.</summary>
    public static void EndBuild()
    {
        List<IDisposable> unused;
        lock (Gate)
        {
            _building = false;
            var keys = Services.Keys.Where(k => !Used.Contains(k)).ToList();
            unused = keys.Select(k => Services[k]).ToList();
            foreach (var k in keys)
                Services.Remove(k);
        }
        foreach (var s in unused)
            Dispose(s);
    }

    public static void DisposeAll()
    {
        List<IDisposable> all;
        lock (Gate)
        {
            all = Services.Values.ToList();
            Services.Clear();
        }
        foreach (var s in all)
            Dispose(s);
    }

    static void Dispose(IDisposable s)
    {
        try { s.Dispose(); }
        catch (Exception ex) { Log.Write($"Service dispose failed: {ex.Message}"); }
    }
}
