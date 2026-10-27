# Pedal Comp

A clean, efficient stereo compressor effect for [ReBuzz](https://github.com/wasteddesign/ReBuzz).

![Pedal Comp](screenshot.png)

---

## Features

- **Hard or soft knee**: knee width from 0 (hard) to 12 dB, a smooth quadratic curve
- **Peak or RMS detection**: peak for precise transient control, RMS (10 ms window) for smoother bus compression
- **Program-dependent release**: release shortens automatically on sudden signal drops, reducing pumping
- **Lookahead**: up to 20 ms delay on the audio path so the detector sees transients before they arrive
- **Wet/Dry mix**: parallel compression; the dry path is time-aligned through the lookahead buffer
- **Auto makeup gain**: computes output compensation from threshold and ratio
- **Output soft-clip**: gentle saturation above about -0.9 dBFS instead of hard clipping
- **Display panel** at the top of the parameter window:
  - IN, GR and OUT meters with GR peak hold, and a SAT light (lights in the soft-clip zone, clears after about 2 s, click to reset)
  - Transfer curve with a dot at the current detector level, so you can see whether the signal sits below, in or above the knee
  - **4-second history**: input level (green) and output level (blue) from the bottom on a -60 to 0 dBFS scale, gain reduction (red) hanging from the top on a 0 to 20 dB scale, threshold as a dashed line, a mark every second. Shows pumping and whether the release recovers between hits. Kept by the machine, so it carries on while the window is closed
  - Readout of the settings as they act on the audio: threshold, ratio, knee, attack and release in ms, the makeup actually applied (including Auto Makeup's value), mix, lookahead in ms and samples, and peak in and out levels over the last 3 seconds
- **Sleeps when idle**: a muted or silent input costs almost nothing, and audio resumes clean, with no leftover lookahead samples

---

## Parameters

| # | Parameter | Range | Default | Description |
|---|---|---|---|---|
| 0 | Threshold | 0 dB to -60 dB | -18 dB | Compression threshold. |
| 1 | Ratio | 1 to 30 | 4 | Ratio N:1. 1 = no compression, 30 = near-limiting. |
| 2 | Knee | Hard, 1 to 12 dB | Hard | Soft-knee width. |
| 3 | Attack | 1 to 200 ms | 10 ms | How fast the compressor responds to rising levels. |
| 4 | Release | 10 to 2000 ms | 100 ms | How fast it recovers. Shortens automatically on sudden drops. |
| 5 | Makeup Gain | 0 to +24 dB | 0 dB | Output gain. Ignored when Auto Makeup is on. |
| 6 | Auto Makeup | Off, On | Off | Makeup computed from threshold and ratio. |
| 7 | Wet/Dry | 0% to 100% | 100% | Blend between compressed and dry signal. |
| 8 | Lookahead | Off, 1 to 20 ms | Off | Delay on the audio path. Adds equivalent latency. |
| 9 | Detection | Peak, RMS | Peak | Envelope detection mode. |

Parameter names, order and stored values are unchanged since 1.0, so existing
songs load as before. The Buzz 1503 build
([pedal-comp-buzz-v1503](https://github.com/thepedal/pedal-comp-buzz-v1503))
has the same parameters and display; there, Wet/Dry is named Wet Dry.

---

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and ReBuzz
installed at `C:\Program Files\ReBuzz\` (the build references
`BuzzGUI.Interfaces.dll` and `BuzzGUI.Common.dll` from there).

From an **elevated** prompt (needed to write to Program Files), with ReBuzz closed:

```powershell
dotnet build -c Release
```

The build copies the DLL to `C:\Program Files\ReBuzz\Gear\Effects\Pedal Comp.NET.dll`
(look for the `Deploying` line). If ReBuzz is installed elsewhere:

```powershell
dotnet build -c Release -p:ReBuzzDir="D:\ReBuzz"
```

Restart ReBuzz and **Pedal Comp** appears in the Effects list.

---

## Version History

| Version | Changes |
|---|---|
| 1.5 | Brings across the additions from the Buzz 1503 build. Display panel widened to 560 px, with a 4-second level and gain-reduction history, a readout of the settings as applied, and a dot on the transfer curve at the detector level. Idle handling: when the input stops, the meters, detector, lookahead and RMS buffers are cleared, so audio resumes without replaying stale samples; the history keeps running. Text labels for Threshold, Knee, Makeup Gain, Wet/Dry and Lookahead (`-18 dB`, `Hard`, `100%`, `Off`); Makeup Gain previously showed wrong values (6 dB read as 15.6 dB, 0 dB as -inf). The panel refreshes only while it is on screen. About entry in the right-click menu. GPL-3.0 licence. Audio is unchanged from 1.4 (bit-identical output). |
| 1.4 | Fixes crackle (clusters of ticks on drum hits) under compression. The fast dB conversion used for the detector jumped by 1.76 dB just below every power of two, so the gain reduction stepped instantly each time the envelope crossed -6, -12, -18, -24 dBFS and so on (about 1.5 dB at 8:1). Both fast conversions now use cubics that are exact at their range ends: level to dB within 0.01 dB, dB to gain within 0.002 dB, with no steps. Soft-knee fix (as in the Buzz 1503 port): the knee divided by W instead of 2W, applying double the gain reduction inside the knee and a step at its top edge (4.8 dB at knee 11 dB, 8:1); the knee now joins the ratio line smoothly. Gain reduction is lower than in 1.3, especially with wide knees, so check thresholds on existing songs. |
| 1.3 | Fast log/exp in DSP hot path, auto makeup gain, GR peak hold, transfer curve display |
| 1.2 | Cached DSP coefficients, output soft-clip, program-dependent release, Peak/RMS label |
| 1.1 | Zero CPU when idle (WM_NOIO), RMS detection mode, auto-clearing SAT indicator |
| 1.0 | Initial release — threshold, ratio, knee, attack, release, makeup, wet/dry, lookahead |

---

## Licence

GNU General Public License v3.0, with an additional permission to combine the
machine with the ReBuzz and Jeskola Buzz hosts and their interface libraries.
See `LICENSE` and the header of `PedalComp.cs`.
