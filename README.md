# Pedal Add-R

An 8-voice polyphonic time-domain additive synth for ReBuzz, built around
**living partials** — a bank of independent sinusoids you can morph along one
continuous axis from a sustained harmonic tone (organ/pad) to a struck
*inharmonic* resonator (bells, mallets, plucks) — and that drift, beat, and
breathe on their own while sustaining.

It's the deliberate architectural complement to **Pedal invFFT**, which
synthesises a static spectrum in the frequency domain via iFFT/OLA. Working in
the time domain instead gives Add-R what invFFT structurally can't: zero
latency, sharp attacks, and per-partial behaviour that isn't capped at the hop
rate. The trade is cost — invFFT amortises one transform across all partials,
Add-R pays per partial — so Add-R earns its keep at modest partial counts with
transients and movement, exactly invFFT's blind spots.

## Engine

Each partial is a complex phasor rotated one step per sample:

```
z ← z · (cos ω + j·sin ω)        out += amp · real(z)
```

- **Inharmonicity** bends partial ratios from pure integer harmonics
  (`B = 0`) toward stretched/bell ratios via `f_n = f0 · n · √(1 + B·n²)`.
- **Damping** (+ **Damp Tilt**) gives each partial its own exponential
  ringdown. `Damping = 0` is sustain (additive); higher values turn the bank
  into struck/plucked modes, with the tilt making highs decay faster than lows.
- **Drift** gives each partial an independent, slow, mean-reverting random
  detune — "ensemble from within". Per-voice RNG means chord voices drift
  independently (invFFT §21.2), so a held chord shimmers like an ensemble
  rather than one voice multiplied. Opt-in: zero cost when the knob is at 0.

Phase rotation and amplitude decay are decoupled: the phasor stays a near-unit
oscillator (renormalised to unit magnitude at control rate with a single Newton
step — no `sqrt`), and a separate per-partial amplitude scalar carries the
decay. Rotor/decay/drift coefficients, glide, and Nyquist gating all update at a
32-sample control rate.

## Polyphony

8 voices, track-index = voice-index (M1 §1) — play a chord by putting notes on
multiple tracks of one row. Chord recovery uses the shape-tolerant `pvalues`
reader (Core §42), so chords don't collapse to last-track-only on either ReBuzz
layout (`ConcurrentDictionary` ≤1826, `int[256]` 1827+). A fixed mixer headroom
keeps dense chords clean (a single voice sits well below the soft-clip knee), so
push Volume / the master if it feels quiet. Transport Stop force-fades all voices
over ~5 ms (Core §27).

## Parameters

| Param        | Range  | Notes                                                       |
|--------------|--------|-------------------------------------------------------------|
| Volume       | 0–127  | Output level                                                |
| Partials     | 1–64   | Bank size (auto-gated below Nyquist)                        |
| Inharmonic   | 0–127  | Harmonic → stretched/bell. *The identity knob.*             |
| Brightness   | 0–127  | Spectral tilt (1/nˢ). Latches at note-on                    |
| Damping      | 0–127  | Sustain → struck/plucked ringdown                           |
| Damp Tilt    | 0–127  | How much faster high partials decay                         |
| Drift        | 0–127  | Per-partial slow random detune (ensemble from within)       |
| Attack/Decay/Sustain/Release | 0–127 | Amp ADSR (times ≈1 ms … ≈8 s)            |
| Glide        | 0–127  | 0 = instant, up = portamento                                |
| Note         | track  | z=C-4, s=C#-4 …                                             |

Starting points: organ/pad = Damping 0, Brightness mid, a little Drift; lush
string ensemble = Damping 0 + Drift high, played as a chord; struck bell =
Inharmonic high + Damping mid + Sustain 0; pluck = Inharmonic low + Damping high
+ fast attack + Sustain 0.

## Build & deploy

`dotnet build` produces `Pedal Add-R.NET.dll` and the post-build target copies
it to `C:\Program Files\ReBuzz\Gear\Generators\`. If ReBuzz is installed
elsewhere, change the path in the one `<Copy>` in the `.csproj`. Requires the
.NET 10 SDK; references `BuzzGUI.Interfaces.dll` from the ReBuzz install.

## Status and roadmap

**Done:** the partial-bank engine, the additive↔modal morph, 8-voice polyphony
with chord recovery, transport-stop fade, mixer headroom, per-voice Drift.

**Next:**

- **Profile.** Point `pedal-profiler2` at the stress patch (8 voices held, 64
  partials, Drift up). `ControlUpdate` is the hot spot; the cheap win if it's
  heavy is skipping the rotor recompute when pitch/inharmonicity are static —
  note that only helps when Drift is off (Drift forces a per-block recompute).
- **Phase spread** — controllable inter-partial phase as a timbral axis.
- **Per-voice key-synced LFO** with a random sync offset (invFFT §21.2).
- **Excitation blend** (strike↔drive) and **Formant** (a movable spectral peak).
- **Live Brightness** — currently latches at note-on; make it update mid-note.
- **Preset bank** (`.prs.xml`, Build §3) once the parameter surface settles.

## Files

`PedalAddR.cs` (machine + params + §42 note recovery + Work), `Voice.cs`
(partial-bank engine + drift + ADSR), `DspMath.cs` (FastPow2, note conversion,
soft clip), the `.csproj`.
