using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClassroomCaption.App;

// The interactive HWND is always 48 DIP. A separate fixed-bounds stage performs
// the morph; both ends hand over at exactly the same screen-space geometry.
internal sealed class CaptionDockWindow : Window
{
    private const double Diameter=CaptionMorphVisual.Diameter;
    private readonly Func<bool> blocked;
    private readonly CancellationTokenSource lifetime=new();
    private bool moving;
    private Point? press;
    private CaptionVisualSnapshot? saved;
    internal CaptionTransitionWindow? Transition {get;private set;}
    internal CaptionVisualSnapshot? SavedPresentation=>saved;
    public event Action? StartRequested;
    public CaptionDockWindow(Func<bool> blocked)
    {
        this.blocked=blocked;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        Width=Height=Diameter;ToolTip="双击继续翻译，拖动可移动";
        Content=new CaptionMorphVisual(null,new Rect(0,0,Diameter,Diameter),new Rect(0,0,Diameter,Diameter),new Point(Diameter/2,Diameter/2),true)
            {Progress=1,IsHitTestVisible=true};
        PreviewMouseLeftButtonDown+=(_,e)=>
        {
            if(moving||blocked())return;
            e.Handled=true;
            if(e.ClickCount>=2){ReleaseMouseCapture();press=null;RequestStart();return;}
            press=e.GetPosition(this);CaptureMouse();
        };
        MouseLeftButtonUp+=(_,_)=>{press=null;ReleaseMouseCapture();};
        MouseMove+=async (_,e)=>
        {
            if(press is not Point down||e.LeftButton!=MouseButtonState.Pressed||moving||blocked())return;
            var delta=e.GetPosition(this)-down;
            if(Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            press=null;ReleaseMouseCapture();
            try{DragMove();await SnapAsync();}catch(OperationCanceledException){ }catch(InvalidOperationException){ }
        };
        SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProc);
        Closed+=(_,_)=>{lifetime.Cancel();Transition?.Close();};
    }
    private void RequestStart(){if(!moving&&!blocked())StartRequested?.Invoke();}
    internal void ShowAtRight(Window owner,bool present=true)
    {
        var work=PanelWindowPlacement.WorkArea(owner);
        var position=DockPosition(work,new Point(work.Right-Diameter,work.Top+(work.Height-Diameter)/2),true);
        Left=position.X;Top=position.Y;if(present)Show();
    }
    private nint WindowProc(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(message==0x0203){press=null;ReleaseMouseCapture();RequestStart();handled=true;return 0;}
        if(message==0x007B){handled=true;return 0;}
        return 0;
    }
    internal async Task CollapseAsync(OverlayWindow source,bool present=true)
    {
        moving=true;source.FreezeReading();
        try
        {
            saved=CaptionVisualSnapshot.Capture(source);
            var center=new Point(saved.Bounds.Left+saved.Bounds.Width/2-Diameter/2,saved.Bounds.Top+saved.Bounds.Height/2-Diameter/2);
            var dock=DockPosition(WorkArea(source),center,true);
            Transition=new(saved,saved.Bounds,dock,lifetime.Token);
            if(present){await Transition.PresentAsync();source.Hide();Transition.Opacity=1;}
            await Transition.RunAsync(false);
            Left=dock.X;Top=dock.Y;
            if(present)await ShowBallAtHandoffAsync();
        }
        finally{Transition?.Close();Transition=null;moving=false;}
    }
    internal async Task ExpandAsync(OverlayWindow target,bool present=true)
    {
        moving=true;
        try
        {
            saved??=CaptionVisualSnapshot.Capture(target);
            Transition=new(saved,saved.Bounds,new Point(Left,Top),lifetime.Token);
            Transition.Scene.Progress=1;
            if(present){await Transition.PresentAsync();Hide();Transition.Opacity=1;}
            await Transition.RunAsync(true);
            if(present)
            {
                target.Opacity=0;target.Show();await CaptionTransitionWindow.WaitForFrameAsync(lifetime.Token);
                Transition.Opacity=0;target.Opacity=1;await CaptionTransitionWindow.WaitForFrameAsync(lifetime.Token);
            }
        }
        finally{Transition?.Close();Transition=null;moving=false;}
    }
    private async Task ShowBallAtHandoffAsync()
    {
        Opacity=0;Show();await CaptionTransitionWindow.WaitForFrameAsync(lifetime.Token);
        if(Transition is not null)Transition.Opacity=0;
        Opacity=1;await CaptionTransitionWindow.WaitForFrameAsync(lifetime.Token);
    }
    private async Task SnapAsync()
    {
        moving=true;
        try
        {
            var from=new Rect(Left,Top,Diameter,Diameter);var dock=DockPosition(WorkArea(this),from.TopLeft);
            if((dock-from.TopLeft).Length<.5)return;
            Transition=new(null,from,dock,lifetime.Token,true);
            await Transition.PresentAsync();Hide();Transition.Opacity=1;await Transition.RunAsync(false);
            Left=dock.X;Top=dock.Y;await ShowBallAtHandoffAsync();
        }
        finally{Transition?.Close();Transition=null;moving=false;}
    }
    internal static Point DockPosition(Rect work,Point position,bool right=false)
        =>new(right||position.X+Diameter/2>=work.Left+work.Width/2?Math.Max(work.Left,work.Right-Diameter-8):work.Left+8,
            Math.Clamp(position.Y,work.Top+8,Math.Max(work.Top+8,work.Bottom-Diameter-8)));
    private static Rect WorkArea(Window window)
    {
        var hwnd=new WindowInteropHelper(window).Handle;if(hwnd==0)return SystemParameters.WorkArea;
        var source=HwndSource.FromHwnd(hwnd);
        if(source?.CompositionTarget is null||!GetWindowRect(hwnd,out var native))return SystemParameters.WorkArea;
        var rect=NativeDisplays.FromWindow(hwnd).WorkingArea;var inverse=source.CompositionTarget.TransformFromDevice;
        return new(window.Left+(rect.Left-native.Left)*inverse.M11,window.Top+(rect.Top-native.Top)*inverse.M22,rect.Width*inverse.M11,rect.Height*inverse.M22);
    }
    [StructLayout(LayoutKind.Sequential)]private struct Bounds {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out Bounds bounds);
}
