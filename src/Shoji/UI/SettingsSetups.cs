using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Appearance → Setups: a theme together with pages made for it, applied in one go (Core/Setups.cs).</summary>
public sealed partial class SettingsWindow
{
    void AddSetups(AppConfig config)
    {
        Section(L.T("Setups"), L.T("A theme together with pages made for it. Picking one replaces your pages and theme; tiles you already had keep their own settings, and the layout from before is saved so you can bring it back."));
        var wrap = new WrapPanel();
        foreach (var setup in Setups.All)
            wrap.Children.Add(SetupCard(setup));
        _panel.Children.Add(wrap);

        if (Setups.BackupSaved is { } saved)
        {
            var card = Card();
            Setting(card, L.T("Your layout from before"), L.F("Saved {0}, when you picked a setup", saved.ToString("g", L.Culture)), Button(L.T("Bring it back"), () =>
            {
                if (MessageBox.Show(this, L.T("Bring back the pages and theme you had before the setup? The setup's pages are removed."), "Shoji", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;
                if (Change(c => Setups.Restore(c), rebuild: true))
                    _page = 0;
                Rebuild();
            }), last: true, icon: "");
        }
    }

    Border SetupCard(SetupDef setup)
    {
        var t = Theme.Find(setup.Theme);
        Brush Solid(string c) => Theme.Freeze(new SolidColorBrush(Theme.Parse(c)));
        Brush bg = t.Background2 == null ? Solid(t.Background) : new LinearGradientBrush(Theme.Parse(t.Background), Theme.Parse(t.Background2), 90);

        // The pages as small tiles in the theme's own colours, each with the icons of what is on it.
        var pages = new Grid { Margin = new Thickness(8) };
        for (int i = 0; i < setup.Pages.Length; i++)
        {
            pages.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var type in ModuleCatalog.Tiles(setup.Pages[i].Tiles).Select(m => m.Type).Distinct().Take(8))
            {
                var icon = SettingsIcons.Tile(SettingsIcons.ForTile(type), 18);
                icon.Margin = new Thickness(0, 0, 3, 3);
                icons.Children.Add(icon);
            }
            string name = L.T(setup.Pages[i].Name);
            var mini = new Border
            {
                Background = Solid(t.Tile), BorderBrush = Solid(t.TileGlow ? t.Accent : t.TileBorder ?? t.Tile),
                BorderThickness = new Thickness(t.TileBorder != null || t.TileGlow ? 1 : 0),
                CornerRadius = new CornerRadius(t.Shape is TileShape.Hud or TileShape.Chamfer or TileShape.Drafting ? 0 : Math.Min(t.Radius, 9)),
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(3),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = t.UpperTitles ? name.ToUpper(L.Culture) : name, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Solid(t.Accent), TextTrimming = TextTrimming.CharacterEllipsis },
                        icons,
                    },
                },
            };
            Grid.SetColumn(mini, i);
            pages.Children.Add(mini);
        }
        var preview = new Border { Height = 104, CornerRadius = new CornerRadius(10), Background = bg, ClipToBounds = true, Child = pages };

        var title = new DockPanel { Margin = new Thickness(6, 9, 6, 0) };
        var theme = Pill(t.Name, PillKind.Neutral, small: true);
        DockPanel.SetDock(theme, Dock.Right);
        title.Children.Add(theme);
        title.Children.Add(new TextBlock { Text = L.T(setup.Name), FontWeight = FontWeights.SemiBold });
        var body = new StackPanel
        {
            Children =
            {
                preview, title,
                Ref(new TextBlock { Text = L.T(setup.Description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Height = 50, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 2, 6, 4), ToolTip = L.T(setup.Description) }, TextBlock.ForegroundProperty, TextSecondary),
            },
        };
        var card = HoverCard(body, 300, new Thickness(6));
        card.Margin = new Thickness(0, 0, 14, 14);
        card.CornerRadius = new CornerRadius(16);
        card.Cursor = Cursors.Hand;
        Clickable(card, () =>
        {
            string question = L.F("Use the {0} setup? It replaces your pages with its own ({1}) and switches the theme to {2}. The layout you have now is saved: \"Bring it back\" on this page restores it.",
                L.T(setup.Name), string.Join(", ", setup.Pages.Select(p => L.T(p.Name))), t.Name);
            if (MessageBox.Show(this, question, "Shoji", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            try
            {
                if (Change(c => Setups.Apply(c, setup), rebuild: true))
                    _page = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.F("The setup wasn't applied, because your current layout couldn't be saved first: {0}", ex.Message), "Shoji", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Rebuild();
        });
        return card;
    }
}
