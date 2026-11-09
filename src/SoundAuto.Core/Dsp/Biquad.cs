namespace SoundAuto.Dsp;

/// <summary>
/// Second-order IIR filter (transposed direct form II, double precision) with one state per channel.
/// </summary>
internal sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private readonly double[] _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2, int channels)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
        _z1 = new double[channels];
        _z2 = new double[channels];
    }

    public double Process(double x, int channel)
    {
        double y = _b0 * x + _z1[channel];
        _z1[channel] = _b1 * x - _a1 * y + _z2[channel];
        _z2[channel] = _b2 * x - _a2 * y;
        return y;
    }

    /// <summary>RBJ-cookbook high shelf.</summary>
    public static Biquad HighShelf(double sampleRate, double frequency, double gainDb, double q, int channels)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q), sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;
        return new Biquad(
            a * ((a + 1) + (a - 1) * cos + sqrtA2Alpha),
            -2 * a * ((a - 1) + (a + 1) * cos),
            a * ((a + 1) + (a - 1) * cos - sqrtA2Alpha),
            (a + 1) - (a - 1) * cos + sqrtA2Alpha,
            2 * ((a - 1) - (a + 1) * cos),
            (a + 1) - (a - 1) * cos - sqrtA2Alpha,
            channels);
    }

    /// <summary>RBJ-cookbook high pass.</summary>
    public static Biquad HighPass(double sampleRate, double frequency, double q, int channels)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha, channels);
    }
}
