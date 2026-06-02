using System;

namespace PedalAddR
{
    // ─────────────────────────────────────────────────────────────────────────
    // Pedal Add-R voice — a time-domain additive partial bank.
    //
    // Each partial is a complex phasor rotated one step per sample:
    //     z <- z * (cos w + j sin w)        out += amp * real(z)
    // the cheapest stable way to run many sinusoids without a per-sample Pow/Sin.
    //
    //   • Inharmonicity (B): f_n = f0 * n * sqrt(1 + B*n^2).
    //   • Damping (d) + tilt: per-partial exponential ringdown.
    //   • Drift: per-partial slow random detune (mean-reverting OU walk),
    //     per-voice RNG so chord voices drift independently (invFFT §21.2).
    //   • Phase spread: at NoteOn each partial gets a random starting phase
    //     scaled by the knob — sharp-strike ↔ smeared-onset.
    //   • Click-protect: 1 ms fade before phasor reseed on retriggers.
    //
    // LFO (v0.6). One per voice. Phase is per-voice so chord voices can
    // modulate independently (the `Sync` param scales how randomised the
    // start phase is at NoteOn — 0 = lockstep, max = fully random).
    // Advances at control rate (1.5 kHz, well above any audible LFO speed).
    // Routes to four destinations simultaneously with bipolar amounts
    // (M1 §14 pattern): pitch, brightness, inharmonicity, drift depth.
    // Negative amounts invert the modulation polarity.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class Voice
    {
        public const int MaxPartials = 64;
        const int CtrlBlock = 32;

        const float DriftStep   = 0.05f;
        const float DriftRevert = 0.008f;

        const float TrigTauSec  = 0.001f;
        const float TrigSeedThr = 0.005f;

        float _sr = 44100f;

        readonly float[] _re    = new float[MaxPartials];
        readonly float[] _im    = new float[MaxPartials];
        readonly float[] _cos   = new float[MaxPartials];
        readonly float[] _sin   = new float[MaxPartials];
        readonly float[] _amp   = new float[MaxPartials];
        readonly float[] _env   = new float[MaxPartials];
        readonly float[] _drift = new float[MaxPartials];
        int _active;

        readonly Random _rng;

        // Pitch / glide.
        float _currentMidi = 60f;
        float _targetMidi  = 60f;

        // Static params (per-block).
        int   _partials    = 48;
        float _b           = 0f;
        float _slope       = 1f;
        float _dGlobal     = 0f;
        float _dampTilt    = 1f;
        float _driftDepth  = 0f;
        float _phaseSpread = 0f;
        float _glideCoef   = 1f;

        // LFO state + config.
        float _lfoPhase    = 0f;       // [0, 1)
        float _shVal       = 0f;       // S&H current sample
        float _lfoSpeedHz  = 0.5f;
        int   _lfoWave     = 0;        // 0=Sin 1=Tri 2=Sqr 3=S&H
        float _lfoSync     = 0f;       // 0..1, NoteOn phase randomness
        float _lfoPitchAmt = 0f;       // signed semitones at LFO=±1
        float _lfoBrightAmt = 0f;      // signed slope delta
        float _lfoInharmAmt = 0f;      // signed B delta
        float _lfoDriftAmt  = 0f;      // signed drift-depth delta

        // Click-protect.
        float _trigGain       = 1f;
        float _trigGainTarget = 1f;
        float _trigCoef       = 1f;
        bool  _retrigPending;
        int   _pendingMidi;

        int _ctrl;
        readonly AdsrEnvelope _ampEnv = new AdsrEnvelope();

        public bool HasNoteOn;
        public bool HasNoteOff;
        public byte PendingBuzzNote;

        public Voice(int seed) { _rng = new Random(seed); }

        public bool IsActive => _ampEnv.IsActive || _retrigPending;

        public void SetSampleRate(float sr)
        {
            _sr = sr;
            _trigCoef = 1f - MathF.Exp(-1f / (TrigTauSec * sr));
        }

        public void SetParams(int partials, float b, float slope,
                              float dGlobal, float dampTilt, float driftDepth,
                              float phaseSpread, float glideCoef,
                              float aSec, float dSec, float sustain, float rSec,
                              float lfoSpeedHz, int lfoWave, float lfoSync,
                              float lfoPitchAmt, float lfoBrightAmt,
                              float lfoInharmAmt, float lfoDriftAmt)
        {
            _partials    = Math.Clamp(partials, 1, MaxPartials);
            _b           = b;
            _slope       = slope;
            _dGlobal     = dGlobal;
            _dampTilt    = dampTilt;
            _driftDepth  = driftDepth;
            _phaseSpread = phaseSpread;
            _glideCoef   = glideCoef;
            _lfoSpeedHz  = lfoSpeedHz;
            _lfoWave     = lfoWave;
            _lfoSync     = lfoSync;
            _lfoPitchAmt  = lfoPitchAmt;
            _lfoBrightAmt = lfoBrightAmt;
            _lfoInharmAmt = lfoInharmAmt;
            _lfoDriftAmt  = lfoDriftAmt;
            _ampEnv.SetParams(aSec, dSec, sustain, rSec, _sr);
        }

        public void NoteOn(int midi, bool wasIdle)
        {
            if (wasIdle || _ampEnv.IsBelowAudible)
            {
                _trigGain       = 1f;
                _trigGainTarget = 1f;
                _retrigPending  = false;
                SeedBank(midi, snapPitch: wasIdle);
                SeedLfo();
                _ampEnv.NoteOn();
            }
            else
            {
                _retrigPending  = true;
                _pendingMidi    = midi;
                _trigGainTarget = 0f;
            }
        }

        public void NoteOff()
        {
            if (_retrigPending)
            {
                _retrigPending  = false;
                _trigGainTarget = 1f;
            }
            _ampEnv.NoteOff();
        }

        public void ForceFade(float sr)
        {
            _retrigPending  = false;
            _trigGainTarget = 1f;
            _ampEnv.ForcedRelease(sr);
        }

        void SeedBank(int midi, bool snapPitch)
        {
            _targetMidi = midi;
            if (snapPitch) _currentMidi = midi;

            if (_phaseSpread > 0f)
            {
                float spread = _phaseSpread * MathF.PI;
                for (int p = 0; p < MaxPartials; p++)
                {
                    _env[p] = 1f;
                    float phi = (float)(_rng.NextDouble() * 2.0 - 1.0) * spread;
                    _re[p] = MathF.Cos(phi);
                    _im[p] = MathF.Sin(phi);
                }
            }
            else
            {
                for (int p = 0; p < MaxPartials; p++)
                {
                    _env[p] = 1f;
                    _re[p]  = 1f; _im[p] = 0f;
                }
            }
            Array.Clear(_drift, 0, MaxPartials);

            _active = _partials;
            _ctrl   = 0;
        }

        void SeedLfo()
        {
            // Key-synced with random offset. Sync=0 → phase 0 (lockstep across
            // voices). Sync=1 → fully random per voice (chord shimmer).
            _lfoPhase = (float)_rng.NextDouble() * _lfoSync;
            _shVal    = (float)(_rng.NextDouble() * 2.0 - 1.0);
        }

        float LfoValue()
        {
            switch (_lfoWave)
            {
                case 0: return MathF.Sin(_lfoPhase * DspMath.TwoPi);
                case 1:                                                  // triangle (0→+1→0→-1→0)
                {
                    float p = _lfoPhase;
                    return p < 0.25f ? 4f * p
                         : p < 0.75f ? 2f - 4f * p
                                     : 4f * p - 4f;
                }
                case 2: return _lfoPhase < 0.5f ? 1f : -1f;              // square
                case 3: return _shVal;                                   // S&H
                default: return 0f;
            }
        }

        void ControlUpdate()
        {
            _currentMidi += (_targetMidi - _currentMidi) * _glideCoef;
            if (MathF.Abs(_targetMidi - _currentMidi) < 0.001f) _currentMidi = _targetMidi;

            // ── LFO advance ────────────────────────────────────────────────
            float prevPhase = _lfoPhase;
            _lfoPhase += _lfoSpeedHz * (CtrlBlock / _sr);
            if (_lfoPhase >= 1f) _lfoPhase -= 1f;
            // Sample a new S&H value on phase wrap (cheap; only matters for wave 3).
            if (_lfoPhase < prevPhase)
                _shVal = (float)(_rng.NextDouble() * 2.0 - 1.0);
            float lfo = LfoValue();

            // ── Apply LFO to working copies (don't mutate static field values) ─
            float effMidi       = _currentMidi + lfo * _lfoPitchAmt;
            float effSlope      = _slope       + lfo * _lfoBrightAmt;
            float effB          = MathF.Max(0f, _b          + lfo * _lfoInharmAmt);
            float effDriftDepth = MathF.Max(0f, _driftDepth + lfo * _lfoDriftAmt);
            bool  driftOn       = effDriftDepth > 0f
                                  || _driftDepth > 0f
                                  || MathF.Abs(_lfoDriftAmt) > 1e-6f;

            float f0     = 440f * DspMath.FastPow2((effMidi - 69f) / 12f);
            float nyq    = _sr * 0.5f;
            float wScale = DspMath.TwoPi / _sr;
            bool  damp   = _dGlobal > 0f;

            int   active  = _partials;
            float sumBase = 0f;
            for (int p = 0; p < _partials; p++)
            {
                int   nn    = p + 1;
                float ratio = nn * MathF.Sqrt(1f + effB * nn * nn);
                float freq  = f0 * ratio;

                if (driftOn)
                {
                    float w  = (float)(_rng.NextDouble() * 2.0 - 1.0);
                    float dr = _drift[p] + w * DriftStep - _drift[p] * DriftRevert;
                    if (dr > 2f) dr = 2f; else if (dr < -2f) dr = -2f;
                    _drift[p] = dr;
                    freq *= 1f + dr * effDriftDepth;
                }

                if (freq >= nyq) { active = p; break; }

                float wRot = wScale * freq;
                _cos[p] = MathF.Cos(wRot);
                _sin[p] = MathF.Sin(wRot);

                if (damp)
                {
                    float dN = _dGlobal * MathF.Pow(nn, _dampTilt);
                    _env[p] *= MathF.Exp(-dN * CtrlBlock / _sr);
                }

                float mag2 = _re[p] * _re[p] + _im[p] * _im[p];
                float k    = 1.5f - 0.5f * mag2;
                _re[p] *= k; _im[p] *= k;

                float ba = 1f / MathF.Pow(nn, effSlope);
                _amp[p]  = ba * _env[p];
                sumBase += ba;
            }
            _active = active;

            float totalAmp = 0f;
            if (sumBase > 0f)
            {
                float inv = 1f / sumBase;
                for (int p = 0; p < active; p++)
                {
                    _amp[p] *= inv;
                    totalAmp += _amp[p];
                }
            }
            if (totalAmp < 1e-5f && !_retrigPending) _ampEnv.ForceIdle();
        }

        public void Render(float[] outBuf, int n)
        {
            for (int i = 0; i < n; i++)
            {
                _trigGain += (_trigGainTarget - _trigGain) * _trigCoef;

                if (_retrigPending && _trigGain < TrigSeedThr)
                {
                    SeedBank(_pendingMidi, snapPitch: false);
                    SeedLfo();
                    _ampEnv.NoteOn();
                    _retrigPending  = false;
                    _trigGainTarget = 1f;
                }

                if (_ctrl <= 0) { ControlUpdate(); _ctrl = CtrlBlock; }
                _ctrl--;

                float s = 0f;
                for (int p = 0; p < _active; p++)
                {
                    float re = _re[p], im = _im[p];
                    float nr = re * _cos[p] - im * _sin[p];
                    float ni = re * _sin[p] + im * _cos[p];
                    _re[p] = nr; _im[p] = ni;
                    s += _amp[p] * nr;
                }
                outBuf[i] = s * _ampEnv.Process() * _trigGain;
            }
        }

        // ── Small per-sample ADSR ────────────────────────────────────────────
        sealed class AdsrEnvelope
        {
            enum St { Idle, Attack, Decay, Sustain, Release }
            St _st = St.Idle;
            float _level;
            float _attInc, _decCoef, _relCoef, _sustain;
            float _forcedCoef;
            bool  _forced;

            public bool IsActive       => _st != St.Idle;
            public bool IsBelowAudible => _level < 0.005f;

            public void SetParams(float aSec, float dSec, float sustain, float rSec, float sr)
            {
                _attInc  = aSec <= 0f ? 1f : 1f / (aSec * sr);
                _decCoef = dSec <= 0f ? 0f : MathF.Exp(-1f / (dSec * sr));
                _relCoef = rSec <= 0f ? 0f : MathF.Exp(-1f / (rSec * sr));
                _sustain = sustain;
            }

            public void NoteOn()    { _forced = false; _level = 0f; _st = St.Attack; }
            public void NoteOff()   { if (_st != St.Idle) _st = St.Release; }
            public void ForceIdle() { _st = St.Idle; _level = 0f; }

            public void ForcedRelease(float sr)
            {
                if (_st == St.Idle) return;
                _forcedCoef = MathF.Exp(-1f / (0.005f * sr));
                _forced = true;
                _st = St.Release;
            }

            public float Process()
            {
                switch (_st)
                {
                    case St.Attack:
                        _level += _attInc;
                        if (_level >= 1f) { _level = 1f; _st = St.Decay; }
                        break;
                    case St.Decay:
                        _level = _sustain + (_level - _sustain) * _decCoef;
                        if (_level <= _sustain + 0.0001f)
                        {
                            _level = _sustain;
                            _st = _sustain <= 0.0005f ? St.Idle : St.Sustain;
                        }
                        break;
                    case St.Sustain:
                        _level = _sustain;
                        break;
                    case St.Release:
                        _level *= _forced ? _forcedCoef : _relCoef;
                        if (_level <= 0.0001f) { _level = 0f; _st = St.Idle; }
                        break;
                    default:
                        return 0f;
                }
                return _level;
            }
        }
    }
}
