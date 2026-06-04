# Pedal Add-R

An 8-voice polyphonic time-domain additive synth for ReBuzz, built around
**living partials** — a bank of independent sinusoids you can morph along one
continuous axis from a sustained harmonic tone (organ/pad) to a struck
*inharmonic* resonator (bells, mallets, plucks) — that drift, beat, and breathe
on their own while sustaining, and whose phase relationships you can shape at
the moment of strike.

It's the deliberate architectural complement to **Pedal invFFT**, which
synthesises a static spectrum in the frequency domain via iFFT/OLA. Working in
the time domain instead gives Add-R what invFFT structurally can't: zero
latency, sharp attacks, and per-partial behaviour that isn't capped at the hop
rate. The trade is cost — invFFT amortises one transform across all partials,
Add-R pays per partial — but profiler2 measurements at the stress patch (8
voices × 64 partials × Drift up) put Add-R at ~8 % of one core, well within
budget; the time-domain bet has paid off.

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
  independently (invFFT §21.2). Opt-in: zero cost when the knob is at 0.
- **Phase** sets the inter-partial phase spread at note-on. At 0 all partials
  start in phase — a sharp in-phase transient (the "strike", great for plucks
  and bells). At max each partial gets an independent random starting phase
  from the per-voice RNG, smearing the onset energy for smooth pad attacks.
  Phase is determined by rotor history once running, so this is a note-on-time
  control by design, not a continuous modulation.
- **LFO** is per-voice, key-synced with a `Sync` parameter that scales how
  randomised the start phase is at NoteOn (0 = all voices lock-step,
  127 = each voice gets a fully independent random phase — chord shimmer).
  Four wave shapes (Sine, Triangle, Square, Sample & Hold) and four bipolar
  destinations (pitch, brightness, inharmonicity, drift depth) routable
  simultaneously following the M1 §14 routing pattern.
- **Formant** is one resonant peak applied to the mix (post partial
  accumulation, pre Volume / soft-clip), at audio rate. A trapezoidal-
  prewarped state-variable filter — unconditionally stable up to Nyquist —
  gives the band-pass output, normalised so peak amplitude at resonance is
  unit regardless of Q. `Amount` then sets how much of that peak is mixed
  back over the dry signal. The filter is global (one instance shared across
  voices), so the formant frequency stays fixed regardless of pitch — the
  vocal-tract behaviour, not a tracking filter.
- **Velocity** is a per-track parameter (Group 2, alongside Note) that
  latches between explicit changes — empty pattern rows preserve the last
  value, matching tracker convention. The global `Vel Sens` then scales
  how much velocity actually affects the voice amplitude: at 0 velocity
  is ignored (uniform loudness — what pads usually want); at 127 it
  scales the voice gain directly. Lerp between the two for moderate
  expressivity.

Amplitude is split into a **shape** (`1/nˢ`, recomputed live at control rate so
Brightness can move mid-note, normalised so loudness stays constant as the
spectrum shifts) and a **decay envelope** that carries damping state. They're
multiplied into a cached per-sample amplitude, so the per-sample inner loop is
the same speed as before; only the control-rate recompute does a touch more
work. Rotor/decay/drift coefficients, glide, and Nyquist gating all update at a
32-sample control rate.

## Polyphony

8 voices, track-index = voice-index (M1 §1) — play a chord by putting notes on
multiple tracks of one row. Chord recovery uses the shape-tolerant `pvalues`
reader (Core §42), so chords don't collapse to last-track-only on either ReBuzz
layout (`ConcurrentDictionary` ≤1826, `int[256]` 1827+). Mixer headroom keeps
dense chords clean (a single voice sits well below the soft-clip knee), so push
Volume / the master if it feels quiet. Transport Stop force-fades all voices
over ~5 ms (Core §27).

## Parameters

| Param        | Range  | Notes                                                       |
|--------------|--------|-------------------------------------------------------------|
| Volume       | 0–127  | Output level                                                |
| Partials     | 1–64   | Bank size (auto-gated below Nyquist)                        |
| Inharmonic   | 0–127  | Harmonic → stretched/bell. *The identity knob.*             |
| Brightness   | 0–127  | Spectral tilt (1/nˢ). Live; loudness-neutral                |
| Damping      | 0–127  | Sustain → struck/plucked ringdown                           |
| Damp Tilt    | 0–127  | How much faster high partials decay                         |
| Drift        | 0–127  | Per-partial slow random detune (ensemble from within)       |
| Phase        | 0–127  | Inter-partial phase spread at note-on (sharp ↔ smeared)     |
| Attack/Decay/Sustain/Release | 0–127 | Amp ADSR (times ≈1 ms … ≈8 s)            |
| Glide        | 0–127  | 0 = instant, up = portamento                                |
| LFO Speed    | 0–127  | Log-mapped ~0.02 Hz to ~20 Hz                               |
| LFO Wave     | 0–3    | Sine / Triangle / Square / Sample & Hold                    |
| LFO Sync     | 0–127  | Per-voice random LFO phase at NoteOn (0 = lockstep)         |
| Mod Pitch    | 0–127  | Bipolar LFO→Pitch, 64 = off, ±63 = ±6 semitones             |
| Mod Bright   | 0–127  | Bipolar LFO→slope modulation, 64 = off                      |
| Mod Inharm   | 0–127  | Bipolar LFO→inharmonicity modulation, 64 = off              |
| Mod Drift    | 0–127  | Bipolar LFO→drift-depth modulation, 64 = off                |
| Formant Cutoff | 0–127 | Log-mapped ~100 Hz to ~8 kHz                              |
| Formant Q    | 0–127  | Log-mapped 0.5 (wide) to 30 (razor-sharp)                   |
| Formant Amount | 0–127 | Peak prominence over flat; 0 = off, full = ~14 dB boost   |
| Vel Sens     | 0–127  | How much velocity affects amplitude. 0 = uniform, 127 = full|
| Note         | track  | z=C-4, s=C#-4 …                                             |
| Velocity     | track  | Per-note velocity, latches between explicit changes         |

Starting points: organ/pad = Damping 0, Brightness mid, a little Drift, Phase
mid-high (smooth onset); lush string ensemble = Damping 0 + Drift high + Phase
mid, played as a chord; struck bell = Inharmonic high + Damping mid + Sustain 0
+ Phase 0 (sharp strike); pluck = Inharmonic low + Damping high + fast attack +
Sustain 0 + Phase 0. Try also: sweeping Brightness while a sustained chord
holds, and playing a chord with LFO Sync maxed — each voice gets its own
LFO phase, so the modulation moves through the chord rather than across it.

## Presets

Ships with `Pedal Add-R.prs.xml` — 29 patches showing the range:

- **Pad** — Soft Organ, Glass, String Ensemble, Choir Aaah (sustained side);
  Pulsing Glass, Ensemble Shimmer (LFO-driven); Cathedral (vast slow pad)
- **Bell / Mallet / Pluck** — Crystal, Tubular, Marimba, Harp, Pizzicato
  (struck side); Kalimba, Vibraphone, Steel Drum (v0.8 mallet expansion);
  Vox (formant-coloured pluck)
- **Bass** — Sub, Reese (drift wobble), Slap (formant pluck), Sine Sub (clean)
- **Lead** — Vibrato Sax (LFO pitch); Acid Squelch (LFO wah), Whistle (airy)
- **FX** — Wobbling Glass, Slow Drone, SH Glitch, Wobble (LFO triangle on
  brightness)
- **Formant pads** — Vowel Ahh (F1 ~730 Hz), Vowel Ee (F2 ~2290 Hz)

Each preset has its own Vel Sens value tuned to the patch character:
pads run low (~30 — uniform, ethereal), plucks and leads run high (~100–110
— expressive), bass between (40–110 depending on style). The default Vel
Sens for any new instance is 80 (moderate response).

Right-click the machine to load. Per Build §3.3's append-only rule, the
preset indices stay stable across future versions; adding formant /
excitation later just gives existing presets the new params' defaults.

The bundle is generated from `gen_presets.py` (sparse-override pattern,
Build §3.4) — edit that and re-run rather than hand-editing the XML.
`gen_presets.py` is kept in source but isn't deployed.

## Build & deploy

`dotnet build` produces `Pedal Add-R.NET.dll` and the post-build target
copies both the DLL and `Pedal Add-R.prs.xml` to
`C:\Program Files\ReBuzz\Gear\Generators\` (Build §1.3 / §3.5 — the
preset bundle uses the ItemGroup pattern because the filename has a
space). If ReBuzz is installed elsewhere, change the path in the two
`<Copy>` tasks in the `.csproj`. Requires the .NET 10 SDK; references
`BuzzGUI.Interfaces.dll` from the ReBuzz install.

## Status and roadmap

**Done:** the partial-bank engine, the additive↔modal morph, 8-voice polyphony
with chord recovery, transport-stop fade, mixer headroom, per-voice Drift,
Phase spread, live Brightness, starter preset bank, click-protect retrigger
(v0.5), per-voice key-synced LFO with 4 destinations (v0.6), Formant peak
filter on the mix (v0.7), About banner in the parameter window with version
display (v0.7.1), per-track velocity + Vel Sens scaling + bank expanded to
29 patches (v0.8).

Retrigger semantics: a *fresh* trigger (voice was idle) seeds the bank
immediately and lets the ADSR Attack ramp up from level 0 — output starts
at silence and ramps cleanly, so the partial re-seed is inaudible. A
*retrigger while the voice is still audible* would click without intervention,
because re-seeding phasors mid-flight is a hard discontinuity in the signal.
v0.5 wraps that behind a voice-level click-protect gain (Core §27.1): the
current output fades to ~0 over ~1 ms, *then* the bank re-seeds, *then* the
gain ramps back up over ~1 ms while the ADSR attacks from 0. The phasor
discontinuity still happens — it has to — but at the trough where the gain
masks it by ~46 dB. Total retrigger gap ~5 ms, well under the perceptual
latency floor for percussive material.

**Path to 1.0:**

- **v1.0 — Polish.** Final profile pass on the stress patch with everything
  turned up (LFO active + formant + 8 voices + 64 partials), doc tidy, ship.

Held for v1.x: **Vel→Bright / Vel→Attack** (more expressivity routing) —
deliberate omission from v0.8 to keep the surface tight; easy add later.
**Excitation blend** (strike ↔ drive) — pushes the engine from additive
toward complex-distortion; that's a separate identity and worth its own
release. **LFO → Formant** routing — needs new machinery for global-LFO
modulation since the formant is machine-level while the existing LFO is
per-voice.

## Files

`PedalAddR.cs` (machine + params + §42 note recovery + Work + formant SVF),
`Voice.cs` (partial-bank engine + drift + phase + live brightness +
click-protect + LFO + ADSR), `PedalAddRGui.cs` (About banner — embedded
parameter-window GUI via IMachineGUIFactory per Core §26), `DspMath.cs`
(FastPow2, note conversion, soft clip), `gen_presets.py` (source-only),
`Pedal Add-R.prs.xml` (deployed bundle), the `.csproj`.
