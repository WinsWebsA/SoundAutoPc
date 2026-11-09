namespace SoundAuto.Dsp;

/// <summary>
/// Stereo-linked, feed-forward RMS compressor with a look-ahead peak limiter.
///
/// Signal flow per frame:
///   1. Detector (on the *undelayed* input): sliding-window RMS across all channels, plus the instantaneous peak.
///   2. Gain computer (dB domain): soft-knee compression curve on the RMS level, then makeup gain,
///      then capped so that peak + gain never exceeds the ceiling (brick-wall limiting).
///   3. Look-ahead: a sliding minimum over the last N target gains, so a reduction is already in place
///      N samples before the loud sample reaches the output.
///   4. Envelope: one-pole attack/release smoothing of the gain (in dB) to avoid zipper noise and pumping.
///   5. The gain is applied to the audio delayed by N samples.
///
/// Process() is allocation-free and meant to run on the audio thread.
/// </summary>
public sealed class DynamicRangeCompressor
{
    private const double Epsilon = 1e-12;

    private readonly int _channels;
    private readonly int _sampleRate;

    // Look-ahead delay line, interleaved, one slot per frame.
    private readonly float[] _delay;
    private readonly int _delayFrames;
    private int _delayPos;

    // Sliding RMS window holding per-frame mean-square values.
    private readonly double[] _rmsWindow;
    private double _rmsSum;
    private int _rmsPos;
    private int _rmsRecalcCountdown;

    private readonly SlidingMinimum _lookAheadMin;

    private CompressorSettings _settings;
    private double _attackCoef;
    private double _releaseCoef;
    private double _envelopeDb;

    public DynamicRangeCompressor(int sampleRate, int channels, CompressorSettings settings)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _settings = settings.Clamped();

        _delayFrames = (int)Math.Round(_settings.LookAheadMs * sampleRate / 1000.0);
        _delay = new float[Math.Max(1, _delayFrames) * channels];

        int rmsFrames = Math.Max(1, (int)Math.Round(_settings.RmsWindowMs * sampleRate / 1000.0));
        _rmsWindow = new double[rmsFrames];
        _rmsRecalcCountdown = sampleRate;

        _lookAheadMin = new SlidingMinimum(_delayFrames + 1);
        _envelopeDb = _settings.MakeupDb;
        ApplyCoefficients();
    }

    /// <summary>Latency added by the look-ahead buffer.</summary>
    public double LatencyMs => _delayFrames * 1000.0 / _sampleRate;

    /// <summary>Strongest gain reduction (dB, ≤ 0, excluding makeup) during the last Process() call.</summary>
    public double LastBlockReductionDb { get; private set; }

    /// <summary>Samples the last-resort clamp had to catch during the last Process() call.</summary>
    public int LastBlockClipped { get; private set; }

    /// <summary>
    /// Called on the audio thread at the start of a block. Look-ahead and RMS window lengths are fixed at
    /// construction and are ignored here.
    /// </summary>
    internal void ApplySettings(CompressorSettings settings)
    {
        _settings = settings;
        ApplyCoefficients();
    }

    /// <summary>
    /// Processes interleaved float samples in place. Length must be a multiple of the channel count.
    /// With <paramref name="apply"/> false the detector still runs but gain stays at unity (the delay is kept,
    /// so switching bypass on and off doesn't shift the audio in time).
    /// </summary>
    public void Process(Span<float> samples, bool apply)
    {
        var s = _settings;
        int ch = _channels;
        int frames = samples.Length / ch;
        double invRmsLen = 1.0 / _rmsWindow.Length;

        double minReductionDb = 0;
        int clipped = 0;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * ch;

            // --- 1. Detector: linked mean-square and peak across channels.
            double sumSq = 0, peak = 0;
            for (int c = 0; c < ch; c++)
            {
                double x = samples[baseIdx + c];
                sumSq += x * x;
                double ax = Math.Abs(x);
                if (ax > peak) peak = ax;
            }
            double meanSq = sumSq / ch;

            _rmsSum += meanSq - _rmsWindow[_rmsPos];
            _rmsWindow[_rmsPos] = meanSq;
            if (++_rmsPos == _rmsWindow.Length) _rmsPos = 0;
            if (--_rmsRecalcCountdown == 0) RecalculateRmsSum(); // cancel floating-point drift

            double rmsDb = 10.0 * Math.Log10(Math.Max(_rmsSum * invRmsLen, 0) + Epsilon);

            // --- 2. Gain computer.
            double targetDb = ComputeCompressionDb(rmsDb, s) + s.MakeupDb;
            double peakDb = 20.0 * Math.Log10(peak + Epsilon);
            double limitDb = s.CeilingDb - peakDb;
            if (limitDb < targetDb) targetDb = limitDb;

            // --- 3. Look-ahead: strongest reduction requested anywhere in the look-ahead window.
            double lookAheadDb = _lookAheadMin.Push(targetDb);

            // --- 4. Attack/release envelope.
            double coef = lookAheadDb < _envelopeDb ? _attackCoef : _releaseCoef;
            _envelopeDb = lookAheadDb + coef * (_envelopeDb - lookAheadDb);

            double reductionDb = _envelopeDb - s.MakeupDb;
            if (reductionDb < minReductionDb) minReductionDb = reductionDb;

            float gain = apply ? (float)Math.Pow(10.0, _envelopeDb / 20.0) : 1f;

            // --- 5. Apply gain to the delayed signal.
            for (int c = 0; c < ch; c++)
            {
                int i = baseIdx + c;
                float input = samples[i];
                float delayed;
                if (_delayFrames == 0)
                {
                    delayed = input;
                }
                else
                {
                    int d = _delayPos * ch + c;
                    delayed = _delay[d];
                    _delay[d] = input;
                }

                float y = delayed * gain;
                // Last-resort safety: the limiter should keep us under the ceiling, but never send >0 dBFS.
                if (y > 1f) { y = 1f; clipped++; }
                else if (y < -1f) { y = -1f; clipped++; }
                samples[i] = y;
            }
            if (_delayFrames > 0 && ++_delayPos == _delayFrames) _delayPos = 0;
        }

        LastBlockReductionDb = minReductionDb;
        LastBlockClipped = clipped;
    }

    /// <summary>
    /// Static soft-knee compression curve. Returns the gain change in dB (always &lt;= 0) for a given input level.
    /// </summary>
    public static double ComputeCompressionDb(double levelDb, CompressorSettings s)
    {
        double over = levelDb - s.ThresholdDb;
        double slope = 1.0 / s.Ratio - 1.0; // negative
        double halfKnee = s.KneeDb / 2.0;

        if (over <= -halfKnee) return 0.0;                          // below the knee: untouched
        if (over >= halfKnee || s.KneeDb <= 0) return slope * over; // above the knee: full ratio

        // Inside the knee: quadratic blend between 1:1 and the full ratio.
        double x = over + halfKnee;
        return slope * x * x / (2.0 * s.KneeDb);
    }

    private void ApplyCoefficients()
    {
        _attackCoef = Math.Exp(-1.0 / (_settings.AttackMs * 0.001 * _sampleRate));
        _releaseCoef = Math.Exp(-1.0 / (_settings.ReleaseMs * 0.001 * _sampleRate));
    }

    private void RecalculateRmsSum()
    {
        double sum = 0;
        foreach (double v in _rmsWindow) sum += v;
        _rmsSum = sum;
        _rmsRecalcCountdown = _sampleRate;
    }

    /// <summary>
    /// O(1) amortised sliding-window minimum (monotonic deque in a ring buffer).
    /// </summary>
    private sealed class SlidingMinimum
    {
        private readonly int _window;
        private readonly double[] _values;
        private readonly long[] _indices;
        private int _head, _count;
        private long _n;

        public SlidingMinimum(int window)
        {
            _window = window;
            _values = new double[window + 1];
            _indices = new long[window + 1];
        }

        public double Push(double value)
        {
            int cap = _values.Length;

            // Drop older entries that can never be the minimum again.
            while (_count > 0 && _values[(_head + _count - 1) % cap] >= value) _count--;

            int tail = (_head + _count) % cap;
            _values[tail] = value;
            _indices[tail] = _n;
            _count++;

            // Drop entries that have slid out of the window.
            while (_indices[_head] <= _n - _window) { _head = (_head + 1) % cap; _count--; }

            _n++;
            return _values[_head];
        }
    }
}
