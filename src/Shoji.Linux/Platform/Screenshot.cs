using System;
using System.Collections.Generic;
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
        // SHOJI_SETUP=id | restore (test only): apply that setup (Settings → Appearance) to the saved settings first, or bring the layout from before back.
        if (Environment.GetEnvironmentVariable("SHOJI_SETUP") is { Length: > 0 } setupId)
            AppConfig.Edit(c =>
            {
                if (setupId == "restore")
                    Setups.Restore(c);
                else if (Setups.All.FirstOrDefault(x => x.Id == setupId) is { } setup)
                    Setups.Apply(c, setup);
            });
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

        // SHOJI_MOUSE_MOVE="x,y" (test only): move the mouse there once the page is up (brings up the Settings / Close buttons).
        if (Environment.GetEnvironmentVariable("SHOJI_MOUSE_MOVE") is { } move && dashboard == window)
        {
            var n = move.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var t = Stopwatch.StartNew();
            while (t.Elapsed.TotalSeconds < 1) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(20); }
            window.MouseMove(new Point(n[0], n[1]));
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
            using var probe = Environment.GetEnvironmentVariable("SHOJI_BENCH_PROBE") == "1" ? new BenchProbe() : null;
            long allocated = GC.GetTotalAllocatedBytes();
            int gen2 = GC.CollectionCount(2);
            var cpu = process.TotalProcessorTime;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Timers don't fire without a display: SHOJI_BENCH_AWAKE=1 hands out the motion frames from here, at their real pace.
            bool motion = Environment.GetEnvironmentVariable("SHOJI_BENCH_AWAKE") == "1";
            int handed = UI.FrameClock.Ticks;
            void Motion()
            {
                if (motion && clock.Elapsed.TotalSeconds * UI.FrameClock.Fps >= UI.FrameClock.Ticks - handed)
                    UI.FrameClock.Tick();
            }
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Motion();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(16);
            }
            process.Refresh();
            double used = (process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds;
            Console.WriteLine($"BENCH {config.Theme}: {used * 100:0}% of a core");
            Console.WriteLine($"ALLOC {config.Theme}: {(GC.GetTotalAllocatedBytes() - allocated) / 1e6 / clock.Elapsed.TotalSeconds:0.0} MB per second, {GC.CollectionCount(2) - gen2} full collections");
            probe?.Report(clock.Elapsed.TotalSeconds);

            // Then the same again, counting the frames whose picture changed: on a PC that draws in software every
            // such frame costs the same whatever changed, so this number is what the real CPU use follows.
            int changed = 0, ticks = UI.FrameClock.Ticks;
            byte[]? last = null;
            (int X0, int Y0, int X1, int Y1) area = (int.MaxValue, int.MaxValue, -1, -1);
            ulong before = 0;
            clock.Restart();
            handed = ticks = UI.FrameClock.Ticks;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Motion();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                if (window.CaptureRenderedFrame() is { } shot)
                {
                    ulong sum = Checksum(shot);
                    if (sum != before)
                    {
                        changed++;
                        Where(shot, ref last, ref area);
                    }
                    before = sum;
                    shot.Dispose();
                }
                Thread.Sleep(16);
            }
            Console.WriteLine($"FRAMES {config.Theme}: {changed / clock.Elapsed.TotalSeconds:0.0} changed per second, {(UI.FrameClock.Ticks - ticks) / clock.Elapsed.TotalSeconds:0.0} motion ticks per second{(UI.FrameClock.Paused ? " (paused)" : "")}; moving: {UI.FrameClock.Who}; where: {(area.X1 < 0 ? "nowhere" : $"x {area.X0}–{area.X1}, y {area.Y0}–{area.Y1}")}");
        }
        window.Close();
        if (dashboard != window)
            dashboard?.Close();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine(frame == null ? "No frame rendered" : $"Saved {path}");
        return frame == null ? 1 : 0;
    }

    /// <summary>Grows <paramref name="area"/> by the box of pixels that differ from the frame before.</summary>
    static void Where(Avalonia.Media.Imaging.WriteableBitmap picture, ref byte[]? last, ref (int X0, int Y0, int X1, int Y1) area)
    {
        using var pixels = picture.Lock();
        int stride = pixels.RowBytes, height = pixels.Size.Height;
        var now = new byte[stride * height];
        System.Runtime.InteropServices.Marshal.Copy(pixels.Address, now, 0, now.Length);
        if (last != null && last.Length == now.Length)
            for (int y = 0; y < height; y++)
            {
                var a = now.AsSpan(y * stride, stride);
                var b = last.AsSpan(y * stride, stride);
                if (a.SequenceEqual(b))
                    continue;
                int first = a.CommonPrefixLength(b) / 4, end = stride - 1;
                while (end > 0 && a[end] == b[end])
                    end--;
                area = (Math.Min(area.X0, first), Math.Min(area.Y0, y), Math.Max(area.X1, end / 4), Math.Max(area.Y1, y));
            }
        last = now;
    }

    static ulong Checksum(Avalonia.Media.Imaging.WriteableBitmap picture)
    {
        using var pixels = picture.Lock();
        ulong sum = 1469598103934665603;
        long words = (long)pixels.RowBytes * pixels.Size.Height / 8;
        for (long i = 0; i < words; i++)
            sum = (sum ^ (ulong)System.Runtime.InteropServices.Marshal.ReadInt64(pixels.Address, (int)(i * 8))) * 1099511628211;
        return sum;
    }
}

/// <summary>For SHOJI_BENCH with SHOJI_BENCH_PROBE=1: what gets allocated (by type, sampled by the runtime every ~100 KB) and where exceptions are thrown.</summary>
sealed class BenchProbe : System.Diagnostics.Tracing.EventListener
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _types = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _throws = new();

    readonly Dictionary<string, long> _threads = Threads();

    public BenchProbe() => AppDomain.CurrentDomain.FirstChanceException += OnThrow;

    /// <summary>CPU ticks (1/100 s) per thread so far, "id name" → ticks.</summary>
    static Dictionary<string, long> Threads()
    {
        var all = new Dictionary<string, long>();
        try
        {
            foreach (var task in System.IO.Directory.GetDirectories("/proc/self/task"))
            {
                string stat = System.IO.File.ReadAllText(task + "/stat");
                var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                all[System.IO.Path.GetFileName(task) + " " + System.IO.File.ReadAllText(task + "/comm").Trim()] = long.Parse(f[11]) + long.Parse(f[12]);
            }
        }
        catch { /* not Linux */ }
        return all;
    }

    void OnThrow(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        var frames = new StackTrace(e.Exception, false).GetFrames().Concat(new StackTrace(1, false).GetFrames())
            .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.FullName?.StartsWith("Shoji") == true)
            .Select(m => m!.DeclaringType!.Name + "." + m.Name).Distinct().Take(3);
        _throws.AddOrUpdate(e.Exception.GetType().Name + " @ " + string.Join(" < ", frames), 1, (_, n) => n + 1);
    }

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("GCStart") == true && e.PayloadNames != null && e.Payload != null)
        {
            int reason = e.PayloadNames.IndexOf("Reason"), depth = e.PayloadNames.IndexOf("Depth");
            if (reason >= 0 && depth >= 0)
                _types.AddOrUpdate($"GC gen{e.Payload[depth]} reason {e.Payload[reason]}", 1_000_000, (_, n) => n + 1_000_000);
            return;
        }
        if (e.EventName?.StartsWith("GCAllocationTick") != true || e.PayloadNames == null || e.Payload == null)
            return;
        int type = e.PayloadNames.IndexOf("TypeName"), size = e.PayloadNames.IndexOf("AllocationAmount64"), kind = e.PayloadNames.IndexOf("AllocationKind");
        if (type < 0 || size < 0)
            return;
        string name = (e.Payload[type] as string ?? "?") + (kind >= 0 && Convert.ToInt32(e.Payload[kind]) == 1 ? " (large)" : "");
        _types.AddOrUpdate(name, Convert.ToInt64(e.Payload[size]), (_, n) => n + Convert.ToInt64(e.Payload[size]));
    }

    public void Report(double seconds)
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnThrow;
        foreach (var t in Threads().Select(t => (Name: t.Key[(t.Key.IndexOf(' ') + 1)..], Ticks: t.Value - _threads.GetValueOrDefault(t.Key)))
                     .GroupBy(t => t.Name).Select(g => (Name: g.Key, Ticks: g.Sum(t => t.Ticks))).Where(t => t.Ticks > 0).OrderByDescending(t => t.Ticks).Take(8))
            Console.WriteLine($"  {t.Ticks / seconds,6:0.0} % of a core  thread {t.Name}");
        foreach (var t in _types.OrderByDescending(t => t.Value).Take(14))
            Console.WriteLine($"  {t.Value / 1e6 / seconds,6:0.00} MB/s  {t.Key}");
        foreach (var t in _throws.OrderByDescending(t => t.Value).Take(12))
            Console.WriteLine($"  {t.Value / seconds,6:0.0} throws/s  {t.Key}");
    }
}
