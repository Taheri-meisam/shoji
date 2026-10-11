using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Pages & tiles → Share a layout: export to a file or a code, and import one with a preview (logic in Core/LayoutShare.cs).</summary>
public sealed partial class SettingsWindow
{
    static bool _shareScenes = true;

    static readonly FilePickerFileType LayoutFiles = new("Shoji layout") { Patterns = new[] { "*" + LayoutShare.FileExtension, "*.json" } };

    void AddShare(AppConfig config)
    {
        Section("Share a layout", "All your pages with their tiles and deck buttons, the theme and colours, as a file or a short code to paste. " +
                                  "Tokens, keys, passwords, headers and calendar links are never included.");
        var card = Card();
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        buttons.Children.Add(Button("Export layout…", () => _ = ExportLayoutAsync()));
        buttons.Children.Add(Button("Copy layout code", CopyLayoutCode));
        buttons.Children.Add(AccentButton("Import layout…", () => ImportLayout()));
        ((StackPanel)card.Child!).Children.Add(buttons);
        if (config.Scenes.Count == 0)
            return;
        var scenes = Check(L.F("Include the scenes ({0})", config.Scenes.Count), _shareScenes, on => _shareScenes = on);
        scenes.Margin = new Thickness(0, 0, 0, 12);
        ((StackPanel)card.Child!).Children.Add(scenes);
    }

    async Task ExportLayoutAsync()
    {
        try
        {
            var layout = LayoutShare.Export(AppConfig.Load(), _shareScenes);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Export layout"), SuggestedFileName = LayoutShare.SuggestedFileName(), DefaultExtension = "json",
                FileTypeChoices = new[] { LayoutFiles }, ShowOverwritePrompt = true,
            });
            if (file == null)
                return;
            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(LayoutShare.ToJson(layout));
            Message(L.F("Saved {0}: {1}. Open it with Import layout… on another PC.", file.Name, LayoutShare.Summarize(layout).Text));
        }
        catch (Exception ex)
        {
            Log.Write($"Layout export failed: {ex.Message}");
            Message(L.F("The layout couldn't be saved: {0}", ex.Message));
        }
    }

    void CopyLayoutCode()
    {
        var layout = LayoutShare.Export(AppConfig.Load(), _shareScenes);
        string code = LayoutShare.ToCode(layout);
        Clipboard?.SetTextAsync(code);
        Message(L.F("Layout code copied ({0:N0} characters: {1}). Paste it in a chat or a note; " +
                "Import layout… → paste turns it back into pages.", code.Length, LayoutShare.Summarize(layout).Text));
    }

    /// <summary>The import dialog: paste a code or open a file, see what it holds, then add or replace.</summary>
    void ImportLayout(string? text = null)
    {
        var dialog = new Window
        {
            Title = L.T("Import layout"), Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Look.Card, Foreground = Look.Text1, RequestedThemeVariant = RequestedThemeVariant, FontFamily = FontFamily,
        };
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorDark1" })
            dialog.Resources[key] = Look.AccentColor;

        var box = new TextBox
        {
            Text = text ?? "", Watermark = L.T("Paste a layout code (shoji1:…) here"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 96, FontFamily = new FontFamily("monospace"), FontSize = 12,
        };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
        var warning = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Look.Warn, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
        var add = new RadioButton { Content = L.T("Add as new pages"), IsChecked = true, GroupName = "import", Foreground = Look.Text1 };
        var replace = new RadioButton { Content = L.T("Replace my pages"), GroupName = "import", Foreground = Look.Text1, Margin = new Thickness(16, 0, 0, 0) };
        var theme = new CheckBox { Content = L.T("Also use its theme (colours, day and night, night mode)"), Foreground = Look.Text1, Margin = new Thickness(0, 6, 0, 0) };
        SharedLayout? layout = null;
        Button? import = null;

        void Check()
        {
            layout = null;
            warning.IsVisible = false;
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                preview.Text = L.T("Paste a code above, or open a .shoji.json file.");
                preview.Foreground = Look.Text2;
            }
            else
            {
                try
                {
                    layout = LayoutShare.Parse(box.Text);
                    var summary = LayoutShare.Summarize(layout);
                    preview.Text = summary.Text;
                    preview.Foreground = Look.Text1;
                    warning.Text = summary.Warning ?? "";
                    warning.IsVisible = summary.Warning != null;
                    theme.IsEnabled = summary.ThemeName != null || layout.AccentColor != null || layout.DayNight != null;
                }
                catch (FormatException ex)
                {
                    preview.Text = ex.Message;
                    preview.Foreground = Look.Warn;
                }
            }
            if (import != null)
                import.IsEnabled = layout != null;
        }
        box.TextChanged += (_, _) => Check();

        var open = Button("Open a file…", async () =>
        {
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = L.T("Import layout"), AllowMultiple = false, FileTypeFilter = new[] { LayoutFiles, FilePickerFileTypes.All },
                });
                if (files.FirstOrDefault() is not { } file)
                    return;
                var props = await file.GetBasicPropertiesAsync();
                if (props.Size > 4 * 1024 * 1024)
                {
                    preview.Text = L.T("That file is too large to be a Shoji layout.");
                    preview.Foreground = Look.Warn;
                    return;
                }
                await using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                box.Text = await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                preview.Text = L.F("The file couldn't be read: {0}", ex.Message);
                preview.Foreground = Look.Warn;
            }
        });
        open.Margin = new Thickness(0, 8, 0, 0);

        import = AccentButton("Import", () =>
        {
            if (layout == null)
                return;
            bool replacing = replace.IsChecked == true, useTheme = theme.IsChecked == true && theme.IsEnabled;
            var chosen = layout;
            void Apply()
            {
                int before = AppConfig.Load().Pages.Count;
                if (Change(c => LayoutShare.Apply(c, chosen, replacing, useTheme), rebuild: true))
                    _page = replacing ? 0 : before; // show the first imported page
            }
            dialog.Close();
            if (replacing)
                Message("Replace all your pages with the imported ones? Your current pages and their tiles are removed (export them first to keep a copy).", "Replace", Apply);
            else
                Apply();
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(import);
        buttons.Children.Add(Button("Cancel", dialog.Close));

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22, 20),
            Children =
            {
                new TextBlock { Text = L.T("Paste a layout code or open a layout file. You'll see what it holds before anything changes."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = Look.Text2 },
                box, open, preview, warning,
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { add, replace } },
                theme, buttons,
            },
        };
        Check();
        dialog.ShowDialog(this);
    }
}
