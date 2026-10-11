using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>The app in focus: what scenes, the FPS counter and game sessions key off.</summary>
/// <summary>Reads the foreground window cheaply (process name and path are cached per process id).</summary>
public sealed class ForegroundReader
{
    readonly Dictionary<int, (string Name, string? Path)> _cache = new();
    readonly int _ownPid = Environment.ProcessId;

    public ForegroundInfo Read()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == NativeMethods.GetShellWindow())
            return ForegroundInfo.None;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint upid);
        int pid = (int)upid;
        if (pid == 0 || pid == _ownPid)
            return ForegroundInfo.None;

        if (!_cache.TryGetValue(pid, out var proc))
        {
            proc = ("", null);
            try
            {
                using var p = Process.GetProcessById(pid);
                proc.Name = p.ProcessName;
                try { proc.Path = p.MainModule?.FileName; }
                catch { /* elevated or protected process */ }
            }
            catch
            {
                // exited
            }
            if (_cache.Count > 300)
                _cache.Clear();
            _cache[pid] = proc;
        }

        var title = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        return new ForegroundInfo(pid, proc.Name, proc.Path, title.ToString(), IsFullscreen(hwnd));
    }

    /// <summary>True when the window exactly covers its monitor (exclusive or borderless full screen).</summary>
    static bool IsFullscreen(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
            return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
            return false;
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info))
            return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>Does an app pattern match the app in focus? See <see cref="AppPattern.Matches"/>.</summary>
    public static bool Matches(ForegroundInfo f, string pattern) => AppPattern.Matches(f, pattern);
}
