using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Pages & tiles → Share a layout: export to a file or a code, and import one with a preview (logic in Core/LayoutShare.cs).</summary>
public sealed partial class SettingsWindow
{
    static bool _shareScenes = true;

    static string LayoutFilter => L.T("Shoji layout (*.shoji.json)|*.shoji.json|JSON files (*.json)|*.json|All files (*.*)|*.*");

    void AddShare(AppConfig config)
    {
        Section(L.T("Share a layout"), L.T("All your pages with their tiles and deck buttons, the theme and colours, as a file or a short code to paste. " +
                                  "Tokens, keys, passwords, headers and calendar links are never included."));
        var card = Card();
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        buttons.Children.Add(Button(L.T("Export layout…"), ExportLayout));
        buttons.Children.Add(Button(L.T("Copy layout code"), CopyLayoutCode));
        buttons.Children.Add(AccentButton(L.T("Import layout…"), ImportLayout));
        ((StackPanel)card.Child).Children.Add(buttons);
        if (config.Scenes.Count == 0)
            return;
        var scenes = Check(L.F("Include the scenes ({0})", config.Scenes.Count), _shareScenes, on => _shareScenes = on);
        scenes.Margin = new Thickness(0, 0, 0, 12);
        ((StackPanel)card.Child).Children.Add(scenes);
    }

    void ExportLayout()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Export layout"), FileName = LayoutShare.SuggestedFileName(), Filter = LayoutFilter, DefaultExt = ".json", AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var layout = LayoutShare.Export(AppConfig.Load(), _shareScenes);
            Sensors.AtomicFile.WriteAllText(dialog.FileName, LayoutShare.ToJson(layout));
            MessageBox.Show(this, L.F("Saved {0}: {1}.\n\nOpen it with Import layout… on another PC.", Path.GetFileName(dialog.FileName), LayoutShare.Summarize(layout).Text), "Shoji");
        }
        catch (Exception ex)
        {
            Log.Write($"Layout export failed: {ex.Message}");
            MessageBox.Show(this, L.F("The layout couldn't be saved: {0}", ex.Message), "Shoji", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void CopyLayoutCode()
    {
        var layout = LayoutShare.Export(AppConfig.Load(), _shareScenes);
        string code = LayoutShare.ToCode(layout);
        try
        {
            Clipboard.SetText(code);
        }
        catch (Exception ex)
        {
            // Another app can hold the clipboard for a moment.
            MessageBox.Show(this, L.F("The clipboard is busy, try again: {0}", ex.Message), "Shoji", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, L.F("Layout code copied ({0:N0} characters: {1}).\n\n" +
                              "Paste it in a chat or a note; Import layout… → paste turns it back into pages.", code.Length, LayoutShare.Summarize(layout).Text), "Shoji");
    }

    /// <summary>The import dialog: paste a code or open a file, see what it holds, then add or replace.</summary>
    void ImportLayout()
    {
        var dialog = new Window
        {
            Title = L.T("Import layout"), Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false,
            FontFamily = FontFamily, FontSize = FontSize,
        };
        SettingsPalette.Apply(dialog, _dark);
        dialog.SetResourceReference(BackgroundProperty, SettingsPalette.Card);
        dialog.SetResourceReference(ForegroundProperty, TextPrimary);

        var box = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = L.T("Paste a layout code (shoji1:…) here"),
        };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
        var warning = Ref(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed }, TextBlock.ForegroundProperty, SettingsPalette.Warn);
        var add = new RadioButton { Content = L.T("Add as new pages"), IsChecked = true, GroupName = "import" };
        var replace = new RadioButton { Content = L.T("Replace my pages"), GroupName = "import", Margin = new Thickness(16, 0, 0, 0) };
        var theme = new CheckBox { Content = L.T("Also use its theme (colours, day and night, night mode)"), Margin = new Thickness(0, 8, 0, 0) };
        SharedLayout? layout = null;
        Button? import = null;

        void Check()
        {
            layout = null;
            warning.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                preview.Text = L.T("Paste a code above, or open a .shoji.json file.");
                preview.SetResourceReference(TextBlock.ForegroundProperty, TextSecondary);
            }
            else
            {
                try
                {
                    layout = LayoutShare.Parse(box.Text);
                    var summary = LayoutShare.Summarize(layout);
                    preview.Text = summary.Text;
                    preview.SetResourceReference(TextBlock.ForegroundProperty, TextPrimary);
                    warning.Text = summary.Warning ?? "";
                    warning.Visibility = summary.Warning != null ? Visibility.Visible : Visibility.Collapsed;
                    theme.IsEnabled = summary.ThemeName != null || layout.AccentColor != null || layout.DayNight != null;
                }
                catch (FormatException ex)
                {
                    preview.Text = ex.Message;
                    preview.SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.Warn);
                }
            }
            if (import != null)
                import.IsEnabled = layout != null;
        }
        box.TextChanged += (_, _) => Check();

        var open = Button(L.T("Open a file…"), () =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = L.T("Import layout"), Filter = LayoutFilter };
            if (pick.ShowDialog(dialog) != true)
                return;
            try
            {
                if (new FileInfo(pick.FileName).Length > 4 * 1024 * 1024)
                {
                    preview.Text = L.T("That file is too large to be a Shoji layout.");
                    preview.SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.Warn);
                    return;
                }
                box.Text = File.ReadAllText(pick.FileName);
            }
            catch (Exception ex)
            {
                preview.Text = L.F("The file couldn't be read: {0}", ex.Message);
                preview.SetResourceReference(TextBlock.ForegroundProperty, SettingsPalette.Warn);
            }
        });
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 8, 0, 0);

        import = AccentButton(L.T("Import"), () =>
        {
            if (layout == null)
                return;
            bool replacing = replace.IsChecked == true, useTheme = theme.IsChecked == true && theme.IsEnabled;
            if (replacing && MessageBox.Show(dialog, L.T("Replace all your pages with the imported ones? Your current pages and their tiles are removed (export them first to keep a copy)."),
                    "Shoji", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            int before = AppConfig.Load().Pages.Count;
            var chosen = layout;
            dialog.Close();
            if (Change(c => LayoutShare.Apply(c, chosen, replacing, useTheme), rebuild: true))
                _page = replacing ? 0 : before; // show the first imported page
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(import);
        buttons.Children.Add(Button(L.T("Cancel"), dialog.Close));

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 14),
            Children =
            {
                Ref(new TextBlock { Text = L.T("Paste a layout code or open a layout file. You'll see what it holds before anything changes."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                    TextBlock.ForegroundProperty, TextSecondary),
                box, open, preview, warning,
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { add, replace } },
                theme, buttons,
            },
        };
        Check();
        dialog.ShowDialog();
    }
}
