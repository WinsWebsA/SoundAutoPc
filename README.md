# SoundAuto

Keeps movie and video sound at a steady level in **any** player (VLC, browsers, Netflix, games). Quiet
dialogue and whispers come up. Explosions, loud music, nightclub scenes and long battles come down. No more
reaching for the remote.

```
 VLC / browser / any app ──► Windows default device = CABLE Input (silent virtual cable)
                                          │ loopback capture
                                          ▼
                     SoundAuto:  scene leveler ──► fast compressor + limiter
                                          │
                                          ▼
                     your headphones or speakers (picked automatically)
```

## Using it

**One-time:** install the free **VB-CABLE Driver** from https://vb-audio.com/Cable/ and restart your PC.

**Every time:** double-click **`publish\SoundAuto.exe`** (or run `SoundAuto` in a terminal), then watch your
movie as usual. Minimise the window; close it when you're done.

On its own, SoundAuto:

- **routes Windows sound through itself**: it sets the Windows default device to the cable on start and puts it
  back on exit;
- **picks your output**: headphones (jack, USB or Bluetooth) over laptop speakers, preferring whichever was
  connected most recently. It never uses the cable itself, monitor/TV audio, or Bluetooth "Hands-Free" call mode;
- **follows you live**: plug in or unplug the jack, or connect Bluetooth headphones mid-movie, and the sound
  moves there within about a second;
- **recovers from crashes**: if it's ever killed, the next start (or `SoundAuto --restore`) puts Windows back.

If you change the output in the Windows taskbar sound menu yourself, SoundAuto respects that and stops
processing. Choose **CABLE Input** again to turn it back on.

### Keys (click the SoundAuto window first)

| Key | Action |
|---|---|
| **B** | Processing on/off: compare with the original sound |
| **+ / -** | Louder / quieter overall (boost) |
| **L** | Scene leveler on/off |
| Up / Down | Compressor threshold ±1 dB |
| Left / Right | Compressor ratio ±0.5 |
| [ / ] | Compressor release ±25 ms |
| **O** | Open the settings file |
| **Q** | Quit (restores the Windows sound device) |

Changes made with keys are saved automatically.

### Status line

```
ON  | Headphones (Realtek(R) Audio) | in -14.2 out -17.1 | scene  -7.3 fast  -2.1 [###---------]
```

`in`/`out` are the average levels before and after processing. `scene` is what the leveler is doing right now
(minus = turning a loud scene down, plus = lifting a quiet one). `fast` is the instant reduction applied to sudden
hits.

### Commands

| Command | What it does |
|---|---|
| `SoundAuto` | Normal use: everything automatic |
| `SoundAuto --list` | Connected devices, and which one SoundAuto would use |
| `SoundAuto --restore` | Put Windows sound back on your speakers/headphones (emergency) |
| `SoundAuto --settings` | Open the settings file in Notepad |
| `SoundAuto --output "Speakers"` | Force one device for this run |
| `SoundAuto --file movie.mp4 [--start 1:02:30]` | Test mode: play a file's soundtrack directly (no cable needed) |
| `SoundAuto --log levels.csv` | Record levels 10×/second for analysis |
| `SoundAuto --selftest` | Check the processing with synthetic audio |

## Settings

`%APPDATA%\SoundAuto\settings.json` is created on first run. Edit it while SoundAuto is running and the changes
apply as soon as you save. Comments are allowed, missing values use defaults, and a typo never stops the app
(the last good settings stay in use).

| Setting | Default | Meaning |
|---|---|---|
| `leveler.targetLoudness` | -23 | Loudness every scene is steered to (LUFS) |
| `leveler.maxCutDb` / `maxBoostDb` | 18 / 6 | Most a scene may be turned down / up |
| `leveler.cutSeconds` / `boostSeconds` | 1 / 3 | How quickly loud scenes come down / quiet ones come up |
| `leveler.gateLoudness` | -45 | Below this, the leveler holds still, so silence and hiss aren't pumped up |
| `compressor.thresholdDb` | -24 | Where fast compression starts |
| `compressor.ratio` | 4 | How hard sudden peaks are pushed down |
| `compressor.makeupDb` | 8 | Overall boost after processing (the **+/-** keys) |
| `compressor.ceilingDb` | -1 | Hard limit; nothing louder reaches the speakers |
| `compressor.attackMs` / `releaseMs` | 1 / 200 | Fast-stage reaction / recovery |
| `compressor.lookAheadMs` | 5 | Look-ahead (adds this much delay) |
| `routing.output` | `"auto"` | Or part of a device name, e.g. `"Speakers"`, `"BEES"` |
| `routing.manageWindowsDefault` | true | Switch Windows to the cable and back automatically |
| `routing.allowDisplayAudio` | false | Allow monitor/TV audio in automatic selection |
| `routing.excludeOutputs` | `["Hands-Free"]` | Never pick these automatically |
| `routing.latencyMs` | 20 | Buffer per device; raise it if you hear crackles |

**Quick recipes**
- *Dialogue still too quiet:* press **+** a couple of times.
- *Loud scenes still too loud:* lower `leveler.targetLoudness` (e.g. -26) or press **Down**.
- *Night mode:* `targetLoudness -26`, `maxCutDb 24`, `ratio 6`.

## How it works

Two stages, both written from scratch in `src/SoundAuto.Core/Dsp`:

1. **Scene leveler** (`LoudnessLeveler.cs`) measures perceived loudness the way broadcast meters do
   (ITU-R BS.1770 K-weighting, ~400 ms window). It slowly glides the gain towards the target: down within about
   a second when a scene gets loud, back up over about three seconds. It holds still during silence.
2. **Fast compressor + limiter** (`DynamicRangeCompressor.cs`) uses a 5 ms RMS detector, a soft-knee curve,
   makeup gain and a look-ahead brick-wall limiter. The audio is delayed 5 ms, so the gain is already down when
   a gunshot or the first hit of an explosion arrives.

Synthetic result (`SoundAuto --selftest`): whisper −42 → −29 dB, dialogue −27 → −17, nightclub music −10 → −17,
explosion −4 → −17. The input spans 38 dB; dialogue, music and explosions come out within 1 dB of each other.

## Project layout

```
SoundAuto.slnx
src/SoundAuto.Core/     everything except the UI: reusable by a future tray app or installer
  Dsp/                  leveler, compressor, filters, processing chain
  Audio/                capture/playback engines, device catalogue, automatic output selection
  Settings/             settings file (load/save/watch), crash-recovery state
  Platform/             Windows default-device switching, single instance, power-throttling opt-out
  SoundAutoService.cs   lifecycle: routing, device following, retries, settings reload, logging
src/SoundAuto/          console front end (status line, keys, commands)
tests/SoundAuto.Tests/  unit tests: DSP behaviour, output selection, settings parsing
```

Build, test and publish (needs the .NET 10 SDK):

```powershell
dotnet build
dotnet test
dotnet publish src/SoundAuto -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

**Planned next:** tray icon (on/off, quit), start with Windows, installer that also installs VB-CABLE.
https://vb-audio.com/Cable/