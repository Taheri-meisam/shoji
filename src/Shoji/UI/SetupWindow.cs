using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shoji.Core;

namespace Shoji.UI;

public enum SetupChoice { Install, Portable, JustRun, Cancel }

/// <summary>First start from a download / USB stick: install (or update), run portable, or just run this once.</summary>
public sealed class SetupWindow : Window
{
    SetupChoice _choice = SetupChoice.Cancel;
    readonly CheckBox _dontAsk;

    public static SetupChoice Ask()
    {
        var w = new SetupWindow();
        w.ShowDialog();
        if (w._choice == SetupChoice.JustRun && w._dontAsk.IsChecked == true)
            Installer.Decline();
        return w._choice;
    }

    SetupWindow()
    {
        var installed = Installer.InstalledVersion;
        bool update = installed != null;
        string thisVersion = AppPaths.VersionText;

        Title = update ? L.T("Update Shoji") : "Shoji";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        SettingsPalette.Apply(this, SettingsPalette.SystemIsDark()); // Shoji's light / dark look, as in Settings
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        Topmost = true;
        Icon = AppIcon();

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var icon = new Image { Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0), Source = AppIcon() };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = update ? L.T("Update Shoji?") : L.T("Welcome to Shoji"), FontSize = 20, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = update ? L.F("Installed: {0}   →   this file: {1}", $"{installed!.Major}.{installed.Minor}.{installed.Build}", thisVersion) : L.F("Version {0}", thisVersion),
                }.WithResource(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"),
            },
        });
        panel.Children.Add(header);

        panel.Children.Add(Choice(update ? L.T("Update the installed app") : L.T("Install (recommended)"),
            update ? L.T("Replaces the installed version and keeps all your settings. Shoji restarts by itself.")
                   : L.T("Adds Shoji to the Start menu and to Settings → Apps (with an uninstaller). " +
                     "No admin rights needed. Start with Windows keeps working even if you delete this download."),
            SetupChoice.Install, primary: true));
        panel.Children.Add(Choice(L.T("Run portable"),
            L.T("Keeps everything in a \"data\" folder next to this exe and adds nothing to Windows. Good for a USB stick or just trying it out."),
            SetupChoice.Portable));
        panel.Children.Add(Choice(L.T("Just run this time"),
            L.T("Runs from here without changing anything."), SetupChoice.JustRun));
        _dontAsk = new CheckBox
        {
            Content = L.T("Don't ask again for this copy (Install stays in the tray menu)"),
            Margin = new Thickness(2, 4, 0, 0),
            Visibility = Installer.IsSetupCopy ? Visibility.Collapsed : Visibility.Visible,
        };
        panel.Children.Add(_dontAsk);

        Content = panel;
    }

    Button Choice(string title, string text, SetupChoice choice, bool primary = false)
    {
        var b = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 }.WithResource(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"),
                },
            },
            IsDefault = primary,
        };
        if (primary)
            b.SetResourceReference(StyleProperty, "AccentButtonStyle");
        b.Click += (_, _) =>
        {
            _choice = choice;
            Close();
        };
        return b;
    }

    /// <summary>The embedded app icon (largest size) for windows and the tray.</summary>
    public static BitmapFrame? AppIcon()
    {
        try
        {
            using var s = typeof(SetupWindow).Assembly.GetManifestResourceStream("Shoji.app.ico");
            if (s == null)
                return null;
            var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapFrame best = decoder.Frames[0];
            foreach (var f in decoder.Frames)
                if (f.PixelWidth > best.PixelWidth)
                    best = f;
            return best;
        }
        catch
        {
            return null;
        }
    }
}

static class ResourceExtensions
{
    /// <summary>Sets a property from a theme resource (follows light / dark mode) and returns the element.</summary>
    public static T WithResource<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
