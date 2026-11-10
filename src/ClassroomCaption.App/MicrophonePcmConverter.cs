using System.Buffers.Binary;
using ClassroomCaption.Core;
using NAudio.Dsp;
using NAudio.Wave;

namespace ClassroomCaption.App;

// Feed the resampler complete native frames. Retain its filter history across capture packets.
public sealed class MicrophonePcmConverter
{
    private readonly WaveFormat format;
    private readonly int outputSampleRate;
    private readonly WdlResampler resampler = new();
    private int channel = -1;
    public double LastNativeRms { get; private set; }
    public bool LastNativeClipped { get; private set; }
    public MicrophonePcmConverter(WaveFormat nativeFormat, int outputSampleRate = 16000)
    {
        if (outputSampleRate is not (16000 or 48000)) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        this.outputSampleRate = outputSampleRate;
        format = nativeFormat is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : nativeFormat;
        if (format.Channels < 1 || format.Channels > 32 || format.SampleRate < 8000 ||
            !(format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ||
              format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32))
            throw new UserFacingException("麦克风的原始音频格式暂不支持，请选择其他输入设备。");
        resampler.SetMode(true, 2, false); resampler.SetFilterParms();
        resampler.SetFeedMode(true); resampler.SetRates(format.SampleRate, outputSampleRate);
    }
    public byte[] Convert(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % format.BlockAlign != 0) throw new UserFacingException("麦克风返回的音频帧不完整。");
        var frames = bytes.Length / format.BlockAlign;
        if (frames == 0) return [];
        var energies = new double[format.Channels];
        var clipped = new bool[format.Channels];
        var stride = format.BitsPerSample / 8;
        for (var frame = 0; frame < frames; frame++)
            for (var c = 0; c < format.Channels; c++)
            {
                var sample = Decode(bytes.Slice(frame * format.BlockAlign + c * stride, stride));
                energies[c] += sample * sample;
                if (Math.Abs(sample) > .992f) clipped[c] = true;
            }
        var strongest = 0;
        for (var c = 1; c < energies.Length; c++) if (energies[c] > energies[strongest]) strongest = c;
        // A single microphone channel avoids cancellation from out-of-phase array channels.
        // Hysteresis retains the channel when their levels are similar.
        if (channel < 0 || energies[strongest] > energies[channel] * 4) channel = strongest;
        LastNativeRms = Math.Sqrt(energies[channel] / frames);
        LastNativeClipped = clipped[channel];
        var requested = resampler.ResamplePrepare(frames, 1, out var input, out var offset);
        if (requested != frames) throw new InvalidOperationException("Unexpected input-driven resampler request.");
        for (var frame = 0; frame < frames; frame++)
            input[offset + frame] = Decode(bytes.Slice(frame * format.BlockAlign + channel * stride, stride));
        var output = new float[(int)Math.Ceiling(frames * (double)outputSampleRate / format.SampleRate) + 64];
        var count = resampler.ResampleOut(output, 0, frames, output.Length, 1);
        var pcm = new byte[count * 2];
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)Math.Clamp((int)Math.Round(output[i] * 32768), -32768, 32767));
        return pcm;
    }
    private float Decode(ReadOnlySpan<byte> bytes)
    {
        float sample;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        else sample = format.BitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
            24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f
        };
        return float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
    }
}
