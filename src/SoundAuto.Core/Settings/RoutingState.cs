using System.Text.Json;

namespace SoundAuto.Settings;

/// <summary>
/// Remembers which device Windows was using before SoundAuto switched it to the cable.
/// Written when the switch happens and deleted after a clean restore. If the app is killed in between,
/// the next start finds the file and can still put Windows back the way it was.
/// </summary>
public sealed record RoutingState(string DeviceId, string DeviceName, DateTime SwitchedAtUtc)
{
    private static string FilePath => Path.Combine(SettingsStore.DataDirectory, "state.json");

    public static RoutingState? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<RoutingState>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch (IOException) { }
    }
}
