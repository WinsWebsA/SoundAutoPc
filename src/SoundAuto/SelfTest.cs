using SoundAuto.Dsp;

namespace SoundAuto;

/// <summary>
/// Plays a synthetic "movie" (whisper, dialogue, sudden explosion, nightclub music, whisper again) through the
/// default processing chain and prints what comes out, so the behaviour can be checked without audio devices.
/// </summary>
internal static class SelfTest
{
    public static void Run()
    {
        const int rate = 48000, ch = 2;
        var settings = new Settings.AppSettings();
        var processor = new SoundProcessor(rate, ch, new ProcessorSettings { Leveler = settings.Leveler, Compressor = settings.Compressor });

        Console.WriteLine("Self-test: synthetic soundtrack through the default settings\n");
        Console.WriteLine("  scene                       in (dB)   out (dB)  change   out peak");

        (string name, double db, double seconds, double hz)[] scenes =
        [
            ("whisper", -42, 8, 300),
            ("normal dialogue", -27, 8, 220),
            ("explosion (first 0.5 s)", -4, 0.5, 60),
            ("explosion (continuing)", -4, 5.5, 60),
            ("nightclub music", -10, 10, 110),
            ("dialogue after music", -27, 8, 220),
            ("whisper again", -42, 8, 300),
        ];

        double phase = 0;
        foreach (var (name, db, seconds, hz) in scenes)
        {
            int frames = (int)(rate * seconds);
            var buf = new float[frames * ch];
            double amp = Math.Pow(10, db / 20) * Math.Sqrt(2); // sine RMS = amp/√2
            for (int f = 0; f < frames; f++)
            {
                float v = (float)(amp * Math.Sin(phase));
                phase += 2 * Math.PI * hz / rate;
                buf[f * ch] = v;
                buf[f * ch + 1] = v;
            }

            // Judge steady scenes by their last third (after the leveler has settled); short ones as a whole.
            int from = seconds >= 3 ? buf.Length * 2 / 3 : 0;
            double inDb = RmsDb(buf, from);
            for (int i = 0; i < buf.Length; i += 480 * ch) processor.Process(buf.AsSpan(i, Math.Min(480 * ch, buf.Length - i)));
            double outDb = RmsDb(buf, from);
            double peak = 20 * Math.Log10(buf.Max(Math.Abs) + 1e-12);
            Console.WriteLine($"  {name,-26} {inDb,8:0.0} {outDb,9:0.0} {outDb - inDb,+7:+0.0;-0.0} {peak,9:0.0}");
        }
        Console.WriteLine("\nInput spans 38 dB (whisper to explosion). A good result keeps dialogue, music and the");
        Console.WriteLine("explosion within a few dB of each other, lifts whispers, and never peaks above -1 dB.");
    }

    private static double RmsDb(float[] b, int from)
    {
        double sum = 0;
        for (int i = from; i < b.Length; i++) sum += (double)b[i] * b[i];
        return 10 * Math.Log10(sum / (b.Length - from) + 1e-12);
    }
}
