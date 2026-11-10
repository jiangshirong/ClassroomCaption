using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

public sealed record CaptionRow(long Id, string Label, string Russian, string Chinese, string LabelBrush);
public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly Func<CancellationToken, Task<bool>> checkNetwork;
    private AppSettings settings;
    private ApiCredentials? credentials;
    private readonly ObservableCollection<CaptionRow> rows = [];
    private OverlayWindow? overlay;
    private CaptionEngine? engine;
    private SessionStore? session;
    private AudioCaptureService? audio;
    private LiveTranslateClient? live;
    private CancellationTokenSource? runCancellation;
    private Task? demoTask;
    private bool busy, stopping, closing, allowClose, previewing, recoveredUnsavedText, clippingWarned;
    private bool refreshingDevices, refreshAgain;
    private TrayIcon? tray;
    private CaptionDockWindow? captionBall;
    private bool floatingTransition;
    private bool resumePending;
    private bool stoppedByFault, recoveringAudio;
    private CaptionContinuation continuation=new();
    private int demoExample,demoWordCount;
    internal bool PresentFloatingWindows {get;set;}=true;
    private bool exitRequested;
    public void RestoreWindow(){Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();}
    public void EnableTray()
    {
        tray=new(this,RestoreWindow,()=> {exitRequested=true;Close();},async ()=>await ToggleFloatingAsync(),()=>Running,()=>floatingTransition||busy||stopping||previewing);
    }
    private bool updatingNoiseToggle;

    private long previewLevelTicks;
    private long capturedBytes, receivedText;
    private double peakRms;

    private TimeSpan captureStarted;
    private readonly Stopwatch clock = new();
    private readonly System.Windows.Threading.DispatcherTimer refresh;
    private readonly System.Windows.Threading.DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly System.Windows.Threading.DispatcherTimer autoStartTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool autoStartConsumed, checkingAutoStart;
    private bool isDemo, historyOpen, updatingHistory;
    private string? selectedHistory;

    private int historyLoadVersion;
    private Size compactSize=new(420,460);
    private Point compactPosition;

    public bool Running => runCancellation is not null;
    private SubtitleActivity Activity => stopping ? SubtitleActivity.Stopping : Running
        ? isDemo ? SubtitleActivity.Demo : live?.IsReconnecting == true ? SubtitleActivity.Reconnecting
            : recoveringAudio ? SubtitleActivity.RecoveringAudio : audio is null ? SubtitleActivity.Connecting
            : live?.IsConnected != true ? SubtitleActivity.Buffering : SubtitleActivity.Listening
        : stoppedByFault ? SubtitleActivity.Faulted : session is null ? SubtitleActivity.Ready : SubtitleActivity.Stopped;

    public MainWindow(SettingsStore store, Func<CancellationToken, Task<bool>>? networkCheck = null)
    {
        this.store = store;
        try { settings = store.Load(); }
        catch { settings = new(); }
        checkNetwork = networkCheck ?? CheckNetworkAsync;
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory)) settings.OutputDirectory = Path.Combine(store.Root, "sessions");
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InitializeFloatingBall();
            if (!autoStartConsumed) autoStartTimer.Start();
        };
        autoStartTimer.Tick += async (_, _) => await TryAutoStartAsync();
        Closed += (_, _) => autoStartTimer.Stop();
        NoiseReductionToggle.IsChecked = settings.NoiseReductionEnabled;
        NoiseReductionToggle.Checked += NoiseReductionChanged;
        NoiseReductionToggle.Unchecked += NoiseReductionChanged;
        try { credentials = store.LoadCredentials(); } catch (Exception ex) { Notice(SafeErrors.Describe(ex)); }
        HistoryList.ItemsSource = rows;
        _ = RefreshDevicesAsync();
        DeviceBox.DropDownOpened += (_, _) => { if (refreshingDevices) refreshAgain = true; else _ = RefreshDevicesAsync(); };
        Activated += (_, _) => _ = RefreshDevicesAsync();


        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); Motion.Drawer(NoticePanel, false); };
        refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
        refresh.Tick += (_, _) => RefreshCaptions(); refresh.Start();
    }
    private async Task TryAutoStartAsync()
    {
        if (autoStartConsumed || checkingAutoStart || !CanAutoStart()) return;
        checkingAutoStart = true;
        try
        {
            if (!await checkNetwork(CancellationToken.None) || autoStartConsumed || !CanAutoStart()) return;
            ConsumeAutoStart();
            await StartFloatingCoreAsync(networkConfirmed: true);
        }
        catch (Exception ex) { Notice(SafeErrors.Describe(ex)); }
        finally { checkingAutoStart = false; }
    }

    private bool CanAutoStart() => captionBall is not null && !closing && !Running &&
        !busy && !stopping && !previewing && !floatingTransition && !refreshingDevices;

    private void ConsumeAutoStart()
    {
        autoStartConsumed = true;
        autoStartTimer.Stop();
    }
    private void Notice(string text, bool persistent = false) => Dispatcher.BeginInvoke(() =>
    {
        NoticeText.Text = text; noticeTimer.Stop();
        NoticePanel.Visibility = Visibility.Visible; Motion.Reveal(NoticePanel, 4, 160);
        if(!persistent)noticeTimer.Start();
    });
    private async Task StopForFaultAsync(string code, string message)
    {
        if(stopping || !Running)return;
        stoppedByFault = true;
        session?.Status(code,message);
        await StopRunAsync(code);
        Notice("课堂已停止："+message,true);
    }
    private void NoiseReductionChanged(object sender, RoutedEventArgs e)
    {
        if (updatingNoiseToggle) return;
        var enabled = NoiseReductionToggle.IsChecked == true;
        var previous = settings.NoiseReductionEnabled;
        try
        {
            // Detect missing/incompatible native components before changing the live stream.
            if (enabled) { using var probe = new RnNoiseFilter(); }
            settings.NoiseReductionEnabled = enabled;
            store.Save(settings);
            if (audio is not null) audio.NoiseReductionEnabled = enabled;
        }
        catch (Exception ex)
        {
            settings.NoiseReductionEnabled = previous;
            updatingNoiseToggle = true; NoiseReductionToggle.IsChecked = previous; updatingNoiseToggle = false;
            Notice(SafeErrors.Describe(ex));
        }
    }
    private async Task RefreshDevicesAsync()
    {
        if (refreshingDevices || Running || previewing || busy || closing) return;
        refreshingDevices = true;
        if (DeviceBox.Items.Count == 0) DevicePlaceholder.Text = "正在查找麦克风…";
        try
        {
            var inputs = await Task.Run(AudioCaptureService.Inputs);
            if (Running || previewing || busy || closing) return;
            var selectedId = (DeviceBox.SelectedItem as AudioInput)?.Id ?? settings.InputDeviceId;
            if (DeviceBox.ItemsSource is IReadOnlyList<AudioInput> old && old.SequenceEqual(inputs))
            { if (inputs.Count == 0) DevicePlaceholder.Text = "未发现麦克风"; return; }
            DeviceBox.ItemsSource = inputs;
            DeviceBox.SelectedItem = inputs.FirstOrDefault(d => d.CanRecord && d.Id == selectedId) ?? inputs.FirstOrDefault(d => d.CanRecord && d.Id == settings.InputDeviceId) ?? inputs.FirstOrDefault(d => d.CanRecord);
            DevicePlaceholder.Text = inputs.Any(d => d.CanRecord) ? "选择麦克风" : inputs.Count > 0 ? "麦克风未启用或未连接" : "未发现麦克风";
            DeviceBox.ToolTip = inputs.Any(d => d.CanRecord) ? "选择录音输入" : "请在 Windows 声音设置中启用或连接麦克风";
        }
        catch (Exception ex) { if (DeviceBox.Items.Count == 0) DevicePlaceholder.Text = "无法读取麦克风"; Notice(SafeErrors.Describe(ex)); }
        finally { refreshingDevices = false; if (refreshAgain) { refreshAgain = false; _ = RefreshDevicesAsync(); } }
    }
    private void SaveSettingsSafely() { try { store.Save(settings); } catch (Exception ex) { Notice(SafeErrors.Describe(ex)); } }
    private void SetRunningUi(bool running)
    {
        StartButton.IsEnabled = !running&&!floatingTransition; DemoButton.IsEnabled = !running&&!floatingTransition; StopButton.IsEnabled = running&&!floatingTransition;
        DeviceBox.IsEnabled = !running&&!floatingTransition; PreviewButton.IsEnabled = !running&&!floatingTransition;
        NoiseReductionToggle.IsEnabled = (!running || !isDemo)&&!floatingTransition;
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        DemoButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(MoreButton,running?0:2);Grid.SetColumnSpan(MoreButton,running?3:1);
        StopText.Text = isDemo ? "结束演示" : "结束课堂";
        StateText.Text = SubtitleActivityText.Label(Activity);
        overlay?.SetActivity(Activity);
        StateDot.Fill = UiKit.Brush(running ? "#9BD6CA" : "#91ABA8");
        if (running && SystemParameters.ClientAreaAnimation)
            StateDot.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else { StateDot.BeginAnimation(OpacityProperty, null); StateDot.Opacity = 1; }
    }
    private void SetLevel(double rms, bool clipped)
    {
        if (rms > peakRms) peakRms = rms;
        if (Stopwatch.GetTimestamp() - Interlocked.Read(ref previewLevelTicks) < Stopwatch.Frequency / 8) return;
        Interlocked.Exchange(ref previewLevelTicks, Stopwatch.GetTimestamp());
        Dispatcher.BeginInvoke(() =>
        {
            AudioLevel.Value = Math.Min(100, rms * 300);
            if (clipped && !clippingWarned) { clippingWarned = true; Notice("麦克风音量过大，请降低音量。"); }
        });
    }
    private void CreateSession(bool demo,bool resume=false)
    {
        resume=resume&&resumePending&&session is not null&&engine is not null;
        if(resume&&session?.IsHealthy!=true)throw new UserFacingException("请先导出未保存的课堂文字，再开始翻译。");
        var previous=resume?engine!.State():null;
        isDemo = demo;

        capturedBytes = 0; receivedText = 0; peakRms = 0; captureStarted = TimeSpan.Zero; clippingWarned = false;
        if(!resume)
        {
            session?.Dispose();session = new(settings.OutputDirectory, demo);
            recoveredUnsavedText = false;demoExample=demoWordCount=0;
            var createdSession = session;
            session.Failed += message => Dispatcher.BeginInvoke(() => { if(ReferenceEquals(session,createdSession)&&Running&&!stopping)Notice(message,true); });
        }
        continuation=new(previous);
        if(!resume||!demo)
        {
            engine = new();
            if(resume)engine.LiveState=()=>continuation.Combine(new([],[],"",0,0));
            engine.Committed += session!.SaveCommitted;
            engine.Archived += session.SaveArchived;
        }
        lastRender = -1;
        historyLoadVersion++; selectedHistory = null;
        if(!resume){rows.Clear();overlay?.ResetReading();}
        else session!.Status("resumed","继续课堂");




        if(resume)clock.Start();else clock.Restart();
        resumePending=false;
        runCancellation = new();
        stoppedByFault = recoveringAudio = false;
        ConsumeAutoStart();
        SetRunningUi(true);
        ShowOverlay();
        if (historyOpen) _ = LoadHistorySessionsAsync();
    }
    private async void StartClick(object sender, RoutedEventArgs e)
    {
        if(floatingTransition)return;
        if(captionBall is not null)await StartFloatingAsync();
        else await StartTranslationAsync(sender,e);
    }
    private async Task StartTranslationAsync(object sender,RoutedEventArgs e,bool openSettings=true,bool resume=false,bool networkConfirmed=false)
    {
        if (busy || Running || previewing) return;
        busy = true;
        CancellationToken startToken = default;
        try
        {
            if (!networkConfirmed && !await checkNetwork(CancellationToken.None)) { Notice("未联网", true); return; }
            if (closing) return;
            if (string.IsNullOrWhiteSpace(credentials?.AliyunKey)) { busy = false; if(openSettings)SettingsClick(sender,e);else Notice("请先在连接设置中填写密钥。",true); return; }
            settings.Validate();
            if (DeviceBox.SelectedItem is not AudioInput { CanRecord: true } device) throw new UserFacingException("请选择可用麦克风；已禁用或未连接的设备需先在 Windows 中启用或连接，再选择。");
            settings.InputDeviceId = device.Id; store.Save(settings);
            CreateSession(false,resume);
            startToken = runCancellation!.Token;
            var snapshotSettings = settings.Clone();
            live = new(snapshotSettings, credentials.AliyunKey, recovery: new()
            {
                KeepTrying = true,
                PauseOnNoSemanticResultOutsideClass = true
            });
            var stream=live;var currentContinuation=continuation;
            engine!.LiveState = ()=>currentContinuation.Combine(stream.State.Snapshot());
            var currentSession=session!;
            live.State.Committed += segment=>currentSession.SaveCommitted(currentContinuation.Map(segment));
            live.State.Archived += segment=>currentSession.SaveArchived(currentContinuation.Map(segment));
            live.State.InternalMarkupObserved += currentSession.RawMarker;
            live.Usage.Recorded += currentSession.TokenUsage;
            live.Trace += currentSession.Trace;
            var startingLive = live;
            var startingSession = currentSession;
            live.Gap += startingSession.Gap;
            live.ConnectionChanged += change =>
            {
                startingSession.Status(change.Code, change.Message);
                Dispatcher.BeginInvoke(async () =>
                {
                    if (!ReferenceEquals(live, startingLive) || stopping || !Running) return;
                    if (change.Code == "auto_paused_no_semantic_result")
                    {
                        // An error can arrive while the ball is still expanding.
                        while (floatingTransition && !startToken.IsCancellationRequested)
                            await Task.Delay(50);
                        if (startToken.IsCancellationRequested || !ReferenceEquals(live, startingLive) || stopping || !Running)
                            return;
                        await CollapseAndPauseAsync(change.Code);
                        Notice(change.Message);
                        return;
                    }
                    StateText.Text = SubtitleActivityText.Label(Activity);
                    overlay?.SetActivity(Activity);
                    if (change.Code == "reconnecting") Notice(change.Message, true);
                    else if (change.Code == "reconnected") Notice(change.Message);
                });
            };
            live.Fault += message => Dispatcher.BeginInvoke(async () => { if(ReferenceEquals(live,startingLive))await StopForFaultAsync("translation_error",message); });
            NoticePanel.Visibility = Visibility.Collapsed;
            await startingLive.StartAsync(startToken, waitForConnection: false);
            try { StartCapture(device.Id, startingLive, startToken); }
            catch (Exception ex) { _ = RecoverCaptureAsync(device.Id, startingLive, startToken, SafeErrors.Describe(ex)); }
            StateText.Text = SubtitleActivityText.Label(Activity);
            overlay?.SetActivity(Activity);
        }
        catch (OperationCanceledException) when (startToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (resume && Running)
            {
                stoppedByFault = true;
                await StopRunAsync("start_error", true);
            }
            else await StopForFaultAsync("start_error", SafeErrors.Describe(ex));
            Notice(SafeErrors.Describe(ex), true);
        }
        finally { busy = false; }
    }
    private void StartCapture(string deviceId, LiveTranslateClient stream, CancellationToken token)
    {
        var capture = new AudioCaptureService { NoiseReductionEnabled = settings.NoiseReductionEnabled };
        capture.Pcm += pcm => { Interlocked.Add(ref capturedBytes, pcm.Length); stream.Enqueue(pcm); };
        capture.Level += SetLevel;
        capture.Fault += message => Dispatcher.BeginInvoke(async () =>
        {
            if (ReferenceEquals(audio, capture) && !token.IsCancellationRequested)
                await RecoverCaptureAsync(deviceId, stream, token, message);
        });
        try { capture.Start(deviceId); }
        catch { capture.Dispose(); throw; }
        audio = capture;
        captureStarted = clock.Elapsed;
    }

    private async Task RecoverCaptureAsync(string deviceId, LiveTranslateClient stream, CancellationToken token, string message)
    {
        audio?.Dispose();
        audio = null;
        recoveringAudio = true;
        overlay?.SetActivity(Activity);
        session?.Status("audio_error", message);
        Notice("麦克风已中断，正在自动恢复。", true);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    StartCapture(deviceId, stream, token);
                    recoveringAudio = false;
                    overlay?.SetActivity(Activity);
                    session?.Status("audio_recovered", "麦克风已恢复。");
                    Notice("麦克风已恢复。");
                    return;
                }
                catch (Exception ex)
                {
                    session?.Status("audio_retry", SafeErrors.Describe(ex));
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private Task<bool> CheckNetworkAsync(CancellationToken token)
    {
        Uri service;
        try { service = LiveTranslateProtocol.Endpoint(settings); }
        catch (UserFacingException) { service = new("https://dashscope.aliyuncs.com"); }
        return InternetConnectivity.IsOnlineAsync(service, token);
    }
    private async void StopClick(object sender, RoutedEventArgs e) {if(!floatingTransition)await StopFloatingAsync();}
    private async Task StopRunAsync(string reason = "user",bool pause=false)
    {
        if(stopping)return;
        if(!Running){if(!pause&&resumePending){session?.Dispose();resumePending=false;}return;}
        stopping = true; StopButton.IsEnabled = false; StateText.Text = "正在结束…"; overlay?.SetActivity(Activity);
        NoiseReductionToggle.IsEnabled = false;
        runCancellation!.Cancel();
        try
        {
            if (audio is not null) { await audio.StopAsync(); audio.Dispose(); audio = null; }
            if (live is not null) await live.StopAsync();
            if (demoTask is not null) { try { await demoTask; } catch (OperationCanceledException) { } demoTask = null; }
            RefreshCaptions();

            if (session?.IsHealthy != true) Notice("保存失败，请导出文字。");
        }
        catch (Exception ex) { Notice(SafeErrors.Describe(ex)); }
        finally
        {
            if (live is not null) await live.DisposeAsync();
            // Keep final usage and loss metrics even if graceful finishing timed out.
            session?.Metrics(new { seconds = clock.Elapsed.TotalSeconds, stopReason = reason, model = LiveTranslateProtocol.Model,
                inputTokens = live?.InputTokens ?? 0, outputTokens = live?.OutputTokens ?? 0,
                audioInputTokens = live?.Usage.AudioInputTokens, chineseOutputTokens = live?.Usage.ChineseOutputTokens,
                audioInputMissingResponses = live?.Usage.AudioInputMissingResponses,
                chineseOutputMissingResponses = live?.Usage.ChineseOutputMissingResponses,
                audioSecondsSent = (live?.AudioBytes ?? 0) / 32000.0, droppedAudioSeconds = (live?.DroppedBytes ?? 0) / 32000.0,
                uncertainAudioSeconds = (live?.UncertainAudioBytes ?? 0) / 32000.0 });
            live = null;
            if(pause)
            {
                if(!isDemo&&engine is not null)
                {
                    var state=engine.State();
                    var prefix=new CaptionContinuation(state).Combine(new([],[],"",0,0));
                    foreach(var draft in state.Drafts)session?.SaveArchived(draft);
                    if(!string.IsNullOrWhiteSpace(state.UntranslatedRussian))
                    {
                        var source=prefix.Interrupted!.Last();session?.SaveArchived(source);
                    }
                    session?.SaveDraft(prefix);engine.LiveState=()=>prefix;
                }
                session?.Status("paused","暂时收起字幕");
            }
            else session?.Dispose();
            resumePending=pause;
            runCancellation?.Dispose(); runCancellation = null;
            clock.Stop(); AudioLevel.Value = 0;
            stopping = false; SetRunningUi(false);
            RefreshCaptions();
            if (engine is not null)
            {
                var stoppedState = engine.State();
                overlay?.SetCaption(stoppedState, Activity, isDemo);


            }
        }
    }
    private async void PreviewClick(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        if (previewing || Running || busy) return;
        if (DeviceBox.SelectedItem is not AudioInput { CanRecord: true } device) { Notice("请先启用或连接麦克风，再选择。"); return; }
        previewing = true; clippingWarned = false; StartButton.IsEnabled = false; DemoButton.IsEnabled = false; PreviewButton.IsEnabled = false;
        DeviceBox.IsEnabled = false;
        NoiseReductionToggle.IsEnabled = false;
        try
        {
            Notice("正在录音，4 秒后播放。");
            await AudioCaptureService.PreviewAsync(device.Id, SetLevel, CancellationToken.None, settings.NoiseReductionEnabled);
            Notice("试听结束。");
        }
        catch (Exception ex) { Notice(SafeErrors.Describe(ex)); }
        finally { previewing = false; PreviewButton.IsEnabled = true; StartButton.IsEnabled = true; DemoButton.IsEnabled = true; DeviceBox.IsEnabled = true; NoiseReductionToggle.IsEnabled = true; AudioLevel.Value = 0; }
    }
    private void DemoClick(object sender, RoutedEventArgs e) => StartDemo();
    public void StartDemo()
    {
        if (Running || busy || previewing || floatingTransition) return;
        try
        {
            CreateSession(true);
            NoticePanel.Visibility = Visibility.Collapsed;
            demoTask = DemoAsync(runCancellation!.Token);
        }
        catch (Exception ex) { Notice(SafeErrors.Describe(ex)); }
    }
    private async Task DemoAsync(CancellationToken token)
    {
        for (;demoExample<DemoExamples.Length;demoExample++)
        {
            var words = DemoExamples[demoExample].Ru.Split(' ');
            for (var n = demoWordCount+1; n <= words.Length; n++)
            {
                token.ThrowIfCancellationRequested();
                UpdateDemoCaption(demoExample,n);demoWordCount=n;
                await Task.Delay(550, token);
            }
            await Task.Delay(1800, token);
            demoWordCount=0;
        }
        Notice("演示结束，请点击结束演示。");
    }
    private void UpdateDemoCaption(int index,int wordCount)
    {
        var example=DemoExamples[index];var words=example.Ru.Split(' ');
        engine!.Update(new("demo/"+index,string.Join(" ",words.Take(wordCount)),wordCount==words.Length));
        var snapshot=engine.CreateSnapshot();
        if(snapshot is not null)engine.Apply(snapshot,[new(0,snapshot.Tokens.Count,
            wordCount==words.Length?example.Zh:example.Zh[..Math.Min(example.Zh.Length,Math.Max(2,wordCount))]+"…",wordCount==words.Length)]);
    }
    private long lastRender = -1;
    private void RefreshCaptions()
    {

        if (Running && !isDemo) StateText.Text = SubtitleActivityText.Label(Activity);
        if (engine is null) return;
        var state = engine.State();
        if (state.Confirmed.Count > 0 || state.Drafts.Count > 0 || state.UntranslatedRussian.Length > 0) Interlocked.Exchange(ref receivedText, 1);
        if (session is not null && Running) session.SaveDraft(state);
        if (state.Revision != lastRender && (selectedHistory is null || selectedHistory == session?.FilePath))
        {
            lastRender = state.Revision;
            var nearBottom = HistoryScroll.ScrollableHeight - HistoryScroll.VerticalOffset < 60;
            var desired = new List<CaptionRow>();
            foreach (var s in state.Confirmed.Concat(state.Interrupted ?? []).OrderBy(s => s.At))
                desired.Add(new(s.Id, s.Confirmed ? "" : "中断暂存", s.Russian, s.Chinese, s.Confirmed ? "#B5E1D5" : "#E6C58D"));
            foreach (var s in state.Drafts) desired.Add(new(s.Id, isDemo ? "" : "暂译", s.Russian, s.Chinese, "#E6C58D"));
            if (!string.IsNullOrWhiteSpace(state.UntranslatedRussian)) desired.Add(new(long.MinValue, "识别中", state.UntranslatedRussian, "…", "#E6C58D"));
            var desiredIds = desired.Select(r => r.Id).ToHashSet();
            for (var i = rows.Count - 1; i >= 0; i--) if (!desiredIds.Contains(rows[i].Id)) rows.RemoveAt(i);
            for (var i = 0; i < desired.Count; i++)
            {
                var existing = -1;
                for (var j = i; j < rows.Count; j++) if (rows[j].Id == desired[i].Id) { existing = j; break; }
                if (existing < 0) rows.Insert(i, desired[i]);
                else { if (existing != i) rows.Move(existing, i); if (rows[i] != desired[i]) rows[i] = desired[i]; }
            }

            if (nearBottom) HistoryScroll.ScrollToEnd();
        }
        if (engine.BoundaryWarning is not null) Notice(engine.BoundaryWarning);
        overlay?.SetCaption(state, Activity, isDemo);

        if (Running && !isDemo && audio is not null && Interlocked.Read(ref receivedText) == 0)
        {
            var feedback = Activity == SubtitleActivity.Reconnecting ? "连接中断，正在自动恢复…" : Activity == SubtitleActivity.Buffering ? "收音中，正在连接…" : RecognitionFeedback.Describe((clock.Elapsed - captureStarted).TotalSeconds,
                Interlocked.Read(ref capturedBytes), live?.AudioBytes ?? 0, peakRms, false);


            overlay?.SetListeningFeedback(feedback);
        }
        if (engine is not null) SessionClock.Text = clock.Elapsed.ToString(@"mm\:ss");
    }
    private async Task ToggleFloatingAsync()
    {
        if(Running)await StopFloatingAsync();else await StartFloatingAsync();
    }
    internal void InitializeFloatingBall()
    {
        if(closing||Running||captionBall is not null)return;
        InitializeFloatingBallForTransition();
    }
    private Task StartFloatingAsync() => StartFloatingCoreAsync(networkConfirmed: false);

    private async Task StartFloatingCoreAsync(bool networkConfirmed)
    {
        if(floatingTransition||busy||stopping||previewing||Running)return;
        floatingTransition=true;
        SetRunningUi(Running);
        Task unfolding=Task.CompletedTask;
        var unfoldingStarted=false;
        try
        {
            if(captionBall?.SavedPresentation is not null&&overlay is not null)
            {unfolding=captionBall.ExpandAsync(overlay,PresentFloatingWindows);unfoldingStarted=true;}
            // Every start entry point requests live translation. A paused demo
            // must never bypass network, microphone and credential checks.
            await StartTranslationAsync(this,new RoutedEventArgs(),false,resumePending&&!isDemo,networkConfirmed);
            await unfolding;
            if(!Running)
            {
                if(unfoldingStarted&&captionBall is not null&&overlay is not null)
                {
                    await captionBall.CollapseAsync(overlay,PresentFloatingWindows);
                    captionBall.ToolTip="未能开始翻译，请检查网络、麦克风和连接设置后双击重试。";
                }
                UpdateOverlayToggle();return;
            }
            if(captionBall is not null&&overlay is not null)
            {
                if(!unfoldingStarted)await captionBall.ExpandAsync(overlay,PresentFloatingWindows);
                captionBall.Close();captionBall=null;
            }
            overlay?.ResumeReading();ShowOverlay();
        }
        catch(OperationCanceledException) { }
        catch(Exception ex)
        {
            captionBall?.Close();captionBall=null;
            try{await unfolding;}catch(OperationCanceledException){ }catch(Exception){ }
            if(!closing){overlay?.ResumeReading();ShowOverlay();}Notice(SafeErrors.Describe(ex),true);
        }
        finally{floatingTransition=false;if(!closing)SetRunningUi(Running);}
    }
    private Task StopFloatingAsync() => CollapseAndPauseAsync("user");

    private async Task CollapseAndPauseAsync(string reason)
    {
        if(floatingTransition||stopping)return;
        floatingTransition=true;
        SetRunningUi(Running);
        try
        {
            Task motion=Task.CompletedTask;
            if(overlay is not null&&(overlay.IsVisible||!PresentFloatingWindows))
            {
                overlay.FreezeReading();
                captionBall?.Close();captionBall=null;InitializeFloatingBallForTransition();
                motion=captionBall!.CollapseAsync(overlay,PresentFloatingWindows);
            }
            else { overlay?.Hide();InitializeFloatingBallForTransition(); }
            await Task.WhenAll(StopRunAsync(reason, pause:true),motion);
            overlay?.Hide();UpdateOverlayToggle();
        }
        catch(OperationCanceledException) { }
        catch(Exception ex){if(!closing){overlay?.Hide();captionBall?.Close();captionBall=null;InitializeFloatingBallForTransition();UpdateOverlayToggle();}Notice(SafeErrors.Describe(ex),true);}
        finally{floatingTransition=false;if(!closing)SetRunningUi(Running);}
    }
    private void InitializeFloatingBallForTransition()
    {
        if(captionBall is not null)return;
        var ball=new CaptionDockWindow(()=>floatingTransition||busy||stopping);captionBall=ball;
        ball.StartRequested+=async ()=>await StartFloatingAsync();
        ball.Closed+=(_,_)=>{if(ReferenceEquals(captionBall,ball))captionBall=null;};
        // Place it hidden before a morph, or show it if captions were hidden.
        ball.ShowAtRight(this,PresentFloatingWindows&&overlay?.IsVisible!=true);
    }
    private void ShowOverlay()
    {
        if(!floatingTransition&&captionBall is not null){captionBall.Close();captionBall=null;}
        if (overlay is null)
        {
            overlay = new(settings.Overlay);
            overlay.IsVisibleChanged += (_, _) => UpdateOverlayToggle();
            overlay.Closed += (_, _) => { overlay = null; UpdateOverlayToggle(); };
            overlay.SettingsChanged += SaveSettingsSafely;
            overlay.StopTranslationRequested += async () => await StopFloatingAsync();
        }
        if(PresentFloatingWindows&&!(floatingTransition&&captionBall is not null))overlay.Show();
        UpdateOverlayToggle();
        overlay.SetCaption(engine?.State() ?? new([], [], "", 0, 0), Activity, isDemo);
    }
    private void UpdateOverlayToggle()
    {
        var visible=overlay?.IsVisible==true;
        OverlayToggle.IsChecked=visible;
        OverlayToggle.ToolTip=visible?"隐藏悬浮字幕":"显示悬浮字幕";
    }
    private void ShowOverlayClick(object sender, RoutedEventArgs e)
    {
        if(overlay?.IsVisible==true)overlay.Hide();
        else ShowOverlay();
    }
    private void LatestCaptionClick(object sender,RoutedEventArgs e) => RefreshCaptions();    private void HistoryClick(object sender, RoutedEventArgs e)
    {
        SetHistoryOpen(!historyOpen);
        if (historyOpen) HistoryCloseButton.Focus(); else HistoryButton.Focus();
    }
    private void SetHistoryOpen(bool open)
    {
        if(open!=historyOpen&&WindowState==WindowState.Normal)
        {
            if(open)
            {
                compactSize=new Size(Width,Height);compactPosition=new Point(Left,Top);
                var proposed=new Rect(double.IsFinite(Left)?Left:0,double.IsFinite(Top)?Top:0,Math.Max(800,Width+380),Math.Max(560,Height));
                var fitted=PanelWindowPlacement.Fit(proposed,PanelWindowPlacement.WorkArea(this));
                Width=fitted.Width;Height=fitted.Height;Left=fitted.Left;Top=fitted.Top;
            }
            else
            {
                var proposed=new Rect(double.IsFinite(compactPosition.X)?compactPosition.X:Left,double.IsFinite(compactPosition.Y)?compactPosition.Y:Top,compactSize.Width,compactSize.Height);
                var fitted=PanelWindowPlacement.Fit(proposed,PanelWindowPlacement.WorkArea(this));
                Width=fitted.Width;Height=fitted.Height;Left=fitted.Left;Top=fitted.Top;
            }
        }
        historyOpen=open;
        HistoryButton.IsChecked=open;
        HistoryButton.ToolTip=open?"关闭课堂记录":"查看课堂记录";
        HistoryPane.BeginAnimation(OpacityProperty,null);
        HistoryPane.Visibility=open?Visibility.Visible:Visibility.Collapsed;
        UpdateHistoryLayout();
        if(open) { Motion.Reveal(HistoryPane,0,160); _ = LoadHistorySessionsAsync(); }
    }
    private void UpdateHistoryLayout()
    {
        if(HistoryColumn is null)return;
        var available=PanelBody.ActualWidth>0?PanelBody.ActualWidth:Width-48;
        var recordOnly=historyOpen&&available<688;
        ControlRail.Visibility=recordOnly?Visibility.Collapsed:Visibility.Visible;
        ControlColumn.Width=!historyOpen?new GridLength(1,GridUnitType.Star):recordOnly?new GridLength(0):new GridLength(Math.Min(Math.Max(320,compactSize.Width-48),available-340));
        HistoryGap.Width=new GridLength(historyOpen&&!recordOnly?20:0);
        HistoryColumn.Width=historyOpen?new GridLength(1,GridUnitType.Star):new GridLength(0);
    }
    private void PanelBodySizeChanged(object sender,SizeChangedEventArgs e)=>UpdateHistoryLayout();
    private async Task LoadHistorySessionsAsync()
    {
        var version = ++historyLoadVersion;
        HistoryStatus.Text = "读取中…";
        try
        {
            var directory = settings.OutputDirectory;
            var files = await Task.Run(() => Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.jsonl").OrderByDescending(File.GetLastWriteTime).ToArray() : []);
            if (version != historyLoadVersion) return;
            updatingHistory = true;
            HistorySessions.ItemsSource = files.Select(path => new HistoryFile(path)).ToArray();
            var target = selectedHistory ?? session?.FilePath ?? files.FirstOrDefault();
            HistorySessions.SelectedItem = HistorySessions.Items.OfType<HistoryFile>().FirstOrDefault(x => x.Path == target);
            updatingHistory = false;
            HistoryStatus.Text = "";
            if (target != null && target != session?.FilePath) await ShowHistoryAsync(target);
        }
        catch (Exception ex) { updatingHistory = false; if (version == historyLoadVersion) { HistoryStatus.Text = "读取失败"; Notice(SafeErrors.Describe(ex)); } }
    }
    private sealed record HistoryFile(string Path)
    {
        public override string ToString()
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(Path);
            var match = System.Text.RegularExpressions.Regex.Match(name, @"^(课堂|演示)-(\d{8}-\d{6})");
            return match.Success && DateTime.TryParseExact(match.Groups[2].Value, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) ? $"{date:yyyy-MM-dd HH:mm:ss}" + (match.Groups[1].Value == "演示" ? " · 演示" : "") : name;
        }
    }
    private async void HistorySessionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingHistory || HistorySessions.SelectedItem is not HistoryFile file) return;
        if (file.Path == session?.FilePath && engine is not null) { historyLoadVersion++; HistoryStatus.Text = ""; selectedHistory = file.Path; lastRender = -1; RefreshCaptions(); }
        else await ShowHistoryAsync(file.Path);
    }
    private async Task ShowHistoryAsync(string path)
    {
        var version = ++historyLoadVersion;
        HistoryStatus.Text = "读取中…";
        HistoryExportButton.SetCurrentValue(IsEnabledProperty, false);
        try
        {
            var entries = await Task.Run(() => SessionStore.ReadEntries(path));
            if (version != historyLoadVersion) return;
            selectedHistory = path; rows.Clear();
            var id = 0L;
            foreach (var row in entries) rows.Add(new(++id, row.Label == "已确认" ? "" : row.Label, row.Russian, row.Chinese, "#E6C58D"));
            HistoryScroll.ScrollToTop(); HistoryStatus.Text = "";
        }
        catch (Exception ex) { if (version == historyLoadVersion) { HistoryStatus.Text = "读取失败"; Notice(SafeErrors.Describe(ex)); } }
        finally { if (version == historyLoadVersion) HistoryExportButton.SetCurrentValue(IsEnabledProperty, rows.Count > 0); }
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (MorePopup.IsOpen) { MorePopup.IsOpen = false; MoreButton.Focus(); e.Handled = true; }
        else if (historyOpen) { HistoryClick(sender, e); e.Handled = true; }
    }
    private void MoreClick(object sender, RoutedEventArgs e) => MorePopup.IsOpen = !MorePopup.IsOpen;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void AppearanceClick(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        ShowOverlay(); new AppearanceWindow(settings.Overlay, () => { overlay?.ApplySettings(); SaveSettingsSafely(); }) { Owner = this }.ShowDialog();
    }
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (Running || previewing || busy) { Notice("请先停止课堂或试听，再修改接入配置与密钥。"); return; }
        var dialog = new SettingsWindow(store, settings, credentials) { Owner = this };
        dialog.ShowDialog();
        var overlaySettings = settings.Overlay;
        settings = dialog.Settings; settings.Overlay = overlaySettings; credentials = dialog.Credentials;
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = selectedHistory ?? session?.FilePath;
            if (path is null) throw new UserFacingException("当前没有课堂记录。");
            if (engine is not null && Running) session?.SaveDraft(engine.State());
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "双语文字 (*.txt)|*.txt", FileName = Path.GetFileNameWithoutExtension(path) + ".txt", InitialDirectory = settings.OutputDirectory };
            if (dialog.ShowDialog(this) == true)
            {
                SessionStore.ExportText(path, dialog.FileName, path == session?.FilePath ? engine?.State() : null);
                recoveredUnsavedText = true;
                Notice("已导出。");
            }
        }
        catch (Exception ex) { Notice(SafeErrors.Describe(ex)); }
    }
    private bool collectingDiagnostics;
    private async void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        if(collectingDiagnostics)return;
        collectingDiagnostics=true;
        Notice("正在收集运行诊断…");
        try
        {
            var directory=Path.Combine(store.Root,"diagnostics");Directory.CreateDirectory(directory);
            string? executable=null;Exception? deploymentError=null;
            try{executable=ProbeDeployment.Ensure(store.Root);}catch(Exception ex){deploymentError=ex;}
            var runtimeReport=await RuntimeDiagnostics.SaveAsync(this,directory,executable);
            if(closing)return;
            if(executable is null)throw new UserFacingException("运行诊断已保存，但独立检测工具无法准备："+(deploymentError is null?"未返回检测工具路径":SafeErrors.Describe(deploymentError)));
            var start=new ProcessStartInfo(executable) {UseShellExecute=true,WorkingDirectory=directory};
            start.ArgumentList.Add("--output-dir");start.ArgumentList.Add(directory);
            start.ArgumentList.Add("--no-audio");
            start.ArgumentList.Add("--runtime-report");start.ArgumentList.Add(runtimeReport);
            Process.Start(start);
            Notice("运行诊断已保存；可在检测工具中继续检测环境，不采集声音。");
        }
        catch (Exception ex) { Notice(SafeErrors.Describe(ex)); }
        finally{collectingDiagnostics=false;}
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if(!exitRequested&&tray is not null){Hide();return;}
        if (closing) return;
        if (previewing) { e.Cancel = true; exitRequested=false; Notice("请等待当前试听结束后再退出。"); return; }
        closing = true;
        await StopRunAsync(); refresh.Stop();
        noticeTimer.Stop();
        if (session is not null && !session.IsHealthy && !recoveredUnsavedText)
        {
            var choice = System.Windows.MessageBox.Show(this, "文字尚未保存。取消退出后可导出，仍要退出吗？", "尚有未保存文字", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (choice != MessageBoxResult.OK) { closing = false; exitRequested=false; refresh.Start(); return; }
        }
        captionBall?.Close();captionBall=null;
        if (overlay is not null) { overlay.SavePosition(); overlay.Close(); }
        SaveSettingsSafely(); credentials = null;
        tray?.Dispose();tray=null;
        allowClose = true;
        _ = Dispatcher.BeginInvoke(() => { Close(); System.Windows.Application.Current.Shutdown(); });
    }
    private static readonly (string Ru, string Zh)[] DemoExamples =
    [
        ("Сегодня мы рассмотрим остойчивость судна и влияние положения центра тяжести.", "今天我们讨论船舶稳性，以及重心位置的影响。"),
        ("Если центр тяжести поднимается, начальная остойчивость обычно уменьшается.", "如果重心升高，初稳性通常会减小。"),
        ("В этом примере водоизмещение составляет не пятнадцать, а пятьдесят тысяч тонн.", "这个例子中的排水量不是一万五千吨，而是五万吨。")
    ];

}
