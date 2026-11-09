using System.Globalization;

namespace SoundAuto;

/// <summary>
/// Command-line options. Normal use needs none: tuning lives in the settings file, and routing is automatic.
/// </summary>
internal sealed class Options
{
    public string? OutputOverride { get; private set; }
    public string? FilePath { get; private set; }
    public TimeSpan StartAt { get; private set; }
    public string? LogPath { get; private set; }
    public string? SettingsPath { get; private set; }
    public bool NoSwitch { get; private set; }
    public bool ListDevices { get; private set; }
    public bool OpenSettings { get; private set; }
    public bool Restore { get; private set; }
    public bool SelfTest { get; private set; }
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            string NextPath() => Path.GetFullPath(Next().Trim('"'));

            switch (a)
            {
                case "--list" or "-l": o.ListDevices = true; break;
                case "--settings": o.OpenSettings = true; break;
                case "--restore": o.Restore = true; break;
                case "--settings-file": o.SettingsPath = NextPath(); break;
                case "--output": o.OutputOverride = Next(); break;
                case "--no-switch": o.NoSwitch = true; break;
                case "--file": o.FilePath = NextPath(); break;
                case "--start": o.StartAt = ParseTime(Next()); break;
                case "--log": o.LogPath = NextPath(); break;
                case "--selftest": o.SelfTest = true; break;
                case "--help" or "-h" or "/?": o.ShowHelp = true; break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        return o;
    }

    /// <summary>Accepts "90", "1:30" or "1:02:30".</summary>
    private static TimeSpan ParseTime(string text)
    {
        var parts = text.Split(':');
        if (parts.Length > 3 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            throw new ArgumentException($"--start: '{text}' is not a time (use seconds, m:ss or h:mm:ss)");
        double seconds = parts.Aggregate(0.0, (acc, p) => acc * 60 + double.Parse(p, CultureInfo.InvariantCulture));
        return TimeSpan.FromSeconds(seconds);
    }

    public static void PrintHelp() => Console.WriteLine("""
        SoundAuto: keeps movie and video sound at a steady level.

        Just run:  SoundAuto
          It routes Windows sound through the virtual cable, plays the evened-out result on your
          headphones or speakers (whichever is connected; it follows you when you plug/unplug),
          and puts Windows back to normal when you close it.

        Options (all optional):
          --list               Show connected devices and which one SoundAuto would use
          --settings           Open the settings file in Notepad (changes apply live)
          --output <name>      Use this device for this run instead of choosing automatically
          --no-switch          Don't change the Windows default sound device
          --restore            Put Windows sound back on your speakers/headphones and exit
          --file <path>        Test mode: play a video/audio file's soundtrack through SoundAuto
          --start <time>       With --file: start position (seconds, m:ss or h:mm:ss)
          --log <file.csv>     Record sound levels 10x per second for analysis
          --settings-file <p>  Use a different settings file
          --selftest           Check the sound processing with synthetic audio (no devices needed)
        """);
}
