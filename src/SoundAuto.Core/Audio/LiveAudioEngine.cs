using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundAuto.Dsp;

namespace SoundAuto.Audio;

/// <summary>
/// Live mode: one capture → process → playback session between two fixed devices.
/// If either device fails, <see cref="Faulted"/> fires once; the owner disposes this instance and builds a new one.
/// </summary>
public sealed class LiveAudioEngine : IAudioEngine
{
    private readonly MMDevice _source;
    private readonly MMDevice _target;
    private readonly int _latencyMs;

    private WasapiRecorder? _capture;
    private WasapiPlayer? _output;
    private BufferedWaveProvider? _buffer;
    private WaveFormat? _captureFormat;
    private float[] _floatScratch = Array.Empty<float>();
    private byte[] _discardScratch = Array.Empty<byte>();
    private volatile bool _disposing;
    private int _faulted;

    public event Action<string>? Faulted;
    public event Action? Finished { add { } remove { } } // live capture never "finishes"

    public SoundProcessor? Processor { get; private set; }
    public string OutputDeviceId => _target.ID;
    public string Description { get; private set; } = "";
    public int DroppedChunks { get; private set; }
    public bool LowLatency { get; private set; }

    public LiveAudioEngine(MMDevice source, MMDevice target, int latencyMs)
    {
        if (source.ID == target.ID)
            throw new InvalidOperationException(
                "The capture source and the output device are the same device. Playing processed audio into the " +
                "device we are recording from would feed back on itself.");
        _source = source;
        _target = target;
        _latencyMs = latencyMs;
    }

    public void Start(ProcessorSettings settings)
    {
        // --- Capture: loopback of everything Windows mixes into the source device.
        // Event-driven shared-mode loopback with "Pro Audio" MMCSS priority so the OS schedules us promptly.
        _capture = new WasapiRecorderBuilder()
            .WithDevice(_source)
            .WithLoopbackCapture()
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(_latencyMs)
            .WithMmcssThreadPriority()
            .Build();
        _captureFormat = _capture.WaveFormat;
        int rate = _captureFormat.SampleRate;
        int channels = _captureFormat.Channels;

        Processor = new SoundProcessor(rate, channels, settings);

        // Jitter buffer between the two device clocks. Processed audio is stored as 32-bit float.
        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, channels), TimeSpan.FromMilliseconds(500))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // output silence instead of stopping when capture has nothing (e.g. nothing playing)
        };

        // --- Output chain: adapt sample rate / channel layout to whatever the speakers' mixer wants.
        WaveFormat mix;
        using (var client = _target.CreateAudioClient()) mix = client.MixFormat;
        ISampleProvider chain = _buffer.ToSampleProvider();
        if (mix.SampleRate != rate) chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);
        if (mix.Channels != channels) chain = new ChannelMapSampleProvider(chain, mix.Channels);

        // WithLowLatency() opportunistically uses IAudioClient3 small engine periods when the driver supports them.
        _output = new WasapiPlayerBuilder()
            .WithDevice(_target)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(_latencyMs)
            .WithMmcssThreadPriority()
            .WithLowLatency()
            .Build();
        _output.Init(new SampleToWaveProvider(chain));

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) => OnStopped("capture", e.Exception);
        _output.PlaybackStopped += (_, e) => OnStopped("playback", e.Exception);

        _output.Play();
        _capture.StartRecording();

        LowLatency = _output.LowLatencyActive;
        Description =
            $"{_source.FriendlyName} [{rate} Hz, {channels} ch, {DescribeEncoding(_captureFormat)}]  ->  " +
            $"{_target.FriendlyName} [{mix.SampleRate} Hz, {mix.Channels} ch]";
    }

    /// <summary>Audio currently queued between capture and playback.</summary>
    public double BufferedMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;

    public string StatusTag => $"q {BufferedMs,3:0}ms";

    public void Seek(TimeSpan offset) { } // can't seek live audio

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_disposing || data.IsEmpty || _buffer == null || _captureFormat == null || Processor == null) return;

        try
        {
            int sampleCount = SampleConversion.ToFloat(data, _captureFormat, ref _floatScratch);
            // WASAPI may flag a packet as silent without zeroing it; the content must be ignored.
            if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_floatScratch, 0, sampleCount);
            var samples = _floatScratch.AsSpan(0, sampleCount);

            Processor.Process(samples);

            // Capture and playback run on different hardware clocks; if the speakers drain slightly slower,
            // the queue (and therefore latency) creeps up. Trim back down instead of letting it grow.
            int maxQueuedBytes = _buffer.WaveFormat.AverageBytesPerSecond * Math.Max(3 * _latencyMs, 40) / 1000;
            int excess = _buffer.BufferedBytes - maxQueuedBytes;
            if (excess > 0)
            {
                excess -= excess % _buffer.WaveFormat.BlockAlign;
                if (_discardScratch.Length < excess) _discardScratch = new byte[excess];
                _buffer.Read(_discardScratch.AsSpan(0, excess));
                DroppedChunks++;
            }

            _buffer.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples));
        }
        catch (Exception ex)
        {
            RaiseFault($"processing error: {ex.Message}");
        }
    }

    private void OnStopped(string which, Exception? ex)
    {
        if (_disposing) return;
        RaiseFault(ex == null ? $"{which} stopped unexpectedly" : $"{which} failed: {ex.Message}");
    }

    private void RaiseFault(string reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0) Faulted?.Invoke(reason);
    }

    private static string DescribeEncoding(WaveFormat f) =>
        SampleConversion.IsFloat(f) ? $"{f.BitsPerSample}-bit float" : $"{f.BitsPerSample}-bit PCM";

    public void Dispose()
    {
        _disposing = true;
        // Device calls can throw if the hardware has already vanished; swallow so cleanup always completes.
        try { _capture?.StopRecording(); } catch { }
        try { _output?.Stop(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        _capture = null;
        _output = null;
    }
}
