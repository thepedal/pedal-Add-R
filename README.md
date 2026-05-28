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
| Note         | track  | z=C-4, s=C#-4 …                                             |

Starting points: organ/pad = Damping 0, Brightness mid, a little Drift, Phase
mid-high (smooth onset); lush string ensemble = Damping 0 + Drift high + Phase
mid, played as a chord; struck bell = Inharmonic high + Damping mid + Sustain 0
+ Phase 0 (sharp strike); pluck = Inharmonic low + Damping high + fast attack +
Sustain 0 + Phase 0. Now also try sweeping Brightness while a sustained chord
holds — that's what live spectrum looks like in this engine.

## Presets

Ships with `Pedal Add-R_Presets.prs.xml` — 12 starter patches showing the
range:

- **Pad** — Soft Organ, Glass, String Ensemble, Choir Aaah (sustained side)
- **Bell / Mallet / Pluck** — Crystal, Tubular, Marimba, Harp, Pizzicato (struck side)
- **Bass** — Sub (low-partial harmonic)
- **FX** — Wobbling Glass, Slow Drone (extremes)

Right-click the machine to load. Per Build §3.3's append-only rule, the
preset indices stay stable across future versions; adding LFO / formant
later just gives existing presets the new params' defaults.

The bundle is generated from `gen_presets.py` (sparse-override pattern,
Build §3.4) — edit that and re-run rather than hand-editing the XML.
`gen_presets.py` is kept in source but isn't deployed.

## Build & deploy

`dotnet build` produces `Pedal Add-R.NET.dll` and the post-build target
copies both the DLL and `Pedal Add-R_Presets.prs.xml` to
`C:\Program Files\ReBuzz\Gear\Generators\` (Build §1.3 / §3.5 — the
preset bundle uses the ItemGroup pattern because the filename has a
space). If ReBuzz is installed elsewhere, change the path in the two
`<Copy>` tasks in the `.csproj`. Requires the .NET 10 SDK; references
`BuzzGUI.Interfaces.dll` from the ReBuzz install.

## Status and roadmap

**Done:** the partial-bank engine, the additive↔modal morph, 8-voice polyphony
with chord recovery, transport-stop fade, mixer headroom, per-voice Drift,
Phase spread, live Brightness, starter preset bank, click-protect retrigger
(v0.5).

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

**Next:**

- **Per-voice key-synced LFO** with a random sync offset (invFFT §21.2), routable
  to inharmonicity / brightness / pitch / drift — adds a whole modulation source.
- **Excitation blend** (strike↔drive) and **Formant** (a movable spectral peak).

## Files

`PedalAddR.cs` (machine + params + §42 note recovery + Work), `Voice.cs`
(partial-bank engine + drift + phase + live brightness + ADSR), `DspMath.cs`
(FastPow2, note conversion, soft clip), `gen_presets.py` (source-only),
`Pedal Add-R_Presets.prs.xml` (deployed bundle), the `.csproj`.
