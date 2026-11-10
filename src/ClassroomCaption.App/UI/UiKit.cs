using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Shapes;

namespace ClassroomCaption.App;

public static class UiKit
{
    public static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    public static System.Windows.Shapes.Path Icon(string name, double size = 18) => new()
    {
        Data = (Geometry)System.Windows.Application.Current.FindResource(name + "Icon"),
        Width = size, Height = size, Stretch = Stretch.Uniform,
        Stroke = Brush("#EEF4F2"), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
    };
    public static TextBlock Label(string text, double size = 12, string color = "#ADC0C1") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    public static Border Card(UIElement content, double padding = 18) => new() { Background = Brush("#1B2C35"), BorderBrush = Brush("#354A53"), BorderThickness = new(1), CornerRadius = new(12), Padding = new(padding), Child = content };
    public static Button Button(string text, RoutedEventHandler click, bool primary = false)
    {
        var b = new Button { Content = text, Style = (Style)System.Windows.Application.Current.FindResource(primary ? "Primary" : typeof(Button)) };
        b.Click += click; return b;
    }
    public static void Shell(Window window, string title, UIElement body, double width, double height, UIElement? footer = null)
    {
        window.Title = title; window.Width = width; window.Height = Math.Min(height, SystemParameters.WorkArea.Height - 32); window.MinWidth = width - 80; window.MinHeight = 480;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 50, ResizeBorderThickness = new(6), GlassFrameThickness = new(0), CornerRadius = new(16) });
        var frame = new Border { CornerRadius = new(14), BorderBrush = Brush("#31454D"), BorderThickness = new(1), Background = Brush("#101D24") };
        System.Windows.Documents.TextElement.SetForeground(frame, Brush("#EEF4F2"));
        var grid = new Grid(); frame.Child = grid;
        grid.RowDefinitions.Add(new() { Height = new GridLength(52) }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new(22, 8, 12, 6) };
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Style = (Style)window.FindResource("IconButton"), Content = Icon("X", 15), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "关闭" };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += (_, _) => window.Close(); header.Children.Add(close); grid.Children.Add(header);
        Grid.SetRow(body, 1); grid.Children.Add(body);
        if (footer is not null) { Grid.SetRow(footer, 2); grid.Children.Add(footer); }
        window.Content = frame;
    }
}
