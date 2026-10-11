using System;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji;

static class Program
{
    /// <summary>Started with --settings (the app menu entry): open Settings right away.</summary>
    public static bool OpenSettingsOnStart { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        L.Apply(AppConfig.Load().Language); // before any text is made (screenshots and --install too)

        if (Arg("--screenshot") is { } shot)
            return Screenshot.Run(shot, Arg("--size") ?? "1280x400", Arg("--page"), Arg("--theme"), Arg("--wait"), Arg("--settings-section"));

        // Used by install.sh: put Shoji in the app menu (and optionally start it at login), or take it out.
        if (args.Contains("--install"))
        {
            bool added = Linux.Installer.AddToAppMenu();
            if (args.Contains("--autostart-on"))
                Autostart.Set(true);
            Console.WriteLine((added ? L.T("Shoji is in your app menu") : L.F("Shoji could not add itself to the app menu (see {0}/log.txt)", AppConfig.Folder)) +
                              (Autostart.Enabled ? L.T(" and starts at login.") : "."));
            return added ? 0 : 1;
        }
        if (args.Contains("--uninstall"))
        {
            Linux.Installer.RemoveFromAppMenu();
            Autostart.Set(false);
            Console.WriteLine(L.F("Removed Shoji from the app menu and from starting at login. Your settings stay in {0}", AppConfig.Folder));
            return 0;
        }

        OpenSettingsOnStart = args.Contains("--settings");
        // After an update the old copy may still be shutting down: wait up to 10 s for its lock instead of handing over to it.
        bool first = SingleInstance.TryTake();
        for (int i = 0; !first && args.Contains("--restarted") && i < 40; i++)
        {
            System.Threading.Thread.Sleep(250);
            first = SingleInstance.TryTake();
        }
        if (!first)
        {
            // Already running: starting Shoji again (e.g. from the app grid) opens its Settings.
            SingleInstance.Send("settings");
            return 0;
        }
        Log.Write($"Shoji {AppPaths.VersionText} (Linux) starting");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Avalonia looks for Unity's global menu service, which most desktops don't have: not worth logging.
            if (!e.Exception.ToString().Contains("com.canonical.AppMenu.Registrar"))
                Log.Write($"Background error: {e.Exception}");
            e.SetObserved();
        };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithShojiRendering()
            // No global (Unity-style) menu bar to export to: the dashboard has no menu, and the lookup only logs errors.
            .With(new X11PlatformOptions { UseDBusMenu = false })
            .LogToTrace();

    /// <summary>
    /// Redraw only the parts of the window that changed. Without this, a value changing in the top-left tile and
    /// another in the bottom-right one repaint everything between them, which on a software-rendered desktop (no GPU
    /// driver for OpenGL) is most of the CPU an always-on dashboard uses.
    /// </summary>
    public static AppBuilder WithShojiRendering(this AppBuilder builder) =>
        builder.With(new Avalonia.Rendering.Composition.CompositionOptions
        {
            UseRegionDirtyRectClipping = Environment.GetEnvironmentVariable("SHOJI_NO_REGIONS") == null,
        });
}
