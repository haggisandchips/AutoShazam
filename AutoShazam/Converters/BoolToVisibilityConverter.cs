using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AutoShazam.Converters;

/// <summary>Bool → Visibility, with an optional "Invert" ConverterParameter to flip the result.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool result = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            result = !result;
        }

        return result ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
