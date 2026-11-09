using SoundAuto.Audio;
using SoundAuto.Settings;

namespace SoundAuto.Tests;

public class OutputSelectorTests
{
    // Mirrors the devices on the development laptop.
    private static readonly EndpointInfo Speakers = new("spk", "Speakers (Realtek(R) Audio)", FormFactors.Speakers);
    private static readonly EndpointInfo Jack = new("jack", "Headphones (Realtek(R) Audio)", FormFactors.Headphones);
    private static readonly EndpointInfo Bluetooth = new("bt", "Headphones (BEES)", FormFactors.Headphones);
    private static readonly EndpointInfo HandsFree = new("hf", "Headset (BEES Hands-Free)", FormFactors.Headset);
    private static readonly EndpointInfo Monitor = new("mon", "M2432 (Intel(R) Display Audio)", FormFactors.DisplayAudio);
    private static readonly EndpointInfo Cable = new("cable", "CABLE Input (VB-Audio Virtual Cable)", FormFactors.Speakers);
    private static readonly EndpointInfo Cable16 = new("cable16", "CABLE In 16ch (VB-Audio Virtual Cable)", FormFactors.LineLevel);

    private static readonly RoutingSettings Auto = new();
    private static readonly Dictionary<string, DateTime> NoHistory = [];

    private static EndpointInfo? Pick(params EndpointInfo[] devices) =>
        OutputSelector.Choose(devices, Auto, Cable.Id, NoHistory, null);

    [Fact]
    public void Laptop_speakers_when_nothing_is_plugged_in() =>
        Assert.Equal(Speakers, Pick(Speakers, Monitor, Cable, Cable16));

    [Fact]
    public void Headphone_jack_beats_speakers() =>
        Assert.Equal(Jack, Pick(Speakers, Jack, Monitor, Cable));

    [Fact]
    public void Never_picks_the_cable_or_the_monitor() =>
        Assert.Null(Pick(Cable, Cable16, Monitor));

    [Fact]
    public void Monitor_is_used_when_allowed() =>
        Assert.Equal(Monitor, OutputSelector.Choose([Monitor, Cable], Auto with { AllowDisplayAudio = true }, Cable.Id, NoHistory, null));

    [Fact]
    public void Bluetooth_call_mode_is_skipped() =>
        Assert.Equal(Speakers, Pick(Speakers, HandsFree, Cable));

    [Fact]
    public void Most_recently_connected_headphones_win()
    {
        var history = new Dictionary<string, DateTime> { ["jack"] = new(2026, 1, 1, 10, 0, 0), ["bt"] = new(2026, 1, 1, 10, 5, 0) };
        Assert.Equal(Bluetooth, OutputSelector.Choose([Speakers, Jack, Bluetooth], Auto, Cable.Id, history, null));
    }

    [Fact]
    public void Previous_windows_default_breaks_ties() =>
        Assert.Equal(Bluetooth, OutputSelector.Choose([Jack, Bluetooth], Auto, Cable.Id, NoHistory, "bt"));

    [Fact]
    public void Named_output_wins_when_present_and_falls_back_when_absent()
    {
        var named = Auto with { Output = "Speakers" };
        Assert.Equal(Speakers, OutputSelector.Choose([Speakers, Jack], named, Cable.Id, NoHistory, null));
        Assert.Equal(Jack, OutputSelector.Choose([Jack], named, Cable.Id, NoHistory, null));
    }

    [Fact]
    public void Cable_lookup_prefers_the_stereo_cable_over_the_16_channel_one() =>
        Assert.Equal(Cable, OutputSelector.ChooseCable([Cable16, Cable, Speakers], "CABLE Input"));
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var original = new AppSettings() with
        {
            Compressor = new() { ThresholdDb = -30, MakeupDb = 10 },
            Routing = new() { Output = "Speakers", ExcludeOutputs = ["Foo"] },
        };
        var back = SettingsStore.Parse(SettingsStore.Serialize(original));
        Assert.Equal(original.Compressor, back.Compressor);
        Assert.Equal(original.Leveler, back.Leveler);
        Assert.Equal("Speakers", back.Routing.Output);
        Assert.Equal(["Foo"], back.Routing.ExcludeOutputs);
    }

    [Fact]
    public void Hand_edited_file_with_comments_and_missing_sections_uses_defaults()
    {
        var s = SettingsStore.Parse("""
            {
              // only change the boost
              "compressor": { "makeupDb": 5, },
            }
            """);
        Assert.Equal(5, s.Compressor.MakeupDb);
        Assert.Equal(new Dsp.CompressorSettings().ThresholdDb, s.Compressor.ThresholdDb);
        Assert.True(s.Routing.IsAutomaticOutput);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var s = SettingsStore.Parse("""{ "compressor": { "ratio": 500, "makeupDb": -9 } }""");
        Assert.Equal(50, s.Compressor.Ratio);
        Assert.Equal(0, s.Compressor.MakeupDb);
    }

    [Fact]
    public void Broken_json_reports_a_settings_error() =>
        Assert.Throws<SettingsException>(() => SettingsStore.Parse("{ \"compressor\": "));
}
