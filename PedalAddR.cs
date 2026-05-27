using System;
using System.Collections.Concurrent;
using System.Reflection;
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl, Note, Sample, WorkModes
using BuzzGUI.Interfaces;      // IParameter, ParameterType, IBuzz (.Graph.Buzz.Playing)

namespace PedalAddR
{
    // Pedal Add-R v0.3 — 8-voice polyphonic time-domain additive synth.
    // Single mixed output; chord polyphony from notes on multiple tracks at the
    // same row (track index = voice index, M1 §1). Generator: bool Work(...).
    [MachineDecl(
        Name        = "Pedal Add-R",
        ShortName   = "Add-R",
        Author      = "thepedal",
        MaxTracks   = MAX_VOICES,
        InputCount  = 0,
        OutputCount = 1)]
    public class PedalAddRMachine : IBuzzMachine
    {
        public const int MAX_VOICES = 8;        // = MaxTracks; compile-time const for the attribute

        const float MinTimeSec  = 0.001f;
        const float MixHeadroom = 0.4f;         // per-mix scaling so chords don't slam the clip (M1 §10)

        readonly IBuzzMachineHost _host;
        readonly Voice[] _voices = new Voice[MAX_VOICES];
        float[] _voiceBuf = new float[256];
        float[] _mix      = new float[256];
        float _lastSr;
        bool  _wasPlaying;

        // §14/§42 multi-track note recovery (lazy reflection, shape-tolerant).
        IParameter    _ownNoteParam;
        Func<int,int> _ownNotePValues;

        public PedalAddRMachine(IBuzzMachineHost host)
        {
            _host = host;
            // Distinct, well-spread seeds so each voice's drift RNG is independent.
            for (int i = 0; i < MAX_VOICES; i++)
                _voices[i] = new Voice(unchecked((int)((i + 1) * 0x9E3779B1L)));
        }

        // ── Global parameters ────────────────────────────────────────────────
        [ParameterDecl(Name = "Volume", MinValue = 0, MaxValue = 127, DefValue = 100)]
        public int Volume { get; set; } = 100;

        [ParameterDecl(Name = "Partials", MinValue = 1, MaxValue = 64, DefValue = 48,
            Description = "Number of partials in the bank")]
        public int Partials { get; set; } = 48;

        [ParameterDecl(Name = "Inharmonic", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "0 = pure harmonic (organ/pad), up = stretched/bell partials")]
        public int Inharmonic { get; set; } = 0;

        [ParameterDecl(Name = "Brightness", MinValue = 0, MaxValue = 127, DefValue = 70,
            Description = "Spectral tilt — low = dark (steep), high = bright. Latches at note-on")]
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

        // ── Note (track parameter) ───────────────────────────────────────────
        [ParameterDecl(Name = "Note", IsStateless = true,
            Description = "z=C-4, s=C#-4 …")]
        public void SetNote(Note value, int track)
        {
            if ((uint)track >= MAX_VOICES) return;     // external writes can exceed MaxTracks (invFFT §24)
            SetVoicePending(track, value.Value);

            // Recover sibling tracks of a chord row — ReBuzz delivers only the
            // last track's SetNote; the rest survive in pvalues until the
            // post-tick reset. Shape-tolerant reader handles both layouts
            // (Core §42): ConcurrentDictionary ≤1826, int[256] 1827+.
            if (_ownNotePValues == null) TryInitPValues();
            if (_ownNotePValues != null)
            {
                int noVal = _ownNoteParam.NoValue;     // 0 for Note type
                for (int t = 0; t < MAX_VOICES; t++)
                {
                    if (t == track) continue;
                    var v = _voices[t];
                    if (v.HasNoteOn || v.HasNoteOff) continue;   // a real setter call wins
                    int pv = _ownNotePValues(t);
                    if (pv != noVal && pv != 0) SetVoicePending(t, (byte)pv);
                }
            }
        }

        void SetVoicePending(int track, byte b)
        {
            var v = _voices[track];
            if (b == 0) return;                         // no event this tick
            if (b == 255) { v.HasNoteOff = true; v.HasNoteOn = false; }
            else          { v.HasNoteOn = true; v.HasNoteOff = false; v.PendingBuzzNote = b; }
        }

        void TryInitPValues()
        {
            try
            {
                if (_ownNoteParam == null)
                {
                    var pg = _host?.Machine?.ParameterGroups;   // populated after the ctor (Core §15)
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
            catch { /* recovery is best-effort; never throw from a setter */ }
        }

        // track -> raw pvalue (or NoValue). Tolerates both backing shapes (Core §42).
        static Func<int,int> GetPValuesReader(IParameter p)
        {
            int noVal = p.NoValue;
            var fi = p.GetType().GetField("pvalues",
                BindingFlags.NonPublic | BindingFlags.Instance);
            object raw = fi?.GetValue(p);
            if (raw is ConcurrentDictionary<int,int> dict)            // ≤1826
                return t => dict.TryGetValue(t, out int v) ? v : noVal;
            if (raw is int[] arr)                                     // 1827+
                return t => (uint)t < (uint)arr.Length ? arr[t] : noVal;
            return _ => noVal;                                        // unknown → recovery off, machine still runs
        }

        static float TimeSec(int p) => MinTimeSec * MathF.Pow(2f, (p / 127f) * 13f);

        // ── Audio ────────────────────────────────────────────────────────────
        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            float sr = _host?.MasterInfo?.SamplesPerSec ?? 44100f;
            if (MathF.Abs(sr - _lastSr) > 0.5f)
            {
                for (int t = 0; t < MAX_VOICES; t++) _voices[t].SetSampleRate(sr);   // Core §29
                _lastSr = sr;
            }

            // Map params and push to every voice before draining notes.
            float b        = Inharmonic / 127f;  b = b * b * 0.04f;             // 0 .. 0.04
            float slope    = 2.0f - (Brightness / 127f) * 1.6f;                 // 2.0 (dark) .. 0.4 (bright)
            float dGlobal  = Damping / 127f;     dGlobal = dGlobal * dGlobal * 60f;   // 0 .. 60 /s
            float dampTilt = (DampTilt / 127f) * 2f;                            // 0 .. 2
            float driftN   = Drift / 127f;       float driftDepth = driftN * driftN * 0.04f;   // 0 .. ±~70 cents peak
            float glideSec = Glide == 0 ? 0f : MinTimeSec * MathF.Pow(2f, (Glide / 127f) * 11f);
            float glideCoef = glideSec <= 0f ? 1f : 1f - MathF.Exp(-32f / (glideSec * sr));
            float aSec = TimeSec(Attack), dSec = TimeSec(Decay), rSec = TimeSec(Release);
            float sustainN = Sustain / 127f;

            for (int t = 0; t < MAX_VOICES; t++)
                _voices[t].SetParams(Partials, b, slope, dGlobal, dampTilt, driftDepth, glideCoef,
                                     aSec, dSec, sustainN, rSec);

            // Transport-stop fast-fade on the falling edge of Playing (Core §27).
            bool nowPlaying = _wasPlaying;
            try { nowPlaying = _host?.Machine?.Graph?.Buzz?.Playing ?? false; }
            catch { /* keep previous on a poll glitch — never break audio */ }
            if (_wasPlaying && !nowPlaying)
                for (int t = 0; t < MAX_VOICES; t++) _voices[t].ForceFade(sr);
            _wasPlaying = nowPlaying;

            // Drain pending note events per voice.
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
                float s = DspMath.SoftClip(_mix[i] * scale) * 32768f;   // PedalComp §1 sample scale
                output[i] = new Sample(s, s);
            }
            return true;
        }
    }
}
