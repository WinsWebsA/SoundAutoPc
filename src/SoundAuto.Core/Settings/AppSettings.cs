using System.Text.Json.Serialization;
using SoundAuto.Dsp;

namespace SoundAuto.Settings;

/// <summary>
/// Everything the user can configure. Stored as JSON in %APPDATA%\SoundAuto\settings.json and reloaded
/// automatically when the file changes. Missing values fall back to the defaults below.
/// </summary>
public sealed record AppSettings
{
    public LevelerSettings Leveler { get; init; } = new();
    public CompressorSettings Compressor { get; init; } = new();
    public RoutingSettings Routing { get; init; } = new();

    public AppSettings Clamped() => this with
    {
        Leveler = Leveler.Clamped(),
        Compressor = Compressor.Clamped(),
        Routing = Routing.Clamped(),
    };
}

/// <summary>Where audio comes from and goes to.</summary>
public sealed record RoutingSettings
{
    /// <summary>
    /// Output device: "auto" picks the best one automatically (headphones over speakers, and follows plugging and
    /// unplugging). Anything else is matched against device names, e.g. "Speakers" or "BEES".
    /// </summary>
    public string Output { get; init; } = "auto";

    /// <summary>Name (or part of it) of the virtual cable playback device that Windows plays movies into.</summary>
    public string CableDevice { get; init; } = "CABLE Input";

    /// <summary>
    /// Switch the Windows default playback device to the cable while SoundAuto runs, and back when it exits.
    /// </summary>
    public bool ManageWindowsDefault { get; init; } = true;

    /// <summary>Allow automatic selection to use monitor / TV audio over HDMI or DisplayPort.</summary>
    public bool AllowDisplayAudio { get; init; }

    /// <summary>Devices automatic selection must never pick (matched against names).</summary>
    public string[] ExcludeOutputs { get; init; } = ["Hands-Free"];

    /// <summary>WASAPI buffer per device in milliseconds. Raise it if you hear crackles.</summary>
    public int LatencyMs { get; init; } = 20;

    [JsonIgnore]
    public bool IsAutomaticOutput => string.IsNullOrWhiteSpace(Output) || Output.Equals("auto", StringComparison.OrdinalIgnoreCase);

    public RoutingSettings Clamped() => this with
    {
        LatencyMs = Math.Clamp(LatencyMs, 3, 200),
        ExcludeOutputs = ExcludeOutputs ?? [],
        Output = Output ?? "auto",
        CableDevice = string.IsNullOrWhiteSpace(CableDevice) ? "CABLE Input" : CableDevice,
    };
}
