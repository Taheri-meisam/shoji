using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji;

/// <summary>
/// Renders the dashboard (or a Settings section) to a PNG without a display: "shoji --screenshot out.png
/// --size 1280x400 [--page 2] [--theme tokyo] [--wait 5] [--settings-section look]". Used to check layouts and
/// themes, and handy for the README.
/// </summary>
public static class Screenshot
{
    public static int Run(string path, string size, string? page, string? theme, string? wait, string? settingsSection)
    {
        App.Headless = true;
        // SHOJI_DEMO=1: made-up, always identical data instead of this PC's (Platform/DemoData.cs), for scripts/theme-regression.sh.
        bool demo = Environment.GetEnvironmentVariable("SHOJI_DEMO") == "1";
        if (demo)
            Demo.Enable();
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithShojiRendering()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var dims = size.ToLowerInvariant().Split('x');
        double w = double.Parse(dims[0], CultureInfo.InvariantCulture), h = double.Parse(dims[1], CultureInfo.InvariantCulture);
        var config = AppConfig.Load();
        if (theme != null)
            config.Theme = theme; // only for this picture; not saved

        Window window;
        Dashboard? dashboard = null;
        if (settingsSection != null)
        {
            dashboard = new Dashboard(headless: true) { Width = 800, Height = 480 };
            dashboard.Apply(config);
            window = UI.SettingsWindow.ForScreenshot(dashboard, settingsSection, w, h);
            window.Show();
        }
        else
        {
            dashboard = new Dashboard(headless: true) { Width = w, Height = h };
            window = dashboard;
            dashboard.Show();
            dashboard.Apply(config);
            if (page != null && int.TryParse(page, out int p))
                dashboard.ShowConfigPage(p - 1);
        }

        // SHOJI_EDIT_TILE="x,y[,tap x,y]" (test only): hold the mouse there for 0.8 s (opens the tile edit sheet), optionally tap a point after.
        if (Environment.GetEnvironmentVariable("SHOJI_EDIT_TILE") is { } hold && dashboard == window)
        {
            void Pump(double s) { var t = Stopwatch.StartNew(); while (t.Elapsed.TotalSeconds < s) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(20); } }
            var n = hold.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            Pump(1.5);
            window.MouseDown(new Point(n[0], n[1]), Avalonia.Input.MouseButton.Left);
            Pump(0.8);
            window.MouseUp(new Point(n[0], n[1]), Avalonia.Input.MouseButton.Left);
            for (int k = 2; k + 1 < n.Length; k += 2)
            {
                Pump(0.3);
                window.MouseDown(new Point(n[k], n[k + 1]), Avalonia.Input.MouseButton.Left);
                window.MouseUp(new Point(n[k], n[k + 1]), Avalonia.Input.MouseButton.Left);
            }
        }

        // Let the sensors publish a few times and the tiles settle. A stopwatch, not the wall clock: the regression
        // script freezes that. Demo data goes in at two fixed points (so the number of updates never depends on timing),
        // the second one late enough for the calendar and web value to have loaded.
        double settle = double.Parse(wait ?? "4", CultureInfo.InvariantCulture);
        var waited = Stopwatch.StartNew();
        int published = 0;
        while (waited.Elapsed.TotalSeconds < settle)
        {
            if (demo && published < 2 && waited.Elapsed.TotalSeconds >= settle * (published == 0 ? 0.4 : 0.8))
            {
                published++;
                Demo.Publish();
            }
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(40);
        }
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        frame?.Save(path);
        // SHOJI_BENCH=seconds: run the page live for that long and report the CPU it used (a share of one core).
        if (double.TryParse(Environment.GetEnvironmentVariable("SHOJI_BENCH"), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(16);
            }
            process.Refresh();
            double used = (process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds;
            Console.WriteLine($"BENCH {config.Theme}: {used * 100:0}% of a core");
        }
        window.Close();
        if (dashboard != window)
            dashboard?.Close();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine(frame == null ? "No frame rendered" : $"Saved {path}");
        return frame == null ? 1 : 0;
    }
}
