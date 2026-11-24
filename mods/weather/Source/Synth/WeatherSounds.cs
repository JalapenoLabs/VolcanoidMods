// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Forecast;

namespace JalapenoLabs.Weather.Synth;

/// <summary>Synthesised stereo audio: interleaved left/right samples in [-1, 1].</summary>
public readonly struct SoundBuffer {
    public const int Channels = 2;

    public readonly float[] Samples;
    public readonly int SampleRate;

    public SoundBuffer(float[] samples, int sampleRate) {
        Samples = samples;
        SampleRate = sampleRate;
    }

    public int Frames => Samples.Length / Channels;
}

/// <summary>
/// The weather's synthesised sounds: wind and thunder. (The rain is recorded; see Recordings/CREDITS.md.)
///
/// Wind is band-passed brown noise whose pitch and strength wander, with a whistle in the gusts. Thunder is brown noise
/// behind a falling low-pass filter, swelling in several rolls as the sound arrives from different parts of the bolt;
/// a close strike opens with a rip, a crack and a heavy boom. The wind loop crossfades its end into its start, so it
/// repeats without a seam.
///
/// Each sound is drawn from a fixed seed, so it is the same in every game.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public static class WeatherSounds {
    public const int SampleRate = 44100;

    /// <summary>How tall a close strike stands over the loudest of the thunder rolling after it.</summary>
    const float StrikeOverRolls = 2.8f;

    /// <summary>How much of a loop's end is blended into its start to hide the seam.</summary>
    const float LoopCrossfadeSeconds = 0.5f;

    /// <summary>Gusting wind: band-passed brown noise whose pitch and strength wander.</summary>
    public static SoundBuffer Wind(ulong seed) {
        var random = new StormRandom(seed);
        int frames = 16 * SampleRate;
        int total = frames + (int)(LoopCrossfadeSeconds * SampleRate);
        var left = new float[total];
        var right = new float[total];

        var shared = new BrownNoise();
        var brownLeft = new BrownNoise();
        var brownRight = new BrownNoise();
        var bandLeft = new Biquad(Biquad.Kind.BandPass, 400, 0.8f, SampleRate);
        var bandRight = new Biquad(Biquad.Kind.BandPass, 400, 0.8f, SampleRate);
        var whistleNoise = new PinkNoise();
        var whistle = new Biquad(Biquad.Kind.BandPass, 1600, 6, SampleRate);
        for (int index = 0; index < total; index++) {
            double seconds = index / (double)SampleRate;
            if (index % 64 == 0) {
                float centre = 280 + 350 * (float)(StormRandom.Wander(seed, seconds, 3.5) + 1) / 2;
                bandLeft.Tune(Biquad.Kind.BandPass, centre, 0.8f, SampleRate);
                bandRight.Tune(Biquad.Kind.BandPass, centre * 1.07f, 0.8f, SampleRate);
                float whistlePitch = 1300 + 900 * (float)(StormRandom.Wander(seed + 9, seconds, 6) + 1) / 2;
                whistle.Tune(Biquad.Kind.BandPass, whistlePitch, 6, SampleRate);
            }
            double gust = (StormRandom.Wander(seed + 5, seconds, 5) + 1) / 2;
            float strength = (float)(0.35 + 0.65 * gust * gust);
            float common = shared.Next(ref random);
            float whistling = whistle.Process(whistleNoise.Next(ref random)) * (float)(gust * gust) * 0.6f;
            left[index] = (bandLeft.Process(0.6f * common + 0.4f * brownLeft.Next(ref random)) + whistling) * strength;
            right[index] = (bandRight.Process(0.6f * common + 0.4f * brownRight.Next(ref random)) + whistling)
                * strength;
        }
        return Finish(left, right, frames, 0.8f);
    }

    /// <summary>
    /// One thunderclap. A near one (a few hundred metres) rips open with a crack and its rolls are bright; a far one is
    /// only a low rumble that swells and fades.
    /// </summary>
    public static SoundBuffer Thunder(ulong seed, bool isNear) {
        var random = new StormRandom(seed);
        float seconds = isNear ? 9 : 10;
        int frames = (int)(seconds * SampleRate);
        var left = new float[frames];
        var right = new float[frames];

        // The rolls: sound from each part of a long bolt arrives at its own time, so thunder swells several times.
        int rollCount = isNear ? 4 + (int)(random.NextFloat() * 3) : 3 + (int)(random.NextFloat() * 3);
        var rollTimes = new float[rollCount];
        var rollWidths = new float[rollCount];
        var rollLevels = new float[rollCount];
        for (int roll = 0; roll < rollCount; roll++) {
            bool isFirst = roll == 0;
            rollTimes[roll] = isNear
                ? (isFirst ? random.Range(0.05f, 0.15f) : random.Range(0.3f, 4.5f))
                : random.Range(0.6f, 5.5f);
            rollWidths[roll] = random.Range(0.35f, 1.4f);
            rollLevels[roll] = isFirst ? 1 : random.Range(0.35f, 0.85f);
        }

        var shared = new BrownNoise();
        var brownLeft = new BrownNoise();
        var brownRight = new BrownNoise();
        var lowPassLeft = new Biquad(Biquad.Kind.LowPass, 2000, 0.7f, SampleRate);
        var lowPassRight = new Biquad(Biquad.Kind.LowPass, 2000, 0.7f, SampleRate);
        for (int index = 0; index < frames; index++) {
            float time = index / (float)SampleRate;
            if (index % 64 == 0) {
                // Air absorbs the highs first, so the cutoff falls as the rumble goes on (and starts low far away).
                float cutoff = isNear
                    ? 2500 * (float)Math.Exp(-time / 1.2f) + 180
                    : 500 * (float)Math.Exp(-time / 2.5f) + 90;
                lowPassLeft.Tune(Biquad.Kind.LowPass, cutoff, 0.7f, SampleRate);
                lowPassRight.Tune(Biquad.Kind.LowPass, cutoff * 0.93f, 0.7f, SampleRate);
            }
            float envelope = 0;
            for (int roll = 0; roll < rollCount; roll++) {
                float fromPeak = time - rollTimes[roll];
                // Each roll arrives quickly and dies away slowly.
                float shape = fromPeak < 0
                    ? (float)Math.Exp(-Math.Pow(fromPeak / (0.3f * rollWidths[roll]), 2))
                    : (float)Math.Exp(-fromPeak / rollWidths[roll]);
                envelope += rollLevels[roll] * shape;
            }
            float common = shared.Next(ref random);
            left[index] = lowPassLeft.Process(0.6f * common + 0.4f * brownLeft.Next(ref random)) * envelope;
            right[index] = lowPassRight.Process(0.6f * common + 0.4f * brownRight.Next(ref random)) * envelope;
        }

        if (isNear) {
            // The strike stands about three times as tall as the rolls' loudest moment, as a close strike does over
            // its thunder; the rolls keep the level they have.
            float rollPeak = 1e-6f;
            for (int index = 0; index < frames; index++) {
                rollPeak = Math.Max(rollPeak, Math.Max(Math.Abs(left[index]), Math.Abs(right[index])));
            }
            AddCloseStrike(ref random, left, right, rollPeak * StrikeOverRolls);
        }

        // Fade the very end, so a clip cut at its length never clicks.
        int fadeFrames = SampleRate / 2;
        for (int offset = 0; offset < fadeFrames; offset++) {
            float fade = offset / (float)fadeFrames;
            left[frames - 1 - offset] *= fade;
            right[frames - 1 - offset] *= fade;
        }
        return Interleave(left, right, frames, 0.95f);
    }

    /// <summary>
    /// The sound of a strike a few hundred metres off, before its thunder rolls: a short tearing rip as the channel
    /// forms, a hard crack with the arc's raspy buzz in it, and a heavy boom that falls in pitch, with a few crackles
    /// over it. It is built to have weight, low end above all: a burst of bright noise alone sounds like a cymbal being
    /// tapped, not a strike.
    /// </summary>
    /// <param name="loudness">How loud the strike peaks: kept in proportion to the rolls, which follow it.</param>
    static void AddCloseStrike(ref StormRandom random, float[] left, float[] right, float loudness) {
        int length = Math.Min(left.Length, (int)(1.6f * SampleRate));
        var strikeLeft = new float[length];
        var strikeRight = new float[length];

        // The rip: mid-range noise broken into tiny grains of random strength, rising for about a tenth of a second.
        float ripSeconds = random.Range(0.06f, 0.14f);
        int ripFrames = (int)(ripSeconds * SampleRate);
        var ripLeft = new Biquad(Biquad.Kind.BandPass, 1100, 0.6f, SampleRate);
        var ripRight = new Biquad(Biquad.Kind.BandPass, 1250, 0.6f, SampleRate);
        int grainFrames = 0;
        float grain = 0;
        for (int index = 0; index < ripFrames; index++) {
            if (grainFrames-- <= 0) {
                grainFrames = (int)(random.Range(0.002f, 0.005f) * SampleRate);
                grain = random.Range(0.25f, 1);
            }
            float rising = 0.2f + 0.8f * index / (float)ripFrames;
            float level = grain * rising * 0.55f;
            strikeLeft[index] += ripLeft.Process(Dsp.White(ref random)) * level;
            strikeRight[index] += ripRight.Process(Dsp.White(ref random)) * level;
        }

        // The crack and the boom, from the end of the rip. The crack is full-range noise with the top hiss rolled
        // off, gone in a few hundredths of a second; the boom is noise whose cutoff falls from the mids into the
        // bass as it dies away, over a sub-bass thump sliding down in pitch.
        var crackLeft = new Biquad(Biquad.Kind.LowPass, 5000, 0.7f, SampleRate);
        var crackRight = new Biquad(Biquad.Kind.LowPass, 5000, 0.7f, SampleRate);
        var boomLeft = new Biquad(Biquad.Kind.LowPass, 900, 0.8f, SampleRate);
        var boomRight = new Biquad(Biquad.Kind.LowPass, 900, 0.8f, SampleRate);
        var boomNoise = new BrownNoise();
        double thumpPhase = 0;
        for (int index = ripFrames; index < length; index++) {
            float time = (index - ripFrames) / (float)SampleRate;
            if (index % 64 == 0) {
                float cutoff = 750 * (float)Math.Exp(-time / 0.18f) + 140;
                boomLeft.Tune(Biquad.Kind.LowPass, cutoff, 0.8f, SampleRate);
                boomRight.Tune(Biquad.Kind.LowPass, cutoff * 0.95f, 0.8f, SampleRate);
            }
            float attack = Math.Min(1, time / 0.004f);
            float crack = (float)Math.Exp(-time / 0.014f);
            float boom = attack * (float)Math.Exp(-time / 0.42f);
            float thump = attack * (float)Math.Exp(-time / 0.33f);
            float frequency = 38 + 37 * (float)Math.Exp(-time / 0.25f);
            thumpPhase += 2 * Math.PI * frequency / SampleRate;
            float thumpWave = (float)Math.Sin(thumpPhase) * thump * 0.9f;
            float body = boomNoise.Next(ref random);
            strikeLeft[index] += crackLeft.Process(Dsp.White(ref random)) * crack * 0.9f
                + boomLeft.Process(body + 0.35f * Dsp.White(ref random)) * boom * 1.4f
                + thumpWave;
            strikeRight[index] += crackRight.Process(Dsp.White(ref random)) * crack * 0.9f
                + boomRight.Process(body + 0.35f * Dsp.White(ref random)) * boom * 1.4f
                + thumpWave;
        }

        AddArc(ref random, strikeLeft, strikeRight, ripFrames);

        // A few crackles over the boom: short mid-range bursts, not clicks of white noise.
        var crackleFilter = new Biquad(Biquad.Kind.BandPass, 1800, 1.2f, SampleRate);
        int crackleCount = 3 + (int)(random.NextFloat() * 4);
        for (int crackle = 0; crackle < crackleCount; crackle++) {
            int start = ripFrames + (int)(random.Range(0.02f, 0.5f) * SampleRate);
            float level = random.Range(0.12f, 0.3f);
            float pan = random.NextFloat();
            int crackleLength = Math.Min(length - start, (int)(0.04f * SampleRate));
            for (int offset = 0; offset < crackleLength; offset++) {
                float time = offset / (float)SampleRate;
                float value = crackleFilter.Process(Dsp.White(ref random)) * (float)Math.Exp(-time / 0.012f) * level;
                strikeLeft[start + offset] += value * (float)Math.Sqrt(1 - pan);
                strikeRight[start + offset] += value * (float)Math.Sqrt(pan);
            }
        }

        // Brought to its loudness and laid over the start of the rolls. A gentle saturation keeps the boom's few
        // tallest peaks from setting the level of everything else.
        float strikePeak = 1e-6f;
        for (int index = 0; index < length; index++) {
            strikePeak = Math.Max(strikePeak, Math.Max(Math.Abs(strikeLeft[index]), Math.Abs(strikeRight[index])));
        }
        float gain = 1.4f * loudness / strikePeak;
        for (int index = 0; index < length; index++) {
            left[index] += loudness * (float)Math.Tanh(strikeLeft[index] * gain / loudness);
            right[index] += loudness * (float)Math.Tanh(strikeRight[index] * gain / loudness);
        }
    }

    /// <summary>
    /// The arc at the heart of the crack: the raspy "bzzzt" of the channel itself. It is no tone (a falling tone is a
    /// laser's "pew") but a dense, irregular train of tiny sparks, bursts of noise firing about a hundred and twenty
    /// times a second with no two gaps alike, over a harsh layer of clipped noise in the upper mids. It strikes with
    /// the crack, holds a tenth of a second and is gone within a quarter. The top is rolled off, so it rasps without
    /// hissing.
    /// </summary>
    static void AddArc(ref StormRandom random, float[] left, float[] right, int start) {
        int length = Math.Min(left.Length - start, (int)(0.26f * SampleRate));
        var sparkBand = new Biquad(Biquad.Kind.BandPass, 2300, 0.8f, SampleRate);
        var harshBand = new Biquad(Biquad.Kind.BandPass, 1600, 0.6f, SampleRate);
        var rollOff = new Biquad(Biquad.Kind.LowPass, 4500, 0.7f, SampleRate);
        int nextSpark = 0;
        int sparkFrames = 1;
        int sparkLeft = 0;
        float sparkLevel = 0;
        for (int offset = 0; offset < length; offset++) {
            float time = offset / (float)SampleRate;
            if (offset >= nextSpark) {
                sparkFrames = (int)(random.Range(0.0015f, 0.003f) * SampleRate);
                sparkLeft = sparkFrames;
                sparkLevel = random.Range(0.5f, 1);
                nextSpark = offset + (int)(SampleRate / random.Range(95, 150));
            }
            float gate = 0;
            if (sparkLeft > 0) {
                gate = sparkLevel * sparkLeft / (float)sparkFrames;
                sparkLeft--;
            }
            float spark = sparkBand.Process(Dsp.White(ref random) * gate);
            float harsh = Math.Max(-1, Math.Min(1, harshBand.Process(Dsp.White(ref random)) * 2.5f));
            float onset = Math.Min(1, time / 0.003f);
            float fade = time < 0.08f ? 1 : (float)Math.Exp(-(time - 0.08f) / 0.06f);
            float value = rollOff.Process(spark * 2.2f + harsh * 0.3f) * onset * fade * 0.6f;
            left[start + offset] += value;
            right[start + offset] += value;
        }
    }

    static SoundBuffer Finish(float[] left, float[] right, int frames, float peak) {
        int crossfade = left.Length - frames;
        for (int index = 0; index < crossfade; index++) {
            float head = (float)Math.Sqrt(index / (float)crossfade);
            float tail = (float)Math.Sqrt(1 - index / (float)crossfade);
            left[index] = left[index] * head + left[frames + index] * tail;
            right[index] = right[index] * head + right[frames + index] * tail;
        }
        return Interleave(left, right, frames, peak);
    }

    static SoundBuffer Interleave(float[] left, float[] right, int frames, float peak) {
        float loudest = 1e-6f;
        for (int index = 0; index < frames; index++) {
            loudest = Math.Max(loudest, Math.Max(Math.Abs(left[index]), Math.Abs(right[index])));
        }
        float gain = peak / loudest;
        var samples = new float[frames * SoundBuffer.Channels];
        for (int index = 0; index < frames; index++) {
            samples[index * 2] = left[index] * gain;
            samples[index * 2 + 1] = right[index] * gain;
        }
        return new SoundBuffer(samples, SampleRate);
    }
}
