using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji;

/// <summary>The Linux app: the dashboard window, the tray icon and its menu, and live reload of config.json.</summary>
public sealed class App : Application
{
    Dashboard? _dashboard;
    TrayIcon? _tray;
    FileSystemWatcher? _watcher;
    DispatcherTimer? _debounce;
    System.Runtime.InteropServices.PosixSignalRegistration? _sigterm;

    /// <summary>Set by Program for screenshot runs: no tray, no window placement.</summary>
    public static bool Headless { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = "Shoji";
    }

    public static WindowIcon WindowIcon() => new(AssetLoader.Open(new Uri("avares://shoji/Assets/shoji.png")));

    public override void OnFrameworkInitializationCompleted()
    {
        // Only now: touching the dispatcher before Avalonia has set up its platform (X11) creates one without a
        // main loop, and the app then dies at start with "Operation is not supported on this platform".
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Write($"UI error: {e.Exception}");
            e.Handled = true; // a dashboard should keep running
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && !Headless)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _dashboard = new Dashboard();
            // Closed by the user (Alt+F4; stock GNOME shows no tray icon to quit from): quit, instead of staying
            // behind with a dead dashboard that the next config reload would try to show again.
            _dashboard.Closing += (_, e) =>
            {
                if (!e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
            };
            _dashboard.Apply(AppConfig.Load());
            BuildTray();
            WatchConfig();
            // Update check: a toast (once per version) and an "Install update" item in the tray menu; never installs by itself.
            UpdateChecker.Start(a => Dispatcher.UIThread.Post(a), info => _dashboard?.Toast(L.F("Shoji {0} is available — Settings → About", info.Version)),
                () => desktop.Shutdown());
            UpdateChecker.PendingChanged += () => Dispatcher.UIThread.Post(RebuildMenu);
            SingleInstance.Requests += request => Dispatcher.UIThread.Post(() => OnRequest(request));
            // Logging out (or pkill) sends SIGTERM: shut down properly so nvidia-smi is stopped and today's history
            // and energy are saved, instead of the runtime just exiting.
            _sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
            });
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _watcher?.Dispose();
                _sigterm?.Dispose();
            };
            if (Program.OpenSettingsOnStart)
                Dispatcher.UIThread.Post(OpenSettings, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Another start of Shoji asked this one to do something (e.g. open Settings from the app menu).</summary>
    void OnRequest(string request)
    {
        if (request == "settings")
            OpenSettings();
    }

    void OpenSettings()
    {
        if (_dashboard != null)
            UI.SettingsWindow.Open(_dashboard);
    }

    // ---------------------------------------------------------------- tray

    void BuildTray()
    {
        _tray ??= new TrayIcon { Icon = WindowIcon(), ToolTipText = "Shoji" };
        _tray.Clicked += (_, _) => OpenSettings();
        RebuildMenu();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    void RebuildMenu()
    {
        if (_tray == null)
            return;
        var config = AppConfig.Load();
        var menu = new NativeMenu();
        menu.Add(Item(L.T("Settings…"), OpenSettings));
        if (UpdateChecker.Pending is { } update && _dashboard != null)
            menu.Add(Item(L.F("Install update {0}…", update.Version), () => UI.SettingsWindow.OpenAbout(_dashboard)));
        menu.Add(new NativeMenuItemSeparator());

        var pages = new NativeMenu();
        for (int i = 0; i < config.Pages.Count; i++)
        {
            int index = i;
            pages.Add(Item($"{i + 1}  {config.Pages[i].Name}", () => _dashboard?.ShowConfigPage(index)));
        }
        menu.Add(new NativeMenuItem(L.T("Page")) { Menu = pages });

        var themes = new NativeMenu();
        foreach (var t in UI.Themes.All)
        {
            var id = t.Id;
            themes.Add(Check(L.T(t.Name), UI.Theme.Find(config.Theme).Id == id, () => AppConfig.Edit(c => c.Theme = id)));
        }
        menu.Add(new NativeMenuItem(L.T("Theme")) { Menu = themes });
        menu.Add(Check(L.T("Automatic scenes"), config.ScenesEnabled, () => AppConfig.Edit(c => c.ScenesEnabled = !c.ScenesEnabled)));
        menu.Add(Check(L.T("Always on top"), config.Topmost, () => AppConfig.Edit(c => c.Topmost = !c.Topmost)));
        menu.Add(Check(L.T("Start at login"), Autostart.Enabled, () =>
        {
            Autostart.Set(!Autostart.Enabled);
            RebuildMenu();
        }));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(L.T("Edit config.json…"), () => Open(AppConfig.FilePath)));
        menu.Add(Item(L.T("Open data folder"), () => Open(AppConfig.Folder)));
        // The Sensors tile points here for the ids it can show.
        menu.Add(Item(L.T("Export sensor list…"), () =>
        {
            if (_dashboard?.Collector is { } collector)
                Open(collector.ExportSensorList());
        }));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(L.T("Quit Shoji"), () => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()));
        _tray.Menu = menu;
    }

    static NativeMenuItem Item(string header, Action click)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => click();
        return item;
    }

    static NativeMenuItem Check(string header, bool on, Action click)
    {
        var item = new NativeMenuItem(header) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = on };
        item.Click += (_, _) => click();
        return item;
    }

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open {path}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- config reload

    void WatchConfig()
    {
        Directory.CreateDirectory(AppConfig.Folder);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            try
            {
                var config = AppConfig.Load();
                _dashboard?.Apply(config);
                RebuildMenu();
                UI.SettingsWindow.ConfigChanged();
            }
            catch (Exception ex)
            {
                Log.Write($"Reload failed: {ex}");
            }
        };
        _watcher = new FileSystemWatcher(AppConfig.Folder, Path.GetFileName(AppConfig.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (s, e) => changed(s, e);
        _watcher.EnableRaisingEvents = true;
    }
}

/// <summary>"Start at login": a desktop entry in ~/.config/autostart (the freedesktop way, works in GNOME, KDE and others).</summary>
public static class Autostart
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "autostart", "shoji.desktop");

    public static bool Enabled => File.Exists(FilePath);

    public static void Set(bool on)
    {
        try
        {
            if (!on)
            {
                File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"""
                [Desktop Entry]
                Type=Application
                Name=Shoji
                Comment=Dashboard and control deck for a second screen
                Comment[nb]=Dashbord og knappepanel for en ekstra skjerm
                Exec="{AppPaths.ExePath}" --autostart
                Icon=shoji
                X-GNOME-Autostart-enabled=true
                X-GNOME-Autostart-Delay=5
                """);
        }
        catch (Exception ex)
        {
            Log.Write($"Changing start at login failed: {ex.Message}");
        }
    }
}
