using System;
using System.Collections.Generic;
using System.Linq;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// One switch for everything that moves: set while nobody can see the dashboard (the display is off, the PC is
/// locked, night mode has it black). Backdrop motion, gauges, weather icons, neon flicker and Mirror tiles stop
/// while it's on and pick up again when it goes off. Set and read on the UI thread (Mirror's capture loop only reads).
/// </summary>
public static class Motion
{
    static readonly HashSet<string> Reasons = new();
    static volatile bool _paused;

    /// <summary>True while nothing should animate or capture.</summary>
    public static bool Paused => _paused;

    /// <summary>Raised on the UI thread when <see cref="Paused"/> flips.</summary>
    public static event Action? Changed;

    /// <summary>Turns one reason ("display off", "locked", "night") on or off; paused while any is on.</summary>
    public static void Set(string reason, bool on)
    {
        if (!(on ? Reasons.Add(reason) : Reasons.Remove(reason)))
            return;
        bool paused = Reasons.Count > 0;
        if (paused == _paused)
            return;
        _paused = paused;
        Log.Write(paused ? $"Animations paused ({string.Join(", ", Reasons.OrderBy(r => r))})" : "Animations resumed");
        Changed?.Invoke();
    }
}
