using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

public sealed class AppearanceWindow : Window
{
    public AppearanceWindow(OverlaySettings settings, Action apply)
    {
        var panel=new StackPanel {Margin=new(25,10,25,14)};
        var preview=new StackPanel();
        var russian=new OutlinedText {Text="Сегодня мы рассмотрим остойчивость судна.",TextBackdrop=true,Outline=false,Weight=FontWeights.Normal,Margin=new(0,0,0,10)};
        var chinese=new OutlinedText {Text="今天，我们讨论船舶稳性。",TextBackdrop=true,Outline=false,Weight=FontWeights.Normal,FontFamily=CaptionTypography.Chinese};
        preview.Children.Add(russian);preview.Children.Add(chinese);
        panel.Children.Add(new Border {Background=UiKit.Brush("#C6D0D5"),CornerRadius=new(12),Padding=new(16),Child=preview,Margin=new(0,0,0,18)});
        void Refresh()
        {
            russian.TextSize=settings.RussianSize*.65;chinese.TextSize=settings.ChineseSize*.65;
            russian.LineHeight=russian.TextSize*1.6;chinese.LineHeight=chinese.TextSize*1.5;
            foreach(var text in new[] {russian,chinese})
            {
                text.LightBackdrop=settings.LightTextBackground;
                text.BackdropOpacity=settings.TextBackgroundOpacity;
                text.Color=settings.LightTextBackground?"#000000":"#FFFFFF";
                text.InvalidateVisual();
            }
        }
        var controls=new StackPanel();
        var colors=new StackPanel {Orientation=Orientation.Horizontal,Margin=new(0,0,0,12)};
        foreach(var light in new[] {false,true})
        {
            var option=new RadioButton {Content=light?"白底黑字":"黑底白字",GroupName="TextBackground",IsChecked=settings.LightTextBackground==light,
                Style=(Style)FindResource("CaptionBackgroundChoice"),Margin=new(0,0,16,0)};
            AutomationProperties.SetName(option,light?"白底黑字":"黑底白字");
            option.Checked+=(_,_)=>{settings.LightTextBackground=light;Refresh();apply();};
            colors.Children.Add(option);
        }
        controls.Children.Add(colors);
        void Slider(string title,double value,double minimum,double maximum,Action<double> change,string suffix="")
        {
            var row=new DockPanel {Margin=new(0,7,0,6)};
            var number=UiKit.Label($"{value:F0}{suffix}",12,"#B3DED3");DockPanel.SetDock(number,Dock.Right);row.Children.Add(number);row.Children.Add(new TextBlock {Text=title});controls.Children.Add(row);
            var slider=new Slider {Minimum=minimum,Maximum=maximum,Value=value,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new(0,0,0,7)};
            AutomationProperties.SetName(slider,title);
            if(title=="底色透明度")slider.ToolTip="0% 完全不透明，100% 隐藏底色；文字保持不透明。";
            slider.ValueChanged+=(_,_)=>{change(slider.Value);number.Text=$"{slider.Value:F0}{suffix}";Refresh();apply();};controls.Children.Add(slider);
        }
        Slider("中文大小",settings.ChineseSize,20,90,v=>settings.ChineseSize=v);
        Slider("俄语大小",settings.RussianSize,12,60,v=>settings.RussianSize=v);
        Slider("底色透明度",(1-settings.TextBackgroundOpacity)*100,0,100,v=>settings.TextBackgroundOpacity=1-v/100,"%");
        var extras=new StackPanel();
        void Check(string title,bool value,Action<bool> change)
        {
            var box=new CheckBox {Content=title,IsChecked=value};
            box.Click+=(_,_)=>{change(box.IsChecked==true);apply();};extras.Children.Add(box);
        }
        Check("固定位置和大小",settings.Locked,v=>settings.Locked=v);
        Check("点击字幕时操作下方课件",settings.ClickThrough,v=>settings.ClickThrough=v);
        controls.Children.Add(new Expander {Header="交互选项",Content=extras});
        panel.Children.Add(UiKit.Card(controls));Refresh();
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(25,10,25,20)};
        footer.Children.Add(UiKit.Button("完成",(_,_)=>Close(),true));
        UiKit.Shell(this,"字幕外观",new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},470,650,footer);
    }
}
