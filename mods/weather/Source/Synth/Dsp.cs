// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Forecast;

namespace JalapenoLabs.Weather.Synth;

/// <summary>
/// The building blocks the weather's sounds are synthesised from: noise colours and simple filters.
///
/// The mod ships no audio files. Rain and thunder are made of noise shaped by filters and envelopes, generated once at
/// load, so there are no recordings to license and nothing to rebuild for each platform.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
internal static class Dsp {
    /// <summary>White noise in [-1, 1).</summary>
    public static float White(ref StormRandom random) {
        return random.NextFloat() * 2 - 1;
    }

    /// <summary>Linear interpolation, clamped.</summary>
    public static float Lerp(float from, float to, float amount) {
        return from + (to - from) * Math.Max(0, Math.Min(1, amount));
    }
}

/// <summary>
/// Pink noise (equal energy per octave): the soft "shhh" of rain on everything at once. Paul Kellet's refined filter
/// of white noise, accurate to within a decibel above 10 Hz at 44.1 kHz.
/// </summary>
internal struct PinkNoise {
    float b0;
    float b1;
    float b2;
    float b3;
    float b4;
    float b5;
    float b6;

    public float Next(ref StormRandom random) {
        float white = Dsp.White(ref random);
        b0 = 0.99886f * b0 + white * 0.0555179f;
        b1 = 0.99332f * b1 + white * 0.0750759f;
        b2 = 0.96900f * b2 + white * 0.1538520f;
        b3 = 0.86650f * b3 + white * 0.3104856f;
        b4 = 0.55000f * b4 + white * 0.5329522f;
        b5 = -0.7616f * b5 - white * 0.0168980f;
        float pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362f;
        b6 = white * 0.115926f;
        return pink * 0.11f;
    }
}

/// <summary>Brown noise (a random walk): the deep roll of thunder and the body of wind.</summary>
internal struct BrownNoise {
    float level;

    public float Next(ref StormRandom random) {
        level = (level + 0.02f * Dsp.White(ref random)) / 1.02f;
        return level * 3.5f;
    }
}

/// <summary>
/// A biquad filter (Robert Bristow-Johnson's cookbook): low-pass, high-pass or band-pass, retunable per sample so a
/// sweep (thunder's cutoff falling as it rolls away) stays smooth.
/// </summary>
internal struct Biquad {
    public enum Kind {
        LowPass,
        HighPass,
        BandPass,
    }

    float b0;
    float b1;
    float b2;
    float a1;
    float a2;
    float x1;
    float x2;
    float y1;
    float y2;

    public Biquad(Kind kind, float frequency, float q, int sampleRate) : this() {
        Tune(kind, frequency, q, sampleRate);
    }

    public void Tune(Kind kind, float frequency, float q, int sampleRate) {
        double omega = 2 * Math.PI * Math.Min(frequency, sampleRate * 0.45) / sampleRate;
        double cosine = Math.Cos(omega);
        double alpha = Math.Sin(omega) / (2 * q);
        double a0 = 1 + alpha;
        double numerator0;
        double numerator1;
        double numerator2;
        switch (kind) {
            case Kind.LowPass:
                numerator0 = (1 - cosine) / 2;
                numerator1 = 1 - cosine;
                numerator2 = (1 - cosine) / 2;
                break;
            case Kind.HighPass:
                numerator0 = (1 + cosine) / 2;
                numerator1 = -(1 + cosine);
                numerator2 = (1 + cosine) / 2;
                break;
            default:
                numerator0 = alpha;
                numerator1 = 0;
                numerator2 = -alpha;
                break;
        }
        b0 = (float)(numerator0 / a0);
        b1 = (float)(numerator1 / a0);
        b2 = (float)(numerator2 / a0);
        a1 = (float)(-2 * cosine / a0);
        a2 = (float)((1 - alpha) / a0);
    }

    public float Process(float input) {
        float output = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
        x2 = x1;
        x1 = input;
        y2 = y1;
        y1 = output;
        return output;
    }
}
