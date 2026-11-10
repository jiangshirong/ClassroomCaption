using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

// One capture worker; only the latest immutable frame is retained. Pixels never leave memory.
public sealed class BackdropContrastSampler : IDisposable
{
    private const nuint RefreshTimer = 0xCA71;
    private readonly Window owner;
    private readonly OutlinedText[] captions;
    private readonly Action<bool,string?> status;
    private nint handle;
    private uint previousAffinity;
    private bool excluded, ready;
    private int epoch;
    private volatile bool visible;
    private CancellationTokenSource? cancellation;
    private Frame? latest;
    private sealed record Failure(int Epoch,Exception Error);
    private Failure? failure;
    private Task? worker;
    public bool Active { get; private set; }
    private sealed record Frame(int X,int Y,int Width,int Height,BitmapSource Image,BitmapSource RussianImage,int Epoch=0)
    { public Rect Bounds => new(X,Y,Width,Height); }
    public BackdropContrastSampler(Window owner,Action<bool,string?> status,params OutlinedText[] captions)
    {
        this.owner=owner;this.status=status;this.captions=captions;
        owner.IsVisibleChanged+=(_,_)=>visible=owner.IsVisible;
    }
    public void Configure(nint windowHandle,bool enabled)
    {
        if(windowHandle==0)return;handle=windowHandle;
        if(!enabled){Stop();status(false,null);return;}
        if(Active)return;
        try
        {
            if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))throw new InvalidOperationException("此系统不支持自适应背景采样，已使用固定字色。");
            if(!GetWindowDisplayAffinity(handle,out previousAffinity)||!SetWindowDisplayAffinity(handle,0x11))throw new InvalidOperationException("无法排除字幕自身，已使用固定字色。");
            excluded=true;Active=true;visible=owner.IsVisible;ready=false;
            cancellation=new();var token=cancellation.Token;var hwnd=handle;var version=++epoch;
            if(SetTimer(handle,RefreshTimer,33,0)==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            worker=Task.Factory.StartNew(()=>CaptureLoop(hwnd,token,version),token,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        catch(Exception ex){Stop();status(false,ex.Message);}
    }
    private void CaptureLoop(nint hwnd,CancellationToken token,int version)
    {
        var previousDpi=SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            using var workspace=new CaptureWorkspace();
            while(!token.IsCancellationRequested)
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                if(visible && IsWindowVisible(hwnd) && !IsIconic(hwnd) && GetWindowRect(hwnd,out var rectangle))
                {
                    var width=rectangle.Right-rectangle.Left;var height=rectangle.Bottom-rectangle.Top;
                    if(width>0&&height>0)
                    {
                        if((long)width*height>4_000_000)throw new InvalidOperationException("字幕区域过大，请缩小窗口后重新开启自适应模式。");
                        // This margin lets a moving window immediately sample new positions in the last frame.
                        var margin=width*(long)height>1_000_000?64:128;
                        var frame=workspace.Read(rectangle.Left-margin,rectangle.Top-margin,width+margin*2,height+margin*2);
                        if(token.IsCancellationRequested)return;
                        Volatile.Write(ref latest,frame with {Epoch=version});
                    }
                }
                var wait=Math.Max(2,33-(int)watch.ElapsedMilliseconds);
                if(token.WaitHandle.WaitOne(wait))return;
            }
        }
        catch(Exception ex){if(!token.IsCancellationRequested)Volatile.Write(ref failure,new(version,ex));}
        finally{if(previousDpi!=0)SetThreadDpiAwarenessContext(previousDpi);}
    }
    public void OnNativeMessage(int message,nint wParam)
    {
        if(!Active)return;
        if(message==0x0113 && wParam.ToInt64()!=(long)RefreshTimer)return;
        if(message is not (0x0113 or 0x0047 or 0x0231 or 0x0232 or 0x02E0))return;
        if(Volatile.Read(ref failure) is { } error && error.Epoch==epoch){Stop();status(false,FailureMessage(error.Error));return;}
        try{ApplyLatest();}catch(InvalidOperationException){ /* Window layout can be transient during resize/close. */ }
    }
    public static bool TryMapFrame(Rect capture,Rect current,out Rect source)
    {
        source=Rect.Empty;
        if(current.IsEmpty||!capture.Contains(current))return false;
        source=new(current.X-capture.X,current.Y-capture.Y,current.Width,current.Height);return true;
    }
    private void ApplyLatest()
    {
        var frame=Volatile.Read(ref latest);
        if(frame==null||frame.Epoch!=epoch||!visible||IsIconic(handle)||!GetWindowRect(handle,out var rectangle))return;
        var current=new Rect(rectangle.Left,rectangle.Top,Math.Max(0,rectangle.Right-rectangle.Left),Math.Max(0,rectangle.Bottom-rectangle.Top));
        if(!TryMapFrame(frame.Bounds,current,out _))return;
        var dpi=VisualTreeHelper.GetDpi(owner);
        for(var index=0;index<captions.Length;index++)
        {
            var caption=captions[index];if(caption.ActualWidth<=0||caption.ActualHeight<=0)continue;
            var start=caption.PointToScreen(new Point());
            var source=new Rect(start.X-frame.X,start.Y-frame.Y,caption.ActualWidth*dpi.DpiScaleX,caption.ActualHeight*dpi.DpiScaleY);
            var viewport=new Rect(0,0,caption.ActualWidth,caption.ActualHeight);
            var image=index==0?frame.RussianImage:frame.Image;
            if(caption.AdaptiveForeground is ImageBrush brush)
            {
                if(!ReferenceEquals(brush.ImageSource,image))brush.ImageSource=image;
                if(brush.Viewbox!=source)brush.Viewbox=source;
                if(brush.Viewport!=viewport)brush.Viewport=viewport;
            }
            else caption.AdaptiveForeground=new ImageBrush(image){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=source,ViewportUnits=BrushMappingMode.Absolute,Viewport=viewport,Stretch=Stretch.Fill};
        }
        if(!ready){ready=true;status(true,null);}
    }
    private static string FailureMessage(Exception error)=>error switch
    {
        System.ComponentModel.Win32Exception native=>$"背景采样失败（系统错误 {native.NativeErrorCode}），已使用固定字色。",
        InvalidOperationException { Message:"字幕区域过大，请缩小窗口后重新开启自适应模式。" }=>error.Message,
        _=>"背景采样暂不可用，已使用固定字色。可重新开启后重试。"
    };
    private static Frame Capture(int x,int y,int width,int height)
    { using var workspace=new CaptureWorkspace();return workspace.Read(x,y,width,height); }
    private sealed class CaptureWorkspace:IDisposable
    {
        private System.Drawing.Bitmap? bitmap;
        private System.Drawing.Graphics? graphics;
        private byte[] raw=[],previous=[],output=[],russianOutput=[];
        private BitmapSource? lastChinese,lastRussian;
        public Frame Read(int x,int y,int width,int height)
        {
            if(bitmap==null||bitmap.Width!=width||bitmap.Height!=height)
            {
                graphics?.Dispose();bitmap?.Dispose();bitmap=new(width,height,System.Drawing.Imaging.PixelFormat.Format32bppArgb);graphics=System.Drawing.Graphics.FromImage(bitmap);
                raw=new byte[checked(width*height*4)];previous=new byte[raw.Length];output=new byte[raw.Length];russianOutput=new byte[raw.Length];lastChinese=lastRussian=null;
            }
            var requested=new System.Drawing.Rectangle(x,y,width,height);
            var visible=System.Drawing.Rectangle.Intersect(requested,NativeDisplays.VirtualScreen);
            graphics!.Clear(System.Drawing.Color.Black);
            if(!visible.IsEmpty)
            {
                var source=GetDC(0);if(source==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());nint destination=0;
                try
                {
                    destination=graphics.GetHdc();
                    if(!BitBlt(destination,visible.X-x,visible.Y-y,visible.Width,visible.Height,source,visible.X,visible.Y,0x40CC0020))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally{if(destination!=0)graphics.ReleaseHdc(destination);ReleaseDC(0,source);}
            }
            var data=bitmap.LockBits(new(0,0,width,height),ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try{for(var row=0;row<height;row++)Marshal.Copy(data.Scan0+row*data.Stride,raw,row*width*4,width*4);}finally{bitmap.UnlockBits(data);}
            if(lastChinese==null||lastRussian==null||!raw.AsSpan().SequenceEqual(previous))
            {
                AdaptiveContrast.MapPair(raw,output,russianOutput);lastChinese=CreateImage(width,height,output);
                lastRussian=CreateImage(width,height,russianOutput);
                (raw,previous)=(previous,raw);
            }
            return new(x,y,width,height,lastChinese,lastRussian);
        }
        private static BitmapSource CreateImage(int width,int height,byte[] pixels)
        {var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);image.Freeze();return image;}
        public void Dispose(){graphics?.Dispose();bitmap?.Dispose();}
    }
    private void Stop()
    {
        epoch++;Active=false;ready=false;visible=false;
        if(handle!=0)KillTimer(handle,RefreshTimer);
        var old=cancellation;cancellation=null;old?.Cancel();
        var oldWorker=worker;worker=null;
        if(old!=null){if(oldWorker==null)old.Dispose();else _=oldWorker.ContinueWith(_=>old.Dispose(),TaskScheduler.Default);}
        Volatile.Write(ref latest,null);Volatile.Write(ref failure,null);
        foreach(var caption in captions)caption.AdaptiveForeground=null;
        if(excluded&&handle!=0)SetWindowDisplayAffinity(handle,previousAffinity);excluded=false;
    }
    public void Dispose()=>Stop();
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] private static extern nuint SetTimer(nint hwnd,nuint id,uint milliseconds,nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd,nuint id);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern bool BitBlt(nint destination,int x,int y,int width,int height,nint source,int sourceX,int sourceY,uint operation);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowDisplayAffinity(nint hwnd,uint affinity);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetWindowDisplayAffinity(nint hwnd,out uint affinity);
}
