using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClassroomCaption.App;

internal static class RuntimeDiagnosticsTests
{
    internal static int Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root=Path.GetFullPath(Path.Combine("artifacts","runtime-diagnostics-tests",Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var window=new Window {Width=300,Height=160};new WindowInteropHelper(window).EnsureHandle();
        var results=new List<object>();var failed=0;
        void Check(string name,Action action){try{action();results.Add(new {name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new {name,passed=false,error=ex.Message});Console.WriteLine("FAIL "+name+": "+ex);}}
        try
        {
            Check("Unknown and failed readings retain reasons instead of invented defaults",()=>{
                var missing=RuntimeDiagnostics.Read(()=>null);var denied=RuntimeDiagnostics.Read(()=>throw new UnauthorizedAccessException("test denied"));
                if(missing.Status!="unavailable"||missing.Value is not null||string.IsNullOrEmpty(missing.Reason)||denied.Status!="failed"||denied.Reason?.Contains("test denied")!=true)throw new Exception("Failure evidence lost");
            });
            string? report=null;
            Check("Actual WPF process writes paired JSON and readable diagnostics without touching credentials",()=>{
                var credential=Path.Combine(root,"credentials.json");const string sentinel="synthetic-do-not-collect-secret";File.WriteAllText(credential,sentinel);
                var probe=ProbeDeployment.Ensure(root);
                var task=RuntimeDiagnostics.SaveAsync(window,root,probe);
                var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
                var watch=Stopwatch.StartNew();timer.Tick+=(_,_)=>{if(task.IsCompleted||watch.Elapsed.TotalSeconds>10)frame.Continue=false;};
                timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
                if(!task.IsCompleted)throw new Exception("Runtime diagnostics timed out");report=task.GetAwaiter().GetResult();
                var text=File.ReadAllText(report);using var json=JsonDocument.Parse(text);var data=json.RootElement;
                foreach(var section in new[]{"application","visualEffects","session","wpf","hardware","frameTiming","privacy"})data.GetProperty(section);
                if(data.GetProperty("application").GetProperty("buildId").GetString()!=typeof(App).Assembly.ManifestModule.ModuleVersionId.ToString())throw new Exception("Wrong build identity");
                if(data.GetProperty("application").GetProperty("processId").GetInt32()!=Environment.ProcessId)throw new Exception("Wrong process sampled");
                if(data.GetProperty("visualEffects").GetProperty("wpfClientAreaAnimation").GetProperty("Status").GetString()!="ok")throw new Exception("Actual animation flag missing");
                if(data.GetProperty("captionAnimationPolicy").GetProperty("followsClientAreaAnimation").GetBoolean())throw new Exception("System preference is confused with caption animation policy");
                foreach(var item in data.GetProperty("privacy").EnumerateObject())if(item.Value.GetBoolean())throw new Exception("Unexpected privacy side effect");
                if(text.Contains(sentinel)||File.ReadAllText(credential)!=sentinel||!File.Exists(Path.ChangeExtension(report,".txt")))throw new Exception("Credential isolation or readable report failed");
                Console.WriteLine("Runtime evidence: "+report);
            });
            Check("Embedded native x86 probe links the runtime report and performs no capture or network in offline validation",()=>{
                if(report is null)throw new Exception("Runtime report missing");
                var folder=Path.Combine(root,"native");Directory.CreateDirectory(folder);
                var start=new ProcessStartInfo(ProbeDeployment.Ensure(root)) {UseShellExecute=false,CreateNoWindow=true};
                foreach(var arg in new[]{"--headless","--no-audio","--no-network","--output-dir",folder,"--runtime-report",report})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)!;if(!process.WaitForExit(10000)){process.Kill();throw new Exception("Native diagnostic timed out");}
                if(process.ExitCode!=0)throw new Exception("Native diagnostic failed");
                using var json=JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(folder,"*.json").Single()));var data=json.RootElement;
                var context=data.GetProperty("collectionContext");
                if(context.GetProperty("runtimeReport").GetString()!=report||context.GetProperty("wpfRenderTier").ValueKind!=JsonValueKind.Null)throw new Exception("Collector scopes confused");
                if(context.GetProperty("audioCaptureRequested").GetBoolean()||context.GetProperty("networkProbeRequested").GetBoolean()||data.GetProperty("network").GetArrayLength()!=0)throw new Exception("Capture/network boundary violated");
                foreach(var device in data.GetProperty("audioInputs").EnumerateArray())if(device.GetProperty("captured").GetBoolean())throw new Exception("Audio captured");
            });
        }
        finally {window.Close();}
        File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new {passed=failed==0,tests=results},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:1;
    }
}
