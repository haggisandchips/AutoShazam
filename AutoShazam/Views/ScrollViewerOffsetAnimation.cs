using System.Windows;
using System.Windows.Controls;

namespace AutoShazam.Views;

/// <summary>
/// ScrollViewer.VerticalOffset isn't a dependency property, so it can't be targeted directly by a
/// Storyboard/DoubleAnimation - this attached property is a thin animatable proxy that forwards
/// its value to the real ScrollViewer.ScrollToVerticalOffset on every change.
/// </summary>
public static class ScrollViewerOffsetAnimation
{
    public static readonly DependencyProperty VerticalOffsetProperty = DependencyProperty.RegisterAttached(
        "VerticalOffset",
        typeof(double),
        typeof(ScrollViewerOffsetAnimation),
        new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    public static void SetVerticalOffset(DependencyObject element, double value) => element.SetValue(VerticalOffsetProperty, value);

    public static double GetVerticalOffset(DependencyObject element) => (double)element.GetValue(VerticalOffsetProperty);

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }
}
