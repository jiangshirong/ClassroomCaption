using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ClassroomCaption.App;

// All stream COM objects are opened, read and released on one MTA worker.
public sealed class NativeMicrophoneCapture(string endpointId, bool noiseReduction = false) : IDisposable
{
    private volatile bool noiseReductionEnabled = noiseReduction;
    public bool NoiseReductionEnabled { get => noiseReductionEnabled; set => noiseReductionEnabled = value; }
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? worker;
    public string Mode { get; private set; } = "";
    public string NativeFormat { get; private set; } = "";
    public event Action<byte[]>? DataAvailable;
    public event Action<double, bool>? InputLevel;
    public event Action<Exception?>? RecordingStopped;
    public void StartRecording()
    {
        if (worker is not null) throw new InvalidOperationException("Capture has already started.");
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { started.Task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); }
        catch { cancellation.Cancel(); throw; }
    }
    private void Run()
    {
        AudioClient? client = null;
        Exception? failure = null;
        var running = false;
        try
        {
            try
            {
                client = MicrophoneClientFactory.OpenRaw(endpointId);
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1000000, 0, client.MixFormat, Guid.NewGuid());
                Mode = "原始收音";
            }
            catch (COMException)
            {
                client?.Dispose(); client = null;
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(endpointId);
                client = device.AudioClient;
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1000000, 0, client.MixFormat, Guid.NewGuid());
                Mode = "标准收音";
            }
            var format = client.MixFormat;
            NativeFormat = format.ToString();
            var converter = new MicrophonePcmConverter(format, 48000);
            using var noise = new MicrophoneNoiseProcessor(NoiseReductionEnabled);
            var outputConverter = new MicrophonePcmConverter(new NAudio.Wave.WaveFormat(48000, 16, 1));
            var capture = client.AudioCaptureClient;
            client.Start(); running = true; started.TrySetResult();
            while (!cancellation.IsCancellationRequested)
            {
                while (!cancellation.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                {
                    var pointer = capture.GetBuffer(out var frames, out var flags);
                    byte[] pcm;
                    try
                    {
                        var bytes = new byte[checked(frames * format.BlockAlign)];
                        if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(pointer, bytes, 0, bytes.Length);
                        var source = converter.Convert(bytes);
                        InputLevel?.Invoke(converter.LastNativeRms, converter.LastNativeClipped);
                        noise.Enabled = NoiseReductionEnabled;
                        pcm = outputConverter.Convert(noise.Process(source));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    if (pcm.Length > 0) DataAvailable?.Invoke(pcm);
                }
                cancellation.Token.WaitHandle.WaitOne(10);
            }
            var tail = outputConverter.Convert(noise.Complete());
            if (tail.Length > 0) DataAvailable?.Invoke(tail);
        }
        catch (Exception ex) { failure = ex; started.TrySetException(ex); }
        finally
        {
            try { if (running) client?.Stop(); } catch (COMException) { }
            client?.Dispose();
            RecordingStopped?.Invoke(failure);
        }
    }
    public void StopRecording() => cancellation.Cancel();
    public void Dispose()
    {
        cancellation.Cancel();
        // Run owns the native objects; never release them from the WPF thread.
        if (worker is not null && Task.CurrentId != worker.Id) worker.GetAwaiter().GetResult();
        cancellation.Dispose();
    }
}
