using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace ClassroomCaption.App;

internal sealed class TrayIcon : IDisposable
{
    private const int Message=0x800B;
    private readonly System.Drawing.Icon icon;
    private readonly HwndSource source;
    private Data data;
    private readonly Action restore,exit;
    private readonly Action toggle;
    private readonly Func<bool> running,blocked;
    public TrayIcon(Window window,Action restore,Action exit,Action toggle,Func<bool> running,Func<bool> blocked)
    {
        this.restore=restore;this.exit=exit;
        this.toggle=toggle;this.running=running;this.blocked=blocked;
        using var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.ico")).Stream;
        icon=new System.Drawing.Icon(stream);
        var hwnd=new WindowInteropHelper(window).EnsureHandle();source=HwndSource.FromHwnd(hwnd);source.AddHook(Hook);
        data=new(){Size=(uint)Marshal.SizeOf<Data>(),Window=hwnd,Id=1,Flags=7,Callback=Message,Icon=icon.Handle,Tip="课堂字幕 · 双击打开",Info="",Title=""};
        if(!Shell_NotifyIcon(0,ref data))throw new InvalidOperationException("无法创建托盘图标。");
    }
    private nint Hook(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(message!=Message)return 0;
        if(l.ToInt32()==0x203)restore();
        if(l.ToInt32()==0x205)
        {
            SetForegroundWindow(hwnd);
            var menu=CreateTranslationMenu(toggle,exit,running(),blocked());menu.IsOpen=true;
        }
        handled=true;return 0;
    }
    internal static ContextMenu CreateTranslationMenu(Action toggle,Action exit,bool running,bool blocked)
    {
        var menu=new ContextMenu {Style=(Style)Application.Current.FindResource("TrayContextMenu")};
        var open=new MenuItem {Header=running?"停止翻译":"开始翻译",IsEnabled=!blocked};open.Click+=(_,_)=>toggle();menu.Items.Add(open);
        var quit=new MenuItem {Header="退出软件"};quit.Click+=(_,_)=>exit();menu.Items.Add(quit);
        return menu;
    }
    public void Dispose(){Shell_NotifyIcon(2,ref data);source.RemoveHook(Hook);icon.Dispose();}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Data
    {
        public uint Size;public nint Window;public uint Id,Flags,Callback;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Title;
        public uint InfoFlags;public Guid Guid;public nint BalloonIcon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint message,ref Data data);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint hwnd);
}
