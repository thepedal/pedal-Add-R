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
        public const int MAX_VOICES = 8;

        const float MinTimeSec  = 0.001f;
        const float MixHeadroom = 0.4f;

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

        IParameter    _ownNoteParam;
        Func<int,int> _ownNotePValues;

        public PedalAddRMachine(IBuzzMachineHost host)
        {
            _host = host;
            for (int i = 0; i < MAX_VOICES; i++)
                _voices[i] = new Voice(unchecked((int)((i + 1) * 0x9E3779B1L)));
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

        // ── Note (track parameter) ──────────────────────────────────────────
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

            // LFO mappings. Bipolar destinations: (val-64)/64 → -1..+0.984
            float lfoSpeedHz  = LfoSpeedHz(LfoSpeed);
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

            // Drain note events.
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];
                if (v.HasNoteOn)
                {
                    v.HasNoteOn = false; v.HasNoteOff = false;
                    v.NoteOn(DspMath.BuzzNoteToMidi(v.PendingBuzzNote), !v.IsActive);
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

            float scale = (Volume / 127f) * MixHeadroom;
            for (int i = 0; i < n; i++)
            {
                float s = DspMath.SoftClip(_mix[i] * scale) * 32768f;
                output[i] = new Sample(s, s);
            }
            return true;
        }
    }
}
