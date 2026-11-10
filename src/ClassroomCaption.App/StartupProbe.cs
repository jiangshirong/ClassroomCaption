using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

// Exercises native WPF startup without capture, desktop automation or model calls.
internal static class StartupProbe
{
    public static void Run(Application app,string dataRoot,string report)
    {
        var watch=Stopwatch.StartNew();
        var store=new SettingsStore(dataRoot);SettingsStore.CheckWritable(store.Root);
        var window=new MainWindow(store);app.MainWindow=window;
        window.ContentRendered+=(_,_)=>
        {
            AtomicFile.Write(report,JsonSerializer.Serialize(new{passed=true,windowShown=window.IsVisible,
                startupWorkMs=watch.Elapsed.TotalMilliseconds,processAgeMs=(DateTime.Now-Process.GetCurrentProcess().StartTime).TotalMilliseconds,
                microphoneCaptured=false,modelCalls=false}));
            app.Shutdown();
        };
        window.Show();
    }
}
