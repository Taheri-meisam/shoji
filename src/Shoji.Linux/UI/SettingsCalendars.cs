using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Weather & time → Calendars: the iCal links the Agenda, Clock and Calendar tiles share, each with a Test button.</summary>
public sealed partial class SettingsWindow
{
    void AddCalendars(AppConfig config)
    {
        Section("Calendars", "The Agenda tile, the Clock (\"Next: Standup in 12 min\") and the Calendar tile's dots use these. " +
                             "Google Calendar: Settings → your calendar → Secret address in iCal format. Outlook: Settings → Calendar → Shared calendars → Publish, ICS link. " +
                             "iCloud: share the calendar as a public calendar. A local .ics file works too (file:///…). Checked every 10 minutes.");
        var card = Card();
        var rows = (StackPanel)card.Child!;
        for (int i = 0; i < config.Calendars.Count; i++)
        {
            int index = i;
            var cal = config.Calendars[i];
            var head = new DockPanel { Margin = new Thickness(0, i == 0 ? 10 : 4, 0, 0) };
            var remove = IconButton("delete", "Remove this calendar", () => Change(c =>
            {
                if (index < c.Calendars.Count)
                    c.Calendars.RemoveAt(index);
            }, rebuild: true), danger: true);
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            head.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(cal.Name) ? L.F("Calendar {0}", i + 1) : cal.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text1 });
            rows.Children.Add(head);
            rows.Children.Add(Field("Name", cal.Name, "shown when a link has a problem", v => Change(c => { if (index < c.Calendars.Count) c.Calendars[index].Name = v; })));
            var link = (WrapPanel)Field("iCal link", cal.Url, "https://… or webcal://… ending in .ics", v => Change(c => { if (index < c.Calendars.Count) c.Calendars[index].Url = v ?? ""; }), secret: true);
            rows.Children.Add(link);
            rows.Children.Add(ColourField(cal.Color, v => Change(c => { if (index < c.Calendars.Count) c.Calendars[index].Color = v; }, rebuild: true)));

            var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 6), Foreground = Look.Text2 };
            var test = Button("Test", () => { });
            test.Click += async (_, _) =>
            {
                // What's in the box, saved or not yet.
                string url = (link.Children[1] as TextBox)?.Text ?? "";
                if (string.IsNullOrWhiteSpace(url))
                {
                    result.Text = L.T("Paste the link first.");
                    return;
                }
                test.IsEnabled = false;
                result.Foreground = Look.Text2;
                result.Text = L.T("Checking…");
                var (ok, text) = await Sensors.AgendaService.TestAsync(url);
                result.Text = text;
                result.Foreground = ok ? Look.Good : Look.Warn;
                test.IsEnabled = true;
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(120, 8, 0, 8) };
            line.Children.Add(test);
            line.Children.Add(result);
            rows.Children.Add(line);
            rows.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 0), Background = Look.Divider });
        }

        var add = Button(config.Calendars.Count == 0 ? "+  Add a calendar" : "+  Add another", () => Change(c => c.Calendars.Add(new CalendarLink()), rebuild: true));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 12, 0, 12);
        rows.Children.Add(add);
    }

    static readonly string[] CalendarColours = { "#4FC3F7", "#81C784", "#FFD54F", "#FFB74D", "#E57373", "#F06292", "#BA68C8", "#4DB6AC" };

    /// <summary>"Colour  (Theme) ● ● ● …  [#RRGGBB]": a calendar's colour on the Agenda and Calendar tiles; Theme = the theme picks.</summary>
    Control ColourField(string? current, Action<string?> save)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = L.T("Colour"), VerticalAlignment = VerticalAlignment.Center, Width = 120, Margin = new Thickness(0, 0, 0, 6), Foreground = Look.Text1 });
        var themeChip = Chip("Theme", string.IsNullOrWhiteSpace(current), () => save(null));
        themeChip.Margin = new Thickness(0, 0, 10, 6);
        row.Children.Add(themeChip);
        foreach (var hex in CalendarColours)
        {
            bool on = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.Parse(hex)), BorderThickness = new Thickness(on ? 3 : 1), BorderBrush = on ? Look.Accent : Look.Stroke,
            };
            ToolTip.SetTip(dot, hex);
            string pick = hex;
            Clickable(dot, () => save(pick));
            row.Children.Add(dot);
        }
        var box = TextField(current, 96, v =>
        {
            // Only valid colours are saved; anything else puts the box back.
            if (v == null) save(null);
            else if (Color.TryParse(v.StartsWith('#') ? v : "#" + v, out var col)) save($"#{col.R:X2}{col.G:X2}{col.B:X2}");
            else Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        }, "#RRGGBB");
        box.Watermark = "#RRGGBB";
        box.Margin = new Thickness(6, 0, 0, 6);
        row.Children.Add(box);
        return row;
    }
}
