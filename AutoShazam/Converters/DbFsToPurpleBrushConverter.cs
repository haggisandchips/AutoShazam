using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AutoShazam.Services.Audio;

namespace AutoShazam.Converters;

/// <summary>
/// Maps a dBFS level to a brush blended between the app's neutral secondary-text colour (at or
/// below <see cref="AudioLevelConstants.GlowFloorDbFs"/> - "not coloured") and a vivid purple (at/above
/// <see cref="AudioLevelConstants.FullBrightnessDbFs"/> - full intensity). Blending starts from a
/// floor rather than 0% so the very first sample above the floor is clearly visible rather than
/// fading in imperceptibly.
/// </summary>
public sealed class DbFsToPurpleBrushConverter : IValueConverter
{
    private const double MinVisibleBlend = 0.25;

    private static readonly Color BaseColor = (Color)ColorConverter.ConvertFromString("#9A9CA8");
    private static readonly Color PurpleColor = (Color)ColorConverter.ConvertFromString("#B24BF3");

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double dbFs = value is double d ? d : AudioLevelConstants.GlowFloorDbFs;

        double range = Math.Max(1, AudioLevelConstants.FullBrightnessDbFs - AudioLevelConstants.GlowFloorDbFs);
        double t = Math.Clamp((dbFs - AudioLevelConstants.GlowFloorDbFs) / range, 0.0, 1.0);

        double blend = t <= 0 ? 0 : MinVisibleBlend + ((1 - MinVisibleBlend) * t);

        byte r = (byte)(BaseColor.R + ((PurpleColor.R - BaseColor.R) * blend));
        byte g = (byte)(BaseColor.G + ((PurpleColor.G - BaseColor.G) * blend));
        byte b = (byte)(BaseColor.B + ((PurpleColor.B - BaseColor.B) * blend));

        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
