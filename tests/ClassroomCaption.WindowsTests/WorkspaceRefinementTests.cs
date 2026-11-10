using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.App;
using ClassroomCaption.Core;

internal static class WorkspaceRefinementTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Call(MainWindow window,string method,params object[] args)
        => typeof(MainWindow).GetMethod(method,Private)!.Invoke(window,args);
    private static void Layout(MainWindow window,int width=1280,int height=800)
    {
        var content=(FrameworkElement)window.Content;
        content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32).Render(content);
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T result)yield return result;
            foreach(var descendant in Children<T>(child))yield return descendant;
        }
    }

    public static void Run(Action<string,Action> check,string root)
    {
        check("Compact panel expands records on demand and restores its original size and location",()=>
        {
            var window=new MainWindow(new SettingsStore(root)) {Left=40,Top=50};
            var size=new Size(window.Width,window.Height);var position=new Point(window.Left,window.Top);
            Call(window,"SetHistoryOpen",true);Layout(window,(int)window.Width,(int)window.Height);
            var pane=(Border)window.FindName("HistoryPane");var controls=(Border)window.FindName("ControlRail");var content=(FrameworkElement)window.Content;
            if(window.Width<760||pane.ActualWidth<300||((ToggleButton)window.FindName("HistoryButton")).IsChecked!=true)throw new Exception("History did not expand or show its state");
            var a=controls.TransformToAncestor(content).TransformBounds(new Rect(controls.RenderSize));
            var b=pane.TransformToAncestor(content).TransformBounds(new Rect(pane.RenderSize));
            if(a.IntersectsWith(b))throw new Exception("History covers the controller");
            ((Button)window.FindName("HistoryCloseButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Layout(window,(int)window.Width,(int)window.Height);
            if(new Size(window.Width,window.Height)!=size||new Point(window.Left,window.Top)!=position||pane.Visibility!=Visibility.Collapsed||((ToggleButton)window.FindName("HistoryButton")).IsChecked!=false)throw new Exception("History close did not restore the compact panel");
        });
        check("Long Russian and Chinese records wrap fully within their pane",()=>
        {
            var window=new MainWindow(new SettingsStore(root));Call(window,"SetHistoryOpen",true);
            var russian=string.Join(" ",Enumerable.Repeat("Положение центра тяжести влияет на начальную остойчивость судна.",4));
            var chinese=string.Concat(Enumerable.Repeat("重心升高时船舶的初稳性会减小，需要考虑重物的位置。",4));
            var list=(ItemsControl)window.FindName("HistoryList");list.ItemsSource=new[] {new CaptionRow(1,"",russian,chinese,"#B5E1D5")};
            foreach(var width in new[] {1280,820})
            {
                Layout(window,width);
                foreach(var text in new[] {russian,chinese})
                {
                    var label=Children<TextBlock>(list).Single(t=>t.Text==text);
                    if(label.TextWrapping!=TextWrapping.Wrap||label.TextTrimming!=TextTrimming.None||label.ActualHeight<label.LineHeight*2||label.ActualWidth>list.ActualWidth+1)
                        throw new Exception("A recorded sentence was truncated instead of wrapping");
                }
                if(((ScrollViewer)window.FindName("HistoryScroll")).ExtentWidth>list.ActualWidth+1)throw new Exception("History requires horizontal scrolling");
            }
        });
        check("Workspace toggle states are visible and available to accessibility; hidden overlays reset their indicator",()=>
        {
            var window=new MainWindow(new SettingsStore(root));Layout(window);
            foreach(var name in new[] {"OverlayToggle","HistoryButton"})
            {
                var toggle=(ToggleButton)window.FindName(name);var off=((SolidColorBrush)toggle.Foreground).Color;
                toggle.IsChecked=true;Layout(window);
                var peer=new ToggleButtonAutomationPeer(toggle);
                if(((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).ToggleState!=ToggleState.On||((SolidColorBrush)toggle.Foreground).Color==off||((SolidColorBrush)toggle.Background).Color.A!=255)
                    throw new Exception("Selected toggle has no visual or accessible state");
                toggle.IsChecked=false;
            }
            var overlay=new OverlayWindow(new());typeof(MainWindow).GetField("overlay",Private)!.SetValue(window,overlay);
            var indicator=(ToggleButton)window.FindName("OverlayToggle");indicator.IsChecked=true;Call(window,"UpdateOverlayToggle");
            if(indicator.IsChecked!=false||!Equals(indicator.ToolTip,"显示悬浮字幕"))throw new Exception("Hidden overlay is reported as showing");
        });
        check("Compact microphone names preserve identity, complete hover details and stable duplicate distinctions",()=>
        {
            var source=new[] {
                new AudioInput("internal","麦克风阵列 (适用于数字麦克风的英特尔® 智音技术)"),
                new AudioInput("usb-b","麦克风 (USB Audio Device)"),new AudioInput("usb-a","麦克风 (USB Audio Device)"),
                new AudioInput("realtek","麦克风 (Realtek(R) Audio)",false,"已禁用"),new AudioInput("named","Blue Yeti"),
                new AudioInput("russian","Микрофон (USB Audio Device)")};
            var type=typeof(AudioInput).Assembly.GetType("ClassroomCaption.App.AudioInputNames")!;
            AudioInput[] Names(AudioInput[] inputs)=>((IReadOnlyList<AudioInput>)type.GetMethod("Distinguish")!.Invoke(null,[inputs])!).ToArray();
            var compact=Names(source);var reversed=Names(source.Reverse().ToArray());
            if(compact[0].DisplayName!="麦克风阵列"||compact[4].DisplayName!="Blue Yeti"||compact[5].DisplayName!="Микрофон")throw new Exception("Display names keep driver boilerplate or lose user names");
            if(compact.Select(d=>d.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=compact.Length)throw new Exception("Different microphones look identical");
            foreach(var input in compact)
            {
                var original=source.Single(d=>d.Id==input.Id);
                if(input.Name!=original.Name||input.CanRecord!=original.CanRecord||input.AccessibleName!=original.AccessibleName||input.DisplayName!=reversed.Single(d=>d.Id==input.Id).DisplayName)
                    throw new Exception("Name shortening changed identity, status, full details or refresh stability");
            }
        });
    }
}
