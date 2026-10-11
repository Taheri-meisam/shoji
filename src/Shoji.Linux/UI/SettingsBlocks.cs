using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// Building blocks of the Settings window (cards, rows, segmented choices, pills…). They translate the texts they're
/// given (<see cref="L.T"/>), so callers pass the English; lists that can hold the user's own names (Combo) don't.
/// </summary>
public sealed partial class SettingsWindow
{
    enum PillKind { Neutral, Good, Warn, Accent }

    static Border Pill(string text, PillKind kind, bool small = false)
    {
        var (bg, fg) = kind switch
        {
            PillKind.Good => (Look.GoodSoft, Look.Good),
            PillKind.Warn => (Look.WarnSoft, Look.Warn),
            PillKind.Accent => (Look.AccentSoft, Look.AccentText),
            _ => (Look.Hover, Look.Text2),
        };
        return new Border
        {
            CornerRadius = new CornerRadius(20), Padding = small ? new Thickness(8, 1, 8, 2) : new Thickness(12, 4, 12, 5), Background = bg,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = L.T(text), FontSize = small ? 11 : 12, FontWeight = FontWeight.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    /// <summary>
    /// Makes an element clickable like a button: it acts only when the press both started and ended on it and the pointer
    /// barely moved (so a drag isn't a click). Mouse presses take focus at once (so a text box being edited saves first).
    /// Touch presses are left unhandled and the pointer isn't captured, so the Settings page's ScrollViewer can still pan
    /// when a swipe starts on a row; it then takes the pointer and no click happens. Enter / Space click it too.
    /// </summary>
    static void Clickable(Control el, Action click)
    {
        el.Focusable = true;
        el.Cursor = new Cursor(StandardCursorType.Hand);
        Point? down = null;
        el.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(el).Properties.IsLeftButtonPressed)
                return;
            down = e.GetPosition(el);
            if (e.Pointer.Type == PointerType.Mouse)
            {
                el.Focus();
                e.Handled = true;
            }
        };
        el.PointerCaptureLost += (_, _) => down = null; // e.g. the ScrollViewer started panning
        el.PointerReleased += (_, e) =>
        {
            if (down is not Point start)
                return;
            down = null;
            var p = e.GetPosition(el);
            if (Math.Abs(p.X - start.X) > 10 || Math.Abs(p.Y - start.Y) > 10
                || p.X < 0 || p.Y < 0 || p.X > el.Bounds.Width || p.Y > el.Bounds.Height)
                return;
            e.Handled = true;
            el.Focus();
            click();
        };
        el.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                click();
            }
        };
    }

    void Section(string title, string? subtitle)
    {
        _panel.Children.Add(new TextBlock
        {
            Text = L.T(title), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text1,
            Margin = new Thickness(2, _panel.Children.Count <= 1 ? 18 : 30, 0, subtitle == null ? 10 : 2),
        });
        if (subtitle != null)
            _panel.Children.Add(new TextBlock { Text = L.T(subtitle), TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(2, 0, 0, 12), Foreground = Look.Text2 });
    }

    Control Hint(string text)
    {
        var icon = new TextBlock { Text = "info", FontFamily = UI.Theme.Icons, FontSize = 15, Margin = new Thickness(0, 0, 10, 0), Foreground = Look.Text3, VerticalAlignment = VerticalAlignment.Top };
        var dock = new DockPanel { Margin = new Thickness(4, 6, 0, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = L.T(text), TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Look.Text2 });
        return dock;
    }

    void Banner(string text)
    {
        var icon = new TextBlock { Text = "warning", FontFamily = UI.Theme.Icons, FontSize = 18, Margin = new Thickness(0, 0, 12, 0), Foreground = Look.Warn };
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = L.T(text), TextWrapping = TextWrapping.Wrap, Foreground = Look.Text1 });
        _panel.Children.Add(new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12), Margin = new Thickness(0, 8), Background = Look.WarnSoft, Child = dock });
    }

    Border Card(bool addToPanel = true)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(20, 6),
            Margin = new Thickness(0, 0, 0, 10), Child = new StackPanel(), Background = Look.Card, BorderBrush = Look.Stroke,
        };
        if (addToPanel)
            _panel.Children.Add(card);
        return card;
    }

    /// <summary>One row in a card: optional icon, label (and description), the control on the right, a divider below.</summary>
    static void Setting(Border card, string label, string? description, Control control, bool last = false, string? icon = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = L.T(label), TextWrapping = TextWrapping.Wrap, Foreground = Look.Text1 });
        if (description != null)
            text.Children.Add(new TextBlock { Text = L.T(description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0), Foreground = Look.Text2 });
        control.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Margin = new Thickness(0, 12), LastChildFill = true };
        if (icon != null)
        {
            var glyph = new TextBlock { Text = icon, FontFamily = UI.Theme.Icons, FontSize = 20, Width = 24, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = Look.Text2 };
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
        }
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(text);
        var inner = (StackPanel)card.Child!;
        inner.Children.Add(row);
        if (!last)
            inner.Children.Add(new Border { Height = 1, Background = Look.Divider });
    }

    static Border Chip(string text, bool selected, Action click)
    {
        var t = new TextBlock { Text = L.T(text), Margin = new Thickness(16, 7), FontSize = 13, Foreground = selected ? Brushes.White : Look.Text1, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8), Child = t,
            Background = selected ? Look.Accent : Look.Card, BorderBrush = selected ? Look.Accent : Look.Stroke,
        };
        if (!selected)
        {
            chip.PointerEntered += (_, _) => chip.BorderBrush = Look.Text3;
            chip.PointerExited += (_, _) => chip.BorderBrush = Look.Stroke;
        }
        Clickable(chip, click);
        return chip;
    }

    /// <summary>Joined buttons for a few short choices; shows the new choice at once, then calls <paramref name="changed"/>.</summary>
    static Control Segmented(IReadOnlyList<string> items, int selected, Action<int> changed, bool stretch = false)
    {
        var row = new UniformGrid { Rows = 1 };
        var outer = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(3), Child = row, Background = Look.Input, BorderBrush = Look.Stroke };
        if (!stretch)
            outer.HorizontalAlignment = HorizontalAlignment.Right;
        int current = selected;
        var parts = new List<(Border Box, TextBlock Text)>();
        void Paint()
        {
            for (int i = 0; i < parts.Count; i++)
            {
                var (box, text) = parts[i];
                bool on = i == current;
                box.Background = on ? Look.Card : Brushes.Transparent;
                box.BoxShadow = on ? BoxShadows.Parse("0 1 4 0 #38000000") : default;
                text.Foreground = on ? Look.Text1 : Look.Text2;
                text.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var text = new TextBlock { Text = L.T(items[i]), FontSize = 13, Margin = new Thickness(12, 5, 12, 6), HorizontalAlignment = HorizontalAlignment.Center };
            var box = new Border { CornerRadius = new CornerRadius(7), Child = text, Background = Brushes.Transparent };
            box.PointerEntered += (_, _) => { if (index != current) box.Background = Look.Hover; };
            box.PointerExited += (_, _) => Paint();
            Clickable(box, () =>
            {
                if (index == current)
                    return;
                current = index;
                Paint();
                changed(index);
            });
            parts.Add((box, text));
            row.Children.Add(box);
        }
        Paint();
        return outer;
    }

    static ComboBox Combo(IReadOnlyList<string> items, int selected, Action<int> changed)
    {
        var box = new ComboBox { MinWidth = 240, ItemsSource = items, SelectedIndex = selected };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
                changed(box.SelectedIndex);
        };
        return box;
    }

    static ToggleSwitch Switch(bool value, Action<bool> changed)
    {
        var s = new ToggleSwitch { IsChecked = value, OnContent = null, OffContent = null, MinWidth = 0 };
        s.IsCheckedChanged += (_, _) => changed(s.IsChecked == true);
        return s;
    }

    static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = value, Content = text.Length > 0 ? L.T(text) : null, Foreground = Look.Text1 };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>
    /// Saves of typed text that hasn't been saved yet. Boxes save on Enter or when focus leaves them; closing the
    /// window doesn't move focus, so <see cref="FlushPending"/> saves these when it closes.
    /// </summary>
    static readonly HashSet<Action> PendingCommits = new();

    static void FlushPending()
    {
        foreach (var commit in PendingCommits.ToArray())
            commit();
        PendingCommits.Clear();
    }

    /// <summary>
    /// A text box that saves when you press Enter, click elsewhere, or close the window (empty = null).
    /// <paramref name="check"/> (optional) turns the text into the value to save, or returns false: then the box
    /// is marked invalid with the reason in its tooltip and nothing is saved.
    /// </summary>
    static TextBox TextField(string? value, double width, Action<string?> save, string? tooltip = null, Func<string?, (bool Ok, string? Value)>? check = null, string? invalidHint = null)
    {
        var box = new TextBox { Text = value ?? "", Width = width, VerticalContentAlignment = VerticalAlignment.Center };
        if (tooltip != null)
            ToolTip.SetTip(box, tooltip = L.T(tooltip));
        string last = box.Text ?? "";
        void Commit()
        {
            PendingCommits.Remove(Commit);
            string now = box.Text ?? "";
            if (now == last)
                return;
            string? text = string.IsNullOrWhiteSpace(now) ? null : now.Trim();
            if (check != null)
            {
                var (ok, normal) = check(text);
                MarkInvalid(box, ok ? null : L.T(invalidHint ?? "Not a valid value: it wasn't saved"), tooltip);
                if (!ok)
                    return;
                text = normal;
                if (normal != null && now != normal)
                {
                    box.Text = normal; // show what was saved ("7pm" → "19:00")
                    now = normal;
                }
            }
            last = now;
            save(text);
        }
        box.TextChanged += (_, _) =>
        {
            if ((box.Text ?? "") != last)
                PendingCommits.Add(Commit);
        };
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
        };
        return box;
    }

    /// <summary>Red border and the reason as tooltip (error = null puts the box back to normal).</summary>
    static void MarkInvalid(TextBox box, string? error, string? normalTooltip)
    {
        box.Classes.Set("invalid", error != null);
        ToolTip.SetTip(box, error ?? normalTooltip);
    }

    static readonly IBrush InvalidBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x3E, 0x3E)).ToImmutable();

    /// <summary>
    /// The red border of a box marked invalid. On the template's border (not the TextBox's BorderBrush), so it
    /// also shows while the box has focus or the pointer over it, where the theme sets that border itself.
    /// </summary>
    static Style InvalidBoxStyle() =>
        new(x => x.OfType<TextBox>().Class("invalid").Template().OfType<Border>().Name("PART_BorderElement"))
        {
            Setters =
            {
                new Setter(Border.BorderBrushProperty, InvalidBrush),
                new Setter(Border.BorderThicknessProperty, new Thickness(2)),
            },
        };

    static TextBox Number(double value, Action<double> save) =>
        TextField(value.ToString(CultureInfo.CurrentCulture), 90, v => save(double.Parse(v!, NumberStyles.Float, CultureInfo.CurrentCulture)),
            check: v => double.TryParse(v ?? "0", NumberStyles.Float, CultureInfo.CurrentCulture, out double d) && d >= 0
                ? (true, d.ToString(CultureInfo.CurrentCulture))
                : (false, null),
            invalidHint: "Type a number (0 or more): this wasn't saved");

    /// <summary>
    /// A 0–100 % slider with its value beside it. Saves once the thumb has rested for a moment (every save reloads
    /// the dashboard), or when the window closes.
    /// </summary>
    static Control PercentSlider(int value, Action<int> save)
    {
        var label = new TextBlock { Text = $"{value} %", Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text1 };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = value, Width = 200, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
            slider.Resources[key] = Look.Accent; // the window's vermilion, not Fluent's blue
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        int last = value;
        void Commit()
        {
            timer.Stop();
            PendingCommits.Remove(Commit);
            int now = (int)Math.Round(slider.Value);
            if (now == last)
                return;
            last = now;
            save(now);
        }
        timer.Tick += (_, _) => Commit();
        slider.ValueChanged += (_, _) =>
        {
            label.Text = $"{(int)Math.Round(slider.Value)} %";
            PendingCommits.Add(Commit);
            timer.Stop();
            timer.Start();
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { slider, label } };
    }

    /// <summary>A 24-hour time box: "7", "7pm", "19.30" are saved as "HH:mm"; anything else is marked and not saved.</summary>
    static TextBox ClockField(string? value, double width, Action<string?> save, bool optional = false) =>
        TextField(value, width, save, "24-hour time, e.g. 07:30", v =>
        {
            if (v == null)
                return (optional, null);
            return SceneEngine.TryParseClock(v, out var t) ? (true, $"{(int)t.TotalHours:00}:{t.Minutes:00}") : (false, null);
        }, optional ? "Use a time like 07:30 or 22:00 (or leave empty): this wasn't saved" : "Use a time like 07:30 or 22:00: this wasn't saved");

    /// <summary>"Label  [text box]  hint". Secrets use a masked box.</summary>
    static Control Field(string label, string? value, string hint, Action<string?> save, bool secret = false)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = L.T(label), VerticalAlignment = VerticalAlignment.Center, Width = 120, Foreground = Look.Text1 });
        var box = TextField(value, 300, save);
        if (secret)
            box.PasswordChar = '•';
        box.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = L.T(hint), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Foreground = Look.Text2 });
        return row;
    }

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = L.T(text), Margin = new Thickness(0, 0, 8, 6), MinWidth = 36, Padding = new Thickness(14, 6, 14, 7) };
        b.Click += (_, _) => click();
        return b;
    }

    static Button AccentButton(string text, Action click)
    {
        var b = Button(text, click);
        b.Classes.Add("accent");
        return b;
    }

    /// <summary>A flat icon button (move up / down, delete) that only shows a background under the mouse.</summary>
    static Border IconButton(string glyph, string tooltip, Action click, bool danger = false)
    {
        var t = new TextBlock { Text = glyph, FontFamily = UI.Theme.Icons, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text2 };
        var b = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Child = t, Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0) };
        ToolTip.SetTip(b, L.T(tooltip));
        b.PointerEntered += (_, _) =>
        {
            b.Background = danger ? Look.AccentSoft : Look.Hover;
            t.Foreground = danger ? Look.AccentText : Look.Text1;
        };
        b.PointerExited += (_, _) =>
        {
            b.Background = Brushes.Transparent;
            t.Foreground = Look.Text2;
        };
        Clickable(b, click);
        b.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsEnabledProperty)
                b.Opacity = b.IsEnabled ? 1 : 0.3;
        };
        return b;
    }

    static TextBlock Link(string text, Action click)
    {
        var t = new TextBlock { Text = L.T(text), VerticalAlignment = VerticalAlignment.Center, Foreground = Look.AccentText };
        t.PointerEntered += (_, _) => t.TextDecorations = TextDecorations.Underline;
        t.PointerExited += (_, _) => t.TextDecorations = null;
        Clickable(t, click);
        return t;
    }

    static Control Labeled2(string label, Control control)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = L.T(label), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), Foreground = Look.Text2 });
        p.Children.Add(control);
        return p;
    }

    static Border HoverCard(Control child, double width, Thickness padding)
    {
        var card = new Border { Width = width, Padding = padding, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = child, Background = Look.Card, BorderBrush = Look.Stroke };
        card.PointerEntered += (_, _) => card.BorderBrush = Look.Text3;
        card.PointerExited += (_, _) => card.BorderBrush = Look.Stroke;
        return card;
    }

    static int Nearest(IReadOnlyList<double> options, double value)
    {
        int best = 0;
        for (int i = 1; i < options.Count; i++)
            if (Math.Abs(options[i] - value) < Math.Abs(options[best] - value))
                best = i;
        return best;
    }

    static void Swap<T>(List<T> list, int a, int b)
    {
        if (a < 0 || b < 0 || a >= list.Count || b >= list.Count)
            return;
        (list[a], list[b]) = (list[b], list[a]);
    }
}
