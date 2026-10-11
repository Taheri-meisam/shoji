using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Shoji.Core;

/// <summary>
/// Windows 11 shows a taskbar on every screen, and it stays on top of the dashboard: the dashboard never takes focus
/// (so it can't steal it from a game), and Windows only steps the taskbar back for the focused full-screen window.
/// So, while the dashboard covers a screen, that screen's own taskbar (a "secondary" one) is hidden, and shown again
/// when the dashboard leaves, the option is turned off, or Shoji exits. The main taskbar is never touched, and a
/// taskbar Shoji didn't hide itself is left as it is.
/// </summary>
public static class TaskbarHider
{
    const int SW_HIDE = 0, SW_SHOWNA = 8;
    const string BarClass = "Shell_SecondaryTrayWnd";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);

    // The async variant: the taskbar belongs to Explorer, and a hung Explorer mustn't hang us.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindow(IntPtr hWnd);

    // Only taskbars Shoji hid itself are ever shown again (another tool may hide them on purpose, e.g. DisplayFusion).
    // Locked: RestoreAll can run from a crash handler on another thread.
    static readonly HashSet<IntPtr> Hidden = new();
    static readonly object Gate = new();

    // While any taskbar is hidden, a registry marker says so: if Shoji dies without cleaning up, the next start
    // shows them again (see RecoverAfterCrash).
    const string MarkerKey = @"Software\Shoji", MarkerValue = "HiddenTaskbars";
    static bool _marked;

    /// <summary>
    /// Hides the taskbar that sits on <paramref name="dashboard"/> (screen bounds in physical pixels), and shows again
    /// the ones it hid that aren't on it any more. Null = show them all. Safe to call often (it re-checks after Explorer restarts).
    /// </summary>
    public static void Apply(Rectangle? dashboard)
    {
        lock (Gate)
        {
            try
            {
                Hidden.RemoveWhere(h => !IsBar(h));
                for (IntPtr bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, BarClass, null); bar != IntPtr.Zero;
                     bar = FindWindowEx(IntPtr.Zero, bar, BarClass, null))
                {
                    // Which screen the taskbar belongs to (asking Windows also works for an auto-hidden one, which is mostly off-screen).
                    var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                    if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(bar, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info))
                        continue;
                    var m = info.rcMonitor;
                    bool onDashboard = dashboard is { } d && d == new Rectangle(m.Left, m.Top, m.Width, m.Height);
                    bool visible = NativeMethods.IsWindowVisible(bar);
                    if (onDashboard && visible)
                    {
                        Hidden.Add(bar);
                        Mark(true); // before hiding, so a crash right after still gets cleaned up
                        ShowWindowAsync(bar, SW_HIDE);
                    }
                    else if (!onDashboard && Hidden.Remove(bar) && !visible)
                    {
                        ShowWindowAsync(bar, SW_SHOWNA);
                    }
                }
                if (Hidden.Count == 0)
                    Mark(false);
            }
            catch (Exception ex)
            {
                Log.Write($"Taskbar hiding failed: {ex.Message}");
            }
        }
    }

    /// <summary>Shows every taskbar this app hid (on exit, and from the crash handler).</summary>
    public static void RestoreAll()
    {
        // From a crash handler: don't wait forever on a thread that may never let go.
        if (!System.Threading.Monitor.TryEnter(Gate, 1000))
            return; // the marker stays; the next start cleans up
        try
        {
            foreach (var bar in Hidden)
            {
                try
                {
                    if (IsBar(bar))
                        ShowWindowAsync(bar, SW_SHOWNA);
                }
                catch
                {
                    // Explorer gone; nothing to restore
                }
            }
            Hidden.Clear();
            Mark(false);
        }
        finally
        {
            System.Threading.Monitor.Exit(Gate);
        }
    }

    /// <summary>
    /// Once at startup: if the last Shoji ended while it had taskbars hidden (crash, killed, power cut), show every
    /// hidden secondary taskbar. Secondary taskbars aren't hidden otherwise, short of another tool doing it.
    /// </summary>
    public static void RecoverAfterCrash()
    {
        lock (Gate)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(MarkerKey))
                {
                    if (key?.GetValue(MarkerValue) == null)
                        return;
                }
                for (IntPtr bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, BarClass, null); bar != IntPtr.Zero;
                     bar = FindWindowEx(IntPtr.Zero, bar, BarClass, null))
                {
                    if (!NativeMethods.IsWindowVisible(bar))
                        ShowWindowAsync(bar, SW_SHOWNA);
                }
                _marked = true;
                Mark(false);
                Log.Write("Showed the taskbars a previous Shoji left hidden.");
            }
            catch (Exception ex)
            {
                Log.Write($"Restoring hidden taskbars failed: {ex.Message}");
            }
        }
    }

    /// <summary>Still a secondary taskbar (a closed window's handle can be reused by something else).</summary>
    static bool IsBar(IntPtr hwnd)
    {
        if (!IsWindow(hwnd))
            return false;
        var name = new StringBuilder(64);
        return NativeMethods.GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == BarClass;
    }

    /// <summary>Writes (or clears) the marker, only when that changes: Apply runs every few seconds.</summary>
    static void Mark(bool hidden)
    {
        if (hidden == _marked)
            return;
        try
        {
            if (hidden)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(MarkerKey);
                key.SetValue(MarkerValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(MarkerKey, writable: true);
                key?.DeleteValue(MarkerValue, throwOnMissingValue: false);
            }
            _marked = hidden;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't update the hidden taskbar marker: {ex.Message}");
        }
    }
}
