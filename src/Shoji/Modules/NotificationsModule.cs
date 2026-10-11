using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Windows notifications (Discord, WhatsApp, Outlook…), newest first; tap one to dismiss it, "Clear" empties the tile.</summary>
public sealed class NotificationsModule : Module
{
    readonly WindowsNotifications _reader;
    readonly NotificationFilter _filter;
    readonly StackPanel _rows = new();
    readonly TextBlock _hint;
    readonly Border _clear;
    readonly Dictionary<string, ImageSource?> _icons = new();
    int _version = -1;
    long _newest = -1;      // highest id shown so far; -1 until the first build, so nothing flashes on start
    long _minute;

    public NotificationsModule(ModuleConfig config, Collector collector) : base(config, "Notifications")
    {
        NotificationHub.DemoIfRequested();
        _reader = collector.Service("notifications", () => new WindowsNotifications());
        _filter = new NotificationFilter(config);

        _hint = W.Text("", Theme.Small, Theme.Warn);
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 0, 0, 6);
        _hint.Visibility = Visibility.Collapsed;

        var label = W.Text(L.T("Clear"), Theme.Small, Theme.Dim);
        _clear = Tap.On(new Border
        {
            Child = label, Padding = new Thickness(8, 1, 8, 1), Background = Theme.Track,
            CornerRadius = new CornerRadius(Square ? 0 : 9), HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed,
        }, () =>
        {
            NotificationHub.Clear();
            Rebuild();
        });

        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        View = MakeTile(Body(scroll, _hint), _clear);
    }

    static bool Square => Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;

    public override void Update(Snapshot s)
    {
        // The database is read only while the tile can be seen (its page is showing and the screen is on).
        if (View.IsVisible && !Motion.Paused)
            _reader.Want();
        // Rebuild when something arrived or was dismissed, and once a minute for "2 min ago" and "Keep for".
        long minute = DateTime.Now.Ticks / TimeSpan.TicksPerMinute;
        if (NotificationHub.Version == _version && minute == _minute)
            return;
        Rebuild();
    }

    void Rebuild()
    {
        _version = NotificationHub.Version;
        _minute = DateTime.Now.Ticks / TimeSpan.TicksPerMinute;
        var now = DateTime.Now;
        var items = NotificationHub.Items.Where(i => _filter.Allows(i, now)).ToList();

        string? hint = NotificationHub.Hint;
        _hint.Text = hint ?? "";
        _hint.Visibility = hint != null ? Visibility.Visible : Visibility.Collapsed;
        _clear.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _rows.Children.Clear();
        if (items.Count == 0)
        {
            var empty = W.Text(L.T("No notifications"), Theme.Small + 1, Theme.Dim);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 18, 0, 0);
            _rows.Children.Add(empty);
        }
        foreach (var item in items)
            _rows.Children.Add(Row(item, now, flash: _newest >= 0 && item.Id > _newest));
        _newest = Math.Max(_newest, items.Count > 0 ? items.Max(i => i.Id) : 0);

        // Icons of notifications that are gone needn't stay decoded.
        foreach (var path in _icons.Keys.Where(p => items.All(i => i.IconPath != p)).ToList())
            _icons.Remove(path);
    }

    UIElement Row(NotificationItem item, DateTime now, bool flash)
    {
        var app = W.Text(item.App, Theme.Small, Theme.Accent, FontWeights.SemiBold);
        var ago = W.Text(NotificationText.Ago(item.Time, now), Theme.Small - 1, Theme.Dim);
        ago.Margin = new Thickness(8, 0, 0, 0);
        var top = new DockPanel();
        DockPanel.SetDock(ago, Dock.Right);
        top.Children.Add(ago);
        top.Children.Add(app);

        var text = new StackPanel();
        text.Children.Add(top);
        text.Children.Add(W.Text(item.Title, Theme.Small + 1, Theme.Text, FontWeights.SemiBold));
        if (!_filter.HideText && item.Body.Length > 0)
        {
            var body = W.Text(item.Body, Theme.Small, Theme.Dim);
            body.TextWrapping = TextWrapping.Wrap;
            // Two lines at most (WPF has no MaxLines): a fixed line height and a two-line cap end the second line in "…".
            body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            body.LineHeight = Math.Round(Theme.Small * 1.3);
            body.MaxHeight = body.LineHeight * 2;
            text.Children.Add(body);
        }

        var line = new DockPanel();
        var icon = Icon(item);
        icon.Margin = new Thickness(0, 2, 9, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        line.Children.Add(text);

        var radius = new CornerRadius(Square ? 0 : 8);
        var box = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        box.Children.Add(new Border { Background = Theme.Track, CornerRadius = radius });
        if (flash && !Motion.Paused)
            box.Children.Add(Flash(radius));
        box.Children.Add(new Border { Child = line, Padding = new Thickness(9, 6, 9, 7), Background = Brushes.Transparent });
        Tap.On(box, () =>
        {
            NotificationHub.Dismiss(item.Id);
            Rebuild();
        });
        return box;
    }

    /// <summary>An accent wash that fades out once.</summary>
    static Border Flash(CornerRadius radius)
    {
        var b = new Border { Background = Theme.Accent, CornerRadius = radius, Opacity = 0, IsHitTestVisible = false };
        var fade = new DoubleAnimation(0.35, 0, TimeSpan.FromMilliseconds(1600)) { FillBehavior = FillBehavior.Stop };
        b.BeginAnimation(UIElement.OpacityProperty, fade);
        return b;
    }

    FrameworkElement Icon(NotificationItem item)
    {
        const double size = 22;
        if (item.IconPath is { } path)
        {
            if (!_icons.TryGetValue(path, out var image))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // reads the file now, doesn't keep it open
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.DecodePixelWidth = 44;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    image = bmp;
                }
                catch (Exception)
                {
                    image = null; // unreadable or not an image
                }
                _icons[path] = image;
            }
            if (image != null)
                return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
        }
        // No icon: the app's initial in a small badge.
        var letter = W.Text(item.App.Length > 0 ? char.ToUpperInvariant(item.App[0]).ToString() : "•", Theme.Small, Theme.Background, FontWeights.Bold);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(Square ? 0 : 6), Background = Theme.Accent, Child = letter,
        };
    }
}
