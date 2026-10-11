using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Shoji.UI;

/// <summary>
/// Shoji's own look for the Settings window: warm washi paper in light mode, ink in dark mode, vermilion accent.
/// The colours are window resources, and the Windows 11 (Fluent) control colours are pointed at the same accent,
/// so text boxes, combo boxes and buttons match the custom parts.
/// </summary>
static class SettingsPalette
{
    // Resource keys for the custom parts.
    public const string Bg = "S.Bg", Side = "S.Side", Card = "S.Card", Raised = "S.Raised", Stroke = "S.Stroke", Divider = "S.Divider",
        Input = "S.Input", Hover = "S.Hover", Text1 = "S.Text1", Text2 = "S.Text2", Text3 = "S.Text3",
        Accent = "S.Accent", AccentText = "S.AccentText", AccentSoft = "S.AccentSoft",
        Good = "S.Good", GoodSoft = "S.GoodSoft", Warn = "S.Warn", WarnSoft = "S.WarnSoft";

    public static readonly Color Brand = Color.FromRgb(0xD9, 0x50, 0x3A);

    /// <summary>True when Windows apps are set to dark mode (Settings → Personalisation → Colours).</summary>
    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDark(string? look) => look?.ToLowerInvariant() switch
    {
        "light" => false,
        "dark" => true,
        _ => SystemIsDark(),
    };

    public static void Apply(Window w, bool dark)
    {
        if (SystemParameters.HighContrast)
        {
            ApplyHighContrast(w);
            return;
        }
#pragma warning disable WPF0001
        w.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        var r = w.Resources;
        void Set(string key, string hex) => r[key] = Theme.Freeze(new SolidColorBrush(Theme.Parse(hex)));
        void SetAll(string hex, params string[] keys)
        {
            foreach (var k in keys)
                Set(k, hex);
        }

        string accent = dark ? "#E8603F" : "#D9503A", hover = dark ? "#F0714F" : "#E35E46", pressed = dark ? "#D4573A" : "#C2452F";
        string stroke = dark ? "#2A2F39" : "#E3DDD3";
        Set(Bg, dark ? "#14161B" : "#F5F3EF");
        Set(Side, dark ? "#0F1115" : "#ECE8E1");
        Set(Card, dark ? "#1B1E25" : "#FFFFFF");
        Set(Raised, dark ? "#23272F" : "#FBFAF7");
        Set(Stroke, stroke);
        Set(Divider, dark ? "#262A33" : "#EEE9E1");
        Set(Input, dark ? "#23272F" : "#F3F0EB");
        Set(Hover, dark ? "#12FFFFFF" : "#0D000000");
        Set(Text1, dark ? "#ECEEF2" : "#1C1E23");
        Set(Text2, dark ? "#9AA3B2" : "#5F6673");
        Set(Text3, dark ? "#6B7383" : "#949AA5");
        Set(Accent, accent);
        Set(AccentText, dark ? "#FF8A6B" : "#C2452F");
        Set(AccentSoft, dark ? "#26E8603F" : "#1AD9503A");
        Set(Good, dark ? "#3ECF7A" : "#1E9E55");
        Set(GoodSoft, dark ? "#243ECF7A" : "#1F1E9E55");
        Set(Warn, dark ? "#F0B43C" : "#A86B12");
        Set(WarnSoft, dark ? "#26F0B43C" : "#26D99A2B");

        // The Fluent keys the built-in controls use.
        Set("ApplicationBackgroundBrush", dark ? "#14161B" : "#F5F3EF");
        Set("CardBackgroundFillColorDefaultBrush", dark ? "#1B1E25" : "#FFFFFF");
        Set("CardStrokeColorDefaultBrush", stroke);
        SetAll(accent, "AccentFillColorDefaultBrush", "AccentButtonBackground", "AccentButtonBorderBrush", "CheckBoxCheckBackgroundFillChecked",
            "CheckBoxCheckBackgroundStrokeChecked", "ComboBoxItemPillFillBrush", "ListViewItemPillFillBrush");
        SetAll(hover, "AccentFillColorSecondaryBrush", "AccentButtonBackgroundPointerOver", "AccentButtonBorderBrushPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver");
        SetAll(pressed, "AccentFillColorTertiaryBrush", "AccentButtonBackgroundPressed", "AccentButtonBorderBrushPressed",
            "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeCheckedPressed");
        SetAll(accent, "TextControlSelectionHighlightColor", "ComboBoxBorderBrushFocused");
        SetAll(dark ? "#FF8A6B" : "#C2452F", "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush");
        SetAll("#FFFFFF", "TextOnAccentFillColorPrimaryBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "CheckBoxCheckGlyphForeground");
        SetAll("#E6FFFFFF", "AccentButtonForegroundPressed", "CheckBoxCheckGlyphForegroundPressed");
        // Text boxes: the accent line along the bottom while typing (same shape as Fluent's own brush).
        var focus = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, 2) };
        focus.RelativeTransform = new ScaleTransform { CenterY = 0.5, ScaleY = -1 };
        focus.GradientStops.Add(new GradientStop(Theme.Parse(accent), 1.0));
        focus.GradientStops.Add(new GradientStop(Theme.Parse(dark ? "#2A2F39" : "#D8D2C8"), 1.0));
        Theme.Freeze(focus);
        r["TextControlBorderBrushFocused"] = focus; // text boxes, password boxes
        r["TextControlElevationBorderFocusedBrush"] = focus;

        CaptionColors(w, dark);
    }

    /// <summary>
    /// High contrast: Windows' own colours everywhere (the Fluent high-contrast theme for the controls, system colours
    /// for the custom parts), so nothing of Shoji's palette gets in the way.
    /// </summary>
    static void ApplyHighContrast(Window w)
    {
#pragma warning disable WPF0001
        w.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        var r = w.Resources;
        foreach (var k in new[] { Bg, Side, Card, Raised, Input, AccentSoft, GoodSoft, WarnSoft })
            r[k] = SystemColors.WindowBrush;
        foreach (var k in new[] { Stroke, Divider, Text1, Good, Warn })
            r[k] = SystemColors.WindowTextBrush;
        foreach (var k in new[] { Text2, Text3 })
            r[k] = SystemColors.GrayTextBrush;
        foreach (var k in new[] { Accent, AccentText })
            r[k] = SystemColors.HighlightBrush;
        r[Hover] = SystemColors.ControlBrush;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11: title bar in the same colour as the sidebar (ignored on Windows 10).</summary>
    static void CaptionColors(Window w, bool dark)
    {
        void Paint()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            int Bgr(string hex) { var c = Theme.Parse(hex); return c.R | c.G << 8 | c.B << 16; }
            try
            {
                int caption = Bgr(dark ? "#0F1115" : "#ECE8E1"), text = Bgr(dark ? "#ECEEF2" : "#1C1E23"), border = Bgr(dark ? "#2A2F39" : "#D8D2C8");
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
                DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));    // DWMWA_TEXT_COLOR
                DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
            }
            catch
            {
                // Older Windows: keep the default title bar.
            }
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero)
            Paint();
        else
            w.SourceInitialized += (_, _) => Paint();
    }
}

/// <summary>Coloured icon squares for the sections and tile types (like the icons in phone settings).</summary>
static class SettingsIcons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public record Look(string Glyph, string From, string To);

    // Tile families share a colour: performance blue, heat orange, network teal, media pink, time amber, control vermilion, info green.
    const string BlueA = "#5B9BFF", BlueB = "#2F5FE0", HeatA = "#FFA25C", HeatB = "#E8622A", NetA = "#3FE0C8", NetB = "#119C8C",
        MediaA = "#F57FD0", MediaB = "#B23FC4", TimeA = "#FFCB5C", TimeB = "#E89A1C", CtrlA = "#FF7A6B", CtrlB = "#D9503A",
        InfoA = "#6FE08A", InfoB = "#2C9A4D", SlateA = "#97A2B6", SlateB = "#5B6579";

    static readonly Dictionary<string, Look> Tiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = new("", BlueA, BlueB), ["gpu"] = new("", BlueA, BlueB), ["memory"] = new("", BlueA, BlueB),
        ["power"] = new("", BlueA, BlueB), ["fps"] = new("", BlueA, BlueB), ["processes"] = new("", BlueA, BlueB),
        ["overview"] = new("", BlueA, BlueB), ["history"] = new("", BlueA, BlueB),
        ["temps"] = new("", HeatA, HeatB), ["fans"] = new("", HeatA, HeatB), ["sensors"] = new("", HeatA, HeatB),
        ["network"] = new("", NetA, NetB), ["ping"] = new("", NetA, NetB), ["websites"] = new("", NetA, NetB), ["webvalue"] = new("\uE9D2", NetA, NetB),
        ["media"] = new("", MediaA, MediaB), ["lyrics"] = new("", MediaA, MediaB), ["mixer"] = new("", MediaA, MediaB),
        ["twitch"] = new("", MediaA, MediaB), ["notifications"] = new("", MediaA, MediaB),
        ["clock"] = new("", TimeA, TimeB), ["worldclock"] = new("", TimeA, TimeB), ["calendar"] = new("", TimeA, TimeB),
        ["agenda"] = new("", TimeA, TimeB), ["weather"] = new("", TimeA, TimeB), ["forecast"] = new("", TimeA, TimeB),
        ["timer"] = new("", TimeA, TimeB),
        ["deck"] = new("", CtrlA, CtrlB), ["homeassistant"] = new("", CtrlA, CtrlB), ["mirror"] = new("", CtrlA, CtrlB),
        ["status"] = new("", InfoA, InfoB), ["recap"] = new("", InfoA, InfoB), ["activity"] = new("", InfoA, InfoB),
        ["battery"] = new("", InfoA, InfoB), ["disks"] = new("", InfoA, InfoB), ["github"] = new("", InfoA, InfoB),
        ["ticker"] = new("", InfoA, InfoB), ["system"] = new("", InfoA, InfoB),
        ["photos"] = new("\uE91B", MediaA, MediaB),
    };

    public static Look ForTile(string type) => Tiles.TryGetValue(type, out var l) ? l : new("", SlateA, SlateB);

    /// <summary>A rounded square with a gradient and a white glyph.</summary>
    public static Border Tile(Look look, double size, bool shadow = false)
    {
        var bg = new LinearGradientBrush(Theme.Parse(look.From), Theme.Parse(look.To), new Point(0, 0), new Point(1, 1));
        var box = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = Theme.Freeze(bg),
            Child = new TextBlock
            {
                Text = look.Glyph, FontFamily = Font, FontSize = Math.Round(size * 0.5), Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (shadow)
            box.Effect = new DropShadowEffect { Color = Theme.Parse(look.To), BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.45 };
        return box;
    }
}

/// <summary>An on / off switch (Windows 11 style, in the Settings accent colour). Space or Enter flips it too.</summary>
public sealed class ToggleSwitch : FrameworkElement
{
    static readonly DependencyProperty KnobProperty = DependencyProperty.Register("Knob", typeof(double), typeof(ToggleSwitch),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    bool _on, _hover, _down;

    /// <summary>Raised when the user flips it (not when IsOn is set from code).</summary>
    public event Action<bool>? Toggled;

    public ToggleSwitch(bool on)
    {
        _on = on;
        SetValue(KnobProperty, on ? 1.0 : 0.0);
        Width = 44;
        Height = 22;
        Focusable = true;
        FocusVisualStyle = null; // it draws its own focus ring
        Cursor = Cursors.Hand;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            if (_on == value)
                return;
            _on = value;
            BeginAnimation(KnobProperty, new DoubleAnimation(value ? 1 : 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            InvalidateVisual();
        }
    }

    /// <summary>Flips it as if clicked (also used by screen readers through the automation peer).</summary>
    public void Flip()
    {
        IsOn = !_on;
        Toggled?.Invoke(_on);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _down = true;
        Focus();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_down)
            return;
        _down = false;
        ReleaseMouseCapture();
        e.Handled = true;
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight)
            Flip();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _down = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            if (!e.IsRepeat)
                Flip();
            e.Handled = true;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, knob = (double)GetValue(KnobProperty);
        if (w <= 0 || h <= 0)
            return;
        Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        if (!IsEnabled)
            dc.PushOpacity(0.4);
        var track = new Rect(0.75, 0.75, w - 1.5, h - 1.5);
        double grow = _hover && IsEnabled ? 1 : 0, x = h / 2 + knob * (w - h);
        if (_on)
        {
            dc.DrawRoundedRectangle(Res(SettingsPalette.Accent, Brushes.OrangeRed), null, track, track.Height / 2, track.Height / 2);
            dc.DrawEllipse(Brushes.White, null, new Point(x, h / 2), h / 2 - 5 + grow, h / 2 - 5 + grow);
        }
        else
        {
            var stroke = Res(SettingsPalette.Text2, Brushes.Gray);
            dc.DrawRoundedRectangle(_hover ? Res(SettingsPalette.Hover, Brushes.Transparent) : Brushes.Transparent, new Pen(stroke, 1.25), track, track.Height / 2, track.Height / 2);
            dc.DrawEllipse(stroke, null, new Point(x, h / 2), h / 2 - 6.5 + grow, h / 2 - 6.5 + grow);
        }
        if (IsKeyboardFocused)
        {
            var ring = new Rect(-3, -3, w + 6, h + 6);
            dc.DrawRoundedRectangle(null, new Pen(Res(SettingsPalette.Text1, Brushes.White), 1.5), ring, ring.Height / 2, ring.Height / 2);
        }
        if (!IsEnabled)
            dc.Pop();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>Lets Narrator and other UI Automation clients see it as an on / off toggle.</summary>
    sealed class Peer : FrameworkElementAutomationPeer, IToggleProvider
    {
        public Peer(ToggleSwitch owner) : base(owner) { }

        ToggleSwitch Switch => (ToggleSwitch)Owner;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => "ToggleSwitch";
        protected override bool IsControlElementCore() => true;
        protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } n ? n : Switch.ToolTip as string ?? "";
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Toggle ? this : base.GetPattern(pattern);
        public ToggleState ToggleState => Switch.IsOn ? ToggleState.On : ToggleState.Off;

        public void Toggle()
        {
            if (Switch.IsEnabled)
                Switch.Flip();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
}
