// The shared theme list (Shoji/UI/Themes.cs) names font weights the WPF way ("FontWeights.Light").
// This gives it the same names on Linux; Theme converts them to Avalonia's FontWeight.
namespace System.Windows;

public readonly record struct FontWeight(int Weight)
{
    public Avalonia.Media.FontWeight ToAvalonia() => (Avalonia.Media.FontWeight)Weight;
}

public static class FontWeights
{
    public static FontWeight Thin => new(100);
    public static FontWeight ExtraLight => new(200);
    public static FontWeight Light => new(300);
    public static FontWeight Normal => new(400);
    public static FontWeight Regular => new(400);
    public static FontWeight Medium => new(500);
    public static FontWeight SemiBold => new(600);
    public static FontWeight Bold => new(700);
    public static FontWeight ExtraBold => new(800);
    public static FontWeight Black => new(900);
}
