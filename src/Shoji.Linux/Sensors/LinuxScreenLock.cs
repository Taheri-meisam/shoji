using System;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Whether the desktop's screensaver / lock screen is up (GNOME, or any desktop with the freedesktop ScreenSaver
/// interface such as KDE). Only asked once nobody has touched the PC for a minute, and then at most every 10 s.
/// </summary>
public static class LinuxScreenLock
{
    const double AskAfterIdleSeconds = 60;
    static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(10);

    static DateTime _lastAsk;
    static int _asking;
    static bool _unsupported;

    /// <summary>True while the screensaver or lock screen is showing (as of the last check).</summary>
    public static bool Active { get; private set; }

    /// <summary>Called on every refresh with the seconds since the last keyboard / mouse input. Cheap; never blocks.</summary>
    public static void Update(double idleSeconds)
    {
        if (idleSeconds < AskAfterIdleSeconds || _unsupported)
        {
            // Someone is at the PC, so it isn't locked (or we can't tell).
            Active = false;
            return;
        }
        if (DateTime.UtcNow - _lastAsk < AskEvery || Interlocked.Exchange(ref _asking, 1) == 1)
            return;
        _lastAsk = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await Ask("org.gnome.ScreenSaver").ConfigureAwait(false)
                             ?? await Ask("org.freedesktop.ScreenSaver").ConfigureAwait(false);
                if (answer == null)
                {
                    _unsupported = true;
                    Log.Write("Screen lock state isn't available (no GNOME / freedesktop ScreenSaver on the session bus).");
                }
                Active = answer == true;
            }
            finally
            {
                Interlocked.Exchange(ref _asking, 0);
            }
        });
    }

    /// <summary>"b true" / "b false" from busctl, or null when that service isn't there.</summary>
    static async Task<bool?> Ask(string service)
    {
        string path = "/" + service.Replace('.', '/');
        var (code, output) = await LinuxCli.RunAsync("busctl", new[] { "--user", "call", service, path, service, "GetActive" }, 1500).ConfigureAwait(false);
        if (code != 0)
            return null;
        output = output.Trim();
        return output == "b true" ? true : output == "b false" ? false : null;
    }
}
