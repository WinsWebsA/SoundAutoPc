using SoundAuto.Dsp;

namespace SoundAuto.Audio;

/// <summary>A running process-and-play session that the service can monitor, tune and rebuild.</summary>
public interface IAudioEngine : IDisposable
{
    /// <summary>Fires once when a device or stream fails; the owner disposes the engine and builds a new one.</summary>
    event Action<string>? Faulted;

    /// <summary>Fires when the source has nothing more to play (file mode only).</summary>
    event Action? Finished;

    SoundProcessor? Processor { get; }
    string OutputDeviceId { get; }
    string Description { get; }
    bool LowLatency { get; }

    /// <summary>Short engine-specific text for the end of the status line.</summary>
    string StatusTag { get; }

    void Start(ProcessorSettings settings);

    /// <summary>Jumps forward/backward in the source, where that makes sense.</summary>
    void Seek(TimeSpan offset);
}
