using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Desktop notifications (Discord, mail, chats…), newest first; tap one to dismiss it, "Clear" empties the tile.</summary>
public sealed class NotificationsModule : Module
{
    readonly NotificationFilter _filter;
    readonly StackPanel _rows = new();
    readonly TextBlock _hint;
    readonly Border _clear;
    readonly Dictionary<string, Bitmap?> _icons = new();
    int _version = -1;
    long _newest = -1;      // highest id shown so far; -1 until the first build, so nothing flashes on start
    long _minute;

    public NotificationsModule(ModuleConfig config, LinuxCollector collector) : base(config, "Notifications")
    {
        NotificationHub.DemoIfRequested();
        collector.Service("notifications", () => new LinuxNotifications());
        _filter = new NotificationFilter(config);

        _hint = W.Text("", UI.Theme.Small, UI.Theme.Warn);
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 0, 0, 6);
        _hint.IsVisible = false;

        var label = W.Text(L.T("Clear"), UI.Theme.Small, UI.Theme.Dim);
        _clear = Tap.On(new Border
        {
            Child = label, Padding = new Thickness(8, 1, 8, 1), Background = UI.Theme.Track,
            CornerRadius = new CornerRadius(Square ? 0 : 9), HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false,
        }, () =>
        {
            NotificationHub.Clear();
            Rebuild();
        });

        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        View = MakeTile(Body(scroll, _hint), _clear);
    }

    static bool Square => UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;

    public override void Update(Snapshot s)
    {
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
        _hint.Text = hint == null ? "" : L.T(hint);
        _hint.IsVisible = hint != null;
        _clear.IsVisible = items.Count > 0;

        _rows.Children.Clear();
        if (items.Count == 0)
        {
            var empty = W.Text(L.T("No notifications"), UI.Theme.Small + 1, UI.Theme.Dim);
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

    Control Row(NotificationItem item, DateTime now, bool flash)
    {
        var app = W.Text(item.App, UI.Theme.Small, UI.Theme.Accent, FontWeight.SemiBold);
        var ago = W.Text(NotificationText.Ago(item.Time, now), UI.Theme.Small - 1, UI.Theme.Dim);
        ago.Margin = new Thickness(8, 0, 0, 0);
        var top = new DockPanel();
        DockPanel.SetDock(ago, Avalonia.Controls.Dock.Right);
        top.Children.Add(ago);
        top.Children.Add(app);

        var text = new StackPanel();
        text.Children.Add(top);
        text.Children.Add(W.Text(item.Title, UI.Theme.Small + 1, UI.Theme.Text, FontWeight.SemiBold));
        if (!_filter.HideText && item.Body.Length > 0)
        {
            var body = W.Text(item.Body, UI.Theme.Small, UI.Theme.Dim);
            body.TextWrapping = TextWrapping.Wrap;
            body.MaxLines = 2;
            text.Children.Add(body);
        }

        var line = new DockPanel();
        var icon = Icon(item);
        icon.Margin = new Thickness(0, 2, 9, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
        line.Children.Add(icon);
        line.Children.Add(text);

        var radius = new CornerRadius(Square ? 0 : 8);
        var box = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        box.Children.Add(new Border { Background = UI.Theme.Track, CornerRadius = radius });
        if (flash && !UI.FrameClock.Paused)
            box.Children.Add(Flash(radius));
        box.Children.Add(new Border { Child = line, Padding = new Thickness(9, 6, 9, 7), Background = Brushes.Transparent });
        Tap.On(box, () =>
        {
            NotificationHub.Dismiss(item.Id);
            Rebuild();
        });
        return box;
    }

    /// <summary>An accent wash that fades out once (a one-off transition, not a running animation).</summary>
    static Border Flash(CornerRadius radius)
    {
        var b = new Border { Background = UI.Theme.Accent, CornerRadius = radius, Opacity = 0.35, IsHitTestVisible = false };
        b.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            b.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(1600) } };
            b.Opacity = 0;
        }, DispatcherPriority.Background);
        return b;
    }

    Control Icon(NotificationItem item)
    {
        const double size = 22;
        if (item.IconPath is { } path)
        {
            if (!_icons.TryGetValue(path, out var bmp))
            {
                try
                {
                    using var file = System.IO.File.OpenRead(path);
                    bmp = Bitmap.DecodeToWidth(file, 44);
                }
                catch (Exception)
                {
                    bmp = null; // unreadable or not an image
                }
                _icons[path] = bmp;
            }
            if (bmp != null)
                return new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.Uniform };
        }
        // No icon: the app's initial in a small badge.
        var letter = W.Text(item.App.Length > 0 ? char.ToUpperInvariant(item.App[0]).ToString() : "•", UI.Theme.Small, UI.Theme.Background, FontWeight.Bold);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(Square ? 0 : 6), Background = UI.Theme.Accent, Child = letter,
        };
    }
}
