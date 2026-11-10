using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassroomCaption.App;
using ClassroomCaption.Core;

internal static class FloatingCaptionTests
{
    [DllImport("user32.dll")]private static extern nint SendMessage(nint hwnd,int message,nint w,nint l);
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static object? Call(object target,string method,params object[] args)=>target.GetType().GetMethod(method,Private)!.Invoke(target,args);
    private static BitmapSource Render(FrameworkElement content,double width,double height)
    {
        content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var image=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(width)),Math.Max(1,(int)Math.Ceiling(height)),96,96,PixelFormats.Pbgra32);image.Render(content);return image;
    }
    private static CaptionState State(string suffix="")=>new([], [new(1,"Сегодня мы рассмотрим остойчивость судна."+suffix,
        "今天我们讨论船舶稳性，以及重心位置的影响。"+suffix,false,false,DateTimeOffset.UtcNow,[1])],"",0,1);
    private static OverlayWindow Overlay(SubtitleActivity activity=SubtitleActivity.Stopped,bool locked=false)
    {
        var overlay=new OverlayWindow(new OverlaySettings {ClickThrough=false,Locked=locked});overlay.SetCaption(State(),activity);
        overlay.PrepareTransition();Render((FrameworkElement)overlay.Content,overlay.Width,overlay.Height);
        ((OutlinedText)overlay.FindName("RussianCaption")).AdvanceAnimation(5);((OutlinedText)overlay.FindName("ChineseCaption")).AdvanceAnimation(5);
        Render((FrameworkElement)overlay.Content,overlay.Width,overlay.Height);return overlay;
    }
    private static void Pump(Task task,Action? tick=null)
    {
        if(task.IsCompleted){task.GetAwaiter().GetResult();return;}
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(16)};var watch=Stopwatch.StartNew();Exception? error=null;
        timer.Tick+=(_,_)=>
        {
            try{tick?.Invoke();}catch(Exception ex){error=ex;timer.Stop();frame.Continue=false;return;}
            if(task.IsCompleted||watch.Elapsed.TotalSeconds>8){timer.Stop();frame.Continue=false;}
        };
        timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        if(error is not null)throw error;
        if(!task.IsCompleted)
        {
            var active=Application.Current.Windows.Cast<Window>().OfType<CaptionTransitionWindow>().Select(w=>$"visible={w.IsVisible}, handle={new WindowInteropHelper(w).Handle}, progress={w.Scene.Progress:F3}");
            throw new Exception("Transition did not finish: "+string.Join("; ",active));
        }
        task.GetAwaiter().GetResult();
    }
    public static int Run(bool startsOnly=false,bool compatibilityOnly=false)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var failures=0;var total=0;
        void Check(string name,Action action){if(compatibilityOnly!=name.StartsWith("Captions and ball morphs"))return;if(startsOnly&&!name.StartsWith("Live start"))return;total++;try{action();Console.WriteLine("PASS "+name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex);}}
        Check("Captions and ball morphs stay animated when Windows client animations are disabled",()=>
        {
            // Only override WPF's in-process cache; never change Windows settings.
            var original=SystemParameters.ClientAreaAnimation;var flags=BindingFlags.Static|BindingFlags.NonPublic;
            var field=typeof(SystemParameters).GetField("_clientAreaAnimation",flags)!;
            var valid=(System.Collections.BitArray)typeof(SystemParameters).GetField("_cacheValid",flags)!.GetValue(null)!;
            var slots=typeof(SystemParameters).GetNestedType("CacheSlot",BindingFlags.NonPublic)!;
            var slot=(int)Enum.Parse(slots,"ClientAreaAnimation");var wasValid=valid[slot];
            try
            {
                field.SetValue(null,false);valid[slot]=true;
                if(SystemParameters.ClientAreaAnimation)throw new Exception("Disabled-system-animation fixture failed");
                using var cancellation=new CancellationTokenSource();
                var transition=new CaptionTransitionWindow(null,new Rect(0,0,300,100),new Point(500,0),cancellation.Token,true);
                new WindowInteropHelper(transition).EnsureHandle();
                var task=transition.RunAsync(false);
                if(task.IsCompleted||transition.Scene.Progress>=1)throw new Exception("Ball morph jumped to its endpoint");
                Pump(task,()=>Render(transition.Scene,transition.Width,transition.Height));
                if(transition.Scene.Progress!=1)throw new Exception("Morph failed to complete");transition.Close();
                foreach(var activity in new[] {SubtitleActivity.Listening,SubtitleActivity.Demo})
                {
                    var overlay=new OverlayWindow(new());overlay.SetCaption(State(),activity);overlay.PrepareTransition();
                    Render((FrameworkElement)overlay.Content,overlay.Width,overlay.Height);
                    var ru=(OutlinedText)overlay.FindName("RussianCaption");var zh=(OutlinedText)overlay.FindName("ChineseCaption");
                    if(!ru.IsAnimating||!zh.IsAnimating||zh.StableGlyphCount>=zh.Text.Length)throw new Exception("Captions appeared instantly");
                    ru.AdvanceAnimation(8);zh.AdvanceAnimation(8);Render((FrameworkElement)overlay.Content,overlay.Width,overlay.Height);
                    if(ru.IsAnimating||zh.IsAnimating)throw new Exception("Reveal never completed");overlay.Close();
                }
            }
            finally{field.SetValue(null,original);valid[slot]=wasValid;}
        });
        Check("Tray start/stop remains available while caption and ball context menus are removed",()=>
        {
            foreach(var running in new[] {false,true})
            {
                var toggled=0;var menu=TrayIcon.CreateTranslationMenu(()=>toggled++,()=>{},running,false);
                var item=(MenuItem)menu.Items[0];if(!Equals(item.Header,running?"停止翻译":"开始翻译"))throw new Exception("Wrong tray state");
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));if(toggled!=1)throw new Exception("Wrong tray action");
            }
            if(((MenuItem)TrayIcon.CreateTranslationMenu(()=>{},()=>{},false,true).Items[0]).IsEnabled)throw new Exception("Repeated operation allowed");
        });
        Check("Double click works on native demo/live caption drag regions and locked client regions; right click does nothing",()=>
        {
            foreach(var activity in new[] {SubtitleActivity.Demo,SubtitleActivity.Listening})foreach(var locked in new[] {false,true})
            {
                var overlay=Overlay(activity,locked);var requests=0;overlay.StopTranslationRequested+=()=>requests++;
                var hwnd=new WindowInteropHelper(overlay).Handle;var hit=new Point();var found=false;
                for(var y=0;y<150&&!found;y+=3)for(var x=10;x<400;x+=3)if(overlay.CaptionHitTest(new Point(x,y))==(locked?1:2)){hit=new Point(x,y);found=true;break;}
                if(!found)throw new Exception("Caption fixture has no hit region");
                SendMessage(hwnd,0xA4,2,0);SendMessage(hwnd,0xA5,2,0);SendMessage(hwnd,0x7B,hwnd,-1);
                if(requests!=0||overlay.ContextMenu is not null)throw new Exception("Caption right-click still performs an action");
                if(locked)
                {
                    var dpi=VisualTreeHelper.GetDpi(overlay);var packed=((int)(hit.Y*dpi.DpiScaleY)<<16)|((int)(hit.X*dpi.DpiScaleX)&0xffff);
                    SendMessage(hwnd,0x203,1,new nint(packed));
                }
                else SendMessage(hwnd,0xA3,2,0);
                if(requests!=1||overlay.IsVisible)throw new Exception("Caption double click failed or showed a window");overlay.Close();
            }
        });
        Check("Ball single click is idle; double click resumes once; busy and right-click paths cannot start",()=>
        {
            var blocked=false;var dock=new CaptionDockWindow(()=>blocked);var requested=0;dock.StartRequested+=()=>requested++;
            var hwnd=new WindowInteropHelper(dock).EnsureHandle();
            SendMessage(hwnd,0x201,1,0);SendMessage(hwnd,0x202,0,0);if(requested!=0)throw new Exception("Single click starts translation");
            SendMessage(hwnd,0x205,0,0);SendMessage(hwnd,0x7B,hwnd,-1);if(requested!=0||dock.ContextMenu is not null)throw new Exception("Ball right-click still starts translation");
            SendMessage(hwnd,0x203,1,0);if(requested!=1)throw new Exception("Double click did not resume");
            blocked=true;SendMessage(hwnd,0x203,1,0);if(requested!=1)throw new Exception("Busy double click started another transport");
            if(dock.IsVisible||dock.Width!=48||dock.Height!=48)throw new Exception("Ball probe has wrong native geometry");dock.Close();
        });
        Check("Morph has identical endpoint positions and continuous geometry at every gather/roll blend boundary",()=>
        {
            foreach(var source in new[] {new Rect(20,60,1100,180),new Rect(-1530,-120,900,90),new Rect(40,30,1900,200)})
            {
                var work=new Rect(source.Left-20,source.Top-20,Math.Max(1200,source.Width+40),900);
                var dock=CaptionDockWindow.DockPosition(work,new Point(source.Left+source.Width/2,source.Top),true);
                var center=dock+new Vector(24,24);
                var start=CaptionMorphVisual.Pose(source,center,0);var end=CaptionMorphVisual.Pose(source,center,1);
                if(start.Bounds!=source||start.ImageOpacity!=1||end.Bounds!=new Rect(dock,new Size(48,48))||end.SurfaceOpacity!=1||end.ImageOpacity!=0)throw new Exception("A morph endpoint teleports during handoff");
                foreach(var boundary in new[] {.16,.30,.50,.55,.62,.70})
                {
                    var before=CaptionMorphVisual.Pose(source,center,boundary-.000001);var after=CaptionMorphVisual.Pose(source,center,boundary+.000001);
                    if((before.Bounds.TopLeft-after.Bounds.TopLeft).Length>.03||Math.Abs(before.Bounds.Width-after.Bounds.Width)>.03||Math.Abs(before.ImageOpacity-after.ImageOpacity)>.0001)throw new Exception("Gather/roll phase has a discontinuity");
                }
                for(var i=0;i<=240;i++)
                {
                    var pose=CaptionMorphVisual.Pose(source,center,i/240.0);
                    if(Math.Abs(pose.ImageOpacity+pose.SurfaceOpacity-1)>.000001||pose.Bounds.Width<47.999||pose.Bounds.Height<=0)throw new Exception("Transition loses its visible surface");
                }
            }
        });
        Check("Real morph timelines keep native windows fixed and restore the exact cached bilingual presentation",()=>
        {
            var overlay=Overlay();var ru=(OutlinedText)overlay.FindName("RussianCaption");var zh=(OutlinedText)overlay.FindName("ChineseCaption");
            var oldRussian=ru.Text;var oldChinese=zh.Text;var dock=new CaptionDockWindow(()=>false);var sampled=0;
            Rect? stage=null;CaptionTransitionWindow? last=null;
            void Observe()
            {
                if(dock.IsVisible||overlay.IsVisible)throw new Exception("Offscreen check showed a desktop window");
                if(dock.Width!=48||dock.Height!=48)throw new Exception("The interactive HWND resized during a morph");
                var transition=dock.Transition;if(transition is null)return;
                if(!ReferenceEquals(last,transition)){last=transition;stage=new Rect(transition.Left,transition.Top,transition.Width,transition.Height);}
                if(stage!=new Rect(transition.Left,transition.Top,transition.Width,transition.Height))throw new Exception("Transition HWND moved or resized mid-animation");
                Render(transition.Scene,transition.Width,transition.Height);sampled++;
            }
            Pump(dock.CollapseAsync(overlay,false),Observe);var saved=dock.SavedPresentation!;
            overlay.SetCaption(State("新识别内容"),SubtitleActivity.Listening);overlay.SetListeningFeedback("正在连接");
            if(ru.Text!=oldRussian||zh.Text!=oldChinese)throw new Exception("Paused text changed before unfolding");
            Pump(dock.ExpandAsync(overlay,false),Observe);
            if(!ReferenceEquals(saved,dock.SavedPresentation)||ru.Text!=oldRussian||zh.Text!=oldChinese)throw new Exception("Unfolding replaced or cleared the last presentation");
            overlay.ResumeReading();Render((FrameworkElement)overlay.Content,overlay.Width,overlay.Height);
            if(!ru.Text.StartsWith(oldRussian)||!zh.Text.StartsWith(oldChinese)||zh.StableGlyphCount<oldChinese.Length)throw new Exception("New content failed to continue the restored text");
            Console.WriteLine($"Observed {sampled} real transition frames without moving/resizing an HWND");dock.Close();overlay.Close();
        });
        Check("Startup creates exactly one right-edge ball without starting a classroom, and stopping hidden captions keeps that ball",()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"caption-idle-ball-"+Guid.NewGuid().ToString("N"));
            var window=new MainWindow(new SettingsStore(root)) {PresentFloatingWindows=false};
            window.InitializeFloatingBall();var ball=Field<CaptionDockWindow>(window,"captionBall");
            window.InitializeFloatingBall();
            if(!ReferenceEquals(ball,Field<CaptionDockWindow>(window,"captionBall"))||window.Running||ball.IsVisible||Field<SessionStore?>(window,"session") is not null)throw new Exception("Opening the app duplicates balls or starts translation");
            var work=PanelWindowPlacement.WorkArea(window);
            if(Math.Abs(ball.Left-(work.Right-56))>1||!work.Contains(new Rect(ball.Left,ball.Top,48,48)))throw new Exception("Startup ball is not on the right edge");
            Pump((Task)Call(window,"StopFloatingAsync")!);
            if(!ReferenceEquals(ball,Field<CaptionDockWindow>(window,"captionBall")))throw new Exception("Idle stop destroys the ball");
            ball.Close();Field<DispatcherTimer>(window,"refresh").Stop();Field<DispatcherTimer>(window,"noticeTimer").Stop();
        });
        Check("Live start automatically attempts the floating path once after network becomes available", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "caption-auto-start-" + Guid.NewGuid().ToString("N"));
            var online = false;
            var checks = 0;
            var window = new MainWindow(new SettingsStore(root), _ =>
            {
                checks++;
                return Task.FromResult(online);
            }) { PresentFloatingWindows = false };
            window.InitializeFloatingBall();
            try
            {
                Pump(WaitForDevicesAsync());
                Pump((Task)Call(window, "TryAutoStartAsync")!);
                if (checks != 1 || Field<bool>(window, "autoStartConsumed"))
                    throw new Exception("Offline check consumed the automatic start");
                online = true;
                Pump((Task)Call(window, "TryAutoStartAsync")!);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (checks != 2 || ((TextBlock)window.FindName("NoticeText")).Text != "请先在连接设置中填写密钥。")
                    throw new Exception("Online check did not reach the real translation start validation");
                online = false;
                Pump((Task)Call(window, "TryAutoStartAsync")!);
                online = true;
                Pump((Task)Call(window, "TryAutoStartAsync")!);
                if (checks != 2 || window.Running)
                    throw new Exception("Later connectivity changes triggered another auto-start");
                async Task WaitForDevicesAsync()
                {
                    while (Field<bool>(window, "refreshingDevices")) await Task.Delay(10);
                }
            }
            finally
            {
                Field<CaptionDockWindow?>(window, "captionBall")?.Close();
                Field<DispatcherTimer>(window, "refresh").Stop();
                Field<DispatcherTimer>(window, "noticeTimer").Stop();
            }
        });
        foreach(var entry in new[] {"ball","button","tray"})foreach(var online in new[] {false,true})
        Check("Live start from "+entry+" after demo pause checks "+(online?"credentials":"network")+" instead of replaying demo",()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"caption-live-start-"+Guid.NewGuid().ToString("N"));
            var checks=0;
            var window=new MainWindow(new SettingsStore(root),_=>{checks++;return Task.FromResult(online);}) {PresentFloatingWindows=false};
            try
            {
                window.StartDemo();Call(window,"RefreshCaptions");
                Pump((Task)Call(window,"StopFloatingAsync")!);
                var ball=Field<CaptionDockWindow>(window,"captionBall");
                if(entry=="ball")
                {
                    var hwnd=new WindowInteropHelper(ball).EnsureHandle();
                    SendMessage(hwnd,0x203,1,0);
                    Pump(WaitForTransition());
                }
                else if(entry=="tray")Pump((Task)Call(window,"ToggleFloatingAsync")!);
                else {Call(window,"StartClick",window,new RoutedEventArgs());Pump(WaitForTransition());}
                var expected=online?"请先在连接设置中填写密钥。":"未联网";
                if(checks!=1||window.Running||Field<Task?>(window,"demoTask") is not null)
                    throw new Exception("Start bypassed live checks or resumed the demo");
                if(((TextBlock)window.FindName("NoticeText")).Text!=expected)
                    throw new Exception("Live startup failure is not visible");
                if(!ReferenceEquals(ball,Field<CaptionDockWindow>(window,"captionBall"))||Field<bool>(window,"floatingTransition"))
                    throw new Exception("Failed live start lost its retryable ball");
                async Task WaitForTransition(){while(Field<bool>(window,"floatingTransition"))await Task.Delay(10);}
            }
            finally
            {
                Pump((Task)Call(window,"StopRunAsync","test",false)!);
                Field<CaptionDockWindow?>(window,"captionBall")?.Close();Field<OverlayWindow?>(window,"overlay")?.Close();
                Field<DispatcherTimer>(window,"refresh").Stop();Field<DispatcherTimer>(window,"noticeTimer").Stop();
            }
        });
        Check("Interrupted transition cancels promptly rather than leaving a hidden large window alive",()=>
        {
            var overlay=Overlay();var dock=new CaptionDockWindow(()=>false);var task=dock.CollapseAsync(overlay,false);dock.Close();
            try{Pump(task);throw new Exception("Cancellation was ignored");}catch(OperationCanceledException){ }
            if(dock.Transition is not null)throw new Exception("Canceled transition retained its native window");overlay.Close();
        });
        Check("Deterministic full transition preview uses the actual WPF renderer in both directions",()=>RenderPreview());
        Console.WriteLine($"{total-failures}/{total} floating caption checks passed");return failures==0?0:1;
    }
    private static void RenderPreview()
    {
        var overlay=Overlay();var snapshot=CaptionVisualSnapshot.Capture(overlay);
        var dock=new Point(snapshot.Bounds.Right+180,snapshot.Bounds.Top+snapshot.Bounds.Height/2-24);
        var stage=Rect.Union(snapshot.Bounds,new Rect(dock,new Size(48,48)));stage.Inflate(3,3);
        var scene=new CaptionMorphVisual(snapshot,snapshot.Bounds,stage,dock+new Vector(24,24));
        var directory="artifacts/continuous-morph";Directory.CreateDirectory(directory);
        for(var frame=0;frame<=120;frame++)
        {
            scene.Progress=frame<=60?frame/60.0:1-(frame-60)/60.0;
            var image=Render(scene,stage.Width,stage.Height);var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(237,239,239)),null,new Rect(0,0,1000,320));
                var scale=Math.Min(960/stage.Width,270/stage.Height);var bounds=new Rect((1000-stage.Width*scale)/2,(320-stage.Height*scale)/2,stage.Width*scale,stage.Height*scale);
                dc.DrawImage(image,bounds);
            }
            var bitmap=new RenderTargetBitmap(1000,320,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create($"{directory}/frame-{frame:000}.png");encoder.Save(file);
        }
        overlay.Close();Console.WriteLine("Rendered 121 deterministic WPF frames: 60 steps to ball, 60 steps back to saved subtitles");
    }
}
