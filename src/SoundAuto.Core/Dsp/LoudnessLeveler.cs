namespace SoundAuto.Dsp;

/// <summary>
/// Slow automatic level control based on perceived loudness.
///
/// Loudness is measured the way ITU-R BS.1770 / EBU R128 meters do it: each channel is K-weighted
/// (a high-shelf for the ear's extra sensitivity to presence frequencies, plus a high-pass that ignores
/// rumble), the channel powers are summed, and the result is averaged over ~400 ms ("momentary" loudness).
/// The gain then glides towards <c>target − loudness</c>, limited to MaxBoost/MaxCut, turning down
/// faster than it turns up. Below the gate the gain is frozen, so pauses and silence are never amplified.
///
/// Fast transients are deliberately left to the compressor/limiter that follows; this stage only
/// fixes "this whole scene is too loud / too quiet".
/// </summary>
public sealed class LoudnessLeveler
{
    private const double Epsilon = 1e-12;
    private const double MomentaryWindowSeconds = 0.4;

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly Biquad _shelf;
    private readonly Biquad _highPass;
    private readonly int _lfeChannel;
    private readonly double _msCoef;

    private LevelerSettings _settings;
    private double _cutCoef, _boostCoef;
    private double _meanSquare;
    private double _gainDb;
    private float _gainLinear = 1f;

    public LoudnessLeveler(int sampleRate, int channels, LevelerSettings settings)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        // BS.1770 K-weighting filter parameters.
        _shelf = Biquad.HighShelf(sampleRate, 1681.974450955533, 3.999843853973347, 0.7071752369554196, channels);
        _highPass = Biquad.HighPass(sampleRate, 38.13547087602444, 0.5003270373238773, channels);
        _lfeChannel = channels >= 6 ? 3 : -1; // the LFE channel doesn't count towards loudness
        _msCoef = Math.Exp(-1.0 / (MomentaryWindowSeconds * sampleRate));
        _settings = settings.Clamped();
        ApplyCoefficients();
    }

    /// <summary>Gain currently applied (dB).</summary>
    public double GainDb => _gainDb;

    /// <summary>Most recent momentary loudness reading (LUFS-style).</summary>
    public double Loudness { get; private set; } = double.NegativeInfinity;

    /// <summary>Called on the audio thread at the start of a block.</summary>
    internal void ApplySettings(LevelerSettings settings)
    {
        _settings = settings;
        ApplyCoefficients();
    }

    /// <summary>Processes interleaved samples in place. With <paramref name="apply"/> false only the detector runs.</summary>
    public void Process(Span<float> samples, bool apply)
    {
        var s = _settings;
        int ch = _channels;
        int frames = samples.Length / ch;
        bool active = s.Enabled;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * ch;

            double power = 0;
            for (int c = 0; c < ch; c++)
            {
                if (c == _lfeChannel) continue;
                double k = _highPass.Process(_shelf.Process(samples[baseIdx + c], c), c);
                power += k * k;
            }
            _meanSquare = power + _msCoef * (_meanSquare - power);

            // Updating the target every 32 frames (~0.7 ms) is plenty for second-scale time constants.
            if ((f & 31) == 0)
            {
                double loudness = -0.691 + 10.0 * Math.Log10(_meanSquare + Epsilon);
                Loudness = loudness;

                if (active && loudness > s.GateLoudness)
                {
                    double desired = Math.Clamp(s.TargetLoudness - loudness, -s.MaxCutDb, s.MaxBoostDb);
                    double coef = desired < _gainDb ? _cutCoef : _boostCoef;
                    _gainDb = desired + coef * (_gainDb - desired);
                }
                else if (!active)
                {
                    _gainDb = 0;
                }
                _gainLinear = (float)Math.Pow(10.0, _gainDb / 20.0);
            }

            if (apply && _gainLinear != 1f)
            {
                for (int c = 0; c < ch; c++) samples[baseIdx + c] *= _gainLinear;
            }
        }
    }

    private void ApplyCoefficients()
    {
        // The envelope is updated every 32 frames, so the time constants are expressed in updates.
        double updatesPerSecond = _sampleRate / 32.0;
        _cutCoef = Math.Exp(-1.0 / (_settings.CutSeconds * updatesPerSecond));
        _boostCoef = Math.Exp(-1.0 / (_settings.BoostSeconds * updatesPerSecond));
    }
}
