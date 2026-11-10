using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.Core;
using ClassroomCaption.App;

internal static class Program
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeWindowBounds {public int Left,Top,Right,Bottom;}
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle,out NativeWindowBounds rectangle);
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--probe-backdrop"))
        {
            try
            {
                var method=typeof(BackdropContrastSampler).GetMethod("Capture",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                var bounds=NativeDisplays.VirtualScreen;
                var runs=new List<object>();
                foreach(var size in new[] { (Width:32,Height:32),(Width:1050,Height:245),(Width:1575,Height:368) })
                {
                    var elapsed=System.Diagnostics.Stopwatch.StartNew();
                    for(var repeat=0;repeat<5;repeat++)
                    {
                        var result=Task.Run(()=>method.Invoke(null,new object[] { bounds.X,bounds.Y,size.Width,size.Height })).GetAwaiter().GetResult()!;
                        var image=(BitmapSource)result.GetType().GetProperty("Image")!.GetValue(result)!;
                        var pixels=new byte[size.Width*size.Height*4];image.CopyPixels(pixels,size.Width*4,0);
                        if(pixels.Length!=size.Width*size.Height*4)throw new Exception("Invalid contrast map size");
                        for(var pixelIndex=0;pixelIndex<pixels.Length;pixelIndex+=4) if(pixels[pixelIndex+3]!=255)throw new Exception("Invalid contrast pixel");
                    }
                    elapsed.Stop();runs.Add(new { size.Width,size.Height,calls=5,averageMilliseconds=elapsed.Elapsed.TotalMilliseconds/5 });
                }
                Console.WriteLine(JsonSerializer.Serialize(new { success=true,runs,pixelsSaved=false }));
                return 0;
            }
            catch(Exception ex)
            {
                var error=ex is System.Reflection.TargetInvocationException wrapped ? wrapped.InnerException! : ex;
                Console.WriteLine(JsonSerializer.Serialize(new { success=false,type=error.GetType().Name,error=error.Message,pixelsSaved=false }));
                return 1;
            }
        }
        if(args.Contains("--probe-backdrop-stream"))
        {
            try
            {
                var type=typeof(BackdropContrastSampler).GetNestedType("CaptureWorkspace",System.Reflection.BindingFlags.NonPublic)!;
                using var workspace=(IDisposable)Activator.CreateInstance(type,true)!;
                var read=type.GetMethod("Read")!;var bounds=NativeDisplays.VirtualScreen;
                var times=new List<double>();var allocated=GC.GetTotalAllocatedBytes();
                for(var frame=0;frame<24;frame++)
                {
                    var watch=System.Diagnostics.Stopwatch.StartNew();
                    var result=Task.Run(()=>read.Invoke(workspace,new object[] { bounds.X+(frame*19)%240,bounds.Y+(frame*7)%96,1306,501 })).GetAwaiter().GetResult()!;
                    watch.Stop();times.Add(watch.Elapsed.TotalMilliseconds);
                    var image=(BitmapSource)result.GetType().GetProperty("Image")!.GetValue(result)!;
                    if(!image.IsFrozen||image.PixelWidth!=1306||image.PixelHeight!=501)throw new Exception("Bad worker frame");
                }
                var memory=GC.GetTotalAllocatedBytes()-allocated;times.Sort();
                Console.WriteLine(JsonSerializer.Serialize(new {success=true,frames=24,screenRegionPositionChanges=true,medianMilliseconds=times[12],p95Milliseconds=times[22],managedAllocationBytes=memory,pixelsSaved=false,actualWindowDraggingTested=false}));
                return 0;
            }
            catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new {success=false,type=ex.GetType().Name,error=ex.InnerException?.Message??ex.Message}));return 1;}
        }
        if (args.Contains("--compare-microphones"))
        {
            Console.WriteLine(JsonSerializer.Serialize(MicrophoneComparison.RunAsync().GetAwaiter().GetResult()));
            return 0;
        }
        if (args.Contains("--probe-capture"))
        {
            var device = AudioCaptureService.Inputs().First(d => d.CanRecord);
            using var microphone = new AudioCaptureService();
            long bytes = 0; double rms = 0; string? error = null;
            microphone.Pcm += pcm => Interlocked.Add(ref bytes, pcm.Length);
            microphone.Level += (level, _) => rms = Math.Max(rms, level);
            microphone.Fault += message => error = message;
            microphone.Start(device.Id);
            var mode = microphone.CaptureMode;
            Thread.Sleep(5000);
            microphone.StopAsync().GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new { mode, seconds = bytes / 32000.0, peakRms = rms, error }));
            return error is null && bytes > 0 && rms > .0005 ? 0 : 1;
        }
        if (args.Contains("--audio-inventory"))
        {
            var inventory = AudioCaptureService.InputInventory();
            Console.WriteLine(JsonSerializer.Serialize(new { activeInputs = AudioCaptureService.Inputs().Count(d => d.CanRecord), devices = inventory }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Contains("--audio-health"))
        {
            Console.WriteLine(JsonSerializer.Serialize(AudioCaptureService.InputHealth(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        // Pumping the test dispatcher must not run production startup or its
        // single-instance shutdown path when the installed app is open.
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("pack://application:,,,/ClassroomCaption;component/UI/Icons.xaml")});
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("pack://application:,,,/ClassroomCaption;component/UI/Theme.xaml")});
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if(args.Contains("--caption-stream-tests"))return CaptionStreamingTests.Run();
        var replayIndex=Array.IndexOf(args,"--classroom-replay");
        if(replayIndex>=0&&replayIndex+1<args.Length)return ClassroomReplay.Run(args[replayIndex+1]);
        if(args.Contains("--runtime-diagnostics-tests"))return RuntimeDiagnosticsTests.Run();
        if(args.Contains("--floating-start-tests"))return FloatingCaptionTests.Run(startsOnly:true);
        if(args.Contains("--animation-compat-tests"))return FloatingCaptionTests.Run(compatibilityOnly:true);
        if(args.Contains("--floating-tests"))return FloatingCaptionTests.Run();
        if(args.Contains("--compact-tests"))return CompactPanelTests.Run();
        var flowBenchmark=args.Contains("--caption-flow-benchmark");
        var benchmarkIndex = Array.IndexOf(args, flowBenchmark?"--caption-flow-benchmark":"--caption-benchmark");
        if (benchmarkIndex >= 0)
        {
            var samples = new List<object>();
            foreach (var length in new[] { 120, 360, 720 })
            {
                var value = string.Concat(Enumerable.Repeat("船舶的初稳性会减小，因此需要调整货物位置。", 40))[..length];
                var size=flowBenchmark?new Size(960,132):new Size(1400,650);
                var view = new OutlinedText { Width=size.Width,Height=size.Height,TextSize=flowBenchmark?32:31,Outline=!flowBenchmark,Text=value,TextBackdrop=flowBenchmark,ViewportHeight=flowBenchmark?132:double.PositiveInfinity,FontFamily=flowBenchmark?CaptionTypography.Chinese:new FontFamily("Microsoft YaHei UI, Segoe UI") };
                if(flowBenchmark)view.LineHeight=48;
                void Layout() { view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout(); }
                void Render() { var bitmap = new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32); bitmap.Render(view); }
                Layout();
                var watch = System.Diagnostics.Stopwatch.StartNew(); Render(); watch.Stop();
                var initial = watch.Elapsed.TotalMilliseconds;
                view.SetAnimatedText(value.Replace("初稳性", "横向稳性").Replace("需要调整", "必须重新调整"), true); Layout();
                watch.Restart(); Render(); watch.Stop(); var revision = watch.Elapsed.TotalMilliseconds;
                var frames = new List<double>();
                for (var frame = 0; frame < 16; frame++)
                {
                    watch.Restart(); view.AdvanceAnimation(.016); Render(); watch.Stop(); frames.Add(watch.Elapsed.TotalMilliseconds);
                }
                frames.Sort();
                samples.Add(new { characters = length, initialMs = initial, revisionMs = revision, medianOffscreenFrameMs = frames[frames.Count / 2], maxOffscreenFrameMs = frames[^1] });
            }
            var report = JsonSerializer.Serialize(new { kind = "WPF component CPU/offscreen rendering; not display FPS", samples }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(report);
            if (benchmarkIndex + 1 < args.Length) AtomicFile.Write(args[benchmarkIndex + 1], report);
            return 0;
        }
        var results = new List<object>(); var failed = 0;
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " " + ex.GetType().Name + "\n" + ex.StackTrace); results.Add(new { name, passed = false, error = ex.GetType().Name }); }
        }
        var root = Path.Combine(Path.GetTempPath(), "ClassroomCaption-wpf-tests-" + Guid.NewGuid().ToString("N"));
        SettingsStore.CheckWritable(root);
        Check("Repeated launches signal the original instance and ownership releases on exit",()=>
        {
            var name="Local\\ClassroomCaption.Test."+Guid.NewGuid().ToString("N");
            using(var owner=new SingleInstanceGate(name))
            {
                if(!owner.IsPrimary)throw new Exception("First instance rejected");
                Task.Run(()=>{using var next=new SingleInstanceGate(name);if(next.IsPrimary)throw new Exception("Duplicate instance allowed");next.ActivatePrimary();}).GetAwaiter().GetResult();
                var restored=new TaskCompletionSource();owner.Listen(()=>restored.TrySetResult());
                restored.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            using var restarted=new SingleInstanceGate(name);if(!restarted.IsPrimary)throw new Exception("Exit did not release ownership");
        });
        Check("Embedded x86 probe works without an adjacent tool and repairs a corrupt cached copy",()=>
        {
            var path=ProbeDeployment.Ensure(Path.Combine(root,"probe-cache"));var original=File.ReadAllBytes(path);
            if(original[0]!='M'||original[1]!='Z')throw new Exception("Not an executable");
            var header=BitConverter.ToInt32(original,0x3c);
            if(BitConverter.ToUInt16(original,header+4)!=0x14c)throw new Exception("Probe is not x86");
            File.WriteAllText(path,"corrupted");
            if(ProbeDeployment.Ensure(Path.Combine(root,"probe-cache"))!=path||!File.ReadAllBytes(path).SequenceEqual(original))throw new Exception("Probe cache repair failed");
        });
        Check("Default data uses E drive and legacy migration preserves credentials and classroom history",()=>
        {
            var choose=typeof(ClassroomCaption.App.App).GetMethod("SelectDefaultDataRoot",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            string Select(params string[] drives)=>(string)choose.Invoke(null,new object[]{(Func<string,bool>)(p=>drives.Contains(p))})!;
            if(Select(@"E:\",@"D:\")!=@"E:\课堂字幕\data"||Select(@"D:\")!=@"D:\课堂字幕\data"||Select()!=@"E:\课堂字幕\data")throw new Exception("Default drive priority or missing-drive prompt target incorrect");
            var legacy=new SettingsStore(Path.Combine(root,"old-data"));SettingsStore.CheckWritable(legacy.Root);
            legacy.Save(new AppSettings {OutputDirectory=Path.Combine(legacy.Root,"sessions")});legacy.SaveCredentials(new ApiCredentials("synthetic-migrate"));
            Directory.CreateDirectory(Path.Combine(legacy.Root,"sessions"));File.WriteAllText(Path.Combine(legacy.Root,"sessions","old.jsonl"),"synthetic");
            var next=new SettingsStore(Path.Combine(root,"new-data"));SettingsStore.CheckWritable(next.Root);
            typeof(ClassroomCaption.App.App).GetMethod("MigrateLegacyData",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,new object[]{next,legacy.Root});
            if(next.LoadCredentials()?.AliyunKey!="synthetic-migrate"||next.Load().OutputDirectory!=Path.Combine(next.Root,"sessions")||!File.Exists(Path.Combine(next.Root,"sessions","old.jsonl"))||!File.Exists(legacy.CredentialsPath))throw new Exception("Migration lost data");
            next.SaveCredentials(new ApiCredentials("newer-synthetic-key"));
            typeof(ClassroomCaption.App.App).GetMethod("MigrateLegacyData",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,new object[]{next,legacy.Root});
            if(next.LoadCredentials()?.AliyunKey!="newer-synthetic-key")throw new Exception("Existing credentials overwritten");
        });
        Check("Cached startup locates legacy data beside the original launcher rather than in the runtime cache",()=>
        {
            var original=Environment.GetEnvironmentVariable("CLASSROOM_LAUNCH_DIRECTORY");
            try
            {
                var launcher=Path.Combine(root,"original-launcher");Directory.CreateDirectory(launcher);
                var source=new SettingsStore(Path.Combine(launcher,"data"));source.SaveCredentials(new ApiCredentials("synthetic-cache-migration"));
                var destination=new SettingsStore(Path.Combine(root,"cache-migration-target"));
                Environment.SetEnvironmentVariable("CLASSROOM_LAUNCH_DIRECTORY",launcher);
                if(ClassroomCaption.App.App.LaunchDirectory!=launcher)throw new Exception("Original launcher path was lost");
                typeof(ClassroomCaption.App.App).GetMethod("MigrateLegacyData",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,new object?[]{destination,null});
                if(destination.LoadCredentials()?.AliyunKey!="synthetic-cache-migration")throw new Exception("Adjacent legacy configuration was not migrated");
                Environment.SetEnvironmentVariable("CLASSROOM_LAUNCH_DIRECTORY","relative-invalid-path");
                if(ClassroomCaption.App.App.LaunchDirectory!=AppContext.BaseDirectory)throw new Exception("Invalid launcher directory was accepted");
            }finally{Environment.SetEnvironmentVariable("CLASSROOM_LAUNCH_DIRECTORY",original);}
        });
        Check("Offline start immediately reports not connected without creating a classroom or opening settings",()=>
        {
            var directory=Path.Combine(root,"offline-start");
            var window=new MainWindow(new SettingsStore(directory),_=>Task.FromResult(false));
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            typeof(MainWindow).GetMethod("StartClick",flags)!.Invoke(window,new object[]{window,new RoutedEventArgs()});
            window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if(window.Running||((TextBlock)window.FindName("NoticeText")).Text!="未联网")throw new Exception("Offline start did not fail immediately");
            if(Directory.Exists(directory)&&Directory.EnumerateFiles(directory,"*.jsonl",SearchOption.AllDirectories).Any())throw new Exception("Offline start created a session");
            if(typeof(MainWindow).GetField("audio",flags)!.GetValue(window)is not null||typeof(MainWindow).GetField("live",flags)!.GetValue(window)is not null)throw new Exception("Offline start opened capture or cloud connection");
        });
        Check("Default storage migrates separately configured classroom files once and preserves later folder choices",()=>
        {
            var custom=Path.Combine(root,"external-records");Directory.CreateDirectory(custom);
            File.WriteAllText(Path.Combine(custom,"课堂-20261104-000000-example.jsonl"),"record");
            File.WriteAllText(Path.Combine(custom,"课堂-20261104-000000-example.txt"),"原文：Привет\n译文：你好");
            File.WriteAllText(Path.Combine(custom,"unrelated.jsonl"),"unrelated");
            var store=new SettingsStore(Path.Combine(root,"new-persistent-root"));
            store.Save(new() {OutputDirectory=custom});
            var prepare=typeof(ClassroomCaption.App.App).GetMethod("PrepareDefaultDataLocation",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            prepare.Invoke(null,new object[]{store});
            var settings=store.Load();
            if(settings.OutputDirectory!=Path.Combine(store.Root,"sessions")||settings.DataLocationVersion!=1)throw new Exception("Default records still point at old storage");
            if(Directory.GetFiles(settings.OutputDirectory).Length!=2||!File.Exists(Path.Combine(custom,"课堂-20261104-000000-example.jsonl")))throw new Exception("Migration removed originals or copied unrelated files");
            settings.OutputDirectory=custom;store.Save(settings);prepare.Invoke(null,new object[]{store});
            if(store.Load().OutputDirectory!=custom)throw new Exception("A later explicit folder choice was overwritten");
        });
        Check("Hotspot connectivity verifies a negative Windows Internet signal before blocking translation",()=>
        {
            var method=typeof(InternetConnectivity).GetMethod("ResolveSignalAsync",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            var probes=0;
            bool Resolve(bool? signal,bool reachable)=>((Task<bool>)method.Invoke(null,new object?[]{signal,(Func<Task<bool>>)(()=>{probes++;return Task.FromResult(reachable);})})!).GetAwaiter().GetResult();
            if(!Resolve(false,true)||Resolve(false,false)||!Resolve(true,false)||!Resolve(null,false)||probes!=2)throw new Exception("Hotspot was blocked or unnecessary probe sent");
        });
        Check("Reconnecting subtitles retain existing bilingual content with an explicit recovery label",()=>
        {
            var window=new OverlayWindow(new());
            window.SetCaption(new([new(1,"Привет","你好",true,true,DateTimeOffset.Now,[])],[],"",0,1),SubtitleActivity.Reconnecting);
            if(!((OutlinedText)window.FindName("RussianCaption")).StatusLabel.Contains("自动恢复")||!((OutlinedText)window.FindName("ChineseCaption")).Text.Contains("你好"))throw new Exception("Recovery hid subtitles or state");
        });
        Check("Closing a tray-enabled control window keeps the classroom running",()=>
        {
            var window=new MainWindow(new SettingsStore(Path.Combine(root,"tray-close")));var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var trayField=typeof(MainWindow).GetField("tray",flags)!;
            trayField.SetValue(window,System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(trayField.FieldType));
            using var cancellation=new CancellationTokenSource();typeof(MainWindow).GetField("runCancellation",flags)!.SetValue(window,cancellation);
            var closing=new System.ComponentModel.CancelEventArgs();typeof(MainWindow).GetMethod("OnClosing",flags,null,new[]{typeof(object),typeof(System.ComponentModel.CancelEventArgs)},null)!.Invoke(window,new object?[]{window,closing});
            if(!closing.Cancel||!window.Running||cancellation.IsCancellationRequested)throw new Exception("Close stopped capture");
            trayField.SetValue(window,null);typeof(MainWindow).GetField("runCancellation",flags)!.SetValue(window,null);
        });
        MathCaptionTests.Run(Check,root);
        Check("Fault stops persist their reason and leave a non-expiring explanation", () =>
        {
            var window=new MainWindow(new SettingsStore(Path.Combine(root,"fault-ui")));
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            using var session=new SessionStore(Path.Combine(root,"fault-log"));
            typeof(MainWindow).GetField("session",flags)!.SetValue(window,session);
            typeof(MainWindow).GetField("runCancellation",flags)!.SetValue(window,new CancellationTokenSource());
            var task=(Task)typeof(MainWindow).GetMethod("StopForFaultAsync",flags)!.Invoke(window,new object[]{"translation_error","测试连接中断"})!;
            task.GetAwaiter().GetResult();
            window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if(window.Running||((TextBlock)window.FindName("NoticeText")).Text!="课堂已停止：测试连接中断")throw new Exception("Fault explanation was lost");
            if(((System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("noticeTimer",flags)!.GetValue(window)!).IsEnabled)throw new Exception("Fault explanation still expires");
            if(!File.ReadAllText(session.FilePath).Contains("translation_error"))throw new Exception("Stop reason missing from classroom record");
        });
        Check("Embedded release defaults seed new devices without replacing existing configuration", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "initial-config"));
            using var settings = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new AppSettings { WorkspaceId = "ws-initial", NoiseReductionEnabled = true }));
            using var key = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new ApiCredentials("synthetic-initial-key")));
            InitialConfiguration.Apply(store, settings, key);
            if (store.Load().WorkspaceId != "ws-initial" || !store.Load().NoiseReductionEnabled || store.LoadCredentials()?.AliyunKey != "synthetic-initial-key") throw new Exception("First launch lost embedded defaults");
            store.Save(new AppSettings { WorkspaceId = "ws-user-choice", NoiseReductionEnabled = false });
            store.SaveCredentials(new ApiCredentials("synthetic-user-key"));
            settings.Position = key.Position = 0;
            InitialConfiguration.Apply(store, settings, key);
            if (store.Load().WorkspaceId != "ws-user-choice" || store.Load().NoiseReductionEnabled || store.LoadCredentials()?.AliyunKey != "synthetic-user-key") throw new Exception("Restart replaced user choices");
        });
        Check("A build without embedded credentials does not create empty user configuration", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "no-initial-config"));
            InitialConfiguration.Apply(store, null, null);
            if (File.Exists(store.SettingsPath) || File.Exists(store.CredentialsPath)) throw new Exception("Unexpected configuration was persisted");
        });
        NoiseReductionTests.Run(Check, root);
        Check("Native 48 kHz float microphone retains speech across packets and opposite channels", () =>
        {
            var converter = new MicrophonePcmConverter(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
            var output = new List<byte>();
            for (var packet = 0; packet < 20; packet++)
            {
                var native = new byte[4800 * 8];
                for (var frame = 0; frame < 4800; frame++)
                {
                    var value = (float)(.25 * Math.Sin(2 * Math.PI * 440 * (packet * 4800 + frame) / 48000));
                    BitConverter.TryWriteBytes(native.AsSpan(frame * 8, 4), value);
                    BitConverter.TryWriteBytes(native.AsSpan(frame * 8 + 4, 4), -value);
                }
                output.AddRange(converter.Convert(native));
            }
            if (Math.Abs(output.Count - 64000) > 64) throw new Exception("Resampling lost audio duration");
            var pcm = output.ToArray(); double sum = 0;
            for (var i = 0; i < pcm.Length; i += 2) { var value = BitConverter.ToInt16(pcm, i) / 32768.0; sum += value * value; }
            var rms = Math.Sqrt(sum / (pcm.Length / 2));
            if (rms < .15 || rms > .20) throw new Exception("Speech level was lost");
        });
        Check("Native PCM conversion handles silent channel, integer depths and exact silence", () =>
        {
            foreach (var bits in new[] { 16, 24, 32 })
            {
                var converter = new MicrophonePcmConverter(new NAudio.Wave.WaveFormat(16000, bits, 2));
                var native = new byte[1600 * 2 * bits / 8];
                var stride = bits / 8;
                // Channel one is silent; channel two carries a positive quarter-scale sample.
                for (var frame = 0; frame < 1600; frame++) native[frame * stride * 2 + stride * 2 - 1] = 32;
                var converted = converter.Convert(native);
                if (converted.Length < 3000 || BitConverter.ToInt16(converted, 100) < 8000) throw new Exception("Active channel lost");
                var silence = new MicrophonePcmConverter(new NAudio.Wave.WaveFormat(48000, bits, 2)).Convert(new byte[4800 * 2 * stride]);
                if (silence.Any(b => b != 0)) throw new Exception("Silence generated noise");
            }
        });
        Check("Keys display directly and save without password across restart", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "plain-credentials"));
            var settings = new AppSettings { OutputDirectory = Path.Combine(store.Root, "sessions") };
            var keys = new ApiCredentials("synthetic-aliyun");
            var window = new SettingsWindow(store, settings, keys);
            if (((TextBox)window.FindName("AliyunApiKey")).Text != keys.AliyunKey) throw new Exception();
            if (window.FindName("VaultPassword") is not null) throw new Exception("Unexpected password prompt");
            ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!window.Saved || store.LoadCredentials() != keys) throw new Exception("Save failed");
            var reopened = new SettingsWindow(store, store.Load(), null);
            if (((TextBox)reopened.FindName("AliyunApiKey")).Text != keys.AliyunKey) throw new Exception("Restart lost keys");
        });
        Check("Settings fields and appearance sliders have accessible names and focus treatment", () =>
        {
            var window = new SettingsWindow(new SettingsStore(Path.Combine(root, "accessible-fields")), new AppSettings { OutputDirectory = Path.Combine(root, "sessions") }, null);
            foreach (var name in new[] { "AliyunApiKey", "OutputDirectory" })
                if (window.FindName(name) is not DependencyObject field || string.IsNullOrWhiteSpace(AutomationProperties.GetName(field))) throw new Exception("Field lacks an accessible name: " + name);
            var appearance = new AppearanceWindow(new(), () => { });
            var content = (FrameworkElement)appearance.Content;
            content.Measure(new Size(470, 730)); content.Arrange(new Rect(0, 0, 470, 730)); content.UpdateLayout();
            var sliders = Descendants<Slider>(content).ToArray();
            if (sliders.Length != 3 || sliders.Any(s => string.IsNullOrWhiteSpace(AutomationProperties.GetName(s)))) throw new Exception("Slider name missing");
            foreach (var slider in sliders)
            {
                slider.ApplyTemplate();
                if (!slider.Template.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsKeyboardFocusWithinProperty)) throw new Exception("Slider focus state missing");
            }
        });
        Check("Overlay drag handler covers caption background and still respects lock", () =>
        {
            var window = new OverlayWindow(new());
            var border = (Border)window.FindName("OuterBorder");
            if (!border.IsHitTestVisible) throw new Exception();
            window.SetActivity(SubtitleActivity.Listening);
            window.SetListeningFeedback("等待识别");
            if (((OutlinedText)window.FindName("ChineseCaption")).Text != "等待识别") throw new Exception();
            var locked = new OverlayWindow(new() { Locked = true }); locked.ApplySettings();
            if (locked.ResizeMode != ResizeMode.NoResize) throw new Exception();
        });
        Check("Only visible text backgrounds drag; blank range and clipped lines pass through", () =>
        {
            var settings = new OverlaySettings { PanelOpacity = 0, Locked = false, ClickThrough = false };
            var window = new OverlayWindow(settings);
            var chinese=(OutlinedText)window.FindName("ChineseCaption");chinese.Text="船舶稳性\n重心";
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1050, 245)); content.Arrange(new Rect(0, 0, 1050, 245)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1050, 245, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(400, 215, 1, 1), pixel, 4, 0);
            if (pixel[3] != 0 || settings.PanelOpacity != 0) throw new Exception("Blank pixels are not completely transparent");
            if(window.CaptionHitTest(new(400,215))!=-1||window.CaptionHitTest(new(1047,242))!=-1)throw new Exception("Blank area still initiates native move or resize");
            var colored=chinese.TranslatePoint(new Point(20,25),content);
            if(window.CaptionHitTest(colored)!=2||!chinese.IsOnTextBackdrop(new(20,25)))throw new Exception("Visible background does not initiate native drag");
            var afterShortLine=chinese.TranslatePoint(new Point(350,85),content);
            if(window.CaptionHitTest(afterShortLine)!=-1||chinese.IsOnTextBackdrop(new(350,85)))throw new Exception("Empty trailing line area still drags");
            bitmap.CopyPixels(new Int32Rect((int)afterShortLine.X,(int)afterShortLine.Y,1,1),pixel,4,0);
            if(pixel[3]!=0)throw new Exception("The unused part of a short line still intercepts native layered input");
            var scroll=(ScrollViewer)window.FindName("ChineseScroll");scroll.MaxHeight=50;content.Measure(new Size(1050,245));content.Arrange(new Rect(0,0,1050,245));content.UpdateLayout();bitmap.Render(content);
            if(window.CaptionHitTest(chinese.TranslatePoint(new Point(20,85),content))!=-1)throw new Exception("Clipped background still responds outside its viewport");
            settings.Locked=true;window.ApplySettings();content.Measure(new Size(1050,245));content.Arrange(new Rect(0,0,1050,245));content.UpdateLayout();bitmap.Render(content);
            if(window.CaptionHitTest(colored)!=1)throw new Exception("Fixed position still initiates native drag");
            settings.ClickThrough=true;window.ApplySettings();if(window.CaptionHitTest(colored)!=-1)throw new Exception("Click through no longer works");
        });
        Check("Control window loads XAML resources without credentials", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            if (window.FindName("ModeBox") is not null) throw new Exception("Obsolete scheduling control remains");
            if (window.Running) throw new Exception();
        });
        Check("Opening settings alone does not persist credentials", () =>
        {
            var window = new SettingsWindow(new(root), new() { OutputDirectory = Path.Combine(root, "sessions") }, null);
            if (window.Credentials is not null || File.Exists(Path.Combine(root, "credentials.vault"))) throw new Exception();
        });
        Check("Overlay defaults retain larger Chinese text and accept source revisions", () =>
        {
            var window = new OverlayWindow(new()); window.ApplySettings();
            var chinese = (OutlinedText)window.FindName("ChineseCaption"); var russian = (OutlinedText)window.FindName("RussianCaption");
            if (chinese.TextSize != 40 || russian.TextSize != 22) throw new Exception();
            window.SetCaption(new([], [new(1, "Судно не имеет крена.", "船舶没有横倾。", false, false, DateTimeOffset.UtcNow, [1, 2])], "", 2, 1));
            if (chinese.Text != "船舶没有横倾。" || russian.Text != "Судно не имеет крена.") throw new Exception();
        });
        Check("Demo overlay hides the live status icon", () =>
        {
            var window = new OverlayWindow(new());
            window.SetCaption(new CaptionState([], [], "Привет", 1, 1), SubtitleActivity.Demo);
            if (((OutlinedText)window.FindName("RussianCaption")).StatusIcon is not null || ((OutlinedText)window.FindName("ChineseCaption")).StatusIcon is not null) throw new Exception();
            window.Close();
        });
        Check("Caption revisions preserve matching islands and support Unicode graphemes", () =>
        {
            var old = CaptionDiff.Elements("船舶的初稳性会减小，因此需要调整货物位置。");
            var next = CaptionDiff.Elements("船舶初稳性减小，因此必须调整货物的位置。");
            var matches = CaptionDiff.Match(old, next);
            if (matches.Where(i => i >= 0).Count() < 16) throw new Exception("Shared text was discarded");
            for (var i = 0; i < next.Length; i++) if (matches[i] >= 0 && old[matches[i]] != next[i]) throw new Exception("Wrong glyph reused");
            if (CaptionDiff.Elements("е́😀中").Length != 3) throw new Exception("Grapheme split");
        });
        Check("Caption morph retains old letters, animates deletions and accepts mid-transition updates", () =>
        {
            var view = new OutlinedText { Width = 600, Height = 150, Text = "船舶稳性会减小。", Outline = false };
            void Layout() { view.Measure(new Size(600, 150)); view.Arrange(new Rect(0, 0, 600, 150)); view.UpdateLayout(); var bmp = new RenderTargetBitmap(600, 150, 96, 96, PixelFormats.Pbgra32); bmp.Render(view); }
            Layout();
            view.SetAnimatedText("船舶初稳性将减小。", true); Layout();
            if (!view.IsAnimating || view.StableGlyphCount < 6) throw new Exception("Unchanged characters disappeared");
            view.AdvanceAnimation(.08);
            view.SetAnimatedText("船舶初稳性会明显减小。", true); Layout();
            view.AdvanceAnimation(3); Layout();
            if (view.IsAnimating || view.Text != "船舶初稳性会明显减小。") throw new Exception("Retarget failed");
            view.SetAnimatedText("直接显示", false); Layout();
            if (view.IsAnimating) throw new Exception("Reduced motion still animates");
        });
        Check("Long Chinese results and revisions reveal progressively without flushing pending letters", () =>
        {
            var view=new OutlinedText {TextSize=22,Outline=false};
            void Render(){view.Measure(new Size(1000,300));view.Arrange(new Rect(0,0,1000,300));new RenderTargetBitmap(1000,300,96,96,PixelFormats.Pbgra32).Render(view);}
            var text=string.Concat(Enumerable.Repeat("船舶稳性取决于重心位置和浮力变化。",6));
            view.SetTranslation(text,"",true);Render();
            view.AdvanceAnimation(.6);Render();
            var first=view.StableGlyphCount;
            if(first<3||first>28||!view.IsAnimating)throw new Exception($"Long result appeared in a burst: {first}");
            view.SetTranslation("该"+text,"",true);Render();
            view.AdvanceAnimation(.1);Render();
            if(view.StableGlyphCount>first+6)throw new Exception("Revision flushed the queued unchanged suffix");
            view.AdvanceAnimation(.6);Render();
            if(view.StableGlyphCount<=first||!view.IsAnimating)throw new Exception("Reveal stalled or finished in a burst");
            view.AdvanceAnimation(10);Render();
            if(view.IsAnimating||view.StableGlyphCount!=text.Length+1)throw new Exception("Latest result did not finish");
            view.SetTranslation(string.Concat(Enumerable.Repeat("这是完全不同的新译句。",8)),"",true);Render();
            view.AdvanceAnimation(.6);Render();
            if(!view.IsAnimating||view.StableGlyphCount>30)throw new Exception("Whole-sentence revision bypassed progressive reveal");
        });
        Check("Long rolling results start revealing visible rows without waiting for hidden history", () =>
        {
            var view=new OutlinedText {TextSize=24,LineHeight=36,ViewportHeight=108,Outline=false};
            void Render(){view.Measure(new Size(450,108));view.Arrange(new Rect(0,0,450,108));new RenderTargetBitmap(450,108,96,96,PixelFormats.Pbgra32).Render(view);}
            view.SetTranslation(string.Concat(Enumerable.Repeat("船舶的重心和浮力共同影响稳性。",25)),"",true);Render();
            view.AdvanceAnimation(.35);Render();
            if(view.VisibleGlyphCount<3||!view.IsAnimating)throw new Exception("Visible rows were blank or appeared together");
        });
        Check("Actual demo steps progressively reveal final Chinese on the floating subtitle surface", () =>
        {
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var window=new MainWindow(new SettingsStore(root));var engine=new CaptionEngine();
            using var cancellation=new CancellationTokenSource();
            typeof(MainWindow).GetField("engine",flags)!.SetValue(window,engine);
            typeof(MainWindow).GetField("runCancellation",flags)!.SetValue(window,cancellation);
            typeof(MainWindow).GetField("isDemo",flags)!.SetValue(window,true);
            var examples=((string Ru,string Zh)[])typeof(MainWindow).GetField("DemoExamples",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            var overlay=new OverlayWindow(new());
            var floating=(OutlinedText)overlay.FindName("ChineseCaption");
            var content=(FrameworkElement)window.Content;
            void Render()
            {
                content.Measure(new Size(1000,690));content.Arrange(new Rect(0,0,1000,690));content.UpdateLayout();new RenderTargetBitmap(1000,690,96,96,PixelFormats.Pbgra32).Render(content);
                floating.Measure(new Size(1000,190));floating.Arrange(new Rect(0,0,1000,190));new RenderTargetBitmap(1000,190,96,96,PixelFormats.Pbgra32).Render(floating);
            }
            for(var index=0;index<examples.Length;index++)
            {
                var words=examples[index].Ru.Split(' ');
                for(var n=1;n<=words.Length;n++)
                {
                    typeof(MainWindow).GetMethod("UpdateDemoCaption",flags)!.Invoke(window,[index,n]);
                    typeof(MainWindow).GetMethod("RefreshCaptions",flags)!.Invoke(window,null);
                    overlay.SetCaption(engine.State(),SubtitleActivity.Demo,true);Render();
                    for(var frame=0;frame< (n==words.Length?6:33);frame++){floating.AdvanceAnimation(1.0/60);Render();}
                    if(n==words.Length&&SystemParameters.ClientAreaAnimation)
                    {
                        var length=CaptionFlowComposer.Compose(engine.State()).Chinese.Length;
                        if(floating.StableGlyphCount>=length)throw new Exception($"Demo {index} final translation appeared in one burst");
                    }
                }
                floating.AdvanceAnimation(4);Render();
                var expected=CaptionFlowComposer.Compose(engine.State()).Chinese;
                if(floating.IsAnimating||floating.Text!=expected)throw new Exception("Demo did not finish its queued Chinese");
            }
            typeof(MainWindow).GetField("runCancellation",flags)!.SetValue(window,null);
        });
        Check("Animation frames reuse glyph layout and retained drawing", () =>
        {
            var view = new OutlinedText { Width=700, Height=160, Text="船舶的初稳性会减小，因此需要调整货物位置。", Outline=false };
            view.Measure(new Size(700,160)); view.Arrange(new Rect(0,0,700,160)); view.UpdateLayout();
            void Render() { view.Measure(new Size(700,160)); view.Arrange(new Rect(0,0,700,160)); view.UpdateLayout(); var bitmap = new RenderTargetBitmap(700,160,96,96,PixelFormats.Pbgra32); bitmap.Render(view); }
            Render(); view.SetAnimatedText("船舶初稳性明显减小，因此必须调整货物的位置。",true); Render();
            var builds=view.LayoutBuilds;
            for(var i=0;i<90;i++) { view.AdvanceAnimation(.016); Render(); }
            if(view.LayoutBuilds!=builds || view.IsAnimating) throw new Exception($"Animation layout {builds} -> {view.LayoutBuilds}; active={view.IsAnimating}");
        });
        Check("Text appearance offers paired black/white themes and preserves legacy settings", () =>
        {
            var settings=new OverlaySettings { AdaptiveContrast=true, PanelOpacity=.57, ChineseColor="#A5E4FF", RussianColor="#FFD98A" };
            var window=new AppearanceWindow(settings,()=>{});var root=(FrameworkElement)window.Content;
            root.Measure(new Size(470,730));root.Arrange(new Rect(0,0,470,730));root.UpdateLayout();
            var options=Descendants<RadioButton>(root).ToArray();
            if(options.Length!=2||!options.Any(c=>Equals(c.Content,"黑底白字"))||!options.Any(c=>Equals(c.Content,"白底黑字")))throw new Exception("Missing paired themes");
            foreach(var option in options)
            {
                option.ApplyTemplate();
                if(!option.Template.Triggers.OfType<Trigger>().Any(t=>t.Property==ToggleButton.IsCheckedProperty)||!option.Template.Triggers.OfType<Trigger>().Any(t=>t.Property==UIElement.IsKeyboardFocusedProperty))throw new Exception("Theme selection or focus is invisible");
            }
            options.Single(c=>Equals(c.Content,"白底黑字")).IsChecked=true;
            if(!settings.LightTextBackground||options.Single(c=>Equals(c.Content,"黑底白字")).IsChecked==true)throw new Exception("Theme selection did not update settings");
            if(Descendants<CheckBox>(root).Any(c=>Equals(c.Content,"按背景自动变色")))throw new Exception("Obsolete desktop sampling control remains");
            var transparency=Descendants<Slider>(root).Single(s=>AutomationProperties.GetName(s)=="底色透明度");transparency.Value=65;
            if(Math.Abs(settings.TextBackgroundOpacity-.35)>.00001||Descendants<OutlinedText>(root).Any(v=>Math.Abs(v.BackdropOpacity-.35)>.00001))throw new Exception("Transparency slider did not update both language previews");
            if(settings.PanelOpacity!=.57 || settings.ChineseColor!="#A5E4FF" || settings.RussianColor!="#FFD98A") throw new Exception("Manual colors were overwritten");
        });
        Check("Overlay is frameless with text-sized translucent backgrounds and bundled Chinese font", () =>
        {
            foreach(var light in new[] {false,true})
            {
                var window=new OverlayWindow(new() {LightTextBackground=light,AdaptiveContrast=true,PanelOpacity=1});
                var border=(Border)window.FindName("OuterBorder");
                var brush=(SolidColorBrush)border.Background;
                if(border.BorderThickness!=new Thickness(0)||brush.Opacity>1.0/255+.00001||window.ResizeMode==ResizeMode.CanResizeWithGrip)throw new Exception("Visible outer window chrome remains");
                if(((OutlinedText)window.FindName("ChineseCaption")).StatusIcon is null)throw new Exception("Caption status icon is missing");
                foreach(var name in new[] {"RussianCaption","ChineseCaption"})
                {
                    var view=(OutlinedText)window.FindName(name);
                    if(!view.TextBackdrop||view.LightBackdrop!=light||view.Color!=(light?"#000000":"#FFFFFF")||view.AdaptiveForeground!=null)throw new Exception("Fill is not paired with the text background");
                    if(view.BackdropOpacity!=.75)throw new Exception("Default background is not translucent");
                }
                var chinese=(OutlinedText)window.FindName("ChineseCaption");
                if(chinese.Weight!=FontWeights.Normal||chinese.LineHeight<chinese.TextSize*1.45||!chinese.FontFamily.Source.Contains("Assets/Fonts"))throw new Exception("Chinese typography not updated");
                if(!chinese.FontFamily.GetTypefaces().First().TryGetGlyphTypeface(out var font)||!font.CharacterToGlyphMap.ContainsKey('船'))throw new Exception("Bundled font did not load");
            }
        });
        Check("Background alpha changes independently from text and does not rebuild layout", () =>
        {
            foreach(var light in new[] {false,true})
            {
                var view=new OutlinedText {Text="船舶稳性",TextSize=40,LineHeight=60,FontFamily=CaptionTypography.Chinese,TextBackdrop=true,LightBackdrop=light,Color=light?"#000000":"#FFFFFF",Outline=false,BackdropOpacity=.4};
                view.Measure(new Size(400,100));view.Arrange(new Rect(0,0,400,100));
                byte[] Render(){var bmp=new RenderTargetBitmap(400,100,96,96,PixelFormats.Pbgra32);bmp.Render(view);var pixels=new byte[400*100*4];bmp.CopyPixels(pixels,1600,0);return pixels;}
                var pixels=Render();var background=0;var ink=0;
                for(var i=0;i<pixels.Length;i+=4){if(pixels[i+3]>=100&&pixels[i+3]<=104)background++;if(pixels[i+3]>=250)ink++;}
                if(background<100||ink<100)throw new Exception("Transparency faded the letters or did not affect the background");
                var builds=view.LayoutBuilds;view.BackdropOpacity=.7;var changed=Render();
                if(pixels.SequenceEqual(changed)||view.LayoutBuilds!=builds)throw new Exception("Opacity adjustment failed or rebuilt text layout");
                view.BackdropOpacity=0;Render();if(view.IsOnTextBackdrop(new(20,25)))throw new Exception("An invisible background remains a drag target");
            }
        });
        Check("Connected multiline backgrounds have uniform alpha with no gaps or overlap seams", () =>
        {
            foreach(var light in new[] {false,true})
            foreach(var size in new[] {22.0,40.0})
            {
                var lineHeight=size*(size==22?1.6:1.5);
                var height=(int)Math.Ceiling(OverlayWindow.ThreeLineHeight(lineHeight));
                var view=new OutlinedText {Text="船舶稳性\n重心位置\n载荷分布",TextSize=size,LineHeight=lineHeight,FontFamily=CaptionTypography.Chinese,TextBackdrop=true,LightBackdrop=light,Color=light?"#000000":"#FFFFFF",Outline=false,BackdropOpacity=.4};
                view.Measure(new Size(500,height));view.Arrange(new Rect(0,0,500,height));
                var bitmap=new RenderTargetBitmap(500,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
                var pixels=new byte[500*height*4];bitmap.CopyPixels(pixels,2000,0);
                // Padding at x=5 contains background only, including both row joins.
                for(var y=8;y<(int)(lineHeight*3);y++)
                {
                    var alpha=pixels[(y*500+5)*4+3];
                    if(alpha<100||alpha>104)throw new Exception($"Gap or doubled alpha at row join: {size}/{light}/{y}/{alpha}");
                }
                if(!view.IsOnTextBackdrop(new(5,lineHeight+2)))throw new Exception("The connected background gap is not draggable");
            }
        });
        Check("Overlay reserves three complete lines for each language and uses a compact language gap", () =>
        {
            var window=new OverlayWindow(new());
            var russian=(OutlinedText)window.FindName("RussianCaption");var chinese=(OutlinedText)window.FindName("ChineseCaption");
            russian.Text="АБ\nВГ\nДЕ\nЖЗ";chinese.Text="甲乙\n丙丁\n戊己\n庚辛";
            var height=OverlayWindow.RequiredOverlayHeight(russian.TextSize,chinese.TextSize);
            var content=(FrameworkElement)window.Content;content.Measure(new Size(1366,height));content.Arrange(new Rect(0,0,1366,height));content.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1366,(int)Math.Ceiling(height),96,96,PixelFormats.Pbgra32);bitmap.Render(content);
            if(russian.VisibleGlyphCount!=6||chinese.VisibleGlyphCount!=6)throw new Exception($"Expected three full two-letter rows, got {russian.VisibleGlyphCount}/{chinese.VisibleGlyphCount}");
            if(Math.Abs(russian.ViewportHeight-(3*russian.LineHeight+12))>.01||Math.Abs(chinese.ViewportHeight-(3*chinese.LineHeight+12))>.01)throw new Exception("Viewport still divides a fixed-height panel by percentages");
            if(((ScrollViewer)window.FindName("RussianScroll")).Margin.Bottom>2||((Border)window.FindName("OuterBorder")).Padding!=new Thickness(0))throw new Exception("Large language gap or screen-edge inset remains");
            var ruEnd=russian.TranslatePoint(new Point(0,russian.ActualHeight),content).Y;
            var zhStart=chinese.TranslatePoint(new Point(),content).Y;
            if(zhStart-ruEnd>2.01)throw new Exception("Chinese is detached from the Russian block");
        });
        Check("Overlay fills monitor bounds at mixed DPI and preserves vertical positioning", () =>
        {
            var requested=OverlayWindow.RequiredOverlayHeight(22,40);
            foreach(var (bounds,work) in new[] {
                (new Rect(0,0,1920,1080),new Rect(0,0,1920,1040)),
                (new Rect(-1920,0,1920,1080),new Rect(-1920,0,1920,1040)),
                (new Rect(1920,0,3072,1728),new Rect(1920,0,3072,1696)),
                (new Rect(0,0,2560,1440),new Rect(48,0,2512,1440))})
            {
                var result=OverlayWindow.ScreenLayout(bounds,work,80,requested);
                if(result.Left!=bounds.Left||result.Right!=bounds.Right||result.Top!=80||Math.Abs(result.Height-requested)>.01)throw new Exception("Screen width is capped or uses only the horizontal work area");
                var bottom=OverlayWindow.ScreenLayout(bounds,work,work.Bottom-5,requested);
                if(bottom.Bottom>work.Bottom+.01)throw new Exception("Three-line overlay is pushed below the screen");
            }
        });
        Check("Hidden native overlay initializes at the actual monitor width without showing a desktop window", () =>
        {
            var window=new OverlayWindow(new() {Width=640,Height=150});
            try
            {
                var handle=new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                var screen=NativeDisplays.FromWindow(handle);var dpi=VisualTreeHelper.GetDpi(window);
                if(window.IsVisible||Math.Abs(window.Width-screen.Bounds.Width/dpi.DpiScaleX)>1)throw new Exception("Native initialization keeps the old saved width or shows a window");
                if(Math.Abs(window.Height-OverlayWindow.RequiredOverlayHeight(22,40))>1)throw new Exception("Native initialization keeps the old two-line height");
                if(!GetWindowRect(handle,out var native)||Math.Abs(native.Left-screen.Bounds.Left)>2||Math.Abs(native.Right-screen.Bounds.Right)>2)throw new Exception("Native overlay is not aligned to the monitor edges");
            }
            finally { window.Close(); }
        });
        Check("Wide overlay retains enough source text to fill three rows beyond the old fixed buffer", () =>
        {
            var overlay=new OverlayWindow(new()) {Width=3840};
            overlay.SetCaption(new([], [new(1,new string('я',1800),new string('汉',800),false,false,DateTimeOffset.UtcNow,[1])],"",0,1),SubtitleActivity.Stopped);
            var russian=(OutlinedText)overlay.FindName("RussianCaption");var chinese=(OutlinedText)overlay.FindName("ChineseCaption");
            if(russian.Text.Length<=600||chinese.Text.Length<=400)throw new Exception("Full-width view is still capped by the narrow-window text budget");
        });
        Check("Text backgrounds follow wrapped lines and animate without rebuilding glyph layouts", () =>
        {
            foreach(var light in new[] {false,true})
            {
                var view=new OutlinedText {Text="船舶稳性\n重心",TextSize=40,LineHeight=60,FontFamily=CaptionTypography.Chinese,Weight=FontWeights.Normal,TextBackdrop=true,LightBackdrop=light,Color=light?"#000000":"#FFFFFF",Outline=false};
                view.Measure(new Size(500,180));view.Arrange(new Rect(0,0,500,180));
                byte[] Render(){var bmp=new RenderTargetBitmap(500,180,96,96,PixelFormats.Pbgra32);bmp.Render(view);var data=new byte[500*180*4];bmp.CopyPixels(data,2000,0);return data;}
                var pixels=Render();var widths=new List<int>();
                foreach(var row in new[] {25,85})
                {var right=Enumerable.Range(0,500).Where(x=>pixels[(row*500+x)*4+3]>240).DefaultIfEmpty(-1).Max();widths.Add(right);}
                if(widths[0]<150||widths[0]>230||widths[1]<70||widths[1]>=widths[0]-40)throw new Exception($"Line backgrounds do not fit text: {string.Join(',',widths)}");
                if(pixels[(150*500+450)*4+3]!=0)throw new Exception("Background fills unused display range");
                var opaqueBlack=0;var opaqueWhite=0;
                for(var i=0;i<pixels.Length;i+=4)if(pixels[i+3]>250){if(pixels[i]<5&&pixels[i+1]<5&&pixels[i+2]<5)opaqueBlack++;if(pixels[i]>250&&pixels[i+1]>250&&pixels[i+2]>250)opaqueWhite++;}
                if(opaqueBlack<100||opaqueWhite<100)throw new Exception("Opaque paired fill missing");
                view.SetAnimatedText("船舶的稳性\n重心升高",true);Render();var builds=view.LayoutBuilds;
                for(var frame=0;frame<30;frame++){view.AdvanceAnimation(1.0/60);Render();}
                if(view.LayoutBuilds!=builds)throw new Exception("Text backgrounds rebuilt layout on each animation frame");
            }
        });
        Check("Adaptive contrast changes color inside one Chinese or Russian glyph without rebuilding layout", () =>
        {
            foreach (var letter in new[] { "田", "Ж" })
            {
                var pixels = new byte[180*180*4];
                for (var y=0;y<180;y++) for (var x=0;x<180;x++) { var i=(y*180+x)*4; pixels[i]=pixels[i+1]=pixels[i+2]=(byte)(x<70?255:0); pixels[i+3]=255; }
                var mapped = AdaptiveContrast.MapBgra(pixels);
                var image=BitmapSource.Create(180,180,96,96,PixelFormats.Bgra32,null,mapped,180*4); image.Freeze();
                var view=new OutlinedText { Width=180,Height=180,Text=letter,TextSize=140,Outline=false,
                    AdaptiveForeground=new ImageBrush(image) { ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,180,180),Stretch=Stretch.Fill } };
                view.Measure(new Size(180,180));view.Arrange(new Rect(0,0,180,180));view.UpdateLayout();
                byte[] Render() { var bitmap=new RenderTargetBitmap(180,180,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var result=new byte[180*180*4];bitmap.CopyPixels(result,180*4,0);return result; }
                var result=Render();var left=0;var right=0;
                for(var y=0;y<180;y++) for(var x=0;x<180;x++) { var i=(y*180+x)*4; if(result[i+3]>240) { if(x<69 && result[i]+result[i+1]+result[i+2]<400)left++;if(x>71&&result[i]+result[i+1]+result[i+2]>550)right++; } }
                if(left<30||right<30)throw new Exception("One glyph was assigned a single color");
                var builds=view.LayoutBuilds;
                view.AdaptiveForeground=new LinearGradientBrush(Colors.White,Colors.Black,0);
                Render();if(view.LayoutBuilds!=builds)throw new Exception("Background updates rebuilt text geometry");
            }
        });
        Check("Dragging projects a cached surrounding frame onto the new position without waiting for recapture", () =>
        {
            var capture=new Rect(0,0,500,240);var start=new Rect(64,64,200,100);
            if(!BackdropContrastSampler.TryMapFrame(capture,start,out var before))throw new Exception();
            var moved=new Rect(140,90,200,100);
            if(!BackdropContrastSampler.TryMapFrame(capture,moved,out var after)||after.X-before.X!=76||after.Y-before.Y!=26)throw new Exception("A moved window discarded its usable frame");
            if(BackdropContrastSampler.TryMapFrame(capture,new Rect(450,0,200,100),out _))throw new Exception("Uncovered frame accepted");
            var view=new OutlinedText {Text="田",TextSize=110,Width=160,Height=150,Outline=false};
            var colors=new LinearGradientBrush(Colors.Red,Colors.Cyan,new Point(0,0),new Point(1,0));
            var tile=new DrawingVisual();using(var dc=tile.RenderOpen())dc.DrawRectangle(colors,null,new Rect(0,0,500,150));
            var bitmap=new RenderTargetBitmap(500,150,96,96,PixelFormats.Pbgra32);bitmap.Render(tile);
            var brush=new ImageBrush(bitmap){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,160,150),ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,160,150),Stretch=Stretch.Fill};
            view.AdaptiveForeground=brush;view.Measure(new Size(160,150));view.Arrange(new Rect(0,0,160,150));view.UpdateLayout();
            byte[] Render(){var bmp=new RenderTargetBitmap(160,150,96,96,PixelFormats.Pbgra32);bmp.Render(view);var data=new byte[160*150*4];bmp.CopyPixels(data,640,0);return data;}
            var first=Render();var builds=view.LayoutBuilds;brush.Viewbox=new Rect(300,0,160,150);var second=Render();
            if(first.SequenceEqual(second)||view.LayoutBuilds!=builds)throw new Exception("Moving the sampling viewport did not update color or rebuilt glyph layout");
        });
        Check("A pending ASR revision keeps A visible until B is available", () =>
        {
            var view = new OutlinedText();
            view.SetTranslation("船舶稳性减小。", "等待译文…", false);
            if (!view.SetTranslation("", "等待译文…", true) || view.Text != "船舶稳性减小。") throw new Exception("A disappeared while awaiting B");
            view.SetTranslation("船舶初稳性明显减小。", "等待译文…", true);
            if (view.Text != "船舶初稳性明显减小。") throw new Exception("B not queued");
            if(!view.SetTranslation("船舶", "等待译文…",true,awaitingRevision:true)||view.Text!="船舶初稳性明显减小。")throw new Exception("An intermediate translated prefix deleted the old tail");
            view.SetTranslation("船舶稳性明显降低。","等待译文…",true,awaitingRevision:false);
            if(view.Text!="船舶稳性明显降低。")throw new Exception("The complete replacement was held back");
            view.SetTranslation("", "等待译文…", true, true);
            if (view.Text != "等待译文…") throw new Exception("Old translation leaked into next page");
        });
        Check("Overlay controls do not initiate window dragging", () =>
        {
            if (!OverlayWindow.IsInteractive(new Thumb()) || !OverlayWindow.IsInteractive(new ScrollBar()) || OverlayWindow.IsInteractive(new Border())) throw new Exception();
        });
        Check("Empty subtitles report the actual activity without implying capture before start or after stop", () =>
        {
            var window = new OverlayWindow(new());
            var chinese = (OutlinedText)window.FindName("ChineseCaption");
            string Label() => chinese.StatusLabel;
            if (chinese.Text != "尚未开始翻译") throw new Exception();
            window.SetCaption(new([], [], "", 0, 0), SubtitleActivity.Connecting);
            if (!chinese.Text.Contains("连接") || Label().Contains("收音")) throw new Exception();
            window.SetActivity(SubtitleActivity.Listening);
            if (chinese.Text != "等待俄语发言…" || Label() != "翻译运行中") throw new Exception();
            var runningIcon = chinese.StatusIcon;
            window.SetActivity(SubtitleActivity.Faulted);
            var faultIcon = chinese.StatusIcon;
            if (Label() != "错误中断" || chinese.Text != "翻译因错误停止") throw new Exception();
            window.SetActivity(SubtitleActivity.Stopped);
            if (ReferenceEquals(runningIcon, faultIcon) || ReferenceEquals(chinese.StatusIcon, faultIcon) || ReferenceEquals(chinese.StatusIcon, runningIcon))
                throw new Exception("Status icons are not distinguishable");
            window.SetListeningFeedback("正在聆听");
            if (chinese.Text != "本次翻译已停止" || !Label().StartsWith("已停止")) throw new Exception();
            window.SetCaption(new([], [new(1, "Добрый день!", "中午好！", false, false, DateTimeOffset.UtcNow, [1])], "", 0, 1), SubtitleActivity.Stopped);
            var russian = (OutlinedText)window.FindName("RussianCaption");
            if (chinese.Text != "中午好！" || russian.StatusLabel != "已停止" || chinese.StatusIcon is not null) throw new Exception();
        });
        Check("Status icon is inside the visible backdrop of short captions and moves to the first visible language", () =>
        {
            var window = new OverlayWindow(new());
            var russian = (OutlinedText)window.FindName("RussianCaption");
            var chinese = (OutlinedText)window.FindName("ChineseCaption");
            void RenderAndCheck(OutlinedText active, OutlinedText other)
            {
                window.PrepareTransition();
                var root = (FrameworkElement)window.Content;
                new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight),
                    96, 96, PixelFormats.Pbgra32).Render(root);
                var bounds = active.StatusBounds;
                if (bounds.IsEmpty) throw new Exception("Status icon was not drawn");
                var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                if (bounds.Right > 40 || !active.IsOnTextBackdrop(center) || other.StatusIcon is not null)
                    throw new Exception("Status icon is detached from the visible subtitle");
            }
            window.SetActivity(SubtitleActivity.Listening);
            window.SetListeningFeedback("声音已发送，尚无识别结果");
            RenderAndCheck(chinese, russian);
            window.SetCaption(new([new(1,"Привет","你好",true,true,DateTimeOffset.Now,[])],[],"",0,1), SubtitleActivity.Reconnecting);
            RenderAndCheck(russian, chinese);
            window.Close();
        });
        Check("Subtitle outlines retain bright letter interiors", () =>
        {
            int BrightPixels(bool outline,bool adaptive=false)
            {
                var text = new OutlinedText { Text = "Сегодня мы учимся. 中文字幕清晰可读", TextSize = 22, Outline = outline };
                if(adaptive)text.AdaptiveForeground=Brushes.White;
                text.Measure(new Size(640, 80)); text.Arrange(new Rect(0, 0, 640, 80));
                var bitmap = new RenderTargetBitmap(640, 80, 96, 96, PixelFormats.Pbgra32); bitmap.Render(text);
                var pixels = new byte[640 * 80 * 4]; bitmap.CopyPixels(pixels, 640 * 4, 0);
                var bright = 0;
                for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200) bright++;
                return bright;
            }
            var filled = BrightPixels(false);
            if (filled < 100 || BrightPixels(true) < filled * .9 || BrightPixels(false,true)<filled*.9) throw new Exception("Outline obscures text fill");
        });
        Check("Old default styling upgrades without moving the overlay or replacing custom colors", () =>
        {
            var style = new OverlaySettings { Left = 37, Top = 61, RussianColor = "#D6E9E3", PanelOpacity = .78 };
            style.UpgradeAppearance();
            if (style.RussianColor != "#FFD98A" || style.PanelOpacity != .92 || style.Left != 37 || style.Top != 61) throw new Exception();
            style.RussianColor = "#A5E4FF"; style.PanelOpacity = .5; style.UpgradeAppearance();
            if (style.RussianColor != "#A5E4FF" || style.PanelOpacity != .5) throw new Exception();
        });
        Check("Outlined Russian and Chinese text measure within bounded subtitle width", () =>
        {
            foreach (var text in new[] { "Водоизмещение составляет не пятнадцать, а пятьдесят тысяч тонн.", "这个例子中的排水量不是一万五千吨，而是五万吨。" })
            {
                var control = new OutlinedText { Text = text, TextSize = 40 };
                control.Measure(new Size(600, 150)); control.Arrange(new Rect(0, 0, 600, 150));
                if (!double.IsFinite(control.DesiredSize.Height) || control.DesiredSize.Width > 600) throw new Exception();
            }
        });
        Check("Tray menu paints an opaque surface and retains both actions", () =>
        {
            var trayType=typeof(MainWindow).Assembly.GetType("ClassroomCaption.App.TrayIcon")!;
            var restored=false;var exited=false;
            var menu=(ContextMenu)trayType.GetMethod("CreateTranslationMenu",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,[new Action(()=>restored=true),new Action(()=>exited=true),false,false])!;
            menu.ApplyTemplate();menu.Measure(new Size(220,150));menu.Arrange(new Rect(0,0,220,menu.DesiredSize.Height));menu.UpdateLayout();
            var bitmap=new RenderTargetBitmap(220,(int)Math.Ceiling(menu.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(menu);
            var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            var center=((bitmap.PixelHeight/2)*bitmap.PixelWidth+bitmap.PixelWidth/2)*4;
            if(pixels[center+3]!=255)throw new Exception("Tray menu background is transparent");
            if(menu.Items.Count!=2)throw new Exception("Tray actions missing");
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if(!restored||!exited)throw new Exception("Tray actions no longer work");
            var outputIndex=Array.IndexOf(args,"--render-dir");
            if(outputIndex>=0&&outputIndex+1<args.Length){var folder=Path.GetFullPath(args[outputIndex+1]);Directory.CreateDirectory(folder);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(folder,"tray-menu.png"));encoder.Save(file);}
        });
        Check("Appearance window construction does not require screen capture", () => { _ = new AppearanceWindow(new(), () => { }); });
        Check("Floating captions retain consecutive clauses while the control window stays lightweight", () =>
        {
            var engine = new CaptionEngine();
            engine.Update(new("s1", "Центр тяжести поднимается.", true)); var one = engine.CreateSnapshot()!;
            engine.Apply(one, [new(0, one.Tokens.Count, "重心在升高。", true)]);
            engine.Update(new("s2", "Начальная остойчивость уменьшается.", true)); var two = engine.CreateSnapshot()!;
            engine.Apply(two, [new(0, two.Tokens.Count, "初稳性会减小。", true)]);
            var overlay = new OverlayWindow(new()); overlay.SetCaption(engine.State(), SubtitleActivity.Listening);
            if (((OutlinedText)overlay.FindName("ChineseCaption")).Text != "重心在升高。初稳性会减小。") throw new Exception("Only latest phrase is queued");
            var main = new MainWindow(new(root));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(MainWindow).GetField("engine", flags)!.SetValue(main, engine);
            typeof(MainWindow).GetMethod("RefreshCaptions", flags)!.Invoke(main, null);
            if(main.FindName("CurrentChinese") is not null)throw new Exception("Removed duplicate caption surface returned");

        });
        Check("Ordinary connection setup foregrounds API keys and collapses address settings", () =>
        {
            var window = new SettingsWindow(new(root), new() { OutputDirectory = Path.Combine(root, "sessions") }, null);
            if (window.FindName("AliyunApiKey") is not TextBox) throw new Exception();
            if (window.FindName("AdvancedConnection") is not Expander { IsExpanded: false }) throw new Exception();
        });
        Check("Model connection test is only offered on its own settings tab", () =>
        {
            var window = new SettingsWindow(new(root), new() { OutputDirectory = Path.Combine(root, "sessions") }, null);
            var tabs = (TabControl)window.FindName("SettingsTabs");
            var test = (Button)window.FindName("TestConnectionButton");
            if (test.Visibility != Visibility.Visible) throw new Exception("Connection test is unavailable on the model tab");
            if (test.Background is not SolidColorBrush { Color.A: > 0 }) throw new Exception("Connection test does not look like an action");
            tabs.SelectedIndex = 1;
            if (test.Visibility != Visibility.Collapsed) throw new Exception("Model test appears on the classroom tab");
            tabs.SelectedIndex = 0;
            if (test.Visibility != Visibility.Visible) throw new Exception("Connection test did not return");
        });
        Check("Advanced settings arrow follows the expanded state", () =>
        {
            var window = new SettingsWindow(new(root), new() { OutputDirectory = Path.Combine(root, "sessions") }, null);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(620, 700)); content.Arrange(new Rect(0, 0, 620, 700)); content.UpdateLayout();
            var expander = (Expander)window.FindName("AdvancedConnection");
            var toggle = Descendants<ToggleButton>(expander).First();
            var arrow = (System.Windows.Shapes.Path)toggle.Template.FindName("Chevron", toggle);
            if (arrow.RenderTransform is RotateTransform { Angle: 180 }) throw new Exception("Closed arrow points up");
            expander.IsExpanded = true; content.UpdateLayout();
            if (arrow.RenderTransform is not RotateTransform { Angle: 180 }) throw new Exception("Open arrow does not point up");
        });
        Check("Long microphone names stay on one line without crowding the start button", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            var box = (ComboBox)window.FindName("DeviceBox");
            var name = "课堂麦克风阵列（适用于很长名称的 USB 音频输入设备和驱动程序）";
            var input=new AudioInput("synthetic",name);
            box.ItemsSource = new[] { input }; box.SelectedIndex = 0;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(820, 570)); content.Arrange(new Rect(0, 0, 820, 570)); content.UpdateLayout();
            var label = Descendants<TextBlock>(box).FirstOrDefault(t => t.Text == input.DisplayName) ?? throw new Exception("Selected device label missing");
            if(!Equals(label.ToolTip,name)||input.DisplayName==name)throw new Exception("Short name lost its complete hover detail");
            if (label.TextWrapping != TextWrapping.NoWrap || label.TextTrimming != TextTrimming.CharacterEllipsis || label.ActualHeight > 28)
                throw new Exception("Long device name wraps or does not trim");
            var toggle = Descendants<ToggleButton>(box).FirstOrDefault() ?? throw new Exception("Device menu button missing");
            if (toggle.ActualWidth < box.ActualWidth - 3) throw new Exception("Device menu does not use its full control width");
            var start = (Button)window.FindName("StartButton");
            var deviceBounds = box.TransformToAncestor(content).TransformBounds(new Rect(new Point(), box.RenderSize));
            var startBounds = start.TransformToAncestor(content).TransformBounds(new Rect(new Point(), start.RenderSize));
            if (deviceBounds.IntersectsWith(startBounds) || start.ActualWidth < 120) throw new Exception("Device control overlaps the primary action");
        });
        Check("Dark headings stay readable and long captions stay inside rolling viewports", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(820, 570)); content.Arrange(new Rect(0, 0, 820, 570)); content.UpdateLayout();
            var heading = Descendants<TextBlock>(content).First(t => t.Text == "麦克风");
            if (heading.Foreground is not SolidColorBrush brush || Contrast(brush.Color, Color.FromRgb(21, 38, 46)) < 4.5)
                throw new Exception("Control label lacks contrast");
            var overlay=new OverlayWindow(new());
            overlay.SetCaption(new([], [new(1,string.Join(" ",Enumerable.Repeat("Остойчивость судна.",50)),new string('汉',600),false,false,DateTimeOffset.Now,[])],"",0,1),SubtitleActivity.Stopped);
            var chinese=(OutlinedText)overlay.FindName("ChineseCaption");
            if(!double.IsFinite(chinese.ViewportHeight)||chinese.ViewportHeight>300)throw new Exception("Long floating captions have no bounded viewport");
            if(window.FindName("CaptionScroll") is not null)throw new Exception("Removed duplicate caption viewport returned");
        });
        Check("Continuous overlay captions and main history retain both old and incoming phrases", () =>
        {
            var engine=new CaptionEngine();
            for(var phrase=0;phrase<6;phrase++)
            {
                engine.Update(new("flow-"+phrase,"Фраза номер "+phrase+" завершена.",true));var snapshot=engine.CreateSnapshot()!;
                engine.Apply(snapshot,[new(0,snapshot.Tokens.Count,"这是第"+phrase+"句译文。",true)]);
            }
            var state=engine.State();var flow=CaptionFlowComposer.Compose(state);
            if(!flow.Chinese.Contains("第0句")||!flow.Chinese.Contains("第5句"))throw new Exception("Flow discarded a whole page");
            var overlay=new OverlayWindow(new());overlay.SetCaption(state,SubtitleActivity.Stopped);
            if(((OutlinedText)overlay.FindName("ChineseCaption")).Text!=flow.Chinese)throw new Exception("Overlay still pages through segments");
            var window=new MainWindow(new SettingsStore(root));
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            typeof(MainWindow).GetField("engine",flags)!.SetValue(window,engine);
            typeof(MainWindow).GetMethod("RefreshCaptions",flags)!.Invoke(window,null);
            var history=(ItemsControl)window.FindName("HistoryList");
            if(history.Items.Count!=6||!history.Items.Cast<CaptionRow>().Any(r=>r.Chinese.Contains("第5句")))throw new Exception("Control window history lost captions");
        });
        Check("Overflow scrolls glyphs upward and fades the top edge without clearing shared text", () =>
        {
            var view=new OutlinedText {Text="甲乙丙丁\n戊己庚辛",TextSize=32,LineHeight=48,ViewportHeight=108,FontFamily=CaptionTypography.Chinese,Outline=false,TextBackdrop=true};
            void Layout(){view.Measure(new Size(450,108));view.Arrange(new Rect(0,0,450,view.DesiredSize.Height));view.UpdateLayout();}
            byte[] Render(){Layout();var bitmap=new RenderTargetBitmap(450,108,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var pixels=new byte[450*108*4];bitmap.CopyPixels(pixels,1800,0);return pixels;}
            Render();view.SetAnimatedText("甲乙丙丁\n戊己庚辛\n壬癸子丑",true);Render();
            if(view.ScrollOffset<40||view.StableGlyphCount<8)throw new Exception("Existing visible rows were reset");
            var before=view.LayoutBuilds;
            for(var frame=0;frame<40;frame++){view.AdvanceAnimation(1.0/60);Render();}
            if(view.LayoutBuilds!=before||view.IsAnimating)throw new Exception("Rolling captions rebuild geometry or never settle");
            if(!view.Text.Contains("壬癸子丑")||view.ActualHeight>108)throw new Exception("Latest line is missing or the viewport grew");
            view.SetAnimatedText(string.Join("\n",Enumerable.Repeat("甲乙丙丁戊己庚辛壬癸",30)),true);Render();view.AdvanceAnimation(1);Render();
            if(view.VisibleGlyphCount>35)throw new Exception("Offscreen old glyphs remain in the rendering scene");
            view.SetAnimatedText(view.Text+"新行",true);Render();view.SetAnimatedText(view.Text,false);Render();
            if(view.IsAnimating||view.VisibleGlyphCount==0)throw new Exception("Disabling motion did not reveal the latest viewport");
        });
        Check("Chinese revision categories preserve anchors and crossfade rewrites with no empty frame", () =>
        {
            var anchors=CaptionDiff.Match(CaptionDiff.Elements("甲删除\n乙旧词丙。"),CaptionDiff.Elements("甲\n乙新词丙的。"));
            var sources=CaptionDiff.ReplacementSources(anchors,9);
            if(sources[3]!=5||sources[6]!=-1)throw new Exception("Replacement letters borrow positions from an unrelated line or deletion span");
            var cases=new[] {
                ("船舶稳定。","船舶稳定。货物安全。",CaptionChangeKind.Append),
                ("船舶稳定。","船舶非常稳定。",CaptionChangeKind.Insert),
                ("船舶非常稳定。","船舶稳定。",CaptionChangeKind.Delete),
                ("船舶的稳性减小，因此需要调整货物位置。","船舶的稳性降低，因此必须调整货物位置。",CaptionChangeKind.Replace),
                ("船舶稳定，货物安全。","货物安全，船舶稳定。",CaptionChangeKind.Reorder),
                ("今天天气晴朗。","重心升高会降低初稳性。",CaptionChangeKind.Rewrite)
            };
            foreach(var (a,b,kind) in cases)
            {
                var view=new OutlinedText {Text=a,TextSize=28,Outline=false,FontFamily=CaptionTypography.Chinese};
                void Render(){view.Measure(new Size(800,180));view.Arrange(new Rect(0,0,800,180));var bitmap=new RenderTargetBitmap(800,180,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var pixels=new byte[800*180*4];bitmap.CopyPixels(pixels,3200,0);if(!pixels.Where((_,i)=>i%4==3).Any(alpha=>alpha>100))throw new Exception("A blank frame appeared during a revision");}
                Render();view.SetAnimatedText(b,true);Render();
                if(view.LastChange!=kind)throw new Exception($"Expected {kind}, got {view.LastChange}");
                for(var frame=0;frame<54;frame++){view.AdvanceAnimation(1.0/60);Render();}
                if(view.Text!=b||view.IsAnimating)throw new Exception("Replacement did not settle into the newest translation");
                view.SetAnimatedText(a,true);Render();view.AdvanceAnimation(.06);Render();view.SetAnimatedText(b+"请注意。",true);Render();
                view.AdvanceAnimation(1);Render();if(view.Text!=b+"请注意。")throw new Exception("Interrupted revision restored obsolete text");
            }
        });
        Check("History empty state disappears when a caption is available", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            var history = (ItemsControl)window.FindName("HistoryList");
            var empty = (FrameworkElement)window.FindName("HistoryEmpty");
            typeof(MainWindow).GetMethod("SetHistoryOpen",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,[true]);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(820, 570)); content.Arrange(new Rect(0, 0, 820, 570)); content.UpdateLayout();
            if (empty.Visibility != Visibility.Visible) throw new Exception("Missing history empty state");
            var export = (Button)window.FindName("HistoryExportButton");
            if (export.IsEnabled) throw new Exception("Empty history offers export");
            history.ItemsSource = new[] { new CaptionRow(1, "", "Текст", "文字", "#16825F") };
            content.UpdateLayout();
            if (empty.Visibility != Visibility.Collapsed) throw new Exception("Empty state obscures recorded text");
            if (!export.IsEnabled) throw new Exception("Recorded text cannot be exported");
        });
        Check("The stop action distinguishes a demo from a real classroom", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var demo = typeof(MainWindow).GetField("isDemo", flags)!;
            var update = typeof(MainWindow).GetMethod("SetRunningUi", flags)!;
            demo.SetValue(window,true); update.Invoke(window,new object[] {true});
            if (((TextBlock)window.FindName("StopText")).Text != "结束演示") throw new Exception("Demo is described as a classroom");
            demo.SetValue(window,false); update.Invoke(window,new object[] {true});
            if (((TextBlock)window.FindName("StopText")).Text != "结束课堂") throw new Exception("Classroom stop label missing");
        });
        Check("Classroom history starts closed and technical details stay in secondary controls", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            if (window.FindName("HistoryPane") is not Border { Visibility: Visibility.Collapsed }) throw new Exception();
            if (window.FindName("NoticePanel") is not Border { Visibility: Visibility.Collapsed }) throw new Exception();
            if (window.Width > 1050 || window.MinWidth > 850) throw new Exception();
        });
        WorkspaceRefinementTests.Run(Check,root);
        Check("Unavailable recording devices are described and cannot be selected for capture", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            var box = (ComboBox)window.FindName("DeviceBox");
            var items = box.Items.OfType<AudioInput>().ToArray();
            if (items.Length > 0 && !items.Any(d => d.CanRecord))
            {
                if (box.SelectedIndex != -1) throw new Exception();
                if (((TextBlock)window.FindName("DevicePlaceholder")).Text == "选择麦克风") throw new Exception();
            }
            if (items.Any(d => !d.CanRecord && d.Status is not ("已禁用" or "未连接"))) throw new Exception();
        });
        Check("Microphone inventory refresh runs asynchronously", () =>
        {
            var window = new MainWindow(new SettingsStore(root));
            var method = typeof(MainWindow).GetMethod("RefreshDevicesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(window, null)!;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < until)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), System.Windows.Threading.DispatcherPriority.Background);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(5);
            }
            if (!task.IsCompletedSuccessfully) throw new Exception("Device refresh did not complete");
        });
        Check("Lucide close icon stays within its original coordinate extent", () =>
        {
            var geometry = (Geometry)app.FindResource("XIcon");
            if (geometry.Bounds.Right > 24 || geometry.Bounds.Bottom > 24) throw new Exception();
            var figures = geometry.GetFlattenedPathGeometry().Figures;
            if (figures.Count != 2 || figures[1].StartPoint != new Point(6, 6)) throw new Exception();
            var last = figures[1].Segments.Last();
            var end = last is PolyLineSegment poly ? poly.Points.Last() : last is LineSegment line ? line.Point : new Point();
            if (end != new Point(18, 18)) throw new Exception("Merged SVG paths changed relative line coordinates");
        });
        var renderIndex = Array.IndexOf(args, "--render-dir");
        if (renderIndex >= 0 && renderIndex + 1 < args.Length)
        {
            var renderDirectory = Path.GetFullPath(args[renderIndex + 1]); Directory.CreateDirectory(renderDirectory);
            Check("Offscreen full-layout render: home, smaller window, captions, settings and appearance", () =>
            {
                var window = new MainWindow(new SettingsStore(root));
                RenderWindow(window, Path.Combine(renderDirectory, "home.png"), 1000, 690);
                RenderWindow(window, Path.Combine(renderDirectory, "home-small.png"), 820, 570);
                ((FrameworkElement)window.FindName("LiveCaption")).Visibility = Visibility.Visible;
                ((Button)window.FindName("StartButton")).Visibility = Visibility.Collapsed;
                var stop = (Button)window.FindName("StopButton"); stop.Visibility = Visibility.Visible; stop.IsEnabled = true;
                ((TextBlock)window.FindName("StopText")).Text = "结束演示";
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var engine = new CaptionEngine();
                engine.Update(new("fixture", "Если центр тяжести поднимается, начальная остойчивость уменьшается. Поэтому необходимо учитывать положение тяжёлого груза.", true));
                var snapshot = engine.CreateSnapshot()!;
                engine.Apply(snapshot, [new(0, snapshot.Tokens.Count, "重心升高时，初稳性会减小。因此，需要考虑重物的放置位置。", true)]);
                typeof(MainWindow).GetField("engine", flags)!.SetValue(window, engine);
                var at = DateTimeOffset.UtcNow;
                typeof(MainWindow).GetMethod("RefreshCaptions", flags)!.Invoke(window, null);
                ((TextBlock)window.FindName("StateText")).Text = "收音中 · 合成示例";
                ((FrameworkElement)window.FindName("SessionSummary")).Visibility = Visibility.Collapsed;
                ((TextBlock)window.FindName("SessionClock")).Text = "12:34";
                ((TextBlock)window.FindName("CurrentPhase")).Text = "";
                RenderWindow(window, Path.Combine(renderDirectory, "captions.png"), 1000, 690);
                RenderWindow(window, Path.Combine(renderDirectory, "captions-small.png"), 820, 570);
                ((OutlinedText)window.FindName("CurrentRussian")).Text = "Если центр тяжести поднимается, начальная остойчивость уменьшается. Поэтому необходимо учитывать положение тяжёлого груза и его влияние на судно.";
                ((OutlinedText)window.FindName("CurrentChinese")).Text = "重心升高时，船舶的初稳性会减小。因此，需要仔细考虑重物的放置位置，以及它对船舶的影响。";
                RenderWindow(window, Path.Combine(renderDirectory, "captions-long-small.png"), 820, 570);
                var history = new MainWindow(new SettingsStore(root));
                typeof(MainWindow).GetMethod("SetHistoryOpen",flags)!.Invoke(history,[true]);
                RenderWindow(history, Path.Combine(renderDirectory, "history-empty.png"), 820, 570);
                ((FrameworkElement)history.FindName("LiveCaption")).Visibility = Visibility.Visible;
                ((OutlinedText)history.FindName("CurrentRussian")).Text = "Судно обладает достаточной остойчивостью.";
                ((OutlinedText)history.FindName("CurrentChinese")).Text = "船舶具有足够的稳性。";
                ((ItemsControl)history.FindName("HistoryList")).ItemsSource = new[] { new CaptionRow(1, "", "Судно обладает достаточной остойчивостью. При подъёме центра тяжести начальная остойчивость уменьшается, поэтому положение тяжёлого груза необходимо учитывать.", "船舶具有足够的稳性。重心升高时，初稳性会减小。因此，需要考虑重物的放置位置，以及它对船舶的影响。", "#B5E1D5") };
                RenderWindow(history, Path.Combine(renderDirectory, "history-content.png"), 820, 570);
                RenderWindow(history, Path.Combine(renderDirectory, "history-content-wide.png"),1280,800);
                var pending = new MainWindow(new SettingsStore(root));
                ((FrameworkElement)pending.FindName("LiveCaption")).Visibility = Visibility.Visible;
                ((FrameworkElement)pending.FindName("SessionSummary")).Visibility = Visibility.Visible;
                typeof(MainWindow).GetField("engine", flags)!.SetValue(pending, new CaptionEngine());
                typeof(MainWindow).GetMethod("RefreshCaptions", flags)!.Invoke(pending, null);
                ((TextBlock)pending.FindName("StateText")).Text = "用量与余额 · 合成示例";
                RenderWindow(pending, Path.Combine(renderDirectory, "cost-pending.png"), 820, 570);
                var settings = new SettingsWindow(new(root), new() { OutputDirectory = Path.Combine(root, "sessions") }, null);
                RenderWindow(settings, Path.Combine(renderDirectory, "settings.png"), 620, 700);
                var settingsTabs = (TabControl)settings.FindName("SettingsTabs");
                settingsTabs.SelectedIndex = 1; RenderWindow(settings, Path.Combine(renderDirectory, "settings-classroom.png"), 620, 700);

                settingsTabs.SelectedIndex = 0; ((Expander)settings.FindName("AdvancedConnection")).IsExpanded = true;
                RenderWindow(settings, Path.Combine(renderDirectory, "settings-advanced.png"), 620, 700);
                RenderWindow(new AppearanceWindow(new(), () => { }), Path.Combine(renderDirectory, "appearance.png"), 470, 730);
                var morph = new OutlinedText { TextSize = 31, Color = "#F2F5F2", Outline = false, Text = "船舶的初稳性会减小，因此需要调整货物位置。" };
                var morphSurface = new Border { Width = 760, Height = 160, Padding = new Thickness(24), Background = UiKit.Brush("#15262E"), Child = morph };
                RenderElement(morphSurface, Path.Combine(renderDirectory, "morph-before.png"), 760, 160);
                morph.SetAnimatedText("船舶初稳性减小，因此必须调整货物的位置。", true);
                RenderElement(morphSurface, Path.Combine(renderDirectory, "morph-start.png"), 760, 160);
                morph.AdvanceAnimation(.08);
                RenderElement(morphSurface, Path.Combine(renderDirectory, "morph-middle.png"), 760, 160);
                morph.AdvanceAnimation(.3);
                RenderElement(morphSurface, Path.Combine(renderDirectory, "morph-after.png"), 760, 160);
                var adaptivePreview = new AppearanceWindow(new() { AdaptiveContrast = true }, () => { });
                RenderWindow(adaptivePreview, Path.Combine(renderDirectory, "appearance-adaptive.png"),470,730);
                var backColors=new[] {Color.FromRgb(250,250,250),Color.FromRgb(235,197,168),Color.FromRgb(190,220,180),Color.FromRgb(40,65,100),Color.FromRgb(90,35,80),Color.FromRgb(20,20,20)};
                void AdaptiveSample(string name,Func<int,int,Color> background)
                {
                var modePixels = new byte[900*230*4];
                for(var y=0;y<230;y++) for(var x=0;x<900;x++) { var i=(y*900+x)*4;var color=background(x,y);modePixels[i]=color.B;modePixels[i+1]=color.G;modePixels[i+2]=color.R;modePixels[i+3]=255; }
                var modeBackground=BitmapSource.Create(900,230,96,96,PixelFormats.Bgra32,null,modePixels,900*4);modeBackground.Freeze();
                var modeMap=BitmapSource.Create(900,230,96,96,PixelFormats.Bgra32,null,AdaptiveContrast.MapBgra(modePixels),900*4);modeMap.Freeze();
                var russianMap=BitmapSource.Create(900,230,96,96,PixelFormats.Bgra32,null,AdaptiveContrast.MapBgra(modePixels,CaptionInkRole.Russian),900*4);russianMap.Freeze();
                var splitSample=new Grid { Width=900,Height=230,Background=new ImageBrush(modeBackground) };
                var adaptiveRussian=new OutlinedText { Text="Когда фон меняется, буквы остаются чёткими.",TextSize=34,Outline=false,Margin=new Thickness(25,20,25,0),VerticalAlignment=VerticalAlignment.Top,
                    AdaptiveForeground=new ImageBrush(russianMap) { ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(25,20,850,52),ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,850,52),Stretch=Stretch.Fill } };
                var adaptiveChinese=new OutlinedText { Text="同一个字也能随背后的明暗分区变色。",TextSize=47,Outline=false,Margin=new Thickness(25,100,25,0),VerticalAlignment=VerticalAlignment.Top,
                    AdaptiveForeground=new ImageBrush(modeMap) { ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(25,100,850,80),ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,850,80),Stretch=Stretch.Fill } };
                splitSample.Children.Add(adaptiveRussian);splitSample.Children.Add(adaptiveChinese);
                RenderElement(splitSample,Path.Combine(renderDirectory,name),900,230);
                }
                AdaptiveSample("adaptive-contrast.png",(x,y)=>backColors[x/150]);
                AdaptiveSample("adaptive-white-ppt.png",(x,y)=>Colors.White);
                AdaptiveSample("adaptive-dark-ppt.png",(x,y)=>Color.FromRgb(25,32,47));
                AdaptiveSample("adaptive-busy-ppt.png",(x,y)=>backColors[(x/17+y/29)%backColors.Length]);
                var subtitle = new OverlayWindow(new());
                subtitle.SetCaption(new([], [new(1, "Если центр тяжести поднимается, начальная остойчивость уменьшается.", "重心升高时，初稳性会减小。", false, false, DateTimeOffset.UtcNow, [1]),
                    new(2, "Поэтому необходимо учитывать положение тяжёлого груза.", "因此，需要考虑重物的放置位置。", false, false, DateTimeOffset.UtcNow, [2])], "", 0, 1), SubtitleActivity.Listening);
                RenderWindow(subtitle, Path.Combine(renderDirectory, "overlay-initial-animation.png"), 1050, 245);
                ((OutlinedText)subtitle.FindName("RussianCaption")).AdvanceAnimation(3); ((OutlinedText)subtitle.FindName("ChineseCaption")).AdvanceAnimation(3);
                RenderWindow(subtitle, Path.Combine(renderDirectory, "overlay.png"), 1050, 245);
                foreach(var light in new[] {false,true})
                {
                    var sample=new OverlayWindow(new() {LightTextBackground=light});
                    sample.SetCaption(new([], [new(1,"Если центр тяжести поднимается, начальная остойчивость уменьшается.","重心升高时，船舶的初稳性会减小。",false,false,DateTimeOffset.UtcNow,[1])],"",0,1),SubtitleActivity.Stopped);
                    var content=(FrameworkElement)sample.Content;sample.Content=null;
                    var background=new Grid {Background=light?UiKit.Brush("#34495B"):UiKit.Brush("#EDE9DF")};background.Children.Add(content);
                    RenderElement(background,Path.Combine(renderDirectory,light?"overlay-white-text-background.png":"overlay-black-text-background.png"),1050,245);
                }
                foreach(var light in new[] {false,true})
                {
                    var sample=new OverlayWindow(new() {LightTextBackground=light});
                    sample.SetCaption(new([], [new(1,
                        "Это метр. Здесь будет 2π — две π волн на единицу длины.\nЕсли это расстояние два метра, то будет 4π.\nВолны в пространстве описываются произведением x и k.",
                        "距离是2米的话。\n那就是4π。这描述了空间中的波，用 x（空间）乘以 k。\n也就是米除以π。",false,false,DateTimeOffset.UtcNow,[1])],"",0,1),SubtitleActivity.Stopped);
                    var content=(FrameworkElement)sample.Content;sample.Content=null;
                    var background=new Grid {Background=light?UiKit.Brush("#34495B"):UiKit.Brush("#F4F4F4")};background.Children.Add(content);
                    RenderElement(background,Path.Combine(renderDirectory,light?"overlay-three-lines-white.png":"overlay-three-lines-black.png"),1366,(int)Math.Ceiling(sample.Height));
                }
                var demoSubtitle = new OverlayWindow(new());
                demoSubtitle.SetCaption(new([], [], "Судно обладает достаточной остойчивостью.", 4, 1), SubtitleActivity.Demo);
                RenderWindow(demoSubtitle, Path.Combine(renderDirectory, "overlay-demo.png"), 1050, 245);
            });
        }
        if(args.Contains("--render-motion"))
        {
            var directory=Path.GetFullPath(renderIndex>=0?args[renderIndex+1]:"artifacts/ui-redesign");
            Check("WPF synthetic motion sequence renders local revisions, interruption, rolling and rewrites",()=>CaptionMotionPreview.Render(directory));
        }
        Console.WriteLine($"{results.Count - failed}/{results.Count} passed");
        var i = Array.IndexOf(args, "--report");
        if (i >= 0 && i + 1 < args.Length) AtomicFile.Write(args[i + 1], JsonSerializer.Serialize(new { at = DateTimeOffset.Now, total = results.Count, failed, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v) { var x = v / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    private static void RenderWindow(Window window, string path, int width, int height)
    {
        // Render our own WPF component tree only. No window is shown, no screen is captured and no desktop input is sent.
        RenderElement((FrameworkElement)window.Content, path, width, height);
    }
    private static void RenderElement(FrameworkElement content, string path, int width, int height)
    {
        void SettleAnimations(DependencyObject node)
        {
            if (node is UIElement element)
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                if (element.RenderTransform is TranslateTransform transform)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null); transform.X = 0;
                    transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0;
                }
            }
            for (var child = 0; child < VisualTreeHelper.GetChildrenCount(node); child++) SettleAnimations(VisualTreeHelper.GetChild(node, child));
        }
        SettleAnimations(content);
        content.Width = width; content.Height = height;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
