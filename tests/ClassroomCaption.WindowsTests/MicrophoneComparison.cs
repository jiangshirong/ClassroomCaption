using NAudio.CoreAudioApi;
using NAudio.Wave;
using ClassroomCaption.App;
using System.Runtime.InteropServices;

internal static class MicrophoneComparison
{
    public static Task<object> RunAsync() => Task.Run(Run);
    private static object Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        var index = LegacyMicrophone.FindDevice(device.ID);
        using var native = new WasapiCapture(device, false, 100);
        var converter = new MicrophonePcmConverter(native.WaveFormat);
        long nativeBytes = 0, legacyBytes = 0;
        double nativeRms = 0, convertedRms = 0, legacyRms = 0, meterPeak = 0;
        string? nativeFailure = null, legacyFailure = null;
        string? rawFailure = null;
        AudioClient? raw = null; MicrophonePcmConverter? rawConverter = null;
        long rawBytes = 0; double rawRms = 0;
        var sessions = new List<object>();
        native.DataAvailable += (_, e) =>
        {
            nativeBytes += e.BytesRecorded;
            var pcm = converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
            nativeRms = Math.Max(nativeRms, converter.LastNativeRms);
            convertedRms = Math.Max(convertedRms, Rms(pcm, pcm.Length));
        };
        native.RecordingStopped += (_, e) => nativeFailure = e.Exception?.GetType().Name;
        using var legacy = new WaveInEvent { DeviceNumber = index, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        legacy.DataAvailable += (_, e) => { legacyBytes += e.BytesRecorded; legacyRms = Math.Max(legacyRms, Rms(e.Buffer, e.BytesRecorded)); };
        legacy.RecordingStopped += (_, e) => legacyFailure = e.Exception?.GetType().Name;
        try
        {
            native.StartRecording();
            if (index >= 0) { try { legacy.StartRecording(); } catch (Exception ex) { legacyFailure = ex.GetType().Name; } }
            try
            {
                raw = MicrophoneClientFactory.OpenRaw(device.ID);
                var format = raw.MixFormat;
                rawConverter = new(format);
                raw.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1000000, 0, format, Guid.NewGuid());
                raw.Start();
            }
            catch (Exception ex) { rawFailure = ex.GetType().Name + ":" + ex.HResult.ToString("X8"); raw?.Dispose(); raw = null; }
            for (var i = 0; i < 200; i++)
            {
                meterPeak = Math.Max(meterPeak, device.AudioMeterInformation.MasterPeakValue);
                if (raw is not null)
                {
                    var capture = raw.AudioCaptureClient;
                    while (capture.GetNextPacketSize() > 0)
                    {
                        var pointer = capture.GetBuffer(out var frames, out var flags);
                        try
                        {
                            var bytes = new byte[frames * raw.MixFormat.BlockAlign];
                            if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(pointer, bytes, 0, bytes.Length);
                            rawConverter!.Convert(bytes); rawBytes += bytes.Length;
                            rawRms = Math.Max(rawRms, rawConverter.LastNativeRms);
                        }
                        finally { capture.ReleaseBuffer(frames); }
                    }
                }
                Thread.Sleep(50);
            }
            var collection = device.AudioSessionManager.Sessions;
            for (var i = 0; i < collection.Count; i++)
            {
                using var session = collection[i];
                if (session.GetProcessID == Environment.ProcessId)
                    sessions.Add(new { muted = session.SimpleAudioVolume.Mute, volume = session.SimpleAudioVolume.Volume });
            }
        }
        finally { native.StopRecording(); if (index >= 0) legacy.StopRecording(); if (raw is not null) { raw.Stop(); raw.Dispose(); } }
        Thread.Sleep(300);
        return new { device = device.FriendlyName, muted = device.AudioEndpointVolume.Mute,
            volume = device.AudioEndpointVolume.MasterVolumeLevelScalar, legacyEndpointMatched = index >= 0,
            nativeBytes, nativeRms, convertedRms, legacyBytes, legacyRms, meterPeak, nativeFailure, legacyFailure,
            rawBytes, rawRms, rawFailure, sessions };
    }
    private static double Rms(byte[] bytes, int length)
    {
        double sum = 0;
        for (var i = 0; i + 1 < length; i += 2) { var s = BitConverter.ToInt16(bytes, i) / 32768.0; sum += s * s; }
        return Math.Sqrt(sum / Math.Max(1, length / 2));
    }
}
