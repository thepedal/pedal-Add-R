#!/usr/bin/env python3
"""
Pedal Add-R preset bundle generator (Build §3.4).
Outputs `Pedal Add-R_Presets.prs.xml` next to this script.

Conventions:
  • UTF-8 with BOM (Build §3.1)
  • <PresetDictionary> / <Item> / <Preset> / <Parameters> (Build §3.2)
  • Preset Machine="Pedal Add-R" must match MachineDecl.Name exactly
  • Parameter Index is *declaration order* — Build §3.3 append-only rule:
    NEVER reorder or insert; only append at the end. Existing preset
    indices must remain stable across versions.

Future additions (LFO, formant, …) get tacked onto PARAM_INDEX/DEFAULTS
at the bottom; existing presets continue to load without edits and just
take the new params' DefValues.
"""

from pathlib import Path

MACHINE_NAME = "Pedal Add-R"
OUT_FILE     = "Pedal Add-R_Presets.prs.xml"

# Declaration order from PedalAddR.cs (v0.4) — append-only.
PARAM_INDEX = {
    "Volume":     0,
    "Partials":   1,
    "Inharmonic": 2,
    "Brightness": 3,
    "Damping":    4,
    "Damp Tilt":  5,
    "Drift":      6,
    "Phase":      7,
    "Attack":     8,
    "Decay":      9,
    "Sustain":   10,
    "Release":   11,
    "Glide":     12,
}

# Machine DefValues — mirror PedalAddR.cs.
DEFAULTS = {
    "Volume":     100,
    "Partials":    48,
    "Inharmonic":   0,
    "Brightness":  70,
    "Damping":      0,
    "Damp Tilt":   80,
    "Drift":        0,
    "Phase":        0,
    "Attack":       4,
    "Decay":       60,
    "Sustain":    127,
    "Release":     40,
    "Glide":        0,
}

# Sparse per-preset overrides. Missing keys take DEFAULTS.
# Naming follows the invFFT §23.1 convention "<Category> - <Description>"
# so the right-click preset menu sorts categories together.
PRESETS = {
    # ── Sustained / additive (Damping = 0) ──────────────────────────────
    "Pad - Soft Organ": {
        "Brightness": 60, "Drift": 25, "Phase": 50,
        "Attack": 50, "Release": 60,
    },
    "Pad - Glass": {
        "Inharmonic": 50, "Brightness": 90, "Drift": 20, "Phase": 100,
        "Attack": 60, "Release": 80,
    },
    "Pad - String Ensemble": {
        "Drift": 100, "Phase": 75,
        "Attack": 70, "Release": 90,
    },
    "Pad - Choir Aaah": {
        "Brightness": 55, "Drift": 110, "Phase": 90,
        "Attack": 80, "Release": 100,
    },

    # ── Struck / modal (Damping > 0, Sustain = 0) ───────────────────────
    "Bell - Crystal": {
        "Inharmonic": 95, "Brightness": 80, "Damping": 40, "Damp Tilt": 70,
        "Attack": 2, "Decay": 40, "Sustain": 0, "Release": 30,
    },
    "Bell - Tubular": {
        "Inharmonic": 70, "Brightness": 75, "Damping": 25, "Damp Tilt": 80,
        "Attack": 2, "Decay": 50, "Sustain": 0, "Release": 50,
    },
    "Mallet - Marimba": {
        "Inharmonic": 30, "Damping": 60, "Damp Tilt": 100,
        "Attack": 2, "Decay": 30, "Sustain": 0, "Release": 20,
    },
    "Pluck - Harp": {
        "Brightness": 75, "Damping": 50, "Damp Tilt": 90,
        "Attack": 1, "Decay": 60, "Sustain": 0, "Release": 25,
    },
    "Pluck - Pizzicato": {
        "Partials": 24, "Brightness": 60, "Damping": 80, "Damp Tilt": 110,
        "Attack": 1, "Decay": 30, "Sustain": 0, "Release": 15,
    },
    "Bass - Sub": {
        "Partials": 12, "Brightness": 30, "Damping": 50, "Damp Tilt": 90,
        "Attack": 2, "Decay": 50, "Sustain": 0, "Release": 25,
    },

    # ── FX / extremes ──────────────────────────────────────────────────
    "FX - Wobbling Glass": {
        "Inharmonic": 40, "Brightness": 80, "Drift": 127, "Phase": 70,
        "Attack": 100, "Release": 110,
    },
    "FX - Slow Drone": {
        "Partials": 20, "Brightness": 25, "Drift": 90, "Phase": 60,
        "Attack": 120, "Release": 127,
    },
}


def resolved(overrides):
    """Full ordered (name, index, value) list for one preset."""
    return [
        (name, idx, overrides.get(name, DEFAULTS[name]))
        for name, idx in sorted(PARAM_INDEX.items(), key=lambda kv: kv[1])
    ]


def emit_preset(key, overrides):
    rows = resolved(overrides)
    lines = [
        f'  <Item Key="{key}">',
        f'    <Preset Machine="{MACHINE_NAME}">',
        '      <Parameters>',
    ]
    for name, idx, val in rows:
        lines.append(
            f'        <Parameter Name="{name}" Group="1" '
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
