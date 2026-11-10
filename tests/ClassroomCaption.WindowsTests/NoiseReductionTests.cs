using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using ClassroomCaption.App;
using ClassroomCaption.Core;

internal static class NoiseReductionTests
{
    public static void Run(Action<string, Action> check, string root)
    {
        check("Noise bypass preserves every sample across irregular packets and stop", () =>
        {
            var input = Noise(48000 + 137);
            var output = Convert(input, false, [2, 34, 1998, 502, 960, 18]);
            if (!input.SequenceEqual(output)) throw new Exception("Bypass omitted, reordered or duplicated samples");
            using var empty = new MicrophoneNoiseProcessor();
            if (empty.Complete().Length != 0 || empty.Complete().Length != 0) throw new Exception("Empty drain produced audio");
        });
        check("RNNoise loads real native model and suppresses synthetic background noise", () =>
        {
            var input = Noise(48000 * 3 + 127);
            var timer = Stopwatch.StartNew();
            var output = Convert(input, true, [960]);
            timer.Stop();
            if (input.Length != output.Length) throw new Exception("Denoising changed duration");
            var ratio = Rms(output.AsSpan(48000 * 4)) / Rms(input.AsSpan(48000 * 4));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { noiseTestSeconds = input.Length / 96000.0, processingMs = timer.Elapsed.TotalMilliseconds, residualNoiseRatio = ratio }));
            if (!double.IsFinite(ratio) || ratio >= .8) throw new Exception("Native suppression did not reduce synthetic noise");
        });
        check("RNNoise output is independent of WASAPI packet boundaries", () =>
        {
            var input = Noise(48000 + 479);
            var a = Convert(input, true, [960]);
            var b = Convert(input, true, [14, 30, 500, 2048, 6]);
            if (!a.SequenceEqual(b)) throw new Exception("Packet boundaries changed the filter or tail");
        });
        check("Live noise switching preserves timing and silence and drains the final short frame", () =>
        {
            using var processor = new MicrophoneNoiseProcessor();
            var input = Noise(48000 * 2 + 11);
            using var output = new MemoryStream();
            var offset = 0;
            for (var i = 0; offset < input.Length; i++)
            {
                processor.Enabled = i % 7 < 3;
                var count = Math.Min(742, input.Length - offset);
                output.Write(processor.Process(input.AsSpan(offset, count))); offset += count;
            }
            output.Write(processor.Complete());
            if (output.Length != input.Length) throw new Exception("Switch changed the audio timeline");
            if (processor.Complete().Length != 0) throw new Exception("Second stop replayed audio");
            var silence = Convert(new byte[48000 * 2 + 14], true, [32, 992, 74]);
            if (silence.Length != 96014 || silence.Any(b => b != 0)) throw new Exception("Silence removed or fabricated");
        });
        check("48 kHz denoised stream still produces 16 kHz PCM for LiveTranslate", () =>
        {
            var input = Noise(48000 * 2);
            using var processor = new MicrophoneNoiseProcessor(true);
            var converter = new MicrophonePcmConverter(new NAudio.Wave.WaveFormat(48000, 16, 1));
            using var output = new MemoryStream();
            for (var offset = 0; offset < input.Length; offset += 960)
                output.Write(converter.Convert(processor.Process(input.AsSpan(offset, 960))));
            output.Write(converter.Convert(processor.Complete()));
            if (Math.Abs(output.Length - 64000) > 64) throw new Exception("ASR format conversion lost duration");
        });
        check("Noise switch saves preference and updates capture without recreating it", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "noise-setting"));
            store.Save(new AppSettings { OutputDirectory = store.Root });
            var window = new MainWindow(store);
            var toggle = (CheckBox)window.FindName("NoiseReductionToggle");
            if (toggle.IsChecked != false) throw new Exception("Existing configuration unexpectedly enabled suppression");
            using var capture = new AudioCaptureService();
            var field = typeof(MainWindow).GetField("audio", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            field.SetValue(window, capture);
            toggle.IsChecked = true;
            if (!store.Load().NoiseReductionEnabled || !capture.NoiseReductionEnabled || !ReferenceEquals(field.GetValue(window), capture)) throw new Exception("Live setting did not reach the existing capture");
            var restarted = new MainWindow(store);
            if (((CheckBox)restarted.FindName("NoiseReductionToggle")).IsChecked != true) throw new Exception("Restart lost preference");
            toggle.IsChecked = false;
            if (store.Load().NoiseReductionEnabled || capture.NoiseReductionEnabled) throw new Exception("Bypass was not restored");
        });
    }
    private static byte[] Noise(int samples)
    {
        var random = new Random(8127);
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), (short)random.Next(-1800, 1801));
        return bytes;
    }
    private static double Rms(ReadOnlySpan<byte> bytes)
    {
        double sum = 0;
        for (var i = 0; i < bytes.Length; i += 2) { var x = (double)BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i, 2)); sum += x * x; }
        return Math.Sqrt(sum / (bytes.Length / 2));
    }
    private static byte[] Convert(byte[] input, bool enabled, int[] packets)
    {
        using var processor = new MicrophoneNoiseProcessor(enabled);
        using var output = new MemoryStream();
        var offset = 0;
        for (var i = 0; offset < input.Length; i++)
        {
            var count = Math.Min(packets[i % packets.Length], input.Length - offset);
            output.Write(processor.Process(input.AsSpan(offset, count))); offset += count;
        }
        output.Write(processor.Complete());
        return output.ToArray();
    }
}
