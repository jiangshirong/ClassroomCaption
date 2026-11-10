using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ClassroomCaption.App;

// Reading screen geometry does not require shipping the Windows Forms UI stack.
public static class NativeDisplays
{
    public sealed record Display(Rectangle Bounds, Rectangle WorkingArea);
    public static Rectangle VirtualScreen => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    public static Display FromWindow(nint window)
    {
        var monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Bounds.Rectangle, info.WorkingArea.Rectangle);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Bounds, WorkingArea; public uint Flags; }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
