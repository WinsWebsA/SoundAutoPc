using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundAuto.Dsp;

namespace SoundAuto.Audio;

/// <summary>
/// Test mode: decodes the audio track of a video or audio file (anything Windows Media Foundation can open:
/// mp4, mkv, mp3, m4a, wav, ...) and plays it through the compressor on one device. No loopback and no
/// virtual cable are involved, so you can try the compressor on plain laptop speakers.
/// </summary>
public sealed class FilePlaybackEngine : IAudioEngine
{
    private readonly string _path;
    private readonly MMDevice _target;
    private readonly int _latencyMs;
    private readonly TimeSpan _startAt;

    private MediaFoundationReader? _reader;
    private WasapiPlayer? _output;
    private volatile bool _disposing;
    private int _faulted;

    public event Action<string>? Faulted;
    public event Action? Finished;

    public SoundProcessor? Processor { get; private set; }
    public string OutputDeviceId => _target.ID;
    public string Description { get; private set; } = "";
    public bool LowLatency { get; private set; }

    public FilePlaybackEngine(string path, MMDevice target, int latencyMs, TimeSpan startAt)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
        _path = path;
        _target = target;
        _latencyMs = latencyMs;
        _startAt = startAt;
    }

    /// <summary>Playback position, so a rebuilt engine (after a device hiccup) can resume where it left off.</summary>
    public TimeSpan CurrentTime => _reader?.CurrentTime ?? _startAt;

    public void Start(ProcessorSettings settings)
    {
        // RepositionInRead makes seeking from the UI thread safe while the audio thread is reading.
        _reader = new MediaFoundationReader(_path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
            RepositionInRead = true,
        });
        if (_startAt > TimeSpan.Zero && _startAt < _reader.TotalTime) _reader.CurrentTime = _startAt;

        var fileFormat = _reader.WaveFormat;
        WaveFormat mix;
        using (var client = _target.CreateAudioClient()) mix = client.MixFormat;

        // Adapt to the speakers first (surround down-mix, resample), then compress last so the
        // limiter's ceiling applies to exactly what reaches the device.
        ISampleProvider chain = _reader.ToSampleProvider();
        if (mix.Channels != fileFormat.Channels) chain = new ChannelMapSampleProvider(chain, mix.Channels);
        if (mix.SampleRate != fileFormat.SampleRate) chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);

        Processor = new SoundProcessor(mix.SampleRate, mix.Channels, settings);
        chain = new ProcessingSampleProvider(chain, Processor);

        _output = new WasapiPlayerBuilder()
            .WithDevice(_target)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(_latencyMs)
            .WithMmcssThreadPriority()
            .WithLowLatency()
            .Build();
        _output.Init(new SampleToWaveProvider(chain));
        _output.PlaybackStopped += (_, e) =>
        {
            if (_disposing) return;
            if (e.Exception != null) RaiseFault($"playback failed: {e.Exception.Message}");
            else Finished?.Invoke(); // reached the end of the file
        };
        _output.Play();

        LowLatency = _output.LowLatencyActive;
        Description =
            $"{Path.GetFileName(_path)} [{fileFormat.SampleRate} Hz, {fileFormat.Channels} ch, {Format(_reader.TotalTime)}]  ->  " +
            $"{_target.FriendlyName} [{mix.SampleRate} Hz, {mix.Channels} ch]";
    }

    public string StatusTag => _reader == null ? "" : $"{Format(_reader.CurrentTime)}/{Format(_reader.TotalTime)}";

    public void Seek(TimeSpan offset)
    {
        if (_reader == null) return;
        var t = _reader.CurrentTime + offset;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t >= _reader.TotalTime) t = _reader.TotalTime - TimeSpan.FromSeconds(1);
        _reader.CurrentTime = t;
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void RaiseFault(string reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0) Faulted?.Invoke(reason);
    }

    public void Dispose()
    {
        _disposing = true;
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _output = null;
        _reader = null;
    }

    /// <summary>Runs the processing chain in place on everything pulled through it.</summary>
    private sealed class ProcessingSampleProvider(ISampleProvider source, SoundProcessor processor) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int read = source.Read(buffer);
            int channels = WaveFormat.Channels;
            processor.Process(buffer[..(read - read % channels)]);
            return read;
        }
    }
}
