using System;
using System.Collections.Concurrent;
using System.Reflection;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace PedalAddR
{
    // Pedal Add-R v0.6 — 8-voice polyphonic time-domain additive synth with LFO.
    [MachineDecl(
        Name        = "Pedal Add-R",
        ShortName   = "Add-R",
        Author      = "thepedal",
        MaxTracks   = MAX_VOICES,
        InputCount  = 0,
        OutputCount = 1)]
    public class PedalAddRMachine : IBuzzMachine
    {
        // ── Machine metadata (read by the About banner in PedalAddRGui.cs) ─
        public const string Version = "1.2";

        public const int MAX_VOICES = 8;

        const float MinTimeSec  = 0.001f;
        const float MixHeadroom = 0.7f;         // post-mix scaling. 0.4 (the M1 §10 chord-safety
                                                // figure carried from v0.2) was over-conservative
                                                // — chord summing is already protected by the soft
                                                // clip below. 0.7 puts single-voice peak at V=127
                                                // ≈ -3 dB, V=100 ≈ -5 dB. Bumped in v0.8.

        // LFO destination scaling — chosen so max amount + LFO extremes give
        // a musically dramatic but not unstable swing.
        const float LfoMaxPitchSemi = 6f;       // ±6 semitones (half octave) at full amount + LFO=±1
        const float LfoMaxSlopeDelta = 1.0f;    // ±1.0 slope delta (slope base is 0.4..2.0)
        const float LfoMaxBDelta     = 0.04f;   // ±0.04 (B base is 0..0.04)
        const float LfoMaxDriftDelta = 0.04f;   // ±0.04 (drift base is 0..0.04)

        readonly IBuzzMachineHost _host;
        readonly Voice[] _voices = new Voice[MAX_VOICES];
        float[] _voiceBuf = new float[256];
        float[] _mix      = new float[256];
        float _lastSr;
        bool  _wasPlaying;

        // Formant SVF state (machine-global; applied to the mix post-accumulation,
        // pre-Volume-scale, pre-soft-clip). TPT topology — Cytomic / Simper.
        float _svfZ1, _svfZ2;

        // Per-track latched velocity (v0.8). Updated by SetVelocity, read at
        // NoteOn time. IsStateless on SetVelocity means empty pattern rows
        // don't fire the setter, so the latched value persists between
        // explicit changes — the classic tracker Volume-column behaviour.
        readonly int[] _trackVelocity = new int[MAX_VOICES];

        IParameter    _ownNoteParam;
        Func<int,int> _ownNotePValues;

        public PedalAddRMachine(IBuzzMachineHost host)
        {
            _host = host;
            for (int i = 0; i < MAX_VOICES; i++)
                _voices[i] = new Voice(unchecked((int)((i + 1) * 0x9E3779B1L)));
            Array.Fill(_trackVelocity, 127);    // default latched velocity = full
        }

        // ── Globals ─────────────────────────────────────────────────────────
        [ParameterDecl(Name = "Volume", MinValue = 0, MaxValue = 127, DefValue = 100)]
        public int Volume { get; set; } = 100;

        [ParameterDecl(Name = "Partials", MinValue = 1, MaxValue = 64, DefValue = 48,
            Description = "Number of partials in the bank")]
        public int Partials { get; set; } = 48;

        [ParameterDecl(Name = "Inharmonic", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "0 = pure harmonic (organ/pad), up = stretched/bell partials")]
        public int Inharmonic { get; set; } = 0;

        [ParameterDecl(Name = "Brightness", MinValue = 0, MaxValue = 127, DefValue = 70,
            Description = "Spectral tilt — low = dark (steep), high = bright. Live (loudness-neutral)")]
        public int Brightness { get; set; } = 70;

        [ParameterDecl(Name = "Damping", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "0 = sustain, up = struck/plucked ringdown (the additive↔modal morph)")]
        public int Damping { get; set; } = 0;

        [ParameterDecl(Name = "Damp Tilt", MinValue = 0, MaxValue = 127, DefValue = 80,
            Description = "How much faster high partials decay than low ones")]
        public int DampTilt { get; set; } = 80;

        [ParameterDecl(Name = "Drift", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Per-partial slow random detune — ensemble/chorus from within")]
        public int Drift { get; set; } = 0;

        [ParameterDecl(Name = "Phase", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Inter-partial phase spread at note-on — 0 = sharp strike, up = smeared smooth onset")]
        public int Phase { get; set; } = 0;

        [ParameterDecl(Name = "Attack", MinValue = 0, MaxValue = 127, DefValue = 4)]
        public int Attack { get; set; } = 4;

        [ParameterDecl(Name = "Decay", MinValue = 0, MaxValue = 127, DefValue = 60)]
        public int Decay { get; set; } = 60;

        [ParameterDecl(Name = "Sustain", MinValue = 0, MaxValue = 127, DefValue = 127)]
        public int Sustain { get; set; } = 127;

        [ParameterDecl(Name = "Release", MinValue = 0, MaxValue = 127, DefValue = 40)]
        public int Release { get; set; } = 40;

        [ParameterDecl(Name = "Glide", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "0 = instant, up = portamento time")]
        public int Glide { get; set; } = 0;

        // ── New in v0.6 — LFO appended at end so v0.5 preset indices stay valid ──

        [ParameterDecl(Name = "LFO Speed", MinValue = 0, MaxValue = 127, DefValue = 30,
            Description = "LFO rate, log-mapped ~0.02 Hz to ~20 Hz")]
        public int LfoSpeed { get; set; } = 30;

        [ParameterDecl(Name = "LFO Wave", MinValue = 0, MaxValue = 3, DefValue = 0,
            ValueDescriptions = new[] { "Sine", "Triangle", "Square", "Sample & Hold" },
            Description = "0 = Sine, 1 = Triangle, 2 = Square, 3 = Sample & Hold")]
        public int LfoWave { get; set; } = 0;

        [ParameterDecl(Name = "LFO Sync", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Per-voice LFO phase randomness at NoteOn — 0 = lockstep, max = chord shimmer")]
        public int LfoSync { get; set; } = 0;

        [ParameterDecl(Name = "Mod Pitch", MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "LFO modulation depth for pitch (vibrato). 64 = off, ±63 = ±6 semitones")]
        public int LfoToPitch { get; set; } = 64;

        [ParameterDecl(Name = "Mod Bright", MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "LFO modulation depth for brightness (wah). 64 = off")]
        public int LfoToBright { get; set; } = 64;

        [ParameterDecl(Name = "Mod Inharm", MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "LFO modulation depth for inharmonicity. 64 = off")]
        public int LfoToInharm { get; set; } = 64;

        [ParameterDecl(Name = "Mod Drift", MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "LFO modulation depth for drift depth. 64 = off")]
        public int LfoToDrift { get; set; } = 64;

        // ── New in v0.7 — Formant appended at end so v0.6 preset indices stay valid ──

        [ParameterDecl(Name = "Formant Cutoff", MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "Formant center frequency, log-mapped ~100 Hz to ~8 kHz")]
        public int FormantCutoff { get; set; } = 64;

        [ParameterDecl(Name = "Formant Q", MinValue = 0, MaxValue = 127, DefValue = 30,
            Description = "Formant resonance / sharpness, 0.5 (wide) to 30 (razor-sharp)")]
        public int FormantQ { get; set; } = 30;

        [ParameterDecl(Name = "Formant Amount", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Formant peak prominence over flat. 0 = off (pass-through), full = ~14 dB boost")]
        public int FormantAmount { get; set; } = 0;

        // ── New in v0.8 — Velocity sensitivity appended at end of globals ──

        [ParameterDecl(Name = "Vel Sens", MinValue = 0, MaxValue = 127, DefValue = 80,
            Description = "How much velocity affects amplitude. 0 = uniform loudness (velocity ignored), 127 = full velocity scaling")]
        public int VelSens { get; set; } = 80;

        // ── New in v1.2 — LFO Mode, appended at the end so v1.1.x preset indices stay valid ──

        [ParameterDecl(Name = "LFO Mode", MinValue = 0, MaxValue = 3, DefValue = 0,
            ValueDescriptions = new[] { "Free (Hz)", "Free (ms)", "Free (samples)", "Tempo-sync (ticks)" },
            Description = "LFO Speed interpretation. Free modes are sample-rate-locked at the Hz set by LFO Speed (display only differs). Tempo-sync mode locks the LFO rate to a musical tick division derived from the host tempo.")]
        public int LfoMode { get; set; } = 0;

        // ── Track parameters ────────────────────────────────────────────────
        [ParameterDecl(Name = "Note", IsStateless = true,
            Description = "z=C-4, s=C#-4 …")]
        public void SetNote(Note value, int track)
        {
            if ((uint)track >= MAX_VOICES) return;
            SetVoicePending(track, value.Value);

            if (_ownNotePValues == null) TryInitPValues();
            if (_ownNotePValues != null)
            {
                int noVal = _ownNoteParam.NoValue;
                for (int t = 0; t < MAX_VOICES; t++)
                {
                    if (t == track) continue;
                    var v = _voices[t];
                    if (v.HasNoteOn || v.HasNoteOff) continue;
                    int pv = _ownNotePValues(t);
                    if (pv != noVal && pv != 0) SetVoicePending(t, (byte)pv);
                }
            }
        }

        void SetVoicePending(int track, byte b)
        {
            var v = _voices[track];
            if (b == 0) return;
            if (b == 255) { v.HasNoteOff = true; v.HasNoteOn = false; }
            else          { v.HasNoteOn = true; v.HasNoteOff = false; v.PendingBuzzNote = b; }
        }

        // Velocity track parameter (v0.8). IsStateless = true → empty pattern
        // rows don't fire this setter, so _trackVelocity[t] persists between
        // explicit changes. Multi-track velocity at the same row is subject
        // to the Core §42 pvalues collapse (only the last setter call fires);
        // not recovered here in v0.8 — same-row multi-velocity is uncommon
        // and the workaround (set on the row before the chord) is fine.
        [ParameterDecl(Name = "Velocity", MinValue = 0, MaxValue = 127, DefValue = 127, IsStateless = true,
            Description = "Per-note velocity. Latches per track until next change. Scale via Vel Sens.")]
        public void SetVelocity(int value, int track)
        {
            if ((uint)track >= MAX_VOICES) return;
            _trackVelocity[track] = value;
        }

        void TryInitPValues()
        {
            try
            {
                if (_ownNoteParam == null)
                {
                    var pg = _host?.Machine?.ParameterGroups;
                    if (pg == null) return;
                    foreach (var g in pg)
                    {
                        if (g?.Parameters == null) continue;
                        foreach (var p in g.Parameters)
                            if (p != null && p.Type == ParameterType.Note) { _ownNoteParam = p; break; }
                        if (_ownNoteParam != null) break;
                    }
                }
                if (_ownNoteParam != null && _ownNotePValues == null)
                    _ownNotePValues = GetPValuesReader(_ownNoteParam);
            }
            catch { }
        }

        static Func<int,int> GetPValuesReader(IParameter p)
        {
            int noVal = p.NoValue;
            var fi = p.GetType().GetField("pvalues",
                BindingFlags.NonPublic | BindingFlags.Instance);
            object raw = fi?.GetValue(p);
            if (raw is ConcurrentDictionary<int,int> dict)
                return t => dict.TryGetValue(t, out int v) ? v : noVal;
            if (raw is int[] arr)
                return t => (uint)t < (uint)arr.Length ? arr[t] : noVal;
            return _ => noVal;
        }

        static float TimeSec(int p) => MinTimeSec * MathF.Pow(2f, (p / 127f) * 13f);

        // LFO speed: log-mapped 0.02 Hz to 20 Hz across 0..127.
        static float LfoSpeedHz(int p) => 0.02f * MathF.Pow(1000f, p / 127f);

        // ── LFO tempo-sync tick divisions ──────────────────────────────────
        // In Tempo-sync mode the 0..127 LFO Speed slider quantises to one of
        // 16 musical divisions (8 slider values per division). Sorted slowest
        // to fastest so increasing the slider speeds the LFO up, matching the
        // Free-mode direction. At TPB=4 (default), 16 ticks = 1 bar, 4 ticks
        // = 1 beat, 1 tick = 1/16 note.
        static readonly int[] TickDivisions = {
            256, 192, 128, 96, 64, 48, 32, 24, 16, 12, 8, 6, 4, 3, 2, 1
        };
        static int TicksPerCycle(int lfoSpeed)
        {
            int idx = (lfoSpeed * TickDivisions.Length) / 128;
            if (idx >= TickDivisions.Length) idx = TickDivisions.Length - 1;
            return TickDivisions[idx];
        }

        // ── DescribeValue (Tracker §7.4) ─────────────────────────────────────
        // ReBuzz calls this to populate the second status-bar panel when the
        // user hovers a parameter. Returning null falls back to the default
        // integer or ValueDescriptions display. We override for parameters
        // whose 0..127 raw value hides a natural ground-truth unit (time,
        // Hz, Q, semitones, multiplier). Parameters with no clean external
        // unit (Brightness slope, Damping coefficient, Mod Bright/Inharm/
        // Drift internal deltas, LFO Sync randomness amount, Inharmonic B)
        // are left at their byte values — they're feel knobs.
        public string DescribeValue(IParameter p, int value)
        {
            switch (p?.Name)
            {
                case "LFO Speed":
                {
                    if (LfoMode == 3)   // Tempo-sync (ticks)
                        return $"{TicksPerCycle(value)} ticks";

                    float hz  = LfoSpeedHz(value);
                    float sec = 1f / hz;
                    switch (LfoMode)
                    {
                        case 1: // Free (ms)
                            return sec >= 1f ? $"{sec:0.00} s" : $"{sec * 1000f:0} ms";
                        case 2: // Free (samples)
                            float sr = _lastSr > 0 ? _lastSr : 44100f;
                            return $"{sec * sr:0} samples";
                        default: // Free (Hz) — the original v1.1.1 format
                            return $"{hz:0.00} Hz / {FormatTime(sec)}";
                    }
                }

                case "Attack":
                case "Decay":
                case "Release":
                    return FormatTime(TimeSec(value));

                case "Glide":
                    if (value == 0) return "instant";
                    // Glide has its own log mapping (×2^11, not the ADSR ×2^13).
                    return FormatTime(MinTimeSec * MathF.Pow(2f, (value / 127f) * 11f));

                case "Formant Cutoff":
                {
                    float hz = 100f * MathF.Pow(80f, value / 127f);
                    return hz >= 1000f ? $"{hz / 1000f:0.00} kHz" : $"{hz:0} Hz";
                }

                case "Formant Q":
                {
                    float q = 0.5f * MathF.Pow(60f, value / 127f);
                    return $"Q = {q:0.00}";
                }

                case "Formant Amount":
                    if (value == 0) return "off";
                    return $"{(value / 127f) * 4f:0.00}x";

                case "Mod Pitch":
                    if (value == 64) return "off";
                    float st = (value - 64) / 64f * 6f;
                    return $"{(st >= 0 ? "+" : "")}{st:0.00} st";
            }
            return null;
        }

        // Time-formatter shared by LFO Speed (Free ms/Hz modes) and the ADSR /
        // Glide cases above. ms below 1 second, seconds above.
        static string FormatTime(float sec)
            => sec >= 1f ? $"{sec:0.00} s" : $"{sec * 1000f:0} ms";

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            float sr = _host?.MasterInfo?.SamplesPerSec ?? 44100f;
            if (MathF.Abs(sr - _lastSr) > 0.5f)
            {
                for (int t = 0; t < MAX_VOICES; t++) _voices[t].SetSampleRate(sr);
                _lastSr = sr;
            }

            // Static param mappings.
            float b           = Inharmonic / 127f;  b = b * b * 0.04f;
            float slope       = 2.0f - (Brightness / 127f) * 1.6f;
            float dGlobal     = Damping / 127f;     dGlobal = dGlobal * dGlobal * 60f;
            float dampTilt    = (DampTilt / 127f) * 2f;
            float driftN      = Drift / 127f;       float driftDepth = driftN * driftN * 0.04f;
            float phaseSpread = Phase / 127f;
            float glideSec    = Glide == 0 ? 0f : MinTimeSec * MathF.Pow(2f, (Glide / 127f) * 11f);
            float glideCoef   = glideSec <= 0f ? 1f : 1f - MathF.Exp(-32f / (glideSec * sr));
            float aSec = TimeSec(Attack), dSec = TimeSec(Decay), rSec = TimeSec(Release);
            float sustainN = Sustain / 127f;

            // LFO mappings. Bipolar destinations: (val-64)/64 → -1..+0.984.
            // In tempo-sync mode (LfoMode=3), the slider maps to a tick
            // division and the effective Hz is derived from the host's
            // current SamplesPerTick — read fresh each Work per Core §29.5
            // so BPM changes during playback are picked up automatically.
            float lfoSpeedHz;
            if (LfoMode == 3)
            {
                int ticks = TicksPerCycle(LfoSpeed);
                int samplesPerTick = _host?.MasterInfo.SamplesPerTick ?? 5250;
                float samplesPerCycle = (float)ticks * samplesPerTick;
                lfoSpeedHz = samplesPerCycle > 0f ? sr / samplesPerCycle : LfoSpeedHz(LfoSpeed);
            }
            else
            {
                lfoSpeedHz = LfoSpeedHz(LfoSpeed);
            }
            int   lfoWave     = LfoWave;
            float lfoSync     = LfoSync / 127f;
            float lfoPitchAmt  = (LfoToPitch  - 64) / 64f * LfoMaxPitchSemi;
            float lfoBrightAmt = (LfoToBright - 64) / 64f * LfoMaxSlopeDelta;
            float lfoInharmAmt = (LfoToInharm - 64) / 64f * LfoMaxBDelta;
            float lfoDriftAmt  = (LfoToDrift  - 64) / 64f * LfoMaxDriftDelta;

            for (int t = 0; t < MAX_VOICES; t++)
                _voices[t].SetParams(Partials, b, slope, dGlobal, dampTilt, driftDepth, phaseSpread,
                                     glideCoef, aSec, dSec, sustainN, rSec,
                                     lfoSpeedHz, lfoWave, lfoSync,
                                     lfoPitchAmt, lfoBrightAmt, lfoInharmAmt, lfoDriftAmt);

            // Transport-stop fade (Core §27).
            bool nowPlaying = _wasPlaying;
            try { nowPlaying = _host?.Machine?.Graph?.Buzz?.Playing ?? false; }
            catch { }
            if (_wasPlaying && !nowPlaying)
                for (int t = 0; t < MAX_VOICES; t++) _voices[t].ForceFade(sr);
            _wasPlaying = nowPlaying;

            // Drain note events. Velocity applied here:
            //   rawVelN = latched per-track velocity (0..1)
            //   velSensN = global Vel Sens knob (0..1)
            //   effVel = 1 - velSensN * (1 - rawVelN)
            // → velSensN=0 yields effVel=1 (uniform loudness)
            // → velSensN=1 yields effVel=rawVelN (direct scaling)
            float velSensN = VelSens / 127f;
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];
                if (v.HasNoteOn)
                {
                    v.HasNoteOn = false; v.HasNoteOff = false;
                    float rawVelN = _trackVelocity[t] / 127f;
                    float effVel  = 1f - velSensN * (1f - rawVelN);
                    v.NoteOn(DspMath.BuzzNoteToMidi(v.PendingBuzzNote), !v.IsActive, effVel);
                }
                else if (v.HasNoteOff)
                {
                    v.HasNoteOff = false;
                    v.NoteOff();
                }
            }

            // Idle fast-path (Core §33).
            bool any = false;
            for (int t = 0; t < MAX_VOICES; t++) if (_voices[t].IsActive) { any = true; break; }
            if (!any)
            {
                // No active voice → mix is silent → SVF state would just freeze
                // at its last value. Reset to 0 so the next play resumes from a
                // clean filter.
                _svfZ1 = 0f;
                _svfZ2 = 0f;
                for (int i = 0; i < n; i++) output[i] = new Sample(0f, 0f);
                return false;
            }

            if (_voiceBuf.Length < n) { _voiceBuf = new float[n]; _mix = new float[n]; }
            Array.Clear(_mix, 0, n);
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];
                if (!v.IsActive) continue;
                v.Render(_voiceBuf, n);
                for (int i = 0; i < n; i++) _mix[i] += _voiceBuf[i];
            }

            // ── Formant SVF coefficients (TPT topology, Cytomic / Andrew Simper) ──
            // Computed per-buffer; the filter itself runs per-sample inside the
            // output loop. Cutoff is well below Nyquist by mapping construction.
            float fcHz = 100f * MathF.Pow(80f, FormantCutoff / 127f);
            float q    = 0.5f * MathF.Pow(60f, FormantQ      / 127f);
            float amt  = (FormantAmount / 127f) * 4f;
            float g    = MathF.Tan(MathF.PI * fcHz / sr);
            float k    = 1f / q;
            float a1   = 1f / (1f + g * (g + k));
            float a2   = g * a1;
            float a3   = g * a2;

            float scale = (Volume / 127f) * MixHeadroom;
            for (int i = 0; i < n; i++)
            {
                float dry = _mix[i];

                // Trapezoidal-prewarped state-variable filter. Two integrators,
                // unconditionally stable for any g, k > 0. We take the BP output
                // (= v1) and normalise by k = 1/Q so peak amplitude at resonance
                // is unit regardless of Q — Amount alone controls prominence.
                float v3 = dry - _svfZ2;
                float v1 = a1 * _svfZ1 + a2 * v3;
                float v2 = _svfZ2 + a2 * _svfZ1 + a3 * v3;
                _svfZ1 = 2f * v1 - _svfZ1;
                _svfZ2 = 2f * v2 - _svfZ2;

                float wet = dry + amt * v1 * k;

                float s = DspMath.SoftClip(wet * scale) * 32768f;
                output[i] = new Sample(s, s);
            }
            return true;
        }
    }
}
