using System.Windows;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceGate? instance;
    protected override void OnExit(ExitEventArgs e){instance?.Dispose();base.OnExit(e);}
    public static string DefaultDataRoot => SelectDefaultDataRoot(Directory.Exists);
    private static string SelectDefaultDataRoot(Func<string,bool> exists)
        => exists(@"E:\") ? @"E:\课堂字幕\data" : exists(@"D:\") ? @"D:\课堂字幕\data" : @"E:\课堂字幕\data";
    private static string PreviousDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassroomCaption", "data");
    public static string LaunchDirectory
    {
        get
        {
            var value=Environment.GetEnvironmentVariable("CLASSROOM_LAUNCH_DIRECTORY");
            return !string.IsNullOrWhiteSpace(value)&&Path.IsPathFullyQualified(value)&&Directory.Exists(value)?Path.GetFullPath(value):AppContext.BaseDirectory;
        }
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var startupProbe=Array.IndexOf(e.Args,"--startup-probe");
        if(startupProbe>=0&&startupProbe+1<e.Args.Length)
        {
            var report=Path.GetFullPath(e.Args[startupProbe+1]);
            StartupProbe.Run(this,Path.Combine(Path.GetDirectoryName(report)!,"probe-data"),report);return;
        }
        var verification = Array.IndexOf(e.Args, "--verify-install");
        if (verification >= 0 && verification + 1 < e.Args.Length)
        {
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            var dataRoot = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : Path.Combine(AppContext.BaseDirectory, "data");
            Shutdown(InstallationVerification.Run(dataRoot, e.Args[verification + 1]));
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(SafeErrors.Describe(args.Exception), "课堂字幕", MessageBoxButton.OK, MessageBoxImage.Information);
            args.Handled = true;
        };
        var user=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        instance=new SingleInstanceGate("Local\\ClassroomCaption."+user+"."+System.Diagnostics.Process.GetCurrentProcess().SessionId);
        if(!instance.IsPrimary){instance.ActivatePrimary();Shutdown();return;}
        var args = e.Args;
        var root = DefaultDataRoot;
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) root = Path.GetFullPath(args[index + 1]);
        while (true)
        {
            try { SettingsStore.CheckWritable(root); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show("数据目录不可用或无法写入："+root+"\n请选择可长期保留的目录，用于保存设置和课堂文字。", "选择保存位置");
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择课堂字幕的数据目录" };
                if (dialog.ShowDialog() != true) { Shutdown(); return; }
                root = dialog.FolderName;
            }
        }
        var store = new SettingsStore(root);
        if(index<0)
        {
            try
            {
                MigrateLegacyData(store,PreviousDataRoot);
                MigrateLegacyData(store);
                InitialConfiguration.ApplyEmbedded(store);
                PrepareDefaultDataLocation(store);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                System.Windows.MessageBox.Show("迁移原有课堂数据失败，原文件仍保留。请检查数据目录后重试。", "课堂字幕");
                Shutdown();return;
            }
        }
        else InitialConfiguration.ApplyEmbedded(store);
        var window = new MainWindow(store);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/AppLogo.ico"));
        if (args.Contains("--demo")) window.Loaded += (_, _) => window.StartDemo();
        MainWindow = window; window.Show(); window.EnableTray();
        // A small, credential-free startup receipt verifies the real launcher path.
        try { AtomicFile.Write(Path.Combine(store.Root,"startup-status.json"),System.Text.Json.JsonSerializer.Serialize(new {
            at=DateTimeOffset.Now,processId=Environment.ProcessId,dataRoot=store.Root,
            executable=Environment.ProcessPath,portableExecutable=Environment.GetEnvironmentVariable("CLASSROOM_PORTABLE_EXE"),launchDirectory=LaunchDirectory,windowShown=window.IsVisible })); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { /* Actual session writes retain their own visible failure handling. */ }
        instance.Listen(()=>Dispatcher.BeginInvoke(()=>{if(!Dispatcher.HasShutdownStarted)window.RestoreWindow();}));
    }
    private static void MigrateLegacyData(SettingsStore store,string? legacyPath=null)
    {
        var legacy=legacyPath??Path.Combine(LaunchDirectory,"data");
        if(string.Equals(Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),store.Root.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))return;
        if(!Directory.Exists(legacy))return;
        var migrationId=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(legacy).ToUpperInvariant())))[..16];
        var migrationMarker=Path.Combine(store.Root,".migration-"+migrationId);
        if(File.Exists(migrationMarker))return;
        if(store.Root.StartsWith(Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The destination must not be inside the legacy data directory.");
        var migratedSettings=!File.Exists(store.SettingsPath)&&File.Exists(Path.Combine(legacy,"settings.json"));
        foreach(var file in Directory.EnumerateFiles(legacy,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(legacy,file);
            var target=Path.Combine(store.Root,relative);
            if(File.Exists(target))continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file,target,false);
        }
        if(File.Exists(store.SettingsPath))
        {
            var settings=store.Load();
            if(migratedSettings||string.IsNullOrWhiteSpace(settings.OutputDirectory)||Path.GetFullPath(settings.OutputDirectory).StartsWith(Path.GetFullPath(legacy)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            {CopyClassroomRecords(settings.OutputDirectory,Path.Combine(store.Root,"sessions"));settings.OutputDirectory=Path.Combine(store.Root,"sessions");store.Save(settings);}
        }
        AtomicFile.Write(migrationMarker,"completed");
    }
    private static void PrepareDefaultDataLocation(SettingsStore store)
    {
        var settings=store.Load();
        if(settings.DataLocationVersion>=1)return;
        var target=Path.Combine(store.Root,"sessions");
        CopyClassroomRecords(settings.OutputDirectory,target);
        settings.OutputDirectory=target;
        settings.DataLocationVersion=1;
        store.Save(settings);
    }
    private static void CopyClassroomRecords(string source,string target)
    {
        if(string.IsNullOrWhiteSpace(source)||!Directory.Exists(source)||string.Equals(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar),Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))return;
        // An older settings file can point outside its own data root. Copy only
        // classroom records, never an arbitrary directory tree or unrelated files.
        foreach(var file in Directory.EnumerateFiles(source,"*.jsonl"))
        {
            var name=Path.GetFileName(file);
            if(!name.StartsWith("课堂-",StringComparison.Ordinal)&&!name.StartsWith("演示-",StringComparison.Ordinal))continue;
            Directory.CreateDirectory(target);
            var destination=Path.Combine(target,name);
            if(!File.Exists(destination))File.Copy(file,destination,false);
            var text=Path.ChangeExtension(file,"txt");
            var targetText=Path.ChangeExtension(destination,"txt");
            if(File.Exists(text)&&!File.Exists(targetText))File.Copy(text,targetText,false);
        }
    }
}
