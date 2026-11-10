using System.Buffers.Binary;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

// Capture-thread owned. Both paths have the same RNNoise 0.2 delay so switching
// never jumps forward/backward in the audio timeline. There is deliberately no gate.
public sealed class MicrophoneNoiseProcessor : IDisposable
{
    private const int Frame = RnNoiseFilter.FrameSamples;
    private const int Delay = Frame * 2;
    private readonly float[] input = new float[Frame], wet = new float[Frame], dryDelay = new float[Delay];
    private RnNoiseFilter? filter;
    private int buffered, delayIndex, warmFrames;
    private long samplesProcessed, samplesReceived, samplesEmitted;
    private float mix;
    private bool completed, disposed;
    public bool Enabled { get; set; }

    public MicrophoneNoiseProcessor(bool enabled = false)
    {
        Enabled = enabled;
        if (enabled) filter = new();
    }
    public byte[] Process(ReadOnlySpan<byte> pcm48k)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("Audio was already drained.");
        if (pcm48k.Length % 2 != 0) throw new ArgumentException("Incomplete PCM16 sample.");
        samplesReceived += pcm48k.Length / 2;
        var output = new byte[pcm48k.Length + Frame * 2];
        var written = 0;
        for (var i = 0; i < pcm48k.Length; i += 2)
        {
            input[buffered++] = BinaryPrimitives.ReadInt16LittleEndian(pcm48k.Slice(i, 2));
            if (buffered == Frame) RenderFrame(output, ref written);
        }
        return output.AsSpan(0, written).ToArray();
    }
    public byte[] Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) return [];
        completed = true;
        var output = new byte[checked((int)(samplesReceived - samplesEmitted) * 2)];
        var written = 0;
        // Padding advances the filter; it is never counted as captured audio.
        while (samplesEmitted < samplesReceived)
        {
            Array.Clear(input, buffered, Frame - buffered);
            RenderFrame(output, ref written);
        }
        return output;
    }
    private void RenderFrame(Span<byte> output, ref int written)
    {
        if (Enabled && filter is null) { filter = new(); warmFrames = 0; }
        if (filter is not null) { filter.Process(input, wet); warmFrames++; }
        var target = Enabled && warmFrames > 2 ? 1f : 0f;
        for (var i = 0; i < Frame; i++)
        {
            var dry = dryDelay[delayIndex]; dryDelay[delayIndex] = input[i];
            delayIndex = (delayIndex + 1) % Delay;
            mix += Math.Clamp(target - mix, -1f / 1920, 1f / 1920); // 40 ms crossfade
            var value = dry + mix * (wet[i] - dry);
            if (!float.IsFinite(value)) throw new UserFacingException("本地降噪处理异常，请关闭降噪后重试。");
            samplesProcessed++;
            if (samplesProcessed <= Delay || samplesEmitted >= samplesReceived) continue;
            BinaryPrimitives.WriteInt16LittleEndian(output.Slice(written, 2), (short)Math.Clamp((int)MathF.Round(value), -32768, 32767));
            written += 2; samplesEmitted++;
        }
        buffered = 0;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; filter?.Dispose();
        Array.Clear(input); Array.Clear(wet); Array.Clear(dryDelay);
    }
}
