using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AutoShazam.Services.Audio;

namespace AutoShazam.Converters;

/// <summary>
/// Maps a dBFS level to a brush blended between the app's neutral secondary-text colour (at or
/// below the configured silence threshold - "not coloured") and a vivid purple (at/above
/// <see cref="AudioLevelConstants.FullBrightnessDbFs"/> - full intensity). Blending starts from a
/// floor rather than 0% so the very first sample above the threshold is clearly visible rather
/// than fading in imperceptibly. Takes two bound values: [0] the live level, [1] the current
/// silence threshold (user-configurable), so the "not coloured" point always tracks whatever
/// threshold is actually in effect.
/// </summary>
public sealed class DbFsToPurpleBrushConverter : IMultiValueConverter
{
    private const double MinVisibleBlend = 0.25;

    private static readonly Color BaseColor = (Color)ColorConverter.ConvertFromString("#9A9CA8");
    private static readonly Color PurpleColor = (Color)ColorConverter.ConvertFromString("#B24BF3");

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double dbFs = values.Length > 0 && values[0] is double d ? d : AudioLevelConstants.SilenceThresholdDbFs;
        double threshold = values.Length > 1 && values[1] is double th ? th : AudioLevelConstants.SilenceThresholdDbFs;

        double range = Math.Max(1, AudioLevelConstants.FullBrightnessDbFs - threshold);
        double t = Math.Clamp((dbFs - threshold) / range, 0.0, 1.0);

        double blend = t <= 0 ? 0 : MinVisibleBlend + ((1 - MinVisibleBlend) * t);

        byte r = (byte)(BaseColor.R + ((PurpleColor.R - BaseColor.R) * blend));
        byte g = (byte)(BaseColor.G + ((PurpleColor.G - BaseColor.G) * blend));
        byte b = (byte)(BaseColor.B + ((PurpleColor.B - BaseColor.B) * blend));

        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
