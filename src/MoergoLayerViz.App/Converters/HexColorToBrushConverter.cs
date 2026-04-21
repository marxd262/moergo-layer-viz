using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MoergoLayerViz.App.Converters;

/// <summary>
/// Converts a hex color string (e.g. "#FF0000") to an Avalonia SolidColorBrush.
/// Returns a dark gray fallback for invalid/empty values.
/// </summary>
public class HexColorToBrushConverter : IValueConverter
{
    public static readonly HexColorToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (!hex.StartsWith('#'))
                    hex = "#" + hex;
                return new SolidColorBrush(Color.Parse(hex));
            }
            catch
            {
                // Fall through to default
            }
        }

        return new SolidColorBrush(Color.Parse("#333333"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
