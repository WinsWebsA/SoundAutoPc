using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using SoundAuto;
using SoundAuto.Audio;
using SoundAuto.Platform;
using SoundAuto.Settings;

// SoundAuto: keeps movie audio at a steady level. Quiet dialogue comes up, explosions, loud music and
// nightclub scenes come down. Run it with no arguments and it works out the routing by itself.

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}\n");
    Options.PrintHelp();
    return 2;
}

if (options.ShowHelp) { Options.PrintHelp(); return 0; }
if (options.SelfTest) { SelfTest.Run(); return 0; }

var store = new SettingsStore(options.SettingsPath);
if (options.OpenSettings) { OpenInEditor(store); return 0; }
if (options.ListDevices) { ListDevices(store); return 0; }
if (options.Restore) return RestoreWindowsSound(store);

using var instance = ProcessSetup.TryAcquireSingleInstance();
if (instance == null)
{
    Console.Error.WriteLine("SoundAuto is already running. Close the other window first (two copies would double the sound).");
    return 1;
}

ProcessSetup.DisableBackgroundThrottling();

using var service = new SoundAutoService(store, new ServiceOptions
{
    FilePath = options.FilePath,
    StartAt = options.StartAt,
    LogPath = options.LogPath,
    OutputOverride = options.OutputOverride,
    DoNotManageWindowsDefault = options.NoSwitch,
});

var ui = new ConsoleUi(service, isFileMode: options.FilePath != null);
service.Message += ui.PrintMessage;
service.Finished += ui.RequestQuit;

// Put Windows' sound device back however we are closed: Ctrl+C, Ctrl+Break, closing the window,
// signing out or shutting down. (Windows allows a few seconds for this.)
using var onCtrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; ui.RequestQuit(); });
using var onBreak = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx => { ctx.Cancel = true; ui.RequestQuit(); });
using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => service.Stop());
using var onLogoff = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => service.Stop());

ui.PrintBanner();
service.Start();
ui.Run();
service.Stop();
ui.PrintGoodbye();
return 0;

static void ListDevices(SettingsStore store)
{
    var settings = store.Load();
    using var enumerator = new MMDeviceEnumerator();
    var catalog = new DeviceCatalog(enumerator);
    var devices = catalog.ActivePlaybackDevices();
    string? defaultId = catalog.DefaultPlaybackDeviceId();
    var cable = OutputSelector.ChooseCable(devices, settings.Routing.CableDevice);
    var pick = OutputSelector.Choose(devices, settings.Routing, cable?.Id, new Dictionary<string, DateTime>(), defaultId);

    Console.WriteLine("Connected playback devices:\n");
    foreach (var d in devices)
    {
        string kind = d.FormFactor switch
        {
            FormFactors.Headphones => "headphones",
            FormFactors.Headset => "headset",
            FormFactors.Speakers => "speakers",
            FormFactors.DisplayAudio => "monitor/TV",
            FormFactors.LineLevel => "line out",
            FormFactors.Spdif => "S/PDIF",
            _ => "other",
        };
        var notes = new List<string>();
        if (d.Id == cable?.Id) notes.Add("virtual cable (input)");
        if (d.Id == pick?.Id) notes.Add("SoundAuto would play here");
        if (d.Id == defaultId) notes.Add("Windows default");
        Console.WriteLine($"  {d.Name,-50} {kind,-11} {(notes.Count > 0 ? "<- " + string.Join(", ", notes) : "")}");
    }
    if (cable == null) Console.WriteLine("\n  No virtual cable found. Install the free VB-CABLE Driver from https://vb-audio.com/Cable/");
    Console.WriteLine($"\nSettings file: {store.FilePath}");
}

// Emergency exit: put Windows back on real speakers/headphones without running SoundAuto.
static int RestoreWindowsSound(SettingsStore store)
{
    var settings = store.Load();
    using var enumerator = new MMDeviceEnumerator();
    var catalog = new DeviceCatalog(enumerator);
    var devices = catalog.ActivePlaybackDevices();
    var current = devices.FirstOrDefault(d => d.Id == catalog.DefaultPlaybackDeviceId());

    if (current != null && !OutputSelector.IsVirtualCable(current))
    {
        Console.WriteLine($"Windows is already using {current.Name}; nothing to do.");
        RoutingState.Clear();
        return 0;
    }

    var saved = RoutingState.Load();
    var target = devices.FirstOrDefault(d => d.Id == saved?.DeviceId)
        ?? OutputSelector.Choose(devices, settings.Routing, current?.Id, new Dictionary<string, DateTime>(), null);
    if (target == null)
    {
        Console.Error.WriteLine("No speakers or headphones are connected.");
        return 1;
    }

    DefaultDeviceSwitcher.SetDefault(target.Id);
    RoutingState.Clear();
    Console.WriteLine($"Windows sound restored to {target.Name}.");
    return 0;
}

static void OpenInEditor(SettingsStore store)
{
    store.Load(); // make sure it exists
    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{store.FilePath}\"") { UseShellExecute = true });
    Console.WriteLine($"Opened {store.FilePath}. Changes apply as soon as you save (even while SoundAuto is running).");
}
