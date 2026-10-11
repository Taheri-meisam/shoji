using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    void AddUpdates(AppConfig config)
    {
        if (!_updatesHooked)
        {
            _updatesHooked = true;
            UpdateChecker.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (UpdatesWindow.TryGetTarget(out var w))
                    w?.PaintUpdates();
            });
        }
        UpdatesWindow.SetTarget(this);
        var u = config.Updates;
        bool hasFeed = !string.IsNullOrWhiteSpace(u.Feed);
        Section(L.T("Updates"), hasFeed ? null
            : L.T("No update source is set yet, so nothing is checked. Type the GitHub repository Shoji comes from (owner/repo), or the https link of a latest.json manifest."));
        var card = Card();
        Setting(card, L.T("Check for updates"), L.T("Every 12 hours, and it tells you; nothing is installed until you click Install"),
            Switch(u.Check, v => Change(c => c.Updates.Check = v)), icon: "");
        Setting(card, L.T("Update source"), L.T("owner/repo on GitHub, or a manifest URL"),
            TextField(u.Feed, 280, v => Change(c => c.Updates.Feed = v, rebuild: true), L.T("owner/repo on GitHub, or a manifest URL"),
                v => v == null || UpdateChecker.ValidFeed(v) ? (true, v) : (false, null), L.T("Type owner/repo (GitHub) or an https:// link: this wasn't saved")), icon: "");
        var check = Button(L.T("Check now"), () => _ = UpdateChecker.CheckAsync());
        check.IsEnabled = hasFeed;
        Setting(card, L.F("Version {0}", AppPaths.VersionText), SelfUpdate.Describe + (UpdateState.Load().LastChecked is { } t ? L.F(" · last checked {0}", When(t)) : ""), check, last: true, icon: "");
        _updateStatus = new StackPanel();
        ((StackPanel)card.Child).Children.Add(_updateStatus);
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
                UpdateLine(box, "", TextSecondary, L.T("Checking for updates…"));
                break;
            case UpdateStage.UpToDate:
                UpdateLine(box, "", SettingsPalette.Good, L.F("You're up to date: {0} is the newest version.", AppPaths.VersionText));
                break;
            case UpdateStage.Failed:
                UpdateLine(box, "", SettingsPalette.Warn, UpdateChecker.Message ?? L.T("The update failed (see log.txt)."));
                if (latest != null)
                    Offer(box, latest); // e.g. a download that broke off: Install tries again
                break;
            case UpdateStage.Available:
                Offer(box, latest!);
                break;
            case UpdateStage.Downloading:
                double p = UpdateChecker.Progress;
                UpdateLine(box, "", TextSecondary, L.F("Downloading Shoji {0}…", latest?.Version) + (p >= 0 ? $" {p * 100:0} %" : ""));
                box.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Max(0, p), IsIndeterminate = p < 0, Height = 6, Margin = new Thickness(36, 0, 0, 14) });
                break;
            case UpdateStage.Installing:
                UpdateLine(box, "", TextSecondary, L.F("Installing Shoji {0}; it restarts in a moment…", latest?.Version));
                break;
        }
        box.Children.Insert(0, Ref(new Border { Height = 1 }, Border.BackgroundProperty, SettingsPalette.Divider));
    }

    void Offer(StackPanel box, UpdateInfo latest)
    {
        var body = new StackPanel { Margin = new Thickness(36, 12, 0, 12) };
        body.Children.Add(new TextBlock { Text = L.F("Shoji {0} is available", latest.Version), FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (UpdateChecker.Excerpt(latest.Notes) is { } notes)
            body.Children.Add(Ref(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 4, 0, 0) }, TextBlock.ForegroundProperty, TextSecondary));
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (SelfUpdate.Blocker is { } why)
        {
            body.Children.Add(Ref(new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) }, TextBlock.ForegroundProperty, SettingsPalette.Warn));
            if (latest.Page != null)
                row.Children.Add(AccentButton(L.T("Open download page"), () => Shell(latest.Page, null)));
        }
        else
        {
            row.Children.Add(AccentButton(L.T("Install"), () => _ = UpdateChecker.InstallAsync()));
            if (!string.Equals(_config.Updates.Skipped, latest.Version, StringComparison.OrdinalIgnoreCase))
                row.Children.Add(Button(L.T("Skip this version"), () => UpdateChecker.Skip(latest.Version)));
            if (latest.Page != null)
                row.Children.Add(Button(L.T("Release notes"), () => Shell(latest.Page, null)));
        }
        body.Children.Add(row);
        box.Children.Add(body);
    }

    static void UpdateLine(StackPanel box, string glyph, string colorKey, string text)
    {
        var icon = Ref(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 17, Width = 22, Margin = new Thickness(0, 1, 14, 0), VerticalAlignment = VerticalAlignment.Top, TextAlignment = TextAlignment.Center }, TextBlock.ForegroundProperty, colorKey);
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 12) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        box.Children.Add(row);
    }

    static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? L.F("today at {0:t}", local) : local.ToString(L.T("d MMM, t"), L.Culture);
    }
}
