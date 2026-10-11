using System;
using System.Windows;
using System.Windows.Controls;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>
/// A dashboard tile. To add a new one: subclass this, build the UI in the constructor,
/// refresh it in <see cref="Update"/>, register the type name in <see cref="ModuleFactory"/>
/// and describe it in <see cref="ModuleCatalog"/> so it shows up in Settings.
/// </summary>
/// <summary>Stands in for a tile whose settings made it fail, saying so.</summary>
public sealed class ErrorModule : Module
{
    public ErrorModule(ModuleConfig config, string error) : base(config, ModuleCatalog.Find(config.Type)?.Name ?? config.Type)
    {
        var text = W.Text(L.F("This tile couldn't start: {0}\nCheck its settings (Settings → Pages & tiles).", error), Theme.Small, Theme.Warn);
        text.TextWrapping = TextWrapping.Wrap;
        View = MakeTile(text);
    }

    public override void Update(Snapshot s) { }
}

public abstract class Module : IDisposable
{
    protected Module(ModuleConfig config, string defaultTitle)
    {
        Config = config;
        // The built-in title is translated; a title the user typed is kept as it is.
        Title = string.IsNullOrWhiteSpace(config.Title) ? L.T(defaultTitle) : config.Title!;
        Kanji = ModuleCatalog.Find(config.Type)?.Kanji;
    }

    public ModuleConfig Config { get; }
    public string Title { get; }

    /// <summary>Japanese label shown next to the title by themes that use one.</summary>
    public string? Kanji { get; }

    /// <summary>The tile placed on the dashboard.</summary>
    public FrameworkElement View { get; protected set; } = new Border();

    /// <summary>Called on the UI thread with fresh data, once per refresh (for tiles on hidden pages too).</summary>
    public abstract void Update(Snapshot s);

    /// <summary>Called when the dashboard is rebuilt and this tile is thrown away.</summary>
    public void Dispose()
    {
        (View as Tile)?.StopAnimations();
        OnDispose();
    }

    /// <summary>Stop timers / capture loops here.</summary>
    protected virtual void OnDispose() { }

    /// <summary>Wraps the body in a titled tile; the last child of the body stretches to fill.</summary>
    protected Tile MakeTile(UIElement body, UIElement? headerRight = null) => new(Title, Kanji, body, headerRight);

    /// <summary>A tile whose body is a gauge filling the space, with an optional one-line summary underneath.</summary>
    /// <param name="infoAlways">Keep the summary line when the tile is compact (e.g. the date under an LED clock).</param>
    protected Tile GaugeTile(UIElement gauge, TextBlock? info = null, UIElement? headerRight = null, bool infoAlways = false)
    {
        var d = new DockPanel { LastChildFill = true };
        if (info != null)
        {
            if (!infoAlways)
                info.ShowFrom(Density.Normal); // small tiles: just the gauge
            DockPanel.SetDock(info, Dock.Bottom);
            d.Children.Add(info);
        }
        d.Children.Add(gauge);
        return MakeTile(d, headerRight);
    }

    /// <summary>Small centred summary line under a gauge ("61° · 4.72 GHz · 48 W").</summary>
    protected static TextBlock InfoLine()
    {
        var t = W.Text("", Theme.Small, Theme.Dim);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }

    /// <summary>A DockPanel where the given elements stack at the top and <paramref name="fill"/> takes the rest.</summary>
    protected static DockPanel Body(UIElement fill, params UIElement[] top)
    {
        var d = new DockPanel { LastChildFill = true };
        foreach (var t in top)
        {
            DockPanel.SetDock(t, Dock.Top);
            d.Children.Add(t);
        }
        d.Children.Add(fill);
        return d;
    }
}

public static class ModuleFactory
{
    /// <summary>Like Create, but a tile that fails to start shows the problem instead of stopping the whole dashboard.</summary>
    public static Module? SafeCreate(ModuleConfig config, AppConfig app, Collector collector, IDashboardHost host)
    {
        try
        {
            return Create(config, app, collector, host);
        }
        catch (Exception ex)
        {
            Log.Write($"Tile \"{config.Type}\" failed to start: {ex}");
            return new ErrorModule(config, ex.Message);
        }
    }

    public static Module? Create(ModuleConfig config, AppConfig app, Collector collector, IDashboardHost host)
    {
        string type = config.Type.ToLowerInvariant();
        string style = Theme.StyleFor(type, config.Opt("Style"));
        switch (type)
        {
            case "clock": return style == "segments"
                ? new ClockSegmentsModule(config, app.Use24HourClock, CalendarFeeds.Shared(app, config))
                : new ClockModule(config, app.Use24HourClock, CalendarFeeds.Shared(app, config));
            case "cpu": return style == "classic" ? new CpuModule(config) : new CpuGaugeModule(config, style);
            case "gpu": return style == "classic" ? new GpuModule(config) : new GpuGaugeModule(config, style);
            case "memory": return style == "classic" ? new MemoryModule(config) : new MemoryGaugeModule(config, style);
            case "network": return style == "classic" ? new NetworkModule(config) : new NetworkScopeModule(config);
            case "disks": return new DisksModule(config);
            case "processes": return style == "classic" ? new ProcessesModule(config) : new ProcessRadarModule(config);
            case "activity": return new ActivityModule(config);
            case "media": return new MediaModule(config, collector.Media);
            case "sensors": return new SensorsModule(config);
            case "temps": return style == "classic"
                ? new ReadingsModule(config, "Temperatures", s => s.Temps, temperature: true)
                : new TempsGaugeModule(config, style);
            case "fans": return new ReadingsModule(config, "Fans", s => s.Fans, temperature: false);
            case "system": return new SystemModule(config);
            case "weather": return new WeatherModule(config);
            case "forecast": return new ForecastModule(config, app.Use24HourClock);
            case "ping": return style == "classic" ? new PingModule(config) : new PingGaugeModule(config, style);
            case "calendar": return new CalendarModule(config, CalendarFeeds.Shared(app));
            case "worldclock": return new WorldClockModule(config, app.Use24HourClock);
            case "battery": return style == "classic" ? new BatteryModule(config) : new BatteryGaugeModule(config, style);
            case "fps": return style == "classic" ? new FpsModule(config) : new FpsGaugeModule(config, style);
            case "recap": return new RecapModule(config);
            case "history": return new HistoryModule(config);
            case "deck": return new DeckModule(config, host, app.Obs);
            case "mixer": return new MixerModule(config);
            case "timer": return style == "classic" ? new TimerModule(config, collector) : new TimerGaugeModule(config, collector, style);
            case "lyrics": return new LyricsModule(config, collector);
            case "mirror": return new MirrorModule(config, app);
            case "homeassistant": return new HomeAssistantModule(config, collector);
            case "agenda": return new AgendaModule(config, app);
            case "github": return new GitHubModule(config, collector);
            case "twitch": return new TwitchModule(config, collector);
            case "notifications": return new NotificationsModule(config, collector);
            case "ticker": return new TickerModule(config, collector);
            case "webvalue": return new WebValueModule(config, style);
            case "status": return new StatusModule(config, app);
            case "websites": return new WebsitesModule(config);
            case "power": return style == "classic" ? new PowerModule(config) : new PowerGaugeModule(config, style);
            case "overview": return new OverviewModule(config, style);
            case "photos": return new PhotoModule(config);
            default:
                Log.Write($"Unknown module type '{config.Type}' in config – skipped.");
                return null;
        }
    }
}
