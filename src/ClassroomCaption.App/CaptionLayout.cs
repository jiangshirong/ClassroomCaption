using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClassroomCaption.App;
public static class CaptionLayout
{
    public static bool Fits(string text, double width, double height, double size, FontWeight weight, double lineHeight = 0, FontFamily? family = null)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family ?? new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, 1)
            { MaxTextWidth = Math.Max(20, width - 4) };
        if (double.IsFinite(lineHeight) && lineHeight > 0) formatted.LineHeight = lineHeight;
        return formatted.Height + 4 <= height;
    }
}
