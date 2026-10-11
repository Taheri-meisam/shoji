using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Shared bits for tiles that show data from an online service.</summary>
public abstract class OnlineModule : Module
{
    protected readonly TextBlock Status;

    protected OnlineModule(ModuleConfig config, string title) : base(config, title)
    {
        Status = W.Text("", Theme.Small, Theme.Dim);
        Status.TextWrapping = TextWrapping.Wrap;
    }

    /// <summary>Text shown when the tile still needs settings.</summary>
    protected static TextBlock SetupHint(string what) =>
        new()
        {
            Text = L.F("Set {0} in Settings → Pages → this tile.", what),
            Foreground = Theme.Dim, FontSize = Theme.Small, FontFamily = Theme.Font, TextWrapping = TextWrapping.Wrap,
        };

    protected static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? L.T("just now") : d.TotalHours < 1 ? L.F("{0}m ago", (int)d.TotalMinutes) : d.TotalDays < 1 ? L.F("{0}h ago", (int)d.TotalHours) : L.F("{0}d ago", (int)d.TotalDays);
    }

    protected static BitmapImage? Image(byte[]? bytes, int width)
    {
        if (bytes == null)
            return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = width;
            img.StreamSource = new System.IO.MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Home Assistant entities: tap lights / switches to toggle, cameras show a snapshot.</summary>
public sealed class HomeAssistantModule : OnlineModule
{
    readonly HomeAssistantService? _ha;
    readonly StackPanel _rows = new();
    readonly Dictionary<string, (Border Box, TextBlock Name, TextBlock State, Image? Picture)> _views = new();

    public HomeAssistantModule(ModuleConfig config, Collector collector) : base(config, "Home")
    {
        string? url = config.Opt("Url"), token = config.Opt("Token");
        var entities = HomeAssistantService.ParseEntities(config.OptList("Entities"));
        if (url == null || token == null || entities.Count == 0)
        {
            View = MakeTile(SetupHint(L.T("the server, token and entities")));
            return;
        }
        // Token and labels are part of the key, so a corrected token or a renamed entity takes effect at once.
        _ha = collector.Service($"ha:{url}:{token.GetHashCode()}:{string.Join(",", entities.Select(e => $"{e.Id}={e.Label}"))}",
            () => new HomeAssistantService(url, token, entities));
        View = MakeTile(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, Status);
    }

    public override void Update(Snapshot s)
    {
        if (_ha == null)
            return;
        Status.Text = _ha.Error ?? "";
        Status.Foreground = Theme.Warn;
        var state = _ha.State;
        if (state == null)
            return;

        foreach (var e in state)
        {
            if (!_views.TryGetValue(e.Id, out var v))
                _views[e.Id] = v = MakeRow(e);
            v.Name.Text = e.Label;
            bool on = e.State is "on" or "open" or "unlocked" or "playing" or "home";
            v.State.Text = e.Domain == "camera" ? "" : e.Unit != null ? $"{e.State} {e.Unit}" : e.State;
            v.State.Foreground = on ? Theme.Background : e.State is "unavailable" or "not found" || e.State == L.T("not found") ? Theme.Warn : Theme.Text;
            v.Box.Background = on ? Theme.Accent : Theme.Track;
            v.Name.Foreground = on ? Theme.Background : Theme.Text;
            // Only decode a camera snapshot when a new one arrived (every ~10 s), not every refresh.
            if (v.Picture != null && e.Image != null && !ReferenceEquals(v.Picture.Tag, e.Image))
            {
                v.Picture.Tag = e.Image;
                v.Picture.Source = Image(e.Image, 480);
            }
        }
    }

    (Border, TextBlock, TextBlock, Image?) MakeRow(HaEntity e)
    {
        var name = W.Text(e.Label, Theme.Small + 1);
        var state = W.Text("", Theme.Small + 1);
        state.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new DockPanel();
        DockPanel.SetDock(state, Dock.Right);
        grid.Children.Add(state);
        grid.Children.Add(name);

        Image? picture = null;
        UIElement content = grid;
        if (e.Domain == "camera")
        {
            picture = new Image { Stretch = Stretch.Uniform, MaxHeight = 220, Margin = new Thickness(0, 4, 0, 0) };
            content = W.Stack(grid, picture);
        }
        var square = Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        var box = new Border
        {
            Child = content, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 5),
            CornerRadius = new CornerRadius(square ? 0 : 8), Background = Theme.Track,
        };
        if (HomeAssistantService.IsToggleable(e.Domain))
            Tap.On(box, () => _ = _ha!.ToggleAsync(e.Id));
        _rows.Children.Add(box);
        return (box, name, state, picture);
    }
}

/// <summary>Upcoming events from iCal feeds.</summary>
public sealed class AgendaModule : OnlineModule
{
    readonly AgendaService? _agenda;
    readonly int _days;
    readonly StackPanel _list = new();
    string _shown = "";

    public AgendaModule(ModuleConfig config, AppConfig app) : base(config, "Agenda")
    {
        // The tile's own links, else the calendars from Settings → Weather & time (shared with Clock and Calendar).
        _agenda = CalendarFeeds.Service(CalendarFeeds.For(config, app));
        if (_agenda == null)
        {
            View = MakeTile(new TextBlock
            {
                Text = L.T("Add your calendar's iCal link in Settings → Weather & time → Calendars (Google Calendar: Settings → your calendar → Secret address in iCal format)."),
                Foreground = Theme.Dim, FontSize = Theme.Small, FontFamily = Theme.Font, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        _days = Math.Clamp(config.OptInt("Days", 3), 1, 31);
        View = MakeTile(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, Status);
    }

    /// <summary>A calendar's colour: its own from Settings, else one of the theme's by its place in the list.</summary>
    public static Brush CalendarBrush(AgendaService? agenda, int calendar)
    {
        if (agenda != null && calendar < agenda.Feeds.Count && agenda.Feeds[calendar].Color is { Length: > 0 } hex)
            return Theme.Freeze(new SolidColorBrush(Theme.Parse(hex, "#888888")));
        return (calendar % 4) switch { 0 => Theme.Accent, 1 => Theme.Accent2, 2 => Theme.Good, _ => Theme.Warn };
    }

    public override void Update(Snapshot s)
    {
        if (_agenda == null)
            return;
        Status.Text = _agenda.Error ?? "";
        Status.Foreground = Theme.Warn;
        var all = _agenda.State;
        if (all == null)
            return;
        // The shared service holds the whole month; this tile shows what's still ahead within its days. Something
        // that began earlier but is still on (a conference, a holiday week) is listed under today.
        var now = DateTime.Now;
        var events = all.Where(e => e.Until > now && e.Start < DateTime.Today.AddDays(_days)).ToList();
        bool colours = _agenda.Feeds.Count > 1;

        // Rebuild only when something changed (or the minute did, for "in 5 min").
        string key = s.Time.ToString("HHmm") + string.Join("|", events.Select(e => e.Start.Ticks + e.Title));
        if (key == _shown)
            return;
        _shown = key;
        _list.Children.Clear();

        if (events.Count == 0)
            _list.Children.Add(W.Text(L.T("Nothing coming up."), Theme.Small, Theme.Dim));

        foreach (var day in events.GroupBy(e => e.Start.Date < DateTime.Today ? DateTime.Today : e.Start.Date))
        {
            string heading = day.Key == DateTime.Today ? L.T("Today") : day.Key == DateTime.Today.AddDays(1) ? L.T("Tomorrow") : day.Key.ToString(L.T("dddd d MMM"), L.Culture);
            var h = W.Text(heading, Theme.Small, Theme.Accent, FontWeights.SemiBold);
            h.Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 6, 0, 2);
            _list.Children.Add(h);
            foreach (var e in day)
            {
                string time = e.AllDay ? L.T("all day") : e.Start < DateTime.Today ? "→ " + e.Until.ToString(Theme.Current.Japanese ? "H:mm" : "t") : e.Start.ToString(Theme.Current.Japanese ? "H:mm" : "t");
                var row = W.Row("", out var title, out var when);
                when.Text = time;
                when.Width = 64;
                Grid.SetColumn(when, 0);
                Grid.SetColumn(title, 1);
                row.ColumnDefinitions[0].Width = GridLength.Auto;
                row.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                title.HorizontalAlignment = HorizontalAlignment.Left;
                title.Text = e.Title;
                title.ToolTip = e.Location;
                if (colours)
                    when.Foreground = CalendarBrush(_agenda, e.Calendar); // which calendar it's from
                // The next timed event today stands out, with a countdown when it's close.
                if (!e.AllDay && e.Start.Date == DateTime.Today && e.Start > DateTime.Now && e.Start - DateTime.Now < TimeSpan.FromHours(1))
                {
                    title.Foreground = Theme.Warn;
                    when.Text = L.F("in {0}m", (int)(e.Start - DateTime.Now).TotalMinutes + 1);
                    when.Foreground = Theme.Warn;
                }
                _list.Children.Add(row);
            }
        }
    }
}

/// <summary>CI status and open pull requests per repository.</summary>
public sealed class GitHubModule : OnlineModule
{
    readonly GitHubService? _gh;
    readonly RowList _list = new();

    public GitHubModule(ModuleConfig config, Collector collector) : base(config, "GitHub")
    {
        var repos = config.OptList("Repos");
        if (repos.Count == 0)
        {
            View = MakeTile(SetupHint(L.T("one or more repositories (owner/repo)")));
            return;
        }
        string? token = config.Opt("Token");
        _gh = collector.Service($"github:{string.Join(",", repos)}:{token?.GetHashCode()}", () => new GitHubService(repos, token));
        View = MakeTile(_list, Status);
    }

    public override void Update(Snapshot s)
    {
        if (_gh == null)
            return;
        Status.Text = _gh.Error ?? "";
        var repos = _gh.State;
        if (repos == null)
            return;

        for (int i = 0; i < repos.Count; i++)
        {
            var r = repos[i];
            string name = r.Repo.Contains('/') ? r.Repo[(r.Repo.IndexOf('/') + 1)..] : r.Repo;
            if (r.Error != null)
            {
                _list.Set(i, name, r.Error, valueColor: Theme.Warn);
                continue;
            }
            var (mark, color) = (r.Status, r.Conclusion) switch
            {
                ("completed", "success") => ("✓", Theme.Good),
                ("completed", "failure" or "timed_out" or "startup_failure") => ("✗", Theme.Crit),
                ("completed", _) => ("–", Theme.Dim),
                (null, _) => ("", Theme.Dim),
                _ => ("●", Theme.Warn), // queued / in progress
            };
            string prs = r.OpenPulls == 0 ? "" : $"  ·  {r.OpenPulls} PR{(r.OpenPulls == 1 ? "" : "s")}";
            string when = r.When is DateTime t ? "  " + Ago(t) : "";
            _list.Set(i, $"{name}  {r.Branch}", $"{mark}{when}{prs}", valueColor: color);
        }
        _list.Trim(repos.Count);
    }
}

/// <summary>Live Twitch chat (read-only).</summary>
public sealed class TwitchModule : OnlineModule
{
    readonly TwitchChat? _chat;
    readonly StackPanel _lines = new() { VerticalAlignment = VerticalAlignment.Bottom };
    ChatLine? _lastShown;

    public TwitchModule(ModuleConfig config, Collector collector) : base(config, "Twitch chat")
    {
        string? channel = config.Opt("Channel");
        if (channel == null)
        {
            View = MakeTile(SetupHint(L.T("a channel name")));
            return;
        }
        _chat = collector.Service("twitch:" + channel.ToLowerInvariant(), () => new TwitchChat(channel));
        var clip = new Border { ClipToBounds = true, Child = _lines };
        View = MakeTile(clip, Status);
    }

    public override void Update(Snapshot s)
    {
        if (_chat == null)
            return;
        Status.Text = _chat.Status;
        var lines = _chat.Lines;
        if (lines.Count == 0 || ReferenceEquals(lines[^1], _lastShown))
            return;
        _lastShown = lines[^1];

        // Newest at the bottom; older lines fall off the top of the tile.
        _lines.Children.Clear();
        foreach (var l in lines.TakeLast(25))
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Theme.Font, FontSize = Theme.Small + 0.5, Margin = new Thickness(0, 1, 0, 1) };
            tb.Inlines.Add(new System.Windows.Documents.Run(l.User + ": ")
            {
                Foreground = l.Color != null ? new SolidColorBrush(Theme.Parse(l.Color, "#9146FF")) : Theme.Accent,
                FontWeight = FontWeights.SemiBold,
            });
            tb.Inlines.Add(new System.Windows.Documents.Run(l.Text) { Foreground = Theme.Text });
            _lines.Children.Add(tb);
        }
    }
}

/// <summary>Stock and crypto prices.</summary>
public sealed class TickerModule : OnlineModule
{
    readonly TickerService? _ticker;
    readonly StackPanel _rows = new();
    IReadOnlyList<Quote>? _shown;

    public TickerModule(ModuleConfig config, Collector collector) : base(config, "Markets")
    {
        var stocks = config.OptList("Stocks");
        var coins = config.OptList("Coins");
        if (stocks.Count + coins.Count == 0)
        {
            View = MakeTile(SetupHint(L.T("stock symbols and/or coins")));
            return;
        }
        string? currency = config.Opt("Currency");
        _ticker = collector.Service($"ticker:{string.Join(",", stocks)}:{string.Join(",", coins)}:{currency}",
            () => new TickerService(stocks, coins, currency));
        View = MakeTile(_rows, Status);
    }

    public override void Update(Snapshot s)
    {
        if (_ticker == null)
            return;
        Status.Text = _ticker.Error ?? "";
        var quotes = _ticker.State;
        if (quotes == null || ReferenceEquals(quotes, _shown))
            return;
        _shown = quotes;
        _rows.Children.Clear();

        foreach (var q in quotes)
        {
            var color = q.ChangePercent >= 0 ? Theme.Good : Theme.Crit;
            var name = W.Text(q.Symbol.Length > 0 ? char.ToUpper(q.Symbol[0]) + q.Symbol[1..] : q.Symbol, Theme.Small + 1, weight: FontWeights.SemiBold);
            var price = W.Text(q.Price.ToString(q.Price >= 1000 ? "N0" : q.Price >= 1 ? "N2" : "0.####") + " " + q.Currency, Theme.Small + 1);
            var change = W.Text(q.ChangePercent is double c ? $"{(c >= 0 ? "▲" : "▼")} {Math.Abs(c):0.00}%" : "", Theme.Small, color);
            price.HorizontalAlignment = change.HorizontalAlignment = HorizontalAlignment.Right;

            var row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var spark = new MiniChart(q.Spark, color) { Height = 22, Margin = new Thickness(8, 0, 8, 0) };
            Grid.SetColumn(spark, 1);
            var right = W.Stack(price, change);
            Grid.SetColumn(right, 2);
            row.Children.Add(name);
            row.Children.Add(spark);
            row.Children.Add(right);
            _rows.Children.Add(row);
        }
    }

    /// <summary>Static little line chart (intraday prices).</summary>
    sealed class MiniChart : FrameworkElement
    {
        readonly double[] _data;
        readonly Brush _color;

        public MiniChart(double[] data, Brush color)
        {
            _data = data;
            _color = color;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_data.Length < 2)
                return;
            Graph.Draw(dc, _data, _data.Length, _color, null, _data.Max(), ActualWidth, ActualHeight, _data.Min());
        }
    }
}

/// <summary>Synced lyrics for what's playing, karaoke style: previous / current / next lines.</summary>
public sealed class LyricsModule : Module
{
    readonly LyricsService _lyrics;
    readonly TextBlock _prev, _current, _next, _song;
    readonly ScrollViewer _plainScroll;
    readonly TextBlock _plain;
    readonly Grid _synced;

    public LyricsModule(ModuleConfig config, Collector collector) : base(config, "Lyrics")
    {
        _lyrics = collector.Service("lyrics", () => new LyricsService());
        _song = W.Text("", Theme.Small, Theme.Dim);

        _prev = Line(Theme.Medium, Theme.Dim);
        _current = Line(Theme.Medium + 10, Theme.Accent);
        _current.FontFamily = Theme.Display;
        _current.Effect = Theme.NumberEffect;
        _next = Line(Theme.Medium, Theme.Dim);

        _synced = new Grid();
        _synced.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _synced.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _synced.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _prev.VerticalAlignment = VerticalAlignment.Bottom;
        _next.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(_current, 1);
        Grid.SetRow(_next, 2);
        _synced.Children.Add(_prev);
        _synced.Children.Add(_current);
        _synced.Children.Add(_next);

        _plain = W.Text("", Theme.Medium);
        _plain.TextWrapping = TextWrapping.Wrap;
        _plain.TextTrimming = TextTrimming.None;
        _plainScroll = new ScrollViewer { Content = _plain, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Visibility = Visibility.Collapsed };

        var body = new Grid();
        body.Children.Add(_synced);
        body.Children.Add(_plainScroll);
        View = MakeTile(body, _song);
    }

    static TextBlock Line(double size, Brush color)
    {
        var t = W.Text("", size, color);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.TextAlignment = TextAlignment.Center;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 4, 0, 4);
        return t;
    }

    public override void Update(Snapshot s)
    {
        var m = s.Media;
        if (m == null || string.IsNullOrEmpty(m.Title))
        {
            Show("", L.T("Play some music"), "", null);
            _song.Text = "";
            return;
        }
        _song.Text = $"{m.Artist} – {m.Title}";

        var lyrics = _lyrics.Get(m.Artist, m.Title, m.Duration, out bool loading);
        if (lyrics == null)
        {
            Show("", loading ? L.T("Looking for lyrics…") : L.T("No lyrics found"), "", null);
            return;
        }
        if (lyrics.Synced.Count == 0)
        {
            Show("", "", "", lyrics.Plain);
            return;
        }

        var pos = m.Position ?? TimeSpan.Zero;
        int i = -1;
        for (int k = 0; k < lyrics.Synced.Count && lyrics.Synced[k].Time <= pos; k++)
            i = k;
        string Text(int k) => k >= 0 && k < lyrics.Synced.Count ? (lyrics.Synced[k].Text.Length == 0 ? "♪" : lyrics.Synced[k].Text) : "";
        Show(Text(i - 1), i < 0 ? "♪" : Text(i), Text(i + 1), null);
    }

    void Show(string prev, string current, string next, string? plain)
    {
        _synced.Visibility = plain == null ? Visibility.Visible : Visibility.Collapsed;
        _plainScroll.Visibility = plain == null ? Visibility.Collapsed : Visibility.Visible;
        if (plain != null)
        {
            if (_plain.Text != plain)
                _plain.Text = plain;
            return;
        }
        _prev.Text = prev;
        _current.Text = current;
        _next.Text = next;
    }
}
