using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundAuto.Audio;

/// <summary>Converts raw capture buffers (float or integer PCM) to interleaved 32-bit float.</summary>
public static class SampleConversion
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.IeeeFloat ||
        (f is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);

    private static bool IsPcm(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.Pcm ||
        (f is WaveFormatExtensible ext && ext.SubFormat == PcmSubFormat);

    /// <summary>Fills <paramref name="dest"/> (grown if needed) and returns the number of samples written.</summary>
    public static int ToFloat(ReadOnlySpan<byte> src, WaveFormat format, ref float[] dest)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int count = src.Length / bytesPerSample;
        if (dest.Length < count) dest = new float[count];

        if (IsFloat(format) && format.BitsPerSample == 32)
        {
            MemoryMarshal.Cast<byte, float>(src[..(count * 4)]).CopyTo(dest);
        }
        else if (IsPcm(format) && format.BitsPerSample == 16)
        {
            for (int i = 0; i < count; i++)
                dest[i] = BinaryPrimitives.ReadInt16LittleEndian(src[(i * 2)..]) / 32768f;
        }
        else if (IsPcm(format) && format.BitsPerSample == 24)
        {
            for (int i = 0, b = 0; i < count; i++, b += 3)
                dest[i] = ((src[b] << 8 | src[b + 1] << 16 | src[b + 2] << 24) >> 8) / 8388608f;
        }
        else if (IsPcm(format) && format.BitsPerSample == 32)
        {
            for (int i = 0; i < count; i++)
                dest[i] = BinaryPrimitives.ReadInt32LittleEndian(src[(i * 4)..]) / 2147483648f;
        }
        else
        {
            throw new NotSupportedException($"Unsupported capture format: {format}");
        }
        return count;
    }
}

/// <summary>
/// Minimal channel adapter for when the speakers use a different channel count than the source
/// (e.g. stereo movie → 5.1 output, or 7.1 cable → stereo headphones).
/// 5.1/7.1 → stereo uses a proper centre-preserving down-mix; other down-mixes fold extra channels in;
/// up-mixing repeats the source channels.
/// </summary>
public sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inCh, _outCh;
    private float[] _scratch = Array.Empty<float>();

    public ChannelMapSampleProvider(ISampleProvider source, int outputChannels)
    {
        _source = source;
        _inCh = source.WaveFormat.Channels;
        _outCh = outputChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / _outCh;
        int need = frames * _inCh;
        if (_scratch.Length < need) _scratch = new float[need];
        int framesRead = _source.Read(_scratch.AsSpan(0, need)) / _inCh;

        for (int f = 0; f < framesRead; f++)
        {
            int o = f * _outCh, i = f * _inCh;
            if (_outCh == 2 && _inCh >= 6)
            {
                // 5.1 / 7.1 → stereo (ITU-style). Movie dialogue lives in the centre channel, so it must be
                // folded into both sides rather than dropped. LFE is discarded, as is usual for stereo down-mixes.
                // Order: FL FR C LFE then surround pairs (BL BR [SL SR]).
                const float k = 0.7071f;
                float c = _scratch[i + 2] * k;
                float l = _scratch[i] + c, r = _scratch[i + 1] + c;
                for (int sc = 4; sc + 1 < _inCh; sc += 2)
                {
                    l += _scratch[i + sc] * k;
                    r += _scratch[i + sc + 1] * k;
                }
                float norm = 1f / (1f + k + k * ((_inCh - 4) / 2));
                buffer[o] = l * norm;
                buffer[o + 1] = r * norm;
            }
            else if (_outCh < _inCh)
            {
                for (int c = 0; c < _outCh; c++) buffer[o + c] = 0;
                for (int c = 0; c < _inCh; c++) buffer[o + c % _outCh] += _scratch[i + c];
                float norm = (float)_outCh / _inCh;
                for (int c = 0; c < _outCh; c++) buffer[o + c] *= norm;
            }
            else
            {
                for (int c = 0; c < _outCh; c++) buffer[o + c] = _scratch[i + c % _inCh];
            }
        }
        return framesRead * _outCh;
    }
}
