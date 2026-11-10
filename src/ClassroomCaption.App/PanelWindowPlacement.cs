using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClassroomCaption.App;

internal static class PanelWindowPlacement
{
    internal static Rect Fit(Rect proposed,Rect work)
    {
        var width=Math.Min(proposed.Width,work.Width);var height=Math.Min(proposed.Height,work.Height);
        return new(Math.Clamp(proposed.Left,work.Left,work.Right-width),Math.Clamp(proposed.Top,work.Top,work.Bottom-height),width,height);
    }
    internal static Rect WorkArea(Window window)
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
