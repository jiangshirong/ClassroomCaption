using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClassroomCaption.App;

public static class Motion
{
    public static void Reveal(FrameworkElement element, double distance = 10, int milliseconds = 220)
    {
        if (!SystemParameters.ClientAreaAnimation) { element.Opacity = 1; return; }
        var translation = new TranslateTransform(0, distance);
        element.RenderTransform = translation;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        translation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(milliseconds)));
    }
    public static void Drawer(FrameworkElement element, bool open)
    {
        if (open)
        {
            element.Visibility = Visibility.Visible;
            if (!SystemParameters.ClientAreaAnimation) { element.Opacity = 1; return; }
            var transform = new TranslateTransform(28, 0); element.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(230)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        else
        {
            if (!SystemParameters.ClientAreaAnimation) { element.Visibility = Visibility.Collapsed; return; }
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            animation.Completed += (_, _) => { element.Visibility = Visibility.Collapsed; element.BeginAnimation(UIElement.OpacityProperty, null); };
            element.BeginAnimation(UIElement.OpacityProperty, animation);
        }
    }
}
