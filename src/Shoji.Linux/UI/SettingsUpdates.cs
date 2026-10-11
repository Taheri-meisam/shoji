using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>About & install → Updates: the feed, "Check now", and the result with Install / Skip (Core/UpdateChecker.cs).</summary>
public sealed partial class SettingsWindow
{
    /// <summary>Where the check's result goes; replaced on every rebuild, painted again on every change of the checker.</summary>
    StackPanel? _updateStatus;
    /// <summary>The window showing the Updates card (not always <c>_instance</c>: screenshots build their own).</summary>
    static readonly WeakReference<SettingsWindow?> UpdatesWindow = new(null);
    static bool _updatesHooked;

    /// <summary>Opens Settings on About & install (the tray's "Install update…").</summary>
    public static void OpenAbout(Dashboard dashboard)
    {
        Open(dashboard);
        _instance?.Go("about");
    }

    void AddUpdates(AppConfig config)
    {
        if (!_updatesHooked)
        {
            _updatesHooked = true;
            UpdateChecker.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (UpdatesWindow.TryGetTarget(out var w))
                    w?.PaintUpdates();
            });
        }
        UpdatesWindow.SetTarget(this);
        var u = config.Updates;
        bool hasFeed = !string.IsNullOrWhiteSpace(u.Feed);
        Section("Updates", hasFeed ? null
            : "No update source is set yet, so nothing is checked. Type the GitHub repository Shoji comes from (owner/repo), or the https link of a latest.json manifest.");
        var card = Card();
        Setting(card, "Check for updates", "Every 12 hours, and it tells you; nothing is installed until you tap Install",
            Switch(u.Check, v => Change(c => c.Updates.Check = v)), icon: "system_update");
        var feed = TextField(u.Feed, 280, v => Change(c => c.Updates.Feed = v, rebuild: true), "owner/repo on GitHub, or a manifest URL",
            v => v == null || UpdateChecker.ValidFeed(v) ? (true, v) : (false, null), "Type owner/repo (GitHub) or an https:// link: this wasn't saved");
        feed.Watermark = L.T("owner/repo on GitHub, or a manifest URL");
        Setting(card, "Update source", "owner/repo on GitHub, or a manifest URL", feed, icon: "link");
        var check = Button("Check now", () => _ = UpdateChecker.CheckAsync());
        check.IsEnabled = hasFeed;
        Setting(card, L.F("Version {0}", AppPaths.VersionText), SelfUpdate.Describe + (UpdateState.Load().LastChecked is { } t ? L.F(" · last checked {0}", When(t)) : ""), check, last: true, icon: "info");
        _updateStatus = new StackPanel();
        ((StackPanel)card.Child!).Children.Add(_updateStatus);
        PaintUpdates();

        // The dashboard announced an update that this run hasn't looked up yet (it was restarted since): fetch it now.
        if (hasFeed && UpdateChecker.Stage == UpdateStage.Idle && UpdateState.Load().Notified is { } notified && UpdateChecker.IsNewer(notified, AppPaths.VersionText))
            _ = UpdateChecker.CheckAsync(manual: false);
    }

    void PaintUpdates()
    {
        var box = _updateStatus;
        if (box == null)
            return;
        box.Children.Clear();
        var latest = UpdateChecker.Latest;
        switch (UpdateChecker.Stage)
        {
            case UpdateStage.Idle:
                return;
            case UpdateStage.Checking:
                Status(box, "sync", Look.Text2, "Checking for updates…");
                break;
            case UpdateStage.UpToDate:
                Status(box, "check_circle", Look.Good, L.F("You're up to date: {0} is the newest version.", AppPaths.VersionText));
                break;
            case UpdateStage.Failed:
                Status(box, "error", Look.Warn, UpdateChecker.Message ?? "The update failed (see log.txt).");
                if (latest != null)
                    Offer(box, latest); // e.g. a download that broke off: Install tries again
                break;
            case UpdateStage.Available:
                Offer(box, latest!);
                break;
            case UpdateStage.Downloading:
                double p = UpdateChecker.Progress;
                Status(box, "download", Look.Text2, L.F("Downloading Shoji {0}…", latest?.Version) + (p >= 0 ? $" {p * 100:0} %" : ""));
                box.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Max(0, p), IsIndeterminate = p < 0, Height = 6, MinHeight = 6, Margin = new Thickness(38, 0, 0, 14) });
                break;
            case UpdateStage.Installing:
                Status(box, "system_update", Look.Text2, L.F("Installing Shoji {0}; it restarts in a moment…", latest?.Version));
                break;
        }
        box.Children.Insert(0, new Border { Height = 1, Background = Look.Divider });
    }

    void Offer(StackPanel box, UpdateInfo latest)
    {
        var body = new StackPanel { Margin = new Thickness(38, 12, 0, 12) };
        body.Children.Add(new TextBlock { Text = L.F("Shoji {0} is available", latest.Version), FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text1 });
        if (UpdateChecker.Excerpt(latest.Notes) is { } notes)
            body.Children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), Foreground = Look.Text2 });
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        string? page = latest.Page ?? latest.LinuxDeb?.Url;
        if (SelfUpdate.Blocker is { } why)
        {
            body.Children.Add(new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 8, 0, 0), Foreground = Look.Warn });
            if (page != null)
                row.Children.Add(AccentButton("Open download page", () => App.Open(page)));
        }
        else
        {
            row.Children.Add(AccentButton("Install", () => _ = UpdateChecker.InstallAsync()));
            if (!string.Equals(_config.Updates.Skipped, latest.Version, StringComparison.OrdinalIgnoreCase))
                row.Children.Add(Button("Skip this version", () => UpdateChecker.Skip(latest.Version)));
            if (latest.Page != null)
                row.Children.Add(Button("Release notes", () => App.Open(latest.Page)));
        }
        body.Children.Add(row);
        box.Children.Add(body);
    }

    static void Status(StackPanel box, string icon, IBrush color, string text)
    {
        var glyph = new TextBlock { Text = icon, FontFamily = UI.Theme.Icons, FontSize = 20, Width = 24, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = color };
        var row = new DockPanel { Margin = new Thickness(0, 12) };
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = L.T(text), TextWrapping = TextWrapping.Wrap, Foreground = Look.Text1, VerticalAlignment = VerticalAlignment.Center });
        box.Children.Add(row);
    }

    static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? L.F("today at {0:t}", local) : local.ToString("d MMM, t", L.Culture);
    }
}
