using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundAuto.Settings;

/// <summary>
/// Loads, saves and watches the settings file. Hand-edited files are welcome: comments and trailing commas
/// are accepted, and a broken file never stops the app (the last good settings stay in use).
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly Lock _lock = new();
    private DateTime _lastSeenWriteUtc;

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(DataDirectory, "settings.json");
    }

    /// <summary>%APPDATA%\SoundAuto: per-user, survives app updates, needs no admin rights.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundAuto");

    public string FilePath { get; }

    /// <summary>Reads the file, creating it with defaults on first run.</summary>
    public AppSettings Load()
    {
        lock (_lock) return LoadCore();
    }

    private AppSettings LoadCore()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new AppSettings();
            SaveCore(defaults);
            return defaults;
        }

        _lastSeenWriteUtc = File.GetLastWriteTimeUtc(FilePath);
        return Parse(File.ReadAllText(FilePath));
    }

    public void Save(AppSettings settings)
    {
        lock (_lock) SaveCore(settings);
    }

    private void SaveCore(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Write to a temp file and swap it in, so a crash mid-write can never leave a half-written file.
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
        _lastSeenWriteUtc = File.GetLastWriteTimeUtc(FilePath);
    }

    /// <summary>
    /// Returns the new settings if the file was changed by someone else since the last load or save,
    /// null if unchanged. Throws <see cref="SettingsException"/> if the new contents are invalid.
    /// </summary>
    public AppSettings? ReloadIfChanged()
    {
        lock (_lock) return ReloadIfChangedCore();
    }

    private AppSettings? ReloadIfChangedCore()
    {
        if (!File.Exists(FilePath)) return null;
        var stamp = File.GetLastWriteTimeUtc(FilePath);
        if (stamp == _lastSeenWriteUtc) return null;
        _lastSeenWriteUtc = stamp;

        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (IOException) { _lastSeenWriteUtc = default; return null; } // still being written; retry next poll
        return Parse(text);
    }

    public static AppSettings Parse(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return settings.Clamped();
        }
        catch (JsonException ex)
        {
            throw new SettingsException($"settings file has an error near line {ex.LineNumber + 1}: {ex.Message}", ex);
        }
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);
}

public sealed class SettingsException(string message, Exception inner) : Exception(message, inner);
