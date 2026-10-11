using System.Diagnostics;
using System.IO;

namespace Shoji.Linux;

/// <summary>Short sounds through PipeWire / PulseAudio (best effort: silent if no player is installed).</summary>
public static class Sound
{
    const string Complete = "/usr/share/sounds/freedesktop/stereo/complete.oga";
    const string Alarm = "/usr/share/sounds/freedesktop/stereo/dialog-warning.oga";

    public static void Chime() => Play(Complete);

    public static void Warning() => Play(Alarm);

    static void Play(string file)
    {
        if (!File.Exists(file))
            return;
        foreach (var player in new[] { "pw-play", "paplay" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(player, $"\"{file}\"") { UseShellExecute = false, RedirectStandardError = true })?.Dispose();
                return;
            }
            catch
            {
                // try the next player
            }
        }
    }
}
