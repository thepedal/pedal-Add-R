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
    //   • Inharmonicity (B): ratios bend from pure harmonics (organ/pad) toward
    //     stretched/bell ratios via f_n = f0 * n * sqrt(1 + B*n^2).
    //   • Damping (d) + tilt: per-partial exponential ringdown. d=0 = sustain
    //     (additive); d>0 = struck/plucked modes, highs decaying faster.
    //   • Drift: each partial's frequency takes an independent slow random
    //     wander — "ensemble from within". Per-voice RNG keeps chord voices
    //     drifting independently (invFFT §21.2). Opt-in: zero cost at depth 0.
    //
    // Phase rotation and amplitude decay are decoupled: the phasor is a near-unit
    // oscillator (renormalised to unit magnitude at control rate via one Newton
    // step), and a separate per-partial amplitude scalar carries the decay.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class Voice
    {
        public const int MaxPartials = 64;
        const int CtrlBlock = 32;          // control-rate update cadence (samples)

        // Drift is a per-partial mean-reverting (Ornstein-Uhlenbeck-ish) walk:
        //   drift += white*STEP - drift*REVERT
        // STEP sets activity, REVERT pulls it back to centre so it stays bounded
        // and lazy (sub-Hz wander). These are intentionally loose, not precise.
        const float DriftStep   = 0.05f;
        const float DriftRevert = 0.008f;

        float _sr = 44100f;

        // Per-partial state.
        readonly float[] _re    = new float[MaxPartials];
        readonly float[] _im    = new float[MaxPartials];
        readonly float[] _cos   = new float[MaxPartials];
        readonly float[] _sin   = new float[MaxPartials];
        readonly float[] _amp   = new float[MaxPartials];
        readonly float[] _drift = new float[MaxPartials];   // current detune state, ~[-1,1]
        int _active;

        readonly Random _rng;              // per-voice — distinct seed for chord independence

        // Pitch / glide.
        float _currentMidi = 60f;
        float _targetMidi  = 60f;

        // Live params (pushed each Work block by the machine).
        int   _partials   = 48;
        float _b          = 0f;
        float _slope      = 1f;
        float _dGlobal    = 0f;
        float _dampTilt   = 1f;
        float _glideCoef  = 1f;
        float _driftDepth = 0f;            // max frequency-ratio deviation (0 = off)

        int _ctrl;
        readonly AdsrEnvelope _ampEnv = new AdsrEnvelope();

        // Per-voice pending events — set by the machine's SetNote (incl. the
        // §14/§42 sibling-track recovery), drained at the top of Work.
        public bool HasNoteOn;
        public bool HasNoteOff;
        public byte PendingBuzzNote;

        public Voice(int seed) { _rng = new Random(seed); }

        public bool IsActive => _ampEnv.IsActive;

        public void SetSampleRate(float sr) => _sr = sr;

        public void SetParams(int partials, float b, float slope,
                              float dGlobal, float dampTilt, float driftDepth, float glideCoef,
                              float aSec, float dSec, float sustain, float rSec)
        {
            _partials   = Math.Clamp(partials, 1, MaxPartials);
            _b          = b;
            _slope      = slope;
            _dGlobal    = dGlobal;
            _dampTilt   = dampTilt;
            _driftDepth = driftDepth;
            _glideCoef  = glideCoef;
            _ampEnv.SetParams(aSec, dSec, sustain, rSec, _sr);
        }

        public void NoteOn(int midi, bool wasIdle)
        {
            _targetMidi = midi;
            if (wasIdle) _currentMidi = midi;     // first-note-from-rest snap (SH101 §6.1)

            float sum = 0f;
            for (int p = 0; p < _partials; p++)
            {
                float a = 1f / MathF.Pow(p + 1, _slope);
                _amp[p] = a;
                sum += a;
            }
            float inv = sum > 0f ? 1f / sum : 1f;
            for (int p = 0; p < _partials; p++)
            {
                _amp[p] *= inv;
                _re[p] = 1f; _im[p] = 0f;
            }
            for (int p = _partials; p < MaxPartials; p++)
            {
                _amp[p] = 0f; _re[p] = 1f; _im[p] = 0f;
            }
            Array.Clear(_drift, 0, MaxPartials);  // start in tune; let it wander from there

            _active = _partials;
            _ctrl   = 0;
            _ampEnv.NoteOn();
        }

        public void NoteOff()           => _ampEnv.NoteOff();
        public void ForceFade(float sr) => _ampEnv.ForcedRelease(sr);   // transport stop (Core §27)

        void ControlUpdate()
        {
            _currentMidi += (_targetMidi - _currentMidi) * _glideCoef;
            if (MathF.Abs(_targetMidi - _currentMidi) < 0.001f) _currentMidi = _targetMidi;

            float f0     = 440f * DspMath.FastPow2((_currentMidi - 69f) / 12f);
            float nyq    = _sr * 0.5f;
            float wScale = DspMath.TwoPi / _sr;
            bool  drift  = _driftDepth > 0f;

            int active = _partials;
            float totalAmp = 0f;
            for (int p = 0; p < _partials; p++)
            {
                int   nn    = p + 1;
                float ratio = nn * MathF.Sqrt(1f + _b * nn * nn);
                float freq  = f0 * ratio;

                if (drift)
                {
                    float w  = (float)(_rng.NextDouble() * 2.0 - 1.0);
                    float dr = _drift[p] + w * DriftStep - _drift[p] * DriftRevert;
                    if (dr > 2f) dr = 2f; else if (dr < -2f) dr = -2f;
                    _drift[p] = dr;
                    freq *= 1f + dr * _driftDepth;
                }

                if (freq >= nyq) { active = p; break; }

                float wRot = wScale * freq;
                _cos[p] = MathF.Cos(wRot);
                _sin[p] = MathF.Sin(wRot);

                if (_dGlobal > 0f)
                {
                    float dN = _dGlobal * MathF.Pow(nn, _dampTilt);
                    _amp[p] *= MathF.Exp(-dN * CtrlBlock / _sr);
                }

                float mag2 = _re[p] * _re[p] + _im[p] * _im[p];
                float k    = 1.5f - 0.5f * mag2;
                _re[p] *= k; _im[p] *= k;

                totalAmp += _amp[p];
            }
            _active = active;

            if (totalAmp < 1e-5f) _ampEnv.ForceIdle();   // free a rung-out damped voice
        }

        public void Render(float[] outBuf, int n)
        {
            for (int i = 0; i < n; i++)
            {
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
                outBuf[i] = s * _ampEnv.Process();
            }
        }

        // ── Small per-sample ADSR ────────────────────────────────────────────
        sealed class AdsrEnvelope
        {
            enum St { Idle, Attack, Decay, Sustain, Release }
            St _st = St.Idle;
            float _level;
            float _attInc, _decCoef, _relCoef, _sustain;
            float _forcedCoef;     // fixed fast fade for transport stop
            bool  _forced;

            public bool IsActive => _st != St.Idle;

            public void SetParams(float aSec, float dSec, float sustain, float rSec, float sr)
            {
                _attInc  = aSec <= 0f ? 1f : 1f / (aSec * sr);
                _decCoef = dSec <= 0f ? 0f : MathF.Exp(-1f / (dSec * sr));
                _relCoef = rSec <= 0f ? 0f : MathF.Exp(-1f / (rSec * sr));
                _sustain = sustain;
            }

            public void NoteOn()    { _forced = false; _st = St.Attack; }
            public void NoteOff()   { if (_st != St.Idle) _st = St.Release; }
            public void ForceIdle() { _st = St.Idle; _level = 0f; }

            // ~5 ms fade that overrides the user Release (Core §27.1).
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
