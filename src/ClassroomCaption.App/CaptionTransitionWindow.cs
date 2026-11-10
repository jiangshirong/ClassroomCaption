using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClassroomCaption.App;

// The bounds stay fixed for the entire transition. Only a bitmap and a small
// vector shape animate within this window, then hand off at identical positions.
internal sealed class CaptionTransitionWindow : Window
{
    internal CaptionMorphVisual Scene {get;}
    internal Rect StageBounds {get;}
    private readonly CancellationToken token;
    private readonly double duration;
    internal CaptionTransitionWindow(CaptionVisualSnapshot? snapshot,Rect source,Point ballPosition,CancellationToken token,bool ballOnly=false)
    {
        this.token=token;
        var displacement=new Point(ballPosition.X+CaptionMorphVisual.Diameter/2,ballPosition.Y+CaptionMorphVisual.Diameter/2)-new Point(source.Left+source.Width/2,source.Top+source.Height/2);
        duration=.64+Math.Min(.36,displacement.Length/2400);
        var end=new Rect(ballPosition,new Size(CaptionMorphVisual.Diameter,CaptionMorphVisual.Diameter));
        var bounds=Rect.Union(source,end);bounds.Inflate(2,2);StageBounds=bounds;
        Left=bounds.Left;Top=bounds.Top;Width=bounds.Width;Height=bounds.Height;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;ShowActivated=false;Topmost=true;IsHitTestVisible=false;
        Scene=new(snapshot,source,bounds,new Point(end.Left+end.Width/2,end.Top+end.Height/2),ballOnly);
        Content=Scene;
        SourceInitialized+=(_,_)=>{var hwnd=new WindowInteropHelper(this).Handle;SetWindowLongPtr(hwnd,-20,new nint(GetWindowLongPtr(hwnd,-20).ToInt64()|0x20L|0x08000000L));};
    }
    internal async Task PresentAsync()
    {
        Opacity=0;Show();await WaitForFrameAsync(token);
    }
    internal async Task RunAsync(bool expand)
    {
        token.ThrowIfCancellationRequested();var from=expand?1d:0d;var to=1-from;Scene.Progress=from;
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration=token.Register(()=>done.TrySetCanceled(token));
        var animation=new DoubleAnimation(from,to,TimeSpan.FromSeconds(duration));
        animation.Completed+=(_,_)=>done.TrySetResult();
        Scene.BeginAnimation(CaptionMorphVisual.ProgressProperty,animation);
        try{await done.Task;}finally{Scene.Progress=to;Scene.BeginAnimation(CaptionMorphVisual.ProgressProperty,null);}
    }
    internal static async Task WaitForFrameAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var frames=0;
        EventHandler handler=(_,_)=>{if(++frames>=2)done.TrySetResult();};
        var timeout=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
        timeout.Tick+=(_,_)=>done.TrySetResult();
        using var registration=token.Register(()=>done.TrySetCanceled(token));
        CompositionTarget.Rendering+=handler;timeout.Start();
        try{await done.Task;}finally{timeout.Stop();CompositionTarget.Rendering-=handler;}
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
}
