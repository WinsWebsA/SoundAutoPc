using System.Collections.Concurrent;
using System.Globalization;
using NAudio.CoreAudioApi;
using SoundAuto.Audio;
using SoundAuto.Dsp;
using SoundAuto.Platform;
using SoundAuto.Settings;

namespace SoundAuto;

/// <summary>Per-run options that are not part of the saved settings.</summary>
public sealed record ServiceOptions
{
    /// <summary>Test mode: play this file through the chain instead of capturing system audio.</summary>
    public string? FilePath { get; init; }
    public TimeSpan StartAt { get; init; }

    /// <summary>Append CSV meter readings (10 per second) here.</summary>
    public string? LogPath { get; init; }

    /// <summary>Use this output for this run only (overrides settings.routing.output).</summary>
    public string? OutputOverride { get; init; }

    /// <summary>Don't touch the Windows default device for this run.</summary>
    public bool DoNotManageWindowsDefault { get; init; }
}

/// <summary>
/// The whole app minus the user interface. It picks devices, routes Windows through the cable, runs the
/// audio engine, follows device changes, reloads settings, logs, and puts Windows back on exit.
/// All work happens on one background thread; the public members are safe to call from any thread.
/// </summary>
public sealed class SoundAutoService : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly SettingsStore _store;
    private readonly ServiceOptions _options;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ConcurrentDictionary<string, DateTime> _connectedAtUtc = new();
    private readonly Lock _settingsLock = new();

    private Thread? _thread;
    private volatile bool _stopping;
    private volatile string? _restartReason;
    private volatile bool _devicesChanged;
    private volatile bool _enabled = true;
    private volatile AppSettings _settings;
    private volatile IAudioEngine? _engine;

    // Windows-default bookkeeping (service thread only, except where noted).
    private DeviceCatalog? _catalog;
    private string? _cableId;
    private string? _previousDefaultId;
    private string? _lastOutputId;
    private bool _weSwitchedDefault;
    private volatile bool _userTookOver; // set from the notification thread
    private TimeSpan _resumeAt;

    private StreamWriter? _log;

    public SoundAutoService(SettingsStore store, ServiceOptions options)
    {
        _store = store;
        _options = options;
        _settings = store.Load();
        _resumeAt = options.StartAt;
    }

    /// <summary>Human-readable notices ("Now playing on Headphones", errors, ...). Raised on the service thread.</summary>
    public event Action<string>? Message;

    /// <summary>File mode: the file has finished playing.</summary>
    public event Action? Finished;

    public AppSettings Settings => _settings;
    public string SettingsPath => _store.FilePath;
    public bool Enabled => _enabled;
    public Meters LastMeters { get; private set; } = Meters.Silent;
    public string? OutputName { get; private set; }
    public string StatusTag => _engine?.StatusTag ?? "";

    /// <summary>What the service is doing right now, in plain words.</summary>
    public string Status { get; private set; } = "starting";

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { Name = "SoundAuto service", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Stops audio and puts the Windows default device back. Safe to call more than once.</summary>
    public void Stop()
    {
        _stopping = true;
        _wake.Set();
        if (_thread != null && _thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        Stop();
        _log?.Dispose();
        _wake.Dispose();
    }

    /// <summary>Turns processing on/off (bypass). Not saved: the app always starts enabled.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        ApplyProcessorSettings();
    }

    /// <summary>Changes settings, applies them immediately and saves them to the settings file.</summary>
    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        lock (_settingsLock)
        {
            var old = _settings;
            var updated = change(old).Clamped();
            _settings = updated;
            try { _store.Save(updated); }
            catch (IOException ex) { Notify($"Could not save settings: {ex.Message}"); }
            OnSettingsChanged(old, updated);
        }
    }

    public void Seek(TimeSpan offset) => _engine?.Seek(offset);

    // ------------------------------------------------------------------ service thread

    private void Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        _catalog = new DeviceCatalog(enumerator);
        using var notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);
        notifications.DeviceStateChanged += (_, e) => OnDeviceArrivedOrLeft(e.DeviceId, e.NewState == DeviceState.Active);
        notifications.DeviceAdded += (_, e) => OnDeviceArrivedOrLeft(e.DeviceId, arrived: true);
        notifications.DeviceRemoved += (_, e) => OnDeviceArrivedOrLeft(e.DeviceId, arrived: false);
        notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) OnWindowsDefaultChanged(e.DeviceId);
        };

        PrepareWindowsDefaultBookkeeping();

        while (!_stopping)
        {
            _restartReason = null;
            _devicesChanged = false;
            IAudioEngine? engine = null;
            try
            {
                engine = BuildEngine();
                engine.Faulted += reason => RequestRestart(reason);
                engine.Finished += () => { Finished?.Invoke(); _stopping = true; _wake.Set(); };
                engine.Start(CurrentProcessorSettings());
                _engine = engine;
                _lastOutputId = engine.OutputDeviceId;
                Status = "running";
                Notify($"Playing: {engine.Description}" + (engine.LowLatency ? "  (low-latency mode)" : ""));

                while (!_stopping && _restartReason == null)
                {
                    _wake.Wait(100);
                    _wake.Reset();
                    Tick(engine);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Status = "waiting: " + ex.Message;
                Notify(ex.Message);
                Notify($"Trying again in {RetryDelay.TotalSeconds:0} s...");
                WaitTicking(RetryDelay);
            }
            finally
            {
                _engine = null;
                if (engine is FilePlaybackEngine file) _resumeAt = file.CurrentTime; // resume here after a reconnect
                engine?.Dispose();
                LastMeters = Meters.Silent;
            }

            if (!_stopping && _restartReason != null)
            {
                Notify(_restartReason);
                WaitTicking(TimeSpan.FromMilliseconds(500)); // let Windows finish whatever it was doing
            }
        }

        RestoreWindowsDefault();
        Status = "stopped";
    }

    private IAudioEngine BuildEngine()
    {
        var routing = _settings.Routing;
        if (_options.OutputOverride != null) routing = routing with { Output = _options.OutputOverride };

        var devices = _catalog!.ActivePlaybackDevices();

        if (_options.FilePath != null)
        {
            var fileOut = OutputSelector.Choose(devices, routing, null, _connectedAtUtc, _previousDefaultId)
                ?? throw new InvalidOperationException("No speakers or headphones found.");
            OutputName = fileOut.Name;
            return new FilePlaybackEngine(_options.FilePath, _catalog.GetDevice(fileOut.Id), routing.LatencyMs, _resumeAt);
        }

        var cable = OutputSelector.ChooseCable(devices, routing.CableDevice)
            ?? throw new InvalidOperationException(
                $"Virtual cable \"{routing.CableDevice}\" not found. Install VB-CABLE from https://vb-audio.com/Cable/ " +
                "(the free \"VB-CABLE Driver\"), then restart your PC.");
        _cableId = cable.Id;

        var output = OutputSelector.Choose(devices, routing, cable.Id, _connectedAtUtc, _previousDefaultId)
            ?? throw new InvalidOperationException("No speakers or headphones found. Plug something in or turn Bluetooth headphones on.");
        if (!routing.IsAutomaticOutput && !output.Name.Contains(routing.Output, StringComparison.OrdinalIgnoreCase))
            Notify($"\"{routing.Output}\" is not connected; using {output.Name} for now.");

        if (ManagesWindowsDefault) EnsureWindowsUsesCable(cable);

        OutputName = output.Name;
        return new LiveAudioEngine(_catalog.GetDevice(cable.Id), _catalog.GetDevice(output.Id), routing.LatencyMs);
    }

    private void Tick(IAudioEngine engine)
    {
        LastMeters = engine.Processor?.TakeMeters() ?? Meters.Silent;
        WriteLog(LastMeters);
        PollSettingsFile();

        if (_devicesChanged)
        {
            _devicesChanged = false;
            ReconsiderOutput(engine);
        }
    }

    /// <summary>Keeps settings reloads and device watching alive while waiting to retry.</summary>
    private void WaitTicking(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (!_stopping && DateTime.UtcNow < until)
        {
            _wake.Wait(100);
            _wake.Reset();
            PollSettingsFile();
        }
    }

    /// <summary>A device came or went: if a better output is now available (or ours vanished), switch to it.</summary>
    private void ReconsiderOutput(IAudioEngine engine)
    {
        var routing = _settings.Routing;
        if (_options.OutputOverride != null) routing = routing with { Output = _options.OutputOverride };
        IReadOnlyList<EndpointInfo> devices;
        try { devices = _catalog!.ActivePlaybackDevices(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException) { return; }

        var best = OutputSelector.Choose(devices, routing, _cableId, _connectedAtUtc, _previousDefaultId);
        if (best != null && best.Id != engine.OutputDeviceId)
            RequestRestart($"Switching output to {best.Name}");
        else if (devices.All(d => d.Id != engine.OutputDeviceId))
            RequestRestart("Output device disconnected");
    }

    private void RequestRestart(string reason)
    {
        _restartReason ??= reason;
        _wake.Set();
    }

    private void OnDeviceArrivedOrLeft(string? deviceId, bool arrived)
    {
        if (deviceId != null && arrived) _connectedAtUtc[deviceId] = DateTime.UtcNow;
        _devicesChanged = true;
        _wake.Set();
    }

    // ------------------------------------------------------------------ Windows default device

    private bool ManagesWindowsDefault =>
        _options.FilePath == null && !_options.DoNotManageWindowsDefault && _settings.Routing.ManageWindowsDefault;

    /// <summary>Works out what to restore on exit, including after a previous run that was killed.</summary>
    private void PrepareWindowsDefaultBookkeeping()
    {
        if (!ManagesWindowsDefault) return;
        var leftover = RoutingState.Load();
        string? current = _catalog!.DefaultPlaybackDeviceId();
        var devices = _catalog.ActivePlaybackDevices();
        bool currentIsCable = devices.Any(d => d.Id == current && OutputSelector.IsVirtualCable(d));

        if (!currentIsCable)
        {
            _previousDefaultId = current;
            RoutingState.Clear(); // user already moved away from the cable; nothing to recover
        }
        else if (leftover != null)
        {
            _previousDefaultId = leftover.DeviceId;
            _weSwitchedDefault = true;
            Notify($"Previous session didn't close cleanly; will restore {leftover.DeviceName} on exit.");
        }
    }

    private void EnsureWindowsUsesCable(EndpointInfo cable)
    {
        if (_userTookOver) return; // the user picked another device on purpose; don't fight them
        string? current = _catalog!.DefaultPlaybackDeviceId();
        if (current == cable.Id) return;

        if (current != null)
        {
            _previousDefaultId = current;
            string name = _catalog.ActivePlaybackDevices().FirstOrDefault(d => d.Id == current)?.Name ?? "previous device";
            new RoutingState(current, name, DateTime.UtcNow).Save();
        }
        DefaultDeviceSwitcher.SetDefault(cable.Id);
        _weSwitchedDefault = true;
        Notify("Windows sound now goes through SoundAuto (default device set to the cable).");
    }

    private void OnWindowsDefaultChanged(string? newDefaultId)
    {
        if (!ManagesWindowsDefault || _cableId == null || _stopping) return;
        if (newDefaultId == _cableId)
        {
            if (_userTookOver) Notify("Windows output is the cable again; SoundAuto is active.");
            _userTookOver = false;
        }
        else if (_weSwitchedDefault && !_userTookOver)
        {
            _userTookOver = true;
            Notify("Windows output was changed away from the cable, so audio now bypasses SoundAuto. " +
                   "Pick \"CABLE Input\" in the taskbar sound menu to turn it back on.");
        }
    }

    /// <summary>On exit: if Windows is still on the cable, move it to the device we were playing on.</summary>
    private void RestoreWindowsDefault()
    {
        if (!ManagesWindowsDefault || _catalog == null) return;
        try
        {
            string? current = _catalog.DefaultPlaybackDeviceId();
            if (current != null && current == _cableId && (_weSwitchedDefault || RoutingState.Load() != null))
            {
                var active = _catalog.ActivePlaybackDevices();
                string? target = new[] { _lastOutputId, _previousDefaultId }.FirstOrDefault(id => id != null && active.Any(d => d.Id == id))
                    ?? OutputSelector.Choose(active, _settings.Routing, _cableId, _connectedAtUtc, null)?.Id;
                if (target != null)
                {
                    DefaultDeviceSwitcher.SetDefault(target);
                    Notify($"Windows sound restored to {active.First(d => d.Id == target).Name}.");
                }
            }
            RoutingState.Clear();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Notify($"Could not restore the Windows sound device: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ settings

    private ProcessorSettings CurrentProcessorSettings() => new()
    {
        Leveler = _settings.Leveler,
        Compressor = _settings.Compressor,
        Enabled = _enabled,
    };

    private void ApplyProcessorSettings() => _engine?.Processor?.UpdateSettings(CurrentProcessorSettings());

    private void PollSettingsFile()
    {
        try
        {
            var reloaded = _store.ReloadIfChanged();
            if (reloaded == null) return;
            lock (_settingsLock)
            {
                var old = _settings;
                _settings = reloaded;
                Notify("Settings file changed; new settings applied.");
                OnSettingsChanged(old, reloaded);
            }
        }
        catch (SettingsException ex)
        {
            Notify($"Ignoring settings change: {ex.Message}");
        }
        catch (IOException) { }
    }

    private void OnSettingsChanged(AppSettings old, AppSettings updated)
    {
        ApplyProcessorSettings();
        // Routing, buffer sizes and the look-ahead/RMS window lengths need a fresh engine.
        if (!RoutingEquivalent(old.Routing, updated.Routing))
            RequestRestart("Routing settings changed");
        else if (old.Compressor.LookAheadMs != updated.Compressor.LookAheadMs || old.Compressor.RmsWindowMs != updated.Compressor.RmsWindowMs)
            RequestRestart("Look-ahead/RMS window changed");
    }

    private static bool RoutingEquivalent(RoutingSettings a, RoutingSettings b) =>
        a with { ExcludeOutputs = [] } == b with { ExcludeOutputs = [] } && a.ExcludeOutputs.SequenceEqual(b.ExcludeOutputs);

    // ------------------------------------------------------------------ log

    private void WriteLog(Meters m)
    {
        if (_options.LogPath == null || !m.HasSignal) return;
        try
        {
            if (_log == null)
            {
                bool isNew = !File.Exists(_options.LogPath);
                _log = new StreamWriter(new FileStream(_options.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                if (isNew) _log.WriteLine("time,output,in_db,out_db,loudness,leveler_db,comp_db,out_peak_db,clipped,enabled");
            }
            var c = CultureInfo.InvariantCulture;
            _log.WriteLine(string.Join(",",
                DateTime.Now.ToString("HH:mm:ss.f", c), Csv(OutputName), Db(m.InputDb), Db(m.OutputDb), Db(m.InputLoudness),
                m.LevelerGainDb.ToString("0.0", c), m.CompressorReductionDb.ToString("0.0", c), Db(m.OutputPeakDb),
                m.ClippedSamples, _enabled ? 1 : 0));
        }
        catch (IOException) { } // logging must never interrupt the audio

        static string Db(double v) => double.IsNegativeInfinity(v) || v < -120 ? "-120" : v.ToString("0.0", CultureInfo.InvariantCulture);
        static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";
    }

    private void Notify(string message) => Message?.Invoke(message);
}
