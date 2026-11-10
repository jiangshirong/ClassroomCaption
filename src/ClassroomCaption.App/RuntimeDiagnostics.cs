using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

// No credentials/configuration, audio, screen pixels or model requests are read.
internal static class RuntimeDiagnostics
{
    internal sealed record Reading(string Status, object? Value, string? Reason=null);
    internal static Reading Read(Func<object?> read)
    {
        try { var value=read();return value is null?new("unavailable",null,"API returned no value or registry value is absent"):new("ok",value); }
        catch(Exception ex){return new("failed",null,ex.GetType().Name+": "+ex.Message);}
    }
    private static Reading RegistryValue(string path,string name)=>Read(()=>Registry.GetValue(path,name,null));
    private static object Spi(uint action)
    {
        if(!SystemParametersInfo(action,0,out int value,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        return value!=0;
    }
    internal static async Task<string> SaveAsync(Window window,string directory,string? probe)
    {
        window.Dispatcher.VerifyAccess();
        Directory.CreateDirectory(directory);
        var assembly=typeof(App).Assembly;
        using var process=Process.GetCurrentProcess();
        var hwnd=new WindowInteropHelper(window).Handle;
        var source=hwnd==0?null:HwndSource.FromHwnd(hwnd);
        var tier=RenderCapability.Tier>>16;
        var report=new Dictionary<string,object?>
        {
            ["schemaVersion"]=1,["at"]=DateTimeOffset.Now,
            ["collector"]="actual WPF application process",
            ["scope"]="Main window process; native x86 probe is a separate process and cannot measure this WPF tier. Tier is capability, not proof of hardware rendering for every window.",
            ["captionAnimationPolicy"]=new {subtitles="independently enabled",ballTransitions="independently enabled",
                followsClientAreaAnimation=false,note="This build keeps subtitle reveal and ball morph animations independent of the system animation preference. This records code policy, not proof that visible frames were presented; decorative animations may still follow system settings."},
            ["application"]=new {
                version=assembly.GetName().Version?.ToString(),
                informationalVersion=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                buildId=assembly.ManifestModule.ModuleVersionId.ToString(),
                executable=Read(()=>Environment.ProcessPath),
                portableExecutable=Read(()=>Environment.GetEnvironmentVariable("CLASSROOM_PORTABLE_EXE")),
                launchDirectory=Read(()=>App.LaunchDirectory),
                processId=Environment.ProcessId,sessionId=Read(()=>process.SessionId),
                processArchitecture=RuntimeInformation.ProcessArchitecture.ToString(),osArchitecture=RuntimeInformation.OSArchitecture.ToString(),
                runtime=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,
                nativeProbe=new {path=Read(()=>probe),architecture="x86",wpfRenderTier=(int?)null,reason="Independent Win32 tool does not host WPF"}},
            ["visualEffects"]=new Dictionary<string,object?> {
                ["wpfClientAreaAnimation"]=Read(()=>SystemParameters.ClientAreaAnimation),
                ["spiClientAreaAnimation"]=Read(()=>Spi(0x1042)),
                ["spiUiEffects"]=Read(()=>Spi(0x103E)),
                ["spiMenuAnimation"]=Read(()=>Spi(0x1002)),
                ["spiTooltipAnimation"]=Read(()=>Spi(0x1016)),
                ["spiComboBoxAnimation"]=Read(()=>Spi(0x1004)),
                ["spiListBoxSmoothScrolling"]=Read(()=>Spi(0x1006)),
                ["minAnimate"]=RegistryValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics","MinAnimate"),
                ["userPreferencesMask"]=RegistryValue(@"HKEY_CURRENT_USER\Control Panel\Desktop","UserPreferencesMask"),
                ["visualFxSetting"]=RegistryValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects","VisualFXSetting")},
            ["session"]=new {remoteSession=GetSystemMetrics(0x1000)!=0,remoteControl=GetSystemMetrics(0x2001)!=0,
                protocol=Read(()=>SessionProtocol(process.SessionId)),note="Protocol: 0 console, 1 legacy, 2 RDP. Remote flags alone do not identify every remote-control product."},
            ["wpf"]=new {renderTier=Read(()=>tier),tierMeaning=tier==0?"No WPF hardware acceleration capability reported":tier==1?"Partial hardware acceleration capability":"Tier 2 hardware acceleration capability",
                processRenderMode=Read(()=>RenderOptions.ProcessRenderMode.ToString()),
                mainWindowRenderMode=Read(()=>source?.CompositionTarget?.RenderMode.ToString()),
                mainWindowDpi=Read(()=>{var d=VisualTreeHelper.GetDpi(window);return new {x=d.PixelsPerInchX,y=d.PixelsPerInchY};}),
                disableHwAcceleration=RegistryValue(@"HKEY_CURRENT_USER\Software\Microsoft\Avalon.Graphics","DisableHWAcceleration"),
                interpretation="Default render mode is a preference, not proof that each frame or layered window uses the GPU."},
            ["hardware"]=await Task.Run(()=>new {
                cpuName=RegistryValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString"),
                cpuVendor=RegistryValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","VendorIdentifier"),
                logicalProcessors=Environment.ProcessorCount,
                logicalProcessorScope="Processors available to this process; affinity or quota may reduce this count",
                physicalMemory=Read(Memory),displayAdapters=Read(Adapters),displays=Read(Displays)}),
            ["frameTiming"]=await SampleFramesAsync(window),
            ["privacy"]=new {audioCaptured=false,screenCaptured=false,modelCalls=false,credentialsRead=false}
        };
        var stem=Path.Combine(directory,"运行诊断-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        var json=JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true});
        // File I/O is outside the UI thread; the renderer subscription is already removed.
        await Task.Run(()=>{
            AtomicFile.Write(stem+".json",json);
            AtomicFile.Write(stem+".txt","课堂字幕运行诊断\n主程序版本："+assembly.GetName().Version+"\n构建标识："+assembly.ManifestModule.ModuleVersionId+
                "\nWPF 渲染等级："+tier+"（能力等级，不代表所有窗口实际都使用硬件渲染）\n详细值与失败原因见下方；本报告不录音、不截图、不调用模型。\n\n"+json);
        });
        return stem+".json";
    }
    private static object Memory()
    {
        var memory=new MemoryStatus {Length=(uint)Marshal.SizeOf<MemoryStatus>()};
        if(!GlobalMemoryStatusEx(ref memory))throw new Win32Exception(Marshal.GetLastWin32Error());
        return new {totalBytes=memory.TotalPhysical,availableBytes=memory.AvailablePhysical,loadPercent=memory.Load};
    }
    private static object SessionProtocol(int session)
    {
        if(!WTSQuerySessionInformation(0,session,16,out var pointer,out int bytes))throw new Win32Exception(Marshal.GetLastWin32Error());
        try {if(bytes<2)throw new IOException("WTS protocol value is incomplete");return (ushort)Marshal.ReadInt16(pointer);}
        finally{WTSFreeMemory(pointer);}
    }
    private static object Adapters()
    {
        var result=new List<object>();
        for(uint i=0;i<32;i++)
        {
            var d=new DisplayDevice {Size=Marshal.SizeOf<DisplayDevice>()};if(!EnumDisplayDevices(null,i,ref d,0))break;
            // DeviceKey locates the display driver's settings, never hardware serial numbers.
            var key=d.Key?.Replace(@"\Registry\Machine\",@"HKEY_LOCAL_MACHINE\",StringComparison.OrdinalIgnoreCase);
            result.Add(new {name=d.Description,displayName=d.Name,stateFlags=d.Flags,
                driverVersion=key is null?new Reading("unavailable",null,"No driver registry key"):RegistryValue(key,"DriverVersion"),
                driverDate=key is null?new Reading("unavailable",null,"No driver registry key"):RegistryValue(key,"DriverDate"),
                note="EnumDisplayDevices adapter list; driver values may be absent for indirect/remote adapters."});
        }
        if(result.Count==0)throw new IOException("EnumDisplayDevices returned no adapters");return result;
    }
    private static object Displays()
    {
        var list=new List<object>();
        MonitorCallback callback=(monitor,dc,rect,data)=>{
            var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
            if(!GetMonitorInfo(monitor,ref info)){list.Add(new {status="failed",error=Marshal.GetLastWin32Error()});return true;}
            list.Add(new {bounds=info.Bounds,workingArea=info.Work,primary=(info.Flags&1)!=0,dpi=Read(()=>{
                int hr=GetDpiForMonitor(monitor,0,out uint x,out uint y);Marshal.ThrowExceptionForHR(hr);return new {x,y};}),
                dpiNote="Effective monitor DPI reported to this process; compare mainWindowDpi for the actual WPF window."});return true;
        };
        if(!EnumDisplayMonitors(0,0,callback,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(list.Count==0)throw new IOException("EnumDisplayMonitors returned no monitors");
        return list;
    }
    private static async Task<object> SampleFramesAsync(Window window)
    {
        var intervals=new List<double>();var watch=Stopwatch.StartNew();double? last=null;
        var completed=new TaskCompletionSource<bool>();
        var timer=new DispatcherTimer(DispatcherPriority.Background,window.Dispatcher) {Interval=TimeSpan.FromMilliseconds(1200)};
        EventHandler render=(_,_)=>{var now=watch.Elapsed.TotalMilliseconds;if(last.HasValue)intervals.Add(now-last.Value);last=now;};
        EventHandler finish=(_,_)=>completed.TrySetResult(true);
        var visible=window.IsVisible;
        try{CompositionTarget.Rendering+=render;timer.Tick+=finish;timer.Start();await completed.Task;}
        finally{timer.Stop();timer.Tick-=finish;CompositionTarget.Rendering-=render;watch.Stop();}
        var values=intervals.OrderBy(x=>x).ToArray();
        return new {status=values.Length>=3?"sampled":"insufficient_frames",visibleAtStart=visible,durationMs=watch.Elapsed.TotalMilliseconds,
            intervalCount=values.Length,medianMs=values.Length==0?(double?)null:values[values.Length/2],
            p95Ms=values.Length==0?(double?)null:values[Math.Min(values.Length-1,(int)Math.Ceiling(values.Length*.95)-1)],
            intervalsOver50Ms=values.Count(x=>x>50),intervalsOver100Ms=values.Count(x=>x>100),
            interpretation="Local WPF Rendering callback intervals over ~1.2 seconds; no screen capture and no presentation/GPU FPS measurement. Hidden, occluded or idle windows may yield insufficient frames. No cause is inferred from this short sample."};
    }
    [StructLayout(LayoutKind.Sequential)]private struct MemoryStatus {public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPage,AvailablePage,TotalVirtual,AvailableVirtual,AvailableExtended;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DisplayDevice {public int Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Key;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect {public int Left,Top,Right,Bottom;public int X=>Left;public int Y=>Top;public int Width=>Right-Left;public int Height=>Bottom-Top;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo {public int Size;public Rect Bounds,Work;public uint Flags;}
    private delegate bool MonitorCallback(nint monitor,nint dc,nint rect,nint data);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SystemParametersInfo(uint action,uint param,out int value,uint flags);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)]private static extern bool WTSQuerySessionInformation(nint server,int session,int info,out nint buffer,out int bytes);
    [DllImport("wtsapi32.dll")]private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll",EntryPoint="EnumDisplayDevicesW",CharSet=CharSet.Unicode)]private static extern bool EnumDisplayDevices(string? device,uint number,ref DisplayDevice value,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EnumDisplayMonitors(nint dc,nint rect,MonitorCallback callback,nint data);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW",SetLastError=true)]private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("shcore.dll")]private static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
}
