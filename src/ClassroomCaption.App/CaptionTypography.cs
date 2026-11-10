using System.Windows.Media;

namespace ClassroomCaption.App;

public static class CaptionTypography
{
    public static FontFamily Chinese { get; } = new(new Uri("pack://application:,,,/ClassroomCaption;component/"), "./Assets/Fonts/#Noto Sans SC");
}
