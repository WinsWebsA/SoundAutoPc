using SoundAuto.Dsp;

namespace SoundAuto.Tests;

public class CompressionCurveTests
{
    private static readonly CompressorSettings S = new() { ThresholdDb = -24, Ratio = 4, KneeDb = 6 };

    [Fact]
    public void Below_the_knee_is_untouched() =>
        Assert.Equal(0, DynamicRangeCompressor.ComputeCompressionDb(-40, S));

    [Fact]
    public void Above_the_knee_follows_the_ratio()
    {
        // 12 dB over a 4:1 threshold should come out 3 dB over, i.e. 9 dB of reduction.
        Assert.Equal(-9, DynamicRangeCompressor.ComputeCompressionDb(-12, S), precision: 6);
    }

    [Fact]
    public void Knee_is_continuous_at_both_edges()
    {
        double lower = DynamicRangeCompressor.ComputeCompressionDb(-27 + 1e-9, S);
        double upper = DynamicRangeCompressor.ComputeCompressionDb(-21 - 1e-9, S);
        Assert.Equal(0, lower, precision: 6);
        Assert.Equal((1.0 / 4 - 1) * 3, upper, precision: 6);
    }
}

public class ProcessorTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    [Fact]
    public void Output_never_exceeds_the_ceiling_even_on_a_sudden_full_scale_hit()
    {
        var p = new SoundProcessor(Rate, Channels, new ProcessorSettings());
        var quiet = Sine(-40, 1.0);
        var bang = Sine(0, 1.0);
        Run(p, quiet);
        Run(p, bang);

        double ceiling = Math.Pow(10, new CompressorSettings().CeilingDb / 20);
        Assert.True(bang.Max(Math.Abs) <= ceiling * 1.001, $"peak {bang.Max(Math.Abs)} > ceiling {ceiling}");
    }

    [Fact]
    public void Sustained_loud_music_is_brought_close_to_dialogue_level()
    {
        var dialogue = Sine(-27, 8);
        var music = Sine(-10, 10);

        var p = new SoundProcessor(Rate, Channels, new ProcessorSettings());
        Run(p, dialogue);
        Run(p, music);

        double dialogueOut = TailDb(dialogue);
        double musicOut = TailDb(music);
        // 17 dB apart going in; within 4 dB coming out.
        Assert.InRange(musicOut - dialogueOut, -4, 4);
    }

    [Fact]
    public void Whispers_are_lifted_but_not_beyond_the_boost_limits()
    {
        var s = new ProcessorSettings();
        var p = new SoundProcessor(Rate, Channels, s);
        var whisper = Sine(-42, 8);
        Run(p, whisper);

        double gain = TailDb(whisper) - (-42);
        Assert.InRange(gain, 3, s.Leveler.MaxBoostDb + s.Compressor.MakeupDb + 0.5);
    }

    [Fact]
    public void Silence_is_not_pumped_up_by_the_leveler()
    {
        var leveler = new LoudnessLeveler(Rate, Channels, new LevelerSettings());
        var hiss = Sine(-70, 10); // far below the gate
        leveler.Process(hiss, apply: true);
        Assert.Equal(0, leveler.GainDb, precision: 6);
    }

    [Fact]
    public void Bypass_leaves_level_unchanged()
    {
        var p = new SoundProcessor(Rate, Channels, new ProcessorSettings { Enabled = false });
        var music = Sine(-10, 3);
        double before = TailDb(music);
        Run(p, music);
        Assert.Equal(before, TailDb(music), precision: 1);
    }

    [Fact]
    public void Leveler_matches_the_EBU_reference_tone()
    {
        // EBU Tech 3341: a stereo 1 kHz sine at -23 dBFS peak (≈ -26 dBFS RMS) must read -23 LUFS.
        var leveler = new LoudnessLeveler(Rate, Channels, new LevelerSettings { Enabled = false });
        leveler.Process(Sine(-23 - 3.0103, 3, hz: 1000), apply: false);
        Assert.InRange(leveler.Loudness, -23.5, -22.5);
    }

    private static void Run(SoundProcessor p, float[] buffer)
    {
        const int block = 480 * Channels; // 10 ms blocks, like the real audio callback
        for (int i = 0; i < buffer.Length; i += block)
            p.Process(buffer.AsSpan(i, Math.Min(block, buffer.Length - i)));
    }

    /// <summary>Stereo sine with the given RMS level in dBFS.</summary>
    private static float[] Sine(double rmsDb, double seconds, double hz = 220)
    {
        int frames = (int)(Rate * seconds);
        var buf = new float[frames * Channels];
        double amp = Math.Pow(10, rmsDb / 20) * Math.Sqrt(2);
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * hz * f / Rate));
            buf[f * Channels] = v;
            buf[f * Channels + 1] = v;
        }
        return buf;
    }

    /// <summary>RMS level of the last third, after the slow stage has settled.</summary>
    private static double TailDb(float[] b)
    {
        int from = b.Length * 2 / 3;
        double sum = 0;
        for (int i = from; i < b.Length; i++) sum += (double)b[i] * b[i];
        return 10 * Math.Log10(sum / (b.Length - from) + 1e-12);
    }
}
