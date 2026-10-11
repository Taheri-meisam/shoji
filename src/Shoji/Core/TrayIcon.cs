using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Shoji.Core;

/// <summary>Notification-area icon: quick settings, theme picker, and the way into the Settings window.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly Func<AppConfig> _config;
    readonly Func<Screen?> _currentScreen;
    readonly Func<string> _exportSensors;
    readonly Action _openSettings;
    readonly Func<int> _currentPage;
    readonly Action<int> _showPage;
    readonly Action _exit;

    /// <param name="config">Current config. Changes go through <see cref="AppConfig.Edit"/>, which saves and triggers a live reload.</param>
    public TrayIcon(Func<AppConfig> config, Func<Screen?> currentScreen, Func<string> exportSensors, Action openSettings,
                    Func<int> currentPage, Action<int> showPage, Action exit)
    {
        _config = config;
        _currentScreen = currentScreen;
        _exportSensors = exportSensors;
        _openSettings = openSettings;
        _currentPage = currentPage;
        _showPage = showPage;
        _exit = exit;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon() ?? DrawIcon(),
            Text = "Shoji",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        // Rebuild on open so check marks and the monitor list are always current.
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu(_icon.ContextMenuStrip);
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowMenu();
        };
        _icon.MouseDoubleClick += (_, _) => _openSettings();
        // Asking for the startup mode runs schtasks (up to seconds): never on the UI thread, so it's cached.
        _icon.ContextMenuStrip.Opened += (_, _) => RefreshStartupMode();
        RefreshStartupMode();
        BuildMenu(_icon.ContextMenuStrip);
    }

    static readonly System.Reflection.MethodInfo? ShowContextMenu =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// Left click opens the menu like a right click. NotifyIcon's own (private) way brings the hidden tray window to the
    /// front first, without which the menu doesn't close when you click elsewhere.
    /// </summary>
    void ShowMenu()
    {
        try
        {
            if (ShowContextMenu != null)
            {
                ShowContextMenu.Invoke(_icon, null);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Opening the tray menu the usual way failed: {ex.Message}");
        }
        _icon.ContextMenuStrip?.Show(Cursor.Position);
    }

    StartupMode? _startupMode;
    ToolStripMenuItem[] _startupItems = Array.Empty<ToolStripMenuItem>();
    bool _startupBusy;

    /// <summary>Reads the startup mode in the background and updates the check marks if the menu shows them.</summary>
    void RefreshStartupMode()
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null)
            return;
        Task.Run(() =>
        {
            StartupMode mode;
            try { mode = Startup.Current; }
            catch (Exception ex)
            {
                Log.Write($"Reading the startup mode failed: {ex.Message}");
                return;
            }
            ui.BeginInvoke(() =>
            {
                _startupMode = mode;
                ShowStartupMode();
            });
        });
    }

    void ShowStartupMode()
    {
        var modes = new[] { StartupMode.Off, StartupMode.Normal, StartupMode.Elevated };
        for (int i = 0; i < _startupItems.Length && i < modes.Length; i++)
        {
            _startupItems[i].Checked = _startupMode == modes[i];
            _startupItems[i].Enabled = !_startupBusy;
        }
    }

    public void Notify(string text) => _icon.ShowBalloonTip(4000, "Shoji", text, ToolTipIcon.Info);

    void BuildMenu(ContextMenuStrip menu)
    {
        var config = _config();
        menu.Items.Clear();

        var header = new ToolStripMenuItem("Shoji") { Enabled = false };
        menu.Items.Add(header);
        var settings = new ToolStripMenuItem(L.T("Settings…"), null, (_, _) => _openSettings()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(settings);
        if (!AppPaths.IsInstalledCopy)
        {
            // The new process asks this one to exit, installs (or updates), and starts the installed copy.
            menu.Items.Add(Installer.IsInstalled ? L.T("Update the installed Shoji with this copy…") : L.T("Install Shoji on this PC…"), null,
                (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(AppPaths.ExePath, "--install") { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Write($"Starting the installer failed: {ex.Message}"); }
                });
        }

        if (CpuSensorSetup.Ready)
            menu.Items.Add(new ToolStripMenuItem(L.T("CPU temperature: working (admin + PawnIO)")) { Enabled = false });
        else
            menu.Items.Add(CpuSensorSetup.PawnIoInstalled
                ? L.T("Enable CPU temperature (restart as admin)")
                : L.T("Enable CPU temperature (install PawnIO driver)…"), null, (_, _) => EnableCpuSensors());
        menu.Items.Add(new ToolStripSeparator());

        // --- theme
        var theme = new ToolStripMenuItem(L.T("Theme"));
        foreach (var t in UI.Themes.All)
        {
            var id = t.Id;
            AddChoice(theme, t.Name, UI.Theme.Find(config.Theme).Id == id, () => AppConfig.Edit(c => c.Theme = id));
        }
        menu.Items.Add(theme);

        // --- pages and scenes
        int pageIndex = Math.Clamp(_currentPage(), 0, config.Pages.Count - 1);
        var pages = new ToolStripMenuItem(L.T("Page"));
        for (int i = 0; i < config.Pages.Count; i++)
        {
            int index = i;
            AddChoice(pages, $"{i + 1}. {config.Pages[i].Name}", i == pageIndex, () => _showPage(index));
        }
        menu.Items.Add(pages);

        var scenes = new ToolStripMenuItem(L.T("Automatic scenes")) { Checked = config.ScenesEnabled };
        scenes.Click += (_, _) => AppConfig.Edit(c => c.ScenesEnabled = !c.ScenesEnabled);
        menu.Items.Add(scenes);

        // --- tiles on the page that's showing: quick on/off for every module type
        var page = config.Pages[pageIndex];
        var tiles = new ToolStripMenuItem(L.F("Tiles on \"{0}\"", page.Name));
        foreach (var info in ModuleCatalog.All)
        {
            var type = info.Type;
            bool on = page.Modules.Any(m => m.Enabled && m.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            AddChoice(tiles, L.T(info.Name), on, () => AppConfig.Edit(c => ToggleModule(c.Pages[pageIndex], type, !on)));
        }
        tiles.DropDownItems.Add(new ToolStripSeparator());
        var presets = new ToolStripMenuItem(L.T("Layout preset for this page"));
        foreach (var (id, name, description) in ModuleCatalog.Presets)
        {
            var presetId = id;
            presets.DropDownItems.Add(new ToolStripMenuItem($"{L.T(name)}  –  {L.T(description)}", null,
                (_, _) => AppConfig.Edit(c => ModuleCatalog.ApplyPreset(c.Pages[pageIndex], presetId))));
        }
        tiles.DropDownItems.Add(presets);
        menu.Items.Add(tiles);

        // --- monitor
        var monitor = new ToolStripMenuItem(L.T("Show on monitor"));
        var current = _currentScreen();
        AddChoice(monitor, L.T("Auto (smallest secondary)"), config.Monitor.Equals("auto", StringComparison.OrdinalIgnoreCase),
            () => SetMonitor("auto"));
        foreach (var s in ScreenPlacer.Ordered())
        {
            var screen = s;
            AddChoice(monitor, ScreenPlacer.Describe(screen),
                !config.Monitor.Equals("auto", StringComparison.OrdinalIgnoreCase) && current?.DeviceName == screen.DeviceName,
                () => SetMonitor(screen.DeviceName));
        }
        menu.Items.Add(monitor);

        // --- startup
        var startup = new ToolStripMenuItem(L.T("Start with Windows"));
        AddChoice(startup, L.T("Off"), false, () => SetStartup(StartupMode.Off));
        AddChoice(startup, L.T("On"), false, () => SetStartup(StartupMode.Normal));
        AddChoice(startup, L.T("On, as admin (all sensors, no prompt)"), false, () => SetStartup(StartupMode.Elevated));
        _startupItems = startup.DropDownItems.OfType<ToolStripMenuItem>().ToArray();
        ShowStartupMode(); // the cached mode; a fresh one is read as the menu opens
        menu.Items.Add(startup);

        var topmost = new ToolStripMenuItem(L.T("Always on top")) { Checked = config.Topmost };
        topmost.Click += (_, _) => AppConfig.Edit(c => c.Topmost = !c.Topmost);
        menu.Items.Add(topmost);

        if (!Startup.IsElevated)
            menu.Items.Add(L.T("Restart as admin"), null, (_, _) =>
            {
                if (Startup.RestartElevated())
                    _exit();
            });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Edit config.json…"), null, (_, _) => Open(AppConfig.FilePath, editor: true));
        menu.Items.Add(L.T("Export sensor list…"), null, (_, _) => ExportSensors());
        menu.Items.Add(L.T("Open data folder"), null, (_, _) => Open(AppConfig.Folder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Exit"), null, (_, _) => _exit());
    }

    /// <summary>Switches a module type on or off on a page. Turning on a type that isn't there adds it at the end.</summary>
    public static void ToggleModule(PageConfig page, string type, bool on)
    {
        var existing = page.Modules.Where(m => m.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        if (existing.Count == 0 && on)
            page.Modules.Add(ModuleCatalog.New(type));
        foreach (var m in existing)
            m.Enabled = on;
    }

    /// <summary>Writes sensors.txt in the background (opening the sensors can take seconds) and opens it.</summary>
    public void ExportSensors()
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        Task.Run(() =>
        {
            try
            {
                string path = _exportSensors();
                ui.BeginInvoke(() => Open(path, editor: true));
            }
            catch (Exception ex)
            {
                Log.Write($"Sensor export failed: {ex}");
                ui.BeginInvoke(() => Notify(L.T("Sensor export failed – see log.txt")));
            }
        });
    }

    /// <summary>Installs PawnIO / restarts as admin in the background, then exits this copy if a new one started.</summary>
    public void EnableCpuSensors()
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        Task.Run(() =>
        {
            bool restarted = CpuSensorSetup.Run(text => ui.BeginInvoke(() => Notify(text)));
            if (restarted)
                ui.BeginInvoke(_exit);
        });
    }

    static void AddChoice(ToolStripMenuItem parent, string text, bool isChecked, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { Checked = isChecked };
        item.Click += (_, _) => onClick();
        parent.DropDownItems.Add(item);
    }

    static void SetMonitor(string value) => AppConfig.Edit(c => c.Monitor = value);

    /// <summary>Changes the startup mode in the background (schtasks, a UAC prompt and PowerShell take a while).</summary>
    void SetStartup(StartupMode mode)
    {
        if (_startupBusy)
            return;
        _startupBusy = true;
        ShowStartupMode();
        var ui = System.Windows.Application.Current.Dispatcher;
        Task.Run(() =>
        {
            string? problem = null;
            try
            {
                Startup.Set(mode);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                problem = L.T("Cancelled – admin permission is needed for that option.");
            }
            catch (Exception ex)
            {
                Log.Write($"Changing startup mode failed: {ex}");
                problem = L.T("Could not change the startup setting – see log.txt");
            }
            ui.BeginInvoke(() =>
            {
                _startupBusy = false;
                if (problem != null)
                    Notify(problem);
                RefreshStartupMode(); // show what actually happened
            });
        });
    }

    static void Open(string path, bool editor = false)
    {
        try
        {
            if (editor)
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open {path}: {ex.Message}");
        }
    }

    /// <summary>The app icon at tray size (embedded app.ico).</summary>
    static Icon? LoadIcon()
    {
        try
        {
            using var s = typeof(TrayIcon).Assembly.GetManifestResourceStream("Shoji.app.ico");
            return s == null ? null : new Icon(s, SystemInformation.SmallIconSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fallback: a small "monitor with bars" glyph drawn in code.</summary>
    static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var frame = new SolidBrush(Color.FromArgb(0x15, 0x1A, 0x24));
            using var border = new Pen(Color.FromArgb(0x4F, 0xC3, 0xF7), 2);
            using var bar = new SolidBrush(Color.FromArgb(0x4F, 0xC3, 0xF7));
            g.FillRectangle(frame, 2, 5, 28, 20);
            g.DrawRectangle(border, 2, 5, 28, 20);
            g.FillRectangle(bar, 7, 15, 4, 7);
            g.FillRectangle(bar, 14, 10, 4, 12);
            g.FillRectangle(bar, 21, 13, 4, 9);
            g.FillRectangle(bar, 12, 27, 8, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
