using System.Diagnostics;
using SoundAuto.Dsp;

namespace SoundAuto;

/// <summary>Console front end: live meter line, notices, and tuning keys. Holds no audio logic of its own.</summary>
internal sealed class ConsoleUi(SoundAutoService service, bool isFileMode)
{
    private readonly Lock _consoleLock = new();
    private volatile bool _quit;
    private bool _statusVisible;

    public void RequestQuit() => _quit = true;

    public void PrintBanner()
    {
        Console.WriteLine("SoundAuto: steady movie sound. Leave this window open (minimise it); close it to stop.");
        Console.WriteLine("Keys: B on/off   +/- louder/quieter   L scene leveler on/off   Up/Down threshold   Left/Right ratio");
        Console.WriteLine("      [/] release   O open settings file   Q quit" + (isFileMode ? "   , . seek 10 s (Shift: 60 s)" : ""));
        Console.WriteLine($"Settings: {service.SettingsPath}\n");
    }

    public void PrintGoodbye()
    {
        lock (_consoleLock)
        {
            ClearStatusLine();
            Console.WriteLine("SoundAuto stopped.");
        }
    }

    /// <summary>Prints a notice above the status line. Called from the service thread.</summary>
    public void PrintMessage(string message)
    {
        lock (_consoleLock)
        {
            ClearStatusLine();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    public void Run()
    {
        while (!_quit)
        {
            HandleKeys();
            DrawStatus();
            Thread.Sleep(100);
        }
    }

    private void HandleKeys()
    {
        while (KeyAvailable())
        {
            var key = Console.ReadKey(intercept: true);
            bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
            switch (key.Key)
            {
                case ConsoleKey.Q or ConsoleKey.Escape: _quit = true; return;
                case ConsoleKey.B:
                    service.SetEnabled(!service.Enabled);
                    PrintMessage(service.Enabled ? "Processing ON" : "Processing OFF (bypass), hearing the original sound");
                    break;
                case ConsoleKey.L:
                    Update(s => s with { Leveler = s.Leveler with { Enabled = !s.Leveler.Enabled } },
                        s => $"Scene leveler {(s.Leveler.Enabled ? "ON" : "OFF")}");
                    break;
                case ConsoleKey.OemPlus or ConsoleKey.Add:
                    Update(s => s with { Compressor = s.Compressor with { MakeupDb = s.Compressor.MakeupDb + 1 } }, s => $"Boost {s.Compressor.MakeupDb:+0;-0} dB");
                    break;
                case ConsoleKey.OemMinus or ConsoleKey.Subtract:
                    Update(s => s with { Compressor = s.Compressor with { MakeupDb = s.Compressor.MakeupDb - 1 } }, s => $"Boost {s.Compressor.MakeupDb:+0;-0} dB");
                    break;
                case ConsoleKey.UpArrow:
                    Update(s => s with { Compressor = s.Compressor with { ThresholdDb = s.Compressor.ThresholdDb + 1 } }, s => $"Threshold {s.Compressor.ThresholdDb:0} dB");
                    break;
                case ConsoleKey.DownArrow:
                    Update(s => s with { Compressor = s.Compressor with { ThresholdDb = s.Compressor.ThresholdDb - 1 } }, s => $"Threshold {s.Compressor.ThresholdDb:0} dB");
                    break;
                case ConsoleKey.RightArrow:
                    Update(s => s with { Compressor = s.Compressor with { Ratio = s.Compressor.Ratio + 0.5 } }, s => $"Ratio {s.Compressor.Ratio:0.0}:1");
                    break;
                case ConsoleKey.LeftArrow:
                    Update(s => s with { Compressor = s.Compressor with { Ratio = s.Compressor.Ratio - 0.5 } }, s => $"Ratio {s.Compressor.Ratio:0.0}:1");
                    break;
                case ConsoleKey.Oem6: // ]
                    Update(s => s with { Compressor = s.Compressor with { ReleaseMs = s.Compressor.ReleaseMs + 25 } }, s => $"Release {s.Compressor.ReleaseMs:0} ms");
                    break;
                case ConsoleKey.Oem4: // [
                    Update(s => s with { Compressor = s.Compressor with { ReleaseMs = s.Compressor.ReleaseMs - 25 } }, s => $"Release {s.Compressor.ReleaseMs:0} ms");
                    break;
                case ConsoleKey.O:
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{service.SettingsPath}\"") { UseShellExecute = true });
                    break;
                case ConsoleKey.OemComma when isFileMode: service.Seek(TimeSpan.FromSeconds(shift ? -60 : -10)); break;
                case ConsoleKey.OemPeriod when isFileMode: service.Seek(TimeSpan.FromSeconds(shift ? 60 : 10)); break;
            }
        }
    }

    private void Update(Func<Settings.AppSettings, Settings.AppSettings> change, Func<Settings.AppSettings, string> describe)
    {
        service.UpdateSettings(change);
        PrintMessage(describe(service.Settings) + " (saved)");
    }

    private void DrawStatus()
    {
        if (Console.IsOutputRedirected) return;
        var m = service.LastMeters;
        string line;
        if (!m.HasSignal)
        {
            line = $"{(service.Enabled ? "ON " : "OFF")} | {service.OutputName ?? "-"} | {service.Status}" +
                   (service.Status == "running" ? " (no sound playing)" : "");
        }
        else
        {
            const int barWidth = 12;
            double totalCut = Math.Min(0, m.LevelerGainDb) + m.CompressorReductionDb;
            int filled = (int)Math.Round(Math.Clamp(-totalCut / 30.0, 0, 1) * barWidth);
            line =
                $"{(service.Enabled ? "ON " : "OFF")} | {service.OutputName} | in {Db(m.InputDb)} out {Db(m.OutputDb)} | " +
                $"scene {m.LevelerGainDb,5:+0.0;-0.0} fast {m.CompressorReductionDb,5:0.0} [{new string('#', filled)}{new string('-', barWidth - filled)}]" +
                (m.ClippedSamples > 0 ? " CLIP" : "") + (isFileMode ? $" | {service.StatusTag}" : "");
        }

        lock (_consoleLock)
        {
            int width = Math.Max(20, WindowWidth() - 1);
            if (line.Length > width) line = line[..width];
            Console.Write("\r" + line.PadRight(width));
            _statusVisible = true;
        }
    }

    private void ClearStatusLine()
    {
        if (!_statusVisible || Console.IsOutputRedirected) return;
        Console.Write("\r" + new string(' ', Math.Max(1, WindowWidth() - 1)) + "\r");
        _statusVisible = false;
    }

    private static string Db(double db) => double.IsNegativeInfinity(db) || db < -99 ? " -inf" : $"{db,5:0.0}";

    private static bool KeyAvailable()
    {
        try { return !Console.IsInputRedirected && Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }

    private static int WindowWidth()
    {
        try { return Console.WindowWidth; } catch (IOException) { return 120; }
    }
}
