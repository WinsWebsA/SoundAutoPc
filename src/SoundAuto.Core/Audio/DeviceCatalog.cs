using NAudio.CoreAudioApi;

namespace SoundAuto.Audio;

/// <summary>Thin wrapper over the Windows device enumerator that produces <see cref="EndpointInfo"/> snapshots.</summary>
public sealed class DeviceCatalog(MMDeviceEnumerator enumerator)
{
    public MMDeviceEnumerator Enumerator => enumerator;

    /// <summary>All playback devices that are currently usable.</summary>
    public IReadOnlyList<EndpointInfo> ActivePlaybackDevices()
    {
        var list = new List<EndpointInfo>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                list.Add(new EndpointInfo(d.ID, d.FriendlyName, ReadFormFactor(d)));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // A device vanishing mid-enumeration is normal (e.g. Bluetooth disconnecting); skip it.
            }
        }
        return list;
    }

    public string? DefaultPlaybackDeviceId() =>
        enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var d) ? d.ID : null;

    public MMDevice GetDevice(string id) => enumerator.GetDevice(id);

    private static int ReadFormFactor(MMDevice d) =>
        d.Properties.TryGetValue<uint>(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out var ff) ? (int)ff : 0;
}
