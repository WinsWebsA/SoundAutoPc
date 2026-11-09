namespace SoundAuto.Dsp;

/// <summary>
/// The complete processing chain, in order:
///   1. <see cref="LoudnessLeveler"/>: slow, scene-level loudness control (music, nightclubs, long battles).
///   2. <see cref="DynamicRangeCompressor"/>: fast compression, makeup gain and look-ahead peak limiting
///      (gunshots, the first hit of an explosion).
///
/// Thread model: <see cref="Process"/> runs on the audio thread; <see cref="UpdateSettings"/> and
/// <see cref="TakeMeters"/> may be called from any thread.
/// </summary>
public sealed class SoundProcessor
{
    private const double Epsilon = 1e-12;

    private readonly LoudnessLeveler _leveler;
    private readonly DynamicRangeCompressor _compressor;
    private readonly int _channels;

    private ProcessorSettings _settings;
    private ProcessorSettings? _pending;

    private readonly Lock _meterLock = new();
    private MeterAccumulator _acc = MeterAccumulator.Empty;

    public SoundProcessor(int sampleRate, int channels, ProcessorSettings settings)
    {
        _channels = channels;
        _settings = settings.Clamped();
        _leveler = new LoudnessLeveler(sampleRate, channels, _settings.Leveler);
        _compressor = new DynamicRangeCompressor(sampleRate, channels, _settings.Compressor);
    }

    public double LatencyMs => _compressor.LatencyMs;

    /// <summary>Queues new settings; they take effect at the start of the next audio block.</summary>
    public void UpdateSettings(ProcessorSettings settings) => Volatile.Write(ref _pending, settings.Clamped());

    /// <summary>Processes interleaved float samples in place.</summary>
    public void Process(Span<float> samples)
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending != null)
        {
            _settings = pending;
            _leveler.ApplySettings(pending.Leveler);
            _compressor.ApplySettings(pending.Compressor);
        }

        int frames = samples.Length / _channels;
        if (frames == 0) return;
        samples = samples[..(frames * _channels)];

        double inSumSq = SumOfSquares(samples);

        bool apply = _settings.Enabled;
        _leveler.Process(samples, apply);
        _compressor.Process(samples, apply);

        double outSumSq = SumOfSquares(samples);
        double outPeak = 0;
        foreach (float v in samples) outPeak = Math.Max(outPeak, Math.Abs(v));

        lock (_meterLock)
        {
            _acc = new MeterAccumulator(
                _acc.InSumSq + inSumSq / _channels,
                _acc.OutSumSq + outSumSq / _channels,
                _acc.Frames + frames,
                Math.Max(_acc.OutPeak, outPeak),
                Math.Min(_acc.CompressorReductionDb, _compressor.LastBlockReductionDb),
                _leveler.GainDb,
                _leveler.Loudness,
                _acc.Clipped + _compressor.LastBlockClipped);
        }
    }

    /// <summary>Meter readings accumulated since the previous call (then reset).</summary>
    public Meters TakeMeters()
    {
        MeterAccumulator a;
        lock (_meterLock)
        {
            a = _acc;
            _acc = MeterAccumulator.Empty with { LevelerGainDb = a.LevelerGainDb, Loudness = a.Loudness };
        }
        return new Meters(
            a.Frames == 0 ? double.NegativeInfinity : ToDb(a.InSumSq / a.Frames),
            a.Frames == 0 ? double.NegativeInfinity : ToDb(a.OutSumSq / a.Frames),
            20 * Math.Log10(a.OutPeak + Epsilon),
            a.Loudness,
            a.LevelerGainDb,
            a.CompressorReductionDb,
            a.Clipped,
            a.Frames > 0);
    }

    private static double SumOfSquares(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (float v in s) sum += (double)v * v;
        return sum;
    }

    private static double ToDb(double meanSquare) => 10 * Math.Log10(meanSquare + Epsilon);

    private readonly record struct MeterAccumulator(
        double InSumSq, double OutSumSq, long Frames, double OutPeak,
        double CompressorReductionDb, double LevelerGainDb, double Loudness, int Clipped)
    {
        public static readonly MeterAccumulator Empty = new(0, 0, 0, 0, 0, 0, double.NegativeInfinity, 0);
    }
}

/// <summary>One meter reading, typically covering ~100 ms.</summary>
/// <param name="InputDb">Average input level (dBFS RMS per channel).</param>
/// <param name="OutputDb">Average output level (dBFS RMS per channel).</param>
/// <param name="OutputPeakDb">Highest output sample (dBFS).</param>
/// <param name="InputLoudness">Leveler's momentary loudness reading of the input (LUFS-style).</param>
/// <param name="LevelerGainDb">Gain the leveler is currently applying (+ boost, − cut).</param>
/// <param name="CompressorReductionDb">Strongest fast gain reduction in the interval (≤ 0).</param>
/// <param name="ClippedSamples">Samples caught by the last-resort clamp (should stay 0).</param>
/// <param name="HasSignal">False if no audio arrived at all in the interval.</param>
public readonly record struct Meters(
    double InputDb, double OutputDb, double OutputPeakDb, double InputLoudness,
    double LevelerGainDb, double CompressorReductionDb, int ClippedSamples, bool HasSignal)
{
    public static readonly Meters Silent = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity,
        double.NegativeInfinity, 0, 0, 0, false);
}
