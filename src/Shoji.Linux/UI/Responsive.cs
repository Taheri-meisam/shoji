using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Shoji.UI;

/// <summary>How much room a tile has, decided from its own size (like a CSS container query).</summary>
public enum Density { Compact, Normal, Roomy }

/// <summary>
/// Tiles show less when they're small instead of shrinking everything: an element marked
/// <c>.ShowFrom(Density.Normal)</c> is hidden while its tile is compact. Same rules as on Windows.
/// </summary>
public static class Responsive
{
    public static readonly AttachedProperty<Density?> ShowFromProperty =
        AvaloniaProperty.RegisterAttached<Control, Density?>("ShowFrom", typeof(Responsive));

    public static T ShowFrom<T>(this T element, Density minimum) where T : Control
    {
        element.SetValue(ShowFromProperty, minimum);
        return element;
    }

    public static Density For(double width, double height) =>
        width < 240 || height < 165 ? Density.Compact
        : width >= 460 && height >= 300 ? Density.Roomy
        : Density.Normal;

    public static void Apply(ILogical root, Density density)
    {
        foreach (var child in root.GetLogicalChildren())
        {
            if (child is Control c && c.GetValue(ShowFromProperty) is Density min)
                c.IsVisible = density >= min;
            Apply(child, density);
        }
    }
}
