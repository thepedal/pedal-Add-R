using System;

namespace PedalAddR
{
    // Pure-DSP helpers shared across the machine. No SDK contact (Build §6.3).
    internal static class DspMath
    {
        public const float TwoPi = 6.2831853072f;

        // Fast 2^x for the audio hot path (SH101 §1). Accuracy ~0.04%, which is
        // well under a cent of pitch. Use for per-sample / per-control-block
        // pitch maths; keep MathF.Exp/Pow/Sin/Cos for one-off coefficient setup.
        public static float FastPow2(float x)
        {
            float xi = MathF.Floor(x);
            float xf = x - xi;
            float p  = 1f + xf * (0.69315f + xf * (0.24023f + xf * 0.05550f));
            int   e  = Math.Clamp((int)xi + 127, 1, 254);
            return BitConverter.Int32BitsToSingle(e << 23) * p;
        }

        // Buzz note byte -> 0-based MIDI (Core §7).
        // Layout: [octave:4][semitone:4], semitone 1=C..12=B.
        public static int BuzzNoteToMidi(byte b)
        {
            int octave   = b >> 4;
            int semitone = (b & 0xF) - 1;
            return octave * 12 + semitone;
        }

        // Cheap odd cubic soft clip. Unity-ish below |x|~1, saturates to +/-1 at
        // |x| >= 1.5. Only a safety net here (the bank is amplitude-normalised),
        // but it gracefully absorbs the all-in-phase onset transient of plucks.
        public static float SoftClip(float x)
        {
            if (x <= -1.5f) return -1f;
            if (x >=  1.5f) return  1f;
            return x - (4f / 27f) * x * x * x;
        }
    }
}
