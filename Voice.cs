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
    //   • Phase spread: at NoteOn each partial gets an independent random
    //     starting phase (drawn from per-voice RNG), scaled by the knob.
    //     0 = all in phase (sharp strike transient). Max = energy smeared,
    //     smooth pad onset. Latches at NoteOn — phase mid-note is determined
    //     by rotor history and can't be moved without a click.
    //
    // Click-protect (v0.5). NoteOn re-seeds all phasors discontinuously; if
    // the ADSR is still audibly above zero (very common with short Decay +
    // low Sustain patches in fast patterns), that's a hard step in the
    // waveform → audible click. A per-sample one-pole gain (`_trigGain`,
    // 1 ms tau) fades the voice down to near-silence first, then the seed
    // happens, then the gain ramps back to 1. Total retrigger gap ~5 ms;
    // below-audible retriggers bypass the fade.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class Voice
    {
        public const int MaxPartials = 64;
        const int CtrlBlock = 32;          // control-rate update cadence (samples)

        const float DriftStep   = 0.05f;
        const float DriftRevert = 0.008f;

        const float TrigTauSec  = 0.001f;  // click-protect fade time-constant
        const float TrigSeedThr = 0.005f;  // seed fires when _trigGain falls below this (~-46 dB)

        float _sr = 44100f;

        // Per-partial state.
        readonly float[] _re    = new float[MaxPartials];
        readonly float[] _im    = new float[MaxPartials];
        readonly float[] _cos   = new float[MaxPartials];
        readonly float[] _sin   = new float[MaxPartials];
        readonly float[] _amp   = new float[MaxPartials];   // cached: shape × env (per block)
        readonly float[] _env   = new float[MaxPartials];   // decay envelope state (per partial)
        readonly float[] _drift = new float[MaxPartials];   // OU drift state, ~[-1,1]
        int _active;

        readonly Random _rng;

        // Pitch / glide.
        float _currentMidi = 60f;
        float _targetMidi  = 60f;

        // Live params.
        int   _partials    = 48;
        float _b           = 0f;
        float _slope       = 1f;
        float _dGlobal     = 0f;
        float _dampTilt    = 1f;
        float _driftDepth  = 0f;
        float _phaseSpread = 0f;
        float _glideCoef   = 1f;

        // Click-protect (v0.5).
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

        // A pending retrigger keeps the voice "active" so the fade-out
        // actually gets rendered (otherwise the idle fast-path would skip
        // it before the deferred seed can fire).
        public bool IsActive => _ampEnv.IsActive || _retrigPending;

        public void SetSampleRate(float sr)
        {
            _sr = sr;
            _trigCoef = 1f - MathF.Exp(-1f / (TrigTauSec * sr));
        }

        public void SetParams(int partials, float b, float slope,
                              float dGlobal, float dampTilt, float driftDepth,
                              float phaseSpread, float glideCoef,
                              float aSec, float dSec, float sustain, float rSec)
        {
            _partials    = Math.Clamp(partials, 1, MaxPartials);
            _b           = b;
            _slope       = slope;
            _dGlobal     = dGlobal;
            _dampTilt    = dampTilt;
            _driftDepth  = driftDepth;
            _phaseSpread = phaseSpread;
            _glideCoef   = glideCoef;
            _ampEnv.SetParams(aSec, dSec, sustain, rSec, _sr);
        }

        public void NoteOn(int midi, bool wasIdle)
        {
            // Fresh-from-idle (or already inaudible) → no click to mask, fire
            // immediately. Otherwise defer the seed until _trigGain has faded
            // below threshold; the actual seed happens inside Render().
            if (wasIdle || _ampEnv.IsBelowAudible)
            {
                _trigGain       = 1f;
                _trigGainTarget = 1f;
                _retrigPending  = false;
                SeedBank(midi, snapPitch: wasIdle);
                _ampEnv.NoteOn();
            }
            else
            {
                _retrigPending  = true;
                _pendingMidi    = midi;
                _trigGainTarget = 0f;
                // SeedBank deferred to Render — _trigGain ramps current waveform
                // toward silence first, masking the upcoming phasor discontinuity.
            }
        }

        public void NoteOff()
        {
            if (_retrigPending)
            {
                // A retrigger was queued but the user has lifted the key
                // before it fired. Cancel the pending seed and let the
                // current voice release normally; ramp _trigGain back so
                // the release tail is audible.
                _retrigPending  = false;
                _trigGainTarget = 1f;
            }
            _ampEnv.NoteOff();
        }

        public void ForceFade(float sr)
        {
            // Transport-stop fast-fade (Core §27). Cancel any pending retrigger
            // so we don't restart the voice mid-fade.
            _retrigPending  = false;
            _trigGainTarget = 1f;
            _ampEnv.ForcedRelease(sr);
        }

        // Seed the partial bank from rest — used by both the fresh-trigger
        // path and the deferred retrigger path.
        void SeedBank(int midi, bool snapPitch)
        {
            _targetMidi = midi;
            if (snapPitch) _currentMidi = midi;     // first-note-from-rest snap (SH101 §6.1)

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
                    _re[p]  = 1f; _im[p] = 0f;          // all in phase — the "strike"
                }
            }
            Array.Clear(_drift, 0, MaxPartials);

            _active = _partials;
            _ctrl   = 0;                                // force ControlUpdate on next sample
        }

        void ControlUpdate()
        {
            _currentMidi += (_targetMidi - _currentMidi) * _glideCoef;
            if (MathF.Abs(_targetMidi - _currentMidi) < 0.001f) _currentMidi = _targetMidi;

            float f0     = 440f * DspMath.FastPow2((_currentMidi - 69f) / 12f);
            float nyq    = _sr * 0.5f;
            float wScale = DspMath.TwoPi / _sr;
            bool  drift  = _driftDepth > 0f;
            bool  damp   = _dGlobal    > 0f;

            int   active  = _partials;
            float sumBase = 0f;
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

                if (damp)
                {
                    float dN = _dGlobal * MathF.Pow(nn, _dampTilt);
                    _env[p] *= MathF.Exp(-dN * CtrlBlock / _sr);
                }

                float mag2 = _re[p] * _re[p] + _im[p] * _im[p];
                float k    = 1.5f - 0.5f * mag2;
                _re[p] *= k; _im[p] *= k;

                float ba = 1f / MathF.Pow(nn, _slope);
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
            // ^^ guard against freeing a voice while a retrigger is queued —
            // we want the deferred seed to bring it back, not be stranded.
        }

        public void Render(float[] outBuf, int n)
        {
            for (int i = 0; i < n; i++)
            {
                // Advance click-protect gain (per-sample one-pole).
                _trigGain += (_trigGainTarget - _trigGain) * _trigCoef;

                // Deferred retrigger: once _trigGain has faded below threshold,
                // fire the seed + ADSR.NoteOn and ramp the gain back up.
                if (_retrigPending && _trigGain < TrigSeedThr)
                {
                    SeedBank(_pendingMidi, snapPitch: false);
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
            public bool IsBelowAudible => _level < 0.005f;     // ~-46 dB

            public void SetParams(float aSec, float dSec, float sustain, float rSec, float sr)
            {
                _attInc  = aSec <= 0f ? 1f : 1f / (aSec * sr);
                _decCoef = dSec <= 0f ? 0f : MathF.Exp(-1f / (dSec * sr));
                _relCoef = rSec <= 0f ? 0f : MathF.Exp(-1f / (rSec * sr));
                _sustain = sustain;
            }

            // Reset level so a retrigger ramps from 0 — the click-protect fade
            // has already brought the audible output to near-silence by the
            // time this fires.
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
