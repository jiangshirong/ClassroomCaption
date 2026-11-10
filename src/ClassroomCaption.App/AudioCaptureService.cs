using ClassroomCaption.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassroomCaption.App;

public sealed record AudioInput(string Id, string Name, bool CanRecord = true, string State = "可用")
{
    public string DisplayName { get; init; } = AudioInputNames.ShortName(Name);
    public string Status => CanRecord ? "可用" : State == "未连接" ? "未连接" : "已禁用";
    public string StatusIcon => CanRecord ? "M 3,8 L 6.5,11.5 13,4.5" : Status == "未连接" ? "M 3,3 L 13,13 M 13,3 L 3,13" : "M 8,1.5 A 6.5,6.5 0 1 1 7.99,1.5 M 3.5,3.5 L 12.5,12.5";
    public string StatusColor => CanRecord ? "#9BD6CA" : "#E6C58D";
    public string AccessibleName => Name + "，" + Status;
    public override string ToString() => Name;
}
public sealed record AudioInputInventory(string Name, string State, int StateCode);
public sealed record AudioInputHealth(string Name, bool Muted, float Volume, string DeviceFormat);
public sealed class AudioCaptureService : IDisposable
{
    private NativeMicrophoneCapture? capture;
    private MMDevice? device;
    private TaskCompletionSource? stopped;
    private bool requestedStop;
    private bool noiseReductionEnabled;
    public bool NoiseReductionEnabled
    {
        get => noiseReductionEnabled;
        set { noiseReductionEnabled = value; if (capture is not null) capture.NoiseReductionEnabled = value; }
    }
    public string CaptureMode => capture?.Mode ?? "";
    public event Action<byte[]>? Pcm;
    public event Action<double, bool>? Level;
    public event Action<string>? Fault;
    public static IReadOnlyList<AudioInput> Inputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged);
        var result = new List<AudioInput>();
        foreach (var d in devices)
        {
            using (d)
            {
                var state = d.State;
                var description = state == DeviceState.Disabled ? "已禁用" : state == DeviceState.Unplugged ? "未连接" : "可用";
                result.Add(new(d.ID, d.FriendlyName, state == DeviceState.Active, description));
            }
        }
        return AudioInputNames.Distinguish(result.OrderByDescending(d => d.CanRecord).ToArray());
    }
    public static IReadOnlyList<AudioInputInventory> InputInventory()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All);
        var result = new List<AudioInputInventory>();
        foreach (var d in devices)
        {
            using (d) result.Add(new(d.FriendlyName, d.State.ToString(), (int)d.State));
        }
        return result;
    }
    public static IReadOnlyList<AudioInputHealth> InputHealth()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioInputHealth>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (endpoint)
            {
                using var client = endpoint.AudioClient;
                result.Add(new(endpoint.FriendlyName, endpoint.AudioEndpointVolume.Mute,
                    endpoint.AudioEndpointVolume.MasterVolumeLevelScalar, client.MixFormat.ToString()));
            }
        }
        return result;
    }
    public void Start(string deviceId)
    {
        if (capture is not null) throw new UserFacingException("收音正在进行，请先停止。");
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDevice(deviceId);
        if (device.AudioEndpointVolume.Mute || device.AudioEndpointVolume.MasterVolumeLevelScalar <= 0)
        {
            device.Dispose(); device = null;
            throw new UserFacingException("Windows 中的麦克风已静音或音量为零，请先取消静音并提高输入音量。");
        }
        capture = new NativeMicrophoneCapture(deviceId, NoiseReductionEnabled);
        requestedStop = false;
        stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += pcm =>
        {
            if (pcm.Length == 0) return;
            Pcm?.Invoke(pcm);
        };
        // Keep the meter/clipping warning on the actual input, before suppression.
        capture.InputLevel += (rms, clipped) => Level?.Invoke(rms, clipped);
        capture.RecordingStopped += error =>
        {
            stopped.TrySetResult();
            if (!requestedStop) Fault?.Invoke(error is UserFacingException ? SafeErrors.Describe(error) : "音频输入已中断，可能是设备拔出或被占用。请停止后重新选择设备。");
        };
        try { capture.StartRecording(); }
        catch (Exception ex)
        {
            capture.Dispose(); capture = null; device.Dispose(); device = null;
            if (ex is UserFacingException) throw;
            throw new UserFacingException("无法开启该输入设备的共享录音，请检查设备连接、麦克风权限及驱动。");
        }
    }
    public async Task StopAsync()
    {
        requestedStop = true;
        if (capture is null) return;
        capture.StopRecording();
        if (stopped is not null) { try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { } }
        capture.Dispose(); capture = null; device?.Dispose(); device = null;
    }
    public void Dispose() { requestedStop = true; capture?.Dispose(); capture = null; device?.Dispose(); device = null; }

    public static async Task PreviewAsync(string deviceId, Action<double, bool> level, CancellationToken token, bool noiseReduction = false)
    {
        using var memory = new MemoryStream();
        using var microphone = new AudioCaptureService { NoiseReductionEnabled = noiseReduction };
        microphone.Level += level;
        microphone.Pcm += pcm => { if (memory.Length < 192000) memory.Write(pcm); };
        microphone.Start(deviceId);
        try { await Task.Delay(4000, token); }
        finally { await microphone.StopAsync(); }
        token.ThrowIfCancellationRequested();
        memory.Position = 0;
        using var source = new RawSourceWaveStream(memory, new WaveFormat(16000, 16, 1));
        using var player = new WaveOutEvent();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, _) => finished.TrySetResult();
        player.Init(source); player.Play();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(7), token);
        memory.GetBuffer().AsSpan().Clear();
    }
}
