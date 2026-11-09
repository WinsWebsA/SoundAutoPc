using SoundAuto.Settings;

namespace SoundAuto.Audio;

/// <summary>A playback endpoint as seen by the selection logic (no COM objects, so it is easy to test).</summary>
/// <param name="FormFactor">Windows endpoint form factor (see <see cref="FormFactors"/>).</param>
public sealed record EndpointInfo(string Id, string Name, int FormFactor);

/// <summary>Values of PKEY_AudioEndpoint_FormFactor that matter here.</summary>
public static class FormFactors
{
    public const int Speakers = 1;
    public const int LineLevel = 2;
    public const int Headphones = 3;
    public const int Headset = 5;
    public const int Spdif = 8;
    public const int DisplayAudio = 9; // HDMI / DisplayPort monitors and TVs
}

/// <summary>
/// Decides which real device the processed audio should play on.
///
/// Automatic mode, best first:
///   1. Headphones / headsets (wired jack, USB or Bluetooth). If several are connected, the most recently
///      connected one wins, because that's the one you just put on.
///   2. Speakers (laptop speakers, USB/desktop speakers).
///   3. Other outputs (line out, S/PDIF).
/// Never chosen automatically: the virtual cable itself (that would feed back), monitor/TV audio unless
/// allowed, and anything matching ExcludeOutputs (by default Bluetooth "Hands-Free" call mode, which is mono and
/// low quality).
/// </summary>
public static class OutputSelector
{
    public static EndpointInfo? Choose(
        IReadOnlyList<EndpointInfo> activeDevices,
        RoutingSettings routing,
        string? cableId,
        IReadOnlyDictionary<string, DateTime> connectedAtUtc,
        string? previousWindowsDefaultId)
    {
        var candidates = activeDevices.Where(d => d.Id != cableId && !IsVirtualCable(d)).ToList();

        // An explicitly named device wins whenever it's present.
        if (!routing.IsAutomaticOutput)
        {
            var named = candidates.FirstOrDefault(d => d.Name.Contains(routing.Output, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
        }

        return candidates
            .Where(d => routing.AllowDisplayAudio || d.FormFactor != FormFactors.DisplayAudio)
            .Where(d => !routing.ExcludeOutputs.Any(x => !string.IsNullOrWhiteSpace(x) && d.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(d => Rank(d.FormFactor))
            .ThenByDescending(d => connectedAtUtc.TryGetValue(d.Id, out var t) ? t : DateTime.MinValue)
            .ThenByDescending(d => d.Id == previousWindowsDefaultId)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Lower is better.</summary>
    public static int Rank(int formFactor) => formFactor switch
    {
        FormFactors.Headphones or FormFactors.Headset => 0,
        FormFactors.Speakers => 1,
        FormFactors.DisplayAudio => 3,
        _ => 2,
    };

    public static bool IsVirtualCable(EndpointInfo d) =>
        d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        d.Name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the cable endpoint: an exact "CABLE Input (" style match beats e.g. "CABLE In 16ch".</summary>
    public static EndpointInfo? ChooseCable(IReadOnlyList<EndpointInfo> activeDevices, string cableName) =>
        activeDevices.FirstOrDefault(d => d.Name.StartsWith(cableName + " (", StringComparison.OrdinalIgnoreCase))
        ?? activeDevices.FirstOrDefault(d => d.Name.Equals(cableName, StringComparison.OrdinalIgnoreCase))
        ?? activeDevices.FirstOrDefault(d => d.Name.Contains(cableName, StringComparison.OrdinalIgnoreCase));
}
