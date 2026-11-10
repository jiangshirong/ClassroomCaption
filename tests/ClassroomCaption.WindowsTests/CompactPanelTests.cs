using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.App;
using ClassroomCaption.Core;

internal static class CompactPanelTests
{
    private static void Layout(MainWindow window)
    {
        var content=(FrameworkElement)window.Content;content.Measure(new Size(window.Width,window.Height));content.Arrange(new Rect(0,0,window.Width,window.Height));content.UpdateLayout();
    }
    private static void Save(MainWindow window,string file)
    {
        Layout(window);var image=new RenderTargetBitmap((int)window.Width,(int)window.Height,96,96,PixelFormats.Pbgra32);image.Render((Visual)window.Content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create("artifacts/compact-panel/"+file);encoder.Save(stream);
    }
    private static void Call(MainWindow window,string method,params object[] args)=>typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args);
    public static int Run()
    {
        var total=0;var failed=0;var root=Path.Combine(Path.GetTempPath(),"caption-compact-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory("artifacts/compact-panel");
        void Check(string title,Action action){total++;try{action();Console.WriteLine("PASS "+title);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+title+": "+ex);}}
        WorkspaceRefinementTests.Run(Check,root);
        Check("Default panel has no blank reading canvas; device field and primary action fill its width",()=>
        {
            var window=new MainWindow(new SettingsStore(root));Layout(window);
            if(window.Width!=420||window.Height!=460||window.MinWidth>420||window.MinHeight>460||window.FindName("LiveReadingPane") is not null)throw new Exception("Old large workspace remains");
            var content=(FrameworkElement)window.Content;var device=(ComboBox)window.FindName("DeviceBox");var start=(Button)window.FindName("StartButton");
            var demo=(Button)window.FindName("DemoButton");var more=(Button)window.FindName("MoreButton");
            if(device.ActualWidth<350||start.ActualWidth<350||Math.Abs(demo.ActualWidth-more.ActualWidth)>1||Math.Abs(demo.ActualHeight-more.ActualHeight)>1)throw new Exception("Compact controls are not aligned or full width");
            foreach(var element in new FrameworkElement[] {device,start,demo,more,(FrameworkElement)window.FindName("PanelToolbar")})
            {
                var rect=element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                if(rect.Left<0||rect.Top<0||rect.Right>420||rect.Bottom>460)throw new Exception("Compact controls are clipped");
            }
            var box=(ComboBox)window.FindName("DeviceBox");box.ItemsSource=new[] {new AudioInput("synthetic","麦克风阵列（适用于数字麦克风的英特尔® 智音技术）")};box.SelectedIndex=0;
            Save(window,"home.png");
            ((Border)window.FindName("NoticePanel")).Visibility=Visibility.Visible;((TextBlock)window.FindName("NoticeText")).Text="请连接麦克风后再开始。";Save(window,"notice.png");
            if(start.TranslatePoint(new Point(),content).Y<device.TranslatePoint(new Point(0,device.ActualHeight),content).Y)throw new Exception("Notice overlaps the microphone controls");
        });
        Check("Running panel keeps the stop action and lets More use the secondary row without an empty slot",()=>
        {
            var window=new MainWindow(new SettingsStore(root));Call(window,"SetRunningUi",true);Layout(window);
            var more=(Button)window.FindName("MoreButton");var stop=(Button)window.FindName("StopButton");
            if(more.ActualWidth<350||Grid.GetColumnSpan(more)!=3||stop.Visibility!=Visibility.Visible)throw new Exception("Running secondary row has an empty half");
            ((TextBlock)window.FindName("StateText")).Text="收音中";((TextBlock)window.FindName("SessionClock")).Text="12:34";Save(window,"running.png");
        });
        Check("Record expansion stays inside negative-coordinate and small monitor work areas",()=>
        {
            foreach(var work in new[] {new Rect(0,0,1366,728),new Rect(-1280,-100,1280,800),new Rect(80,0,944,700)})
            {
                var fitted=PanelWindowPlacement.Fit(new Rect(work.Right-420,work.Bottom-460,800,560),work);
                if(!work.Contains(fitted))throw new Exception("Expanded records leave the monitor work area");
            }
        });
        Check("Record preview reads long bilingual text and narrow resizing retains a usable record view",()=>
        {
            var window=new MainWindow(new SettingsStore(root));Call(window,"SetHistoryOpen",true);
            ((ComboBox)window.FindName("DeviceBox")).ItemsSource=new[] {new AudioInput("synthetic","麦克风阵列")};((ComboBox)window.FindName("DeviceBox")).SelectedIndex=0;
            var list=(ItemsControl)window.FindName("HistoryList");list.ItemsSource=new[] {new CaptionRow(1,"","Положение центра тяжести влияет на начальную остойчивость судна. При подъёме центра тяжести начальная остойчивость уменьшается.","重心位置会影响船舶的初稳性。重心升高时，初稳性通常会减小，因此需要考虑重物的放置位置。","#B5E1D5")};
            var pane=(Border)window.FindName("HistoryPane");pane.BeginAnimation(UIElement.OpacityProperty,null);pane.Opacity=1;((TextBlock)window.FindName("HistoryStatus")).Text="";
            Save(window,"records.png");window.Width=600;Layout(window);
            if(((Border)window.FindName("ControlRail")).Visibility!=Visibility.Collapsed||pane.ActualWidth<500)throw new Exception("Narrow records squeeze the content");
            Call(window,"SetHistoryOpen",false);if(window.Width!=420||window.Height!=460)throw new Exception("Narrow record close did not restore default size");
        });
        Console.WriteLine($"{total-failed}/{total} compact panel checks passed");return failed==0?0:1;
    }
}
