using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Shoji.Core;

namespace Shoji;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        HookErrors(app);
        // The language first, so the install / setup dialogs below speak it too (without creating config.json yet).
        L.Apply(PeekLanguage());

        // Install / uninstall / portable switches (from Settings → Apps, the Settings window or the first-run dialog).
        if (args.Contains("--uninstall"))
        {
            Installer.Uninstall(quiet: args.Contains("--quiet"));
            return 0;
        }
        if (args.Contains("--install"))
        {
            // "--install --quiet" (winget and other package managers): no dialogs, exit code 0 only when it worked.
            bool quiet = args.Contains("--quiet");
            bool ok = Installer.Install(quiet);
            return ok || !quiet ? 0 : 1;
        }
        if (args.Contains("--portable"))
        {
            SingleInstance.RequestExit(TimeSpan.FromSeconds(10));
            if (Installer.MakePortable())
                RestartSelf();
            return 0;
        }

        // Shoji used to be called WinDisplay: take over its settings, autostart and install (once).
        if (!args.Contains("--restarted") && LegacyMigration.Needed && !LegacyMigration.Run())
            return 0;

        // Started from a download or USB stick: offer to install / update, or to run portable.
        if (Installer.ShouldOffer(args))
        {
            switch (UI.SetupWindow.Ask())
            {
                case UI.SetupChoice.Install:
                    Installer.Install();
                    return 0;
                case UI.SetupChoice.Portable:
                    if (Installer.MakePortable())
                        RestartSelf();
                    return 0;
                case UI.SetupChoice.Cancel:
                    return 0;
            }
        }

        // One instance only. After "Restart as admin" or an update the old instance may still be exiting, so wait a bit.
        bool restarted = args.Contains("--restarted");
        using var instance = SingleInstance.TryAcquire(restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (instance == null)
            return 0;
        TaskbarHider.RecoverAfterCrash(); // a previous run that crashed may have left a taskbar hidden

        var config = AppConfig.Load();
        L.Apply(config.Language);
        // Settings rebuilds itself in the new language (after the event, which may come from one of its own controls).
        L.Changed += () => app.Dispatcher.BeginInvoke(UI.SettingsWindow.LanguageChanged);
        var window = new MainWindow();
        TrayIcon? tray = null;

        bool exiting = false;
        void Exit()
        {
            if (exiting)
                return; // an update both asks through SingleInstance and quits by itself
            exiting = true;
            tray?.Dispose();
            window.Close();
            app.Shutdown();
        }

        tray = new TrayIcon(
            config: () => config,
            currentScreen: () => window.CurrentScreen,
            exportSensors: () => window.Collector!.ExportSensorList(),
            openSettings: () => UI.SettingsWindow.Open(tray!.ExportSensors, tray!.EnableCpuSensors, () => window.RecentApps, () => window.CurrentPageIndex),
            currentPage: () => window.CurrentPageIndex,
            showPage: index => window.ShowPage((index + 1).ToString()),
            exit: Exit);
        // The tile editor's "More settings…" opens Settings the same way the tray does.
        window.OpenSettings = () => UI.SettingsWindow.Open(tray!.ExportSensors, tray!.EnableCpuSensors, () => window.RecentApps, () => window.CurrentPageIndex);

        // Install, update and uninstall ask the running copy to exit this way.
        instance.ListenForExit(() => app.Dispatcher.BeginInvoke(Exit));
        // Update check: a tray balloon and a dashboard toast once per version; installing waits for a tap in Settings.
        UpdateChecker.Start(a => app.Dispatcher.BeginInvoke(a), info =>
        {
            tray?.Notify(L.F("Shoji {0} is available. Install it in Settings → About & install.", info.Version));
            window.Toast(L.F("Shoji {0} is available — Settings → About", info.Version));
        }, () => app.Dispatcher.BeginInvoke(Exit));
        if (AppPaths.IsInstalledCopy)
            Installer.Register(); // keeps the Start menu shortcut and Settings → Apps entry (and its version) current
        Task.Run(() =>
        {
            // The exe may have moved since autostart was set up (portable folder moved, now installed…).
            var problem = Startup.Repair();
            if (problem != null)
                app.Dispatcher.BeginInvoke(() => tray?.Notify(problem));
        });

        window.Apply(config);
        if (config.IsFallback)
            tray.Notify(L.T("config.json has an error, so defaults are in use. Nothing is saved over it until it's fixed (see log.txt)."));
        if (args.Contains("--installed"))
            tray.Notify(L.F("Shoji {0} is installed. Find it in the Start menu; settings are in this tray icon.", AppPaths.VersionText));
        else if (window.CurrentScreen == null)
            tray.Notify(L.T("No second monitor found yet. Right-click this icon → Show on monitor to pick one."));

        // Live reload: edit config.json (or use the tray menu) and the dashboard rebuilds itself.
        using var watcher = WatchConfig(app.Dispatcher, () =>
        {
            config = AppConfig.Load();
            if (config.IsFallback)
                tray.Notify(L.T("config.json has an error, so defaults are in use. Nothing is saved over it until it's fixed (see log.txt)."));
            window.Apply(config);
            UI.SettingsWindow.ConfigChanged();
        });

        string mode = AppPaths.IsInstalledCopy ? "installed" : AppPaths.IsPortable ? "portable" : "not installed";
        Log.Write($"Started {AppPaths.VersionText} ({mode}, {AppPaths.ExePath}). Elevated: {Startup.IsElevated}. PawnIO: {CpuSensorSetup.PawnIoInstalled}. " +
                  $"Theme: {config.Theme}. Monitor setting: {config.Monitor}");
        app.Run();
        return 0;
    }

    /// <summary>The Language setting from config.json, without <see cref="AppConfig.Load"/> (which creates the file).</summary>
    static string? PeekLanguage()
    {
        try
        {
            if (!File.Exists(AppConfig.FilePath))
                return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppConfig.FilePath),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("Language", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch
        {
            // unreadable: the system language until the config loads properly
        }
        return null;
    }

    static void RestartSelf() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true, Arguments = "--restarted" });

    static FileSystemWatcher WatchConfig(Dispatcher dispatcher, Action reload)
    {
        Directory.CreateDirectory(AppConfig.Folder);
        var debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            try
            {
                reload();
            }
            catch (Exception ex)
            {
                Log.Write($"Reload failed: {ex}");
            }
        };

        var watcher = new FileSystemWatcher(AppConfig.Folder, Path.GetFileName(AppConfig.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        // Editors fire several events per save; restart the debounce timer on each one.
        FileSystemEventHandler changed = (_, _) => dispatcher.BeginInvoke(() =>
        {
            debounce.Stop();
            debounce.Start();
        });
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (s, e) => changed(s, e);

        // The watcher stops for good on an error (buffer overflow, the folder briefly gone, a network drive dropping):
        // restart it, retrying every few seconds, and reload once since changes may have been missed meanwhile.
        var retry = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        void Restart()
        {
            retry.Stop();
            try
            {
                Directory.CreateDirectory(AppConfig.Folder);
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
                debounce.Stop();
                debounce.Start();
            }
            catch (Exception ex)
            {
                Log.Write($"Restarting the config watcher failed: {ex.Message}");
                retry.Start();
            }
        }
        retry.Tick += (_, _) => Restart();
        watcher.Error += (_, e) =>
        {
            Log.Write($"Config watcher error: {e.GetException().Message}");
            dispatcher.BeginInvoke(Restart);
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    static void HookErrors(Application app)
    {
        // A dashboard should keep running; log problems instead of crashing.
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"UI error: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write($"Fatal: {e.ExceptionObject}");
            TaskbarHider.RestoreAll(); // don't leave a screen without its taskbar
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TaskbarHider.RestoreAll();
        // The tray icon and its menu are WinForms: without this, an error there shows WinForms' crash dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => Log.Write($"Tray error: {e.Exception}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"Background error: {e.Exception}");
            e.SetObserved();
        };
    }
}
