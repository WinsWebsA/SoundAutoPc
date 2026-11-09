namespace SoundAuto.Dsp;

/// <summary>
/// Fast stage: catches sudden spikes (gunshots, door slams, the first hit of an explosion) within milliseconds.
/// Immutable, so the audio thread can swap a whole set atomically.
/// </summary>
public sealed record CompressorSettings
{
    /// <summary>Short-term RMS level (dBFS) above which gain reduction starts.</summary>
    public double ThresholdDb { get; init; } = -24.0;

    /// <summary>Compression ratio above the threshold (4 = every 4 dB over becomes 1 dB over).</summary>
    public double Ratio { get; init; } = 4.0;

    /// <summary>Soft-knee width in dB, centred on the threshold. 0 = hard knee.</summary>
    public double KneeDb { get; init; } = 6.0;

    /// <summary>Attack time constant (ms): how fast gain drops when a loud sound arrives.</summary>
    public double AttackMs { get; init; } = 1.0;

    /// <summary>Release time constant (ms): how fast gain recovers once the loud sound ends.</summary>
    public double ReleaseMs { get; init; } = 200.0;

    /// <summary>Look-ahead delay (ms). The detector sees audio this far before you hear it. Applied on restart.</summary>
    public double LookAheadMs { get; init; } = 5.0;

    /// <summary>Length of the sliding RMS window (ms). Applied on restart.</summary>
    public double RmsWindowMs { get; init; } = 5.0;

    /// <summary>Static gain applied after compression, lifting dialogue and whispers.</summary>
    public double MakeupDb { get; init; } = 8.0;

    /// <summary>Peak ceiling (dBFS) enforced by the built-in look-ahead limiter. Nothing louder reaches the speakers.</summary>
    public double CeilingDb { get; init; } = -1.0;

    /// <summary>Returns a copy with every value forced into a sane range.</summary>
    public CompressorSettings Clamped() => this with
    {
        ThresholdDb = Math.Clamp(ThresholdDb, -60.0, 0.0),
        Ratio = Math.Clamp(Ratio, 1.0, 50.0),
        KneeDb = Math.Clamp(KneeDb, 0.0, 24.0),
        AttackMs = Math.Clamp(AttackMs, 0.05, 200.0),
        ReleaseMs = Math.Clamp(ReleaseMs, 5.0, 5000.0),
        LookAheadMs = Math.Clamp(LookAheadMs, 0.0, 50.0),
        RmsWindowMs = Math.Clamp(RmsWindowMs, 0.5, 100.0),
        MakeupDb = Math.Clamp(MakeupDb, 0.0, 30.0),
        CeilingDb = Math.Clamp(CeilingDb, -20.0, 0.0),
    };
}

/// <summary>
/// Slow stage: keeps the *overall* loudness steady across scenes, so a long nightclub scene, a music
/// montage or a whole battle gets turned down, and a quiet conversation gets turned up, over a second or two.
/// </summary>
public sealed record LevelerSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Loudness (LUFS-style, K-weighted) everything is steered towards. Typical film dialogue is about -23 to -27.</summary>
    public double TargetLoudness { get; init; } = -23.0;

    /// <summary>Most the leveler may turn quiet scenes up.</summary>
    public double MaxBoostDb { get; init; } = 6.0;

    /// <summary>Most the leveler may turn loud scenes down.</summary>
    public double MaxCutDb { get; init; } = 18.0;

    /// <summary>Below this loudness the leveler holds its gain, so silence and room hiss are never pumped up.</summary>
    public double GateLoudness { get; init; } = -45.0;

    /// <summary>Time constant for turning down when a scene gets loud.</summary>
    public double CutSeconds { get; init; } = 1.0;

    /// <summary>Time constant for turning back up when a scene gets quiet. Slower, so it never "breathes".</summary>
    public double BoostSeconds { get; init; } = 3.0;

    public LevelerSettings Clamped() => this with
    {
        TargetLoudness = Math.Clamp(TargetLoudness, -40.0, -10.0),
        MaxBoostDb = Math.Clamp(MaxBoostDb, 0.0, 20.0),
        MaxCutDb = Math.Clamp(MaxCutDb, 0.0, 40.0),
        GateLoudness = Math.Clamp(GateLoudness, -80.0, -20.0),
        CutSeconds = Math.Clamp(CutSeconds, 0.1, 30.0),
        BoostSeconds = Math.Clamp(BoostSeconds, 0.1, 60.0),
    };
}

/// <summary>Everything the processing chain needs, swapped as one unit.</summary>
public sealed record ProcessorSettings
{
    public LevelerSettings Leveler { get; init; } = new();
    public CompressorSettings Compressor { get; init; } = new();

    /// <summary>False = bypass: audio passes through untouched (the meters keep running for comparison).</summary>
    public bool Enabled { get; init; } = true;

    public ProcessorSettings Clamped() => this with { Leveler = Leveler.Clamped(), Compressor = Compressor.Clamped() };
}
