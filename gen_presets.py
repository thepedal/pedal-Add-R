#!/usr/bin/env python3
"""
Pedal Add-R preset bundle generator (Build §3.4).
Outputs `Pedal Add-R.prs.xml` next to this script.

Conventions:
  • UTF-8 with BOM (Build §3.1)
  • <PresetDictionary> / <Item> / <Preset> / <Parameters> (Build §3.2)
  • Preset Machine="Pedal Add-R" must match MachineDecl.Name exactly
  • Parameter Index is *declaration order* — Build §3.3 append-only rule:
    NEVER reorder or insert; only append at the end. Existing preset
    indices must remain stable across versions.
"""

from pathlib import Path

MACHINE_NAME = "Pedal Add-R"
OUT_FILE     = "Pedal Add-R.prs.xml"

# Declaration order from PedalAddR.cs — append-only.
# v0.5: Volume..Glide (indices 0..12)
# v0.6: LFO params appended (indices 13..19)
PARAM_INDEX = {
    "Volume":       0,
    "Partials":     1,
    "Inharmonic":   2,
    "Brightness":   3,
    "Damping":      4,
    "Damp Tilt":    5,
    "Drift":        6,
    "Phase":        7,
    "Attack":       8,
    "Decay":        9,
    "Sustain":     10,
    "Release":     11,
    "Glide":       12,
    # ── v0.6 (LFO) ────────────────────────────
    "LFO Speed":   13,
    "LFO Wave":    14,
    "LFO Sync":    15,
    "Mod Pitch":  16,
    "Mod Bright": 17,
    "Mod Inharm": 18,
    "Mod Drift":  19,
    # ── v0.7 (Formant) ────────────────────────
    "Formant Cutoff": 20,
    "Formant Q":      21,
    "Formant Amount": 22,
    # ── v0.8 (Velocity) ───────────────────────
    "Vel Sens":       23,
    "LFO Mode":       24,
}

# Machine DefValues — mirror PedalAddR.cs.
DEFAULTS = {
    "Volume":      100,
    "Partials":     48,
    "Inharmonic":    0,
    "Brightness":   70,
    "Damping":       0,
    "Damp Tilt":    80,
    "Drift":         0,
    "Phase":         0,
    "Attack":        4,
    "Decay":        60,
    "Sustain":     127,
    "Release":      40,
    "Glide":         0,
    "LFO Speed":    30,
    "LFO Wave":      0,
    "LFO Sync":      0,
    "Mod Pitch":   64,    # 64 = off (bipolar, mid)
    "Mod Bright":  64,
    "Mod Inharm":  64,
    "Mod Drift":   64,
    "Formant Cutoff": 64, # ~900 Hz, vowel-friendly mid
    "Formant Q":      30, # ~3.0, moderate sharpness
    "Formant Amount":  0, # 0 = off (formant inaudible)
    "Vel Sens":       80, # moderate velocity response
    "LFO Mode":        0, # Free (Hz) — original behaviour; v1.2 preset bank stays free-running
}

# Sparse per-preset overrides. Missing keys take DEFAULTS.
# v0.5 presets continue to work unchanged — LFO destinations default to 64
# (= zero modulation), so the LFO runs but is inaudible.
PRESETS = {
    # ── Sustained / additive (Damping = 0) ──────────────────────────────
    "Pad - Soft Organ": {
        "Brightness": 60, "Drift": 25, "Phase": 50,
        "Attack": 50, "Release": 60,
        "Vel Sens": 30
    },
    "Pad - Glass": {
        "Inharmonic": 50, "Brightness": 90, "Drift": 20, "Phase": 100,
        "Attack": 60, "Release": 80,
        "Vel Sens": 50
    },
    "Pad - String Ensemble": {
        "Drift": 100, "Phase": 75,
        "Attack": 70, "Release": 90,
        "Vel Sens": 60
    },
    "Pad - Choir Aaah": {
        "Brightness": 55, "Drift": 110, "Phase": 90,
        "Attack": 80, "Release": 100,
        "Vel Sens": 30
    },

    # ── Struck / modal (Damping > 0, Sustain = 0) ───────────────────────
    "Bell - Crystal": {
        "Inharmonic": 95, "Brightness": 80, "Damping": 40, "Damp Tilt": 70,
        "Attack": 2, "Decay": 40, "Sustain": 0, "Release": 30,
        "Vel Sens": 100
    },
    "Bell - Tubular": {
        "Inharmonic": 70, "Brightness": 75, "Damping": 25, "Damp Tilt": 80,
        "Attack": 2, "Decay": 50, "Sustain": 0, "Release": 50,
        "Vel Sens": 95
    },
    "Mallet - Marimba": {
        "Inharmonic": 30, "Damping": 60, "Damp Tilt": 100,
        "Attack": 2, "Decay": 30, "Sustain": 0, "Release": 20,
        "Vel Sens": 110
    },
    "Pluck - Harp": {
        "Brightness": 75, "Damping": 50, "Damp Tilt": 90,
        "Attack": 1, "Decay": 60, "Sustain": 0, "Release": 25,
        "Vel Sens": 95
    },
    "Pluck - Pizzicato": {
        "Partials": 24, "Brightness": 60, "Damping": 80, "Damp Tilt": 110,
        "Attack": 1, "Decay": 30, "Sustain": 0, "Release": 15,
        "Vel Sens": 110
    },
    "Bass - Sub": {
        "Partials": 12, "Brightness": 30, "Damping": 50, "Damp Tilt": 90,
        "Attack": 2, "Decay": 50, "Sustain": 0, "Release": 25,
        "Vel Sens": 60
    },

    # ── FX / extremes ──────────────────────────────────────────────────
    "FX - Wobbling Glass": {
        "Inharmonic": 40, "Brightness": 80, "Drift": 127, "Phase": 70,
        "Attack": 100, "Release": 110,
        "Vel Sens": 70
    },
    "FX - Slow Drone": {
        "Partials": 20, "Brightness": 25, "Drift": 90, "Phase": 60,
        "Attack": 120, "Release": 127,
        "Vel Sens": 30
    },

    # ── v0.6 — LFO showcase ────────────────────────────────────────────
    "Lead - Vibrato Sax": {
        # Sustained harmonic lead with a classic sine vibrato on pitch.
        "Partials": 32, "Inharmonic": 5, "Brightness": 75, "Drift": 10,
        "Phase": 30, "Attack": 5, "Decay": 30, "Sustain": 100, "Release": 30,
        "Glide": 20,
        "LFO Speed": 65,       # ~5 Hz
        "Mod Pitch": 72,      # subtle positive vibrato,
        "Vel Sens": 90
    },
    "Pad - Pulsing Glass": {
        # Glass pad with a slow LFO sweep on brightness + inharm — a single
        # held chord breathes between dark/harmonic and bright/inharmonic.
        "Inharmonic": 50, "Brightness": 90, "Drift": 20, "Phase": 100,
        "Attack": 60, "Release": 80,
        "LFO Speed": 35,       # slow ~1 Hz
        "Mod Bright": 88,
        "Mod Inharm": 72,
        "Vel Sens": 40
    },
    "Pad - Ensemble Shimmer": {
        # String Ensemble + LFO with max Sync. Each voice gets a fully random
        # LFO phase at NoteOn → chord voices modulate independently → shimmer.
        # The poly-LFO-with-random-offset trick from invFFT §21.2.
        "Drift": 100, "Phase": 75,
        "Attack": 70, "Release": 90,
        "LFO Speed": 30,       # slow ~0.7 Hz
        "LFO Sync": 127,       # MAX random per voice
        "Mod Pitch": 68,      # tiny per-voice detune drift
        "Mod Bright": 75,     # per-voice brightness shimmer,
        "Vel Sens": 50
    },
    "FX - SH Glitch": {
        # Sample & Hold LFO routing to pitch + inharm produces stepped random
        # pitch jumps with inharmonic spectrum shifts — bleepy, glitchy texture.
        "Partials": 40, "Inharmonic": 35, "Brightness": 75, "Damping": 20,
        "Damp Tilt": 60, "Attack": 5, "Decay": 80, "Sustain": 50, "Release": 40,
        "LFO Speed": 85,
        "LFO Wave": 3,         # S&H
        "LFO Sync": 70,        # some per-voice randomness
        "Mod Pitch": 92,      # big random pitch jumps
        "Mod Inharm": 80,     # spectrum shifts on each step,
        "Vel Sens": 90
    },

    # ── v0.7 — Formant showcase ─────────────────────────────────────────
    "Pad - Vowel Ahh": {
        # Choir base + formant tuned to the "ah" F1 (~730 Hz) for a vocal
        # coloration on top of the drifty ensemble.
        "Brightness": 55, "Drift": 110, "Phase": 90,
        "Attack": 80, "Release": 100,
        "Formant Cutoff": 57,    # ~730 Hz
        "Formant Q":      50,    # ~5.5 — moderate sharpness
        "Formant Amount": 80,    # ~2.5x peak — prominent vowel,
        "Vel Sens": 50
    },
    "Pad - Vowel Ee": {
        # Same base but formant on the "ee" F2 (~2290 Hz) for the bright
        # vocal character that "ee" has.
        "Brightness": 65, "Drift": 110, "Phase": 90,
        "Attack": 80, "Release": 100,
        "Formant Cutoff": 80,    # ~2290 Hz
        "Formant Q":      70,    # ~9 — sharper, more defined
        "Formant Amount": 90,    # ~2.8x — very prominent,
        "Vel Sens": 50
    },
    "Pluck - Vox": {
        # Plucked harp + "oh"-ish formant ≈ a percussive vocal "doh".
        "Brightness": 75, "Damping": 50, "Damp Tilt": 90,
        "Attack": 1, "Decay": 60, "Sustain": 0, "Release": 25,
        "Formant Cutoff": 50,    # ~570 Hz (oh-ish)
        "Formant Q":      40,    # ~3.3 — moderate
        "Formant Amount": 70,    # ~2.2x,
        "Vel Sens": 100
    },

    # ── v0.8 — Bass / Lead / Mallet / Pad / FX expansion ─────────────────
    "Bass - Reese": {
        # Detuned drift bass, sustained with slow LFO brightness wobble.
        # Slight damping gives a bit of decay shape without full sustain.
        # LFO Sync max → per-voice independent modulation so chords shimmer.
        "Partials": 24, "Inharmonic": 5, "Brightness": 60,
        "Damping": 15, "Drift": 60, "Phase": 30,
        "Attack": 8, "Decay": 60, "Sustain": 100, "Release": 35,
        "LFO Speed": 25, "LFO Sync": 100,
        "Mod Bright": 78,
        "Vel Sens": 100,
    },
    "Bass - Slap": {
        # Slap bass: sharp in-phase attack (Phase=0) gives the thwack, then
        # the body rings briefly. Damping is lighter than a normal pluck so
        # the body has time to speak; Damp Tilt is steep so the upper partials
        # snap off fast, leaving the woody mid-low core. Formant tuned to
        # ~290 Hz adds the vocal "ow" coloration of a thumb slap.
        "Partials": 32, "Brightness": 78,
        "Damping": 35, "Damp Tilt": 95,
        "Phase": 0,
        "Attack": 1, "Decay": 65, "Sustain": 0, "Release": 30,
        "Formant Cutoff": 28, "Formant Q": 45, "Formant Amount": 75,
        "Vel Sens": 110,
    },
    "Bass - Sine Sub": {
        # Clean low harmonic with very few partials and a dark slope —
        # essentially sine + a couple partials. Sub sits low and steady.
        "Partials": 4, "Brightness": 5,
        "Drift": 5, "Phase": 50,
        "Attack": 2, "Decay": 30, "Sustain": 110, "Release": 25,
        "Vel Sens": 40,
    },
    "Lead - Acid Squelch": {
        # Damped pluck-y lead with fast LFO wah on brightness.
        # The Damping + Sustain combo gives that bouncy ducking character.
        "Partials": 32, "Inharmonic": 20, "Brightness": 65,
        "Damping": 45, "Damp Tilt": 80,
        "Attack": 2, "Decay": 35, "Sustain": 60, "Release": 25,
        "LFO Speed": 70,
        "Mod Bright": 90,
        "Vel Sens": 110,
    },
    "Lead - Whistle": {
        # Few partials with a very flat slope (high Brightness) — the
        # upper partials are nearly as loud as the fundamental, giving
        # an airy whistle tone. Subtle vibrato.
        "Partials": 12, "Brightness": 95,
        "Drift": 8, "Phase": 40,
        "Attack": 30, "Decay": 40, "Sustain": 110, "Release": 50,
        "LFO Speed": 60,
        "Mod Pitch": 70,
        "Vel Sens": 70,
    },
    "Pluck - Kalimba": {
        # African thumb piano: mild inharmonicity, very fast tilt so the
        # upper partials disappear within a few ms leaving the body tone.
        "Partials": 32, "Inharmonic": 50, "Brightness": 70,
        "Damping": 70, "Damp Tilt": 115,
        "Attack": 1, "Decay": 25, "Sustain": 0, "Release": 20,
        "Vel Sens": 100,
    },
    "Pluck - Vibraphone": {
        # Sustaining damped mallet with the classic vibe-motor tremolo
        # (LFO sine on brightness rather than volume — close enough).
        "Partials": 40, "Inharmonic": 40, "Brightness": 65,
        "Damping": 35, "Damp Tilt": 85,
        "Attack": 1, "Decay": 50, "Sustain": 0, "Release": 40,
        "LFO Speed": 75,
        "Mod Bright": 85,
        "Vel Sens": 100,
    },
    "Pluck - Steel Drum": {
        # High inharmonicity gives the metallic ringing character;
        # moderate damping with even tilt so partials decay together.
        "Partials": 36, "Inharmonic": 80, "Brightness": 75,
        "Damping": 40, "Damp Tilt": 70,
        "Attack": 1, "Decay": 60, "Sustain": 0, "Release": 30,
        "Vel Sens": 100,
    },
    "Pad - Cathedral": {
        # Vast slow pad — many partials, lots of drift, very smooth onset,
        # near-maximum attack and release. Vel Sens low so it stays even.
        "Partials": 56, "Inharmonic": 8, "Brightness": 50,
        "Drift": 80, "Phase": 110,
        "Attack": 110, "Decay": 80, "Sustain": 127, "Release": 127,
        "Vel Sens": 30,
    },
    "FX - Wobble": {
        # The dubstep wobble: triangle LFO heavy on brightness in the
        # mid-low register. Sustained so the wobble carries through.
        "Partials": 28, "Inharmonic": 10, "Brightness": 50,
        "Drift": 20, "Phase": 20,
        "Attack": 5, "Decay": 50, "Sustain": 110, "Release": 40,
        "LFO Speed": 80, "LFO Wave": 1,
        "Mod Bright": 110,
        "Vel Sens": 80,
    },
}


def resolved(overrides):
    return [
        (name, idx, overrides.get(name, DEFAULTS[name]))
        for name, idx in sorted(PARAM_INDEX.items(), key=lambda kv: kv[1])
    ]


def xml_escape(s):
    """Escape XML special chars for safe use in attribute values.

    Order matters — & must be replaced first, otherwise subsequent
    replacements introduce more & chars and double-escape them.
    """
    return (str(s)
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace('"', "&quot;"))


def emit_preset(key, overrides):
    rows = resolved(overrides)
    lines = [
        f'  <Item Key="{xml_escape(key)}">',
        f'    <Preset Machine="{xml_escape(MACHINE_NAME)}">',
        '      <Parameters>',
    ]
    for name, idx, val in rows:
        lines.append(
            f'        <Parameter Name="{xml_escape(name)}" Group="1" '
            f'Index="{idx}" Track="0" Value="{val}" />'
        )
    lines += [
        '      </Parameters>',
        '      <Attributes />',
        '      <Comment></Comment>',
        '    </Preset>',
        '  </Item>',
    ]
    return "\n".join(lines)


def build():
    body = "\n".join(emit_preset(k, v) for k, v in PRESETS.items())
    doc = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<PresetDictionary>\n'
        f'{body}\n'
        '</PresetDictionary>\n'
    )
    out_path = Path(__file__).parent / OUT_FILE
    with open(out_path, "w", encoding="utf-8-sig") as f:
        f.write(doc)
    print(f"Wrote {out_path} — {len(PRESETS)} presets")


if __name__ == "__main__":
    build()
