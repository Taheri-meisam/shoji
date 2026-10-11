using System;
using System.Linq;
using System.Windows.Forms;

namespace Shoji.Core;

/// <summary>Resolves the "Monitor" setting to an actual screen.</summary>
public static class ScreenPlacer
{
    /// <summary>
    /// The screen to use, or null if it isn't connected (yet). In "auto" mode we deliberately
    /// return null when there is only one monitor: a borderless full-screen dashboard covering
    /// your main display at logon would be worse than waiting for the small screen to appear.
    /// </summary>
    public static Screen? Find(string setting)
    {
        var screens = Screen.AllScreens;
        var value = (setting ?? "auto").Trim();

        if (value.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return Screen.PrimaryScreen;

        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
        {
            return screens
                .Where(s => !s.Primary)
                .OrderBy(s => (long)s.Bounds.Width * s.Bounds.Height)
                .FirstOrDefault();
        }

        if (int.TryParse(value, out int number))
            return number >= 1 && number <= screens.Length ? Ordered(screens)[number - 1] : null;

        return screens.FirstOrDefault(s => s.DeviceName.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Screens in a stable order (left to right, top to bottom) for numbering.</summary>
    public static Screen[] Ordered(Screen[]? screens = null) =>
        (screens ?? Screen.AllScreens).OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();

    public static string Describe(Screen s) =>
        $"{s.DeviceName.Replace(@"\\.\", "")}  {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? "  " + L.T("(main)") : "")}";
}
