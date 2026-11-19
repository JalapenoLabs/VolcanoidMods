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
/// Every sound of the weather, synthesised from noise.
///
/// Rain is a bed of pink noise (rain hitting everything at once) under thousands of individual drops: sharp clicks on
/// hard ground and small rising "plinks" where drops land in water. On a roof each drop rings the panel it hits.
/// Thunder is brown noise behind a falling low-pass filter, swelling in several rolls as the sound arrives from
/// different parts of the bolt; a close strike opens with a ripping crack. The loops crossfade their end into their
/// start, so they repeat without a seam.
///
/// Each sound is drawn from a fixed seed, so it is the same in every game.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public static class WeatherSounds {
    public const int SampleRate = 44100;

    /// <summary>How much of a loop's end is blended into its start to hide the seam.</summary>
    const float LoopCrossfadeSeconds = 0.5f;

    /// <summary>Heavy rain: a roar of noise and a dense hail of drops.</summary>
    public static SoundBuffer Downpour(ulong seed) {
        return RainLoop(seed, 8, dropsPerSecond: 1100, bodyLevel: 0.6f, plinkShare: 0.25f);
    }

    /// <summary>Light rain: individual drops pattering, over a whisper of noise.</summary>
    public static SoundBuffer Patter(ulong seed) {
        return RainLoop(seed, 8, dropsPerSecond: 160, bodyLevel: 0.1f, plinkShare: 0.4f);
    }

    /// <summary>Rain heard from inside: drops drumming on the panels overhead, low and soft.</summary>
    public static SoundBuffer Roof(ulong seed) {
        var random = new StormRandom(seed);
        int frames = (int)(8f * SampleRate);
        int total = frames + (int)(LoopCrossfadeSeconds * SampleRate);
        var left = new float[total];
        var right = new float[total];

        var brownLeft = new BrownNoise();
        var brownRight = new BrownNoise();
        var lowPassLeft = new Biquad(Biquad.Kind.LowPass, 500, 0.7f, SampleRate);
        var lowPassRight = new Biquad(Biquad.Kind.LowPass, 500, 0.7f, SampleRate);
        for (int index = 0; index < total; index++) {
            left[index] = lowPassLeft.Process(brownLeft.Next(ref random)) * 0.5f;
            right[index] = lowPassRight.Process(brownRight.Next(ref random)) * 0.5f;
        }

        // Each drop rings one of the roof panel's modes, damped quickly by the panel's stiffening.
        float[] panelModes = { 380, 520, 760, 1100, 1450 };
        int dropCount = 520 * total / SampleRate;
        for (int drop = 0; drop < dropCount; drop++) {
            int start = (int)(random.NextFloat() * total);
            float loudness = 0.05f + 0.6f * (float)Math.Pow(random.NextFloat(), 2.5);
            float frequency = panelModes[(int)(random.NextFloat() * panelModes.Length)] * random.Range(0.9f, 1.1f);
            float decaySeconds = random.Range(0.025f, 0.07f);
            float pan = random.NextFloat();
            int length = Math.Min(total - start, (int)(decaySeconds * 5 * SampleRate));
            double phaseStep = 2 * Math.PI * frequency / SampleRate;
            for (int offset = 0; offset < length; offset++) {
                float time = offset / (float)SampleRate;
                float value = (float)Math.Sin(phaseStep * offset) * (float)Math.Exp(-time / decaySeconds) * loudness;
                left[start + offset] += value * (float)Math.Sqrt(1 - pan);
                right[start + offset] += value * (float)Math.Sqrt(pan);
            }
        }
        return Finish(left, right, frames, 0.8f);
    }

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
            AddCrack(ref random, left, right);
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

    /// <summary>The rip of a close strike: a sharp burst of bright noise, then a scatter of crackles.</summary>
    static void AddCrack(ref StormRandom random, float[] left, float[] right) {
        var highPass = new Biquad(Biquad.Kind.HighPass, 600, 0.7f, SampleRate);
        int crackFrames = (int)(0.35f * SampleRate);
        for (int index = 0; index < crackFrames; index++) {
            float time = index / (float)SampleRate;
            float value = highPass.Process(Dsp.White(ref random)) * (float)Math.Exp(-time / 0.045f) * 1.6f;
            left[index] += value;
            right[index] += value;
        }

        int crackleCount = 5 + (int)(random.NextFloat() * 5);
        for (int crackle = 0; crackle < crackleCount; crackle++) {
            int start = (int)(random.Range(0.01f, 0.8f) * SampleRate);
            float level = random.Range(0.2f, 0.6f);
            float pan = random.NextFloat();
            int length = Math.Min(left.Length - start, (int)(0.05f * SampleRate));
            for (int offset = 0; offset < length; offset++) {
                float time = offset / (float)SampleRate;
                float value = Dsp.White(ref random) * (float)Math.Exp(-time / 0.008f) * level;
                left[start + offset] += value * (float)Math.Sqrt(1 - pan);
                right[start + offset] += value * (float)Math.Sqrt(pan);
            }
        }
    }

    static SoundBuffer RainLoop(ulong seed, float seconds, int dropsPerSecond, float bodyLevel, float plinkShare) {
        var random = new StormRandom(seed);
        int frames = (int)(seconds * SampleRate);
        int total = frames + (int)(LoopCrossfadeSeconds * SampleRate);
        var left = new float[total];
        var right = new float[total];

        // The bed: pink noise without its rumble and its harshest top, partly shared between the ears for width.
        var shared = new PinkNoise();
        var pinkLeft = new PinkNoise();
        var pinkRight = new PinkNoise();
        var highPassLeft = new Biquad(Biquad.Kind.HighPass, 250, 0.7f, SampleRate);
        var highPassRight = new Biquad(Biquad.Kind.HighPass, 250, 0.7f, SampleRate);
        var lowPassLeft = new Biquad(Biquad.Kind.LowPass, 7000, 0.7f, SampleRate);
        var lowPassRight = new Biquad(Biquad.Kind.LowPass, 7000, 0.7f, SampleRate);
        for (int index = 0; index < total; index++) {
            float common = shared.Next(ref random);
            float leftNoise = 0.6f * common + 0.4f * pinkLeft.Next(ref random);
            float rightNoise = 0.6f * common + 0.4f * pinkRight.Next(ref random);
            left[index] = lowPassLeft.Process(highPassLeft.Process(leftNoise)) * bodyLevel;
            right[index] = lowPassRight.Process(highPassRight.Process(rightNoise)) * bodyLevel;
        }

        int dropCount = dropsPerSecond * total / SampleRate;
        for (int drop = 0; drop < dropCount; drop++) {
            int start = (int)(random.NextFloat() * total);
            // Most drops land far off and are faint; a few land close.
            float loudness = 0.04f + 0.7f * (float)Math.Pow(random.NextFloat(), 3);
            float pan = random.NextFloat();
            float leftGain = (float)Math.Sqrt(1 - pan) * loudness;
            float rightGain = (float)Math.Sqrt(pan) * loudness;
            if (random.NextFloat() < plinkShare) {
                AddPlink(ref random, left, right, start, leftGain, rightGain);
            } else {
                AddClick(ref random, left, right, start, leftGain, rightGain);
            }
        }
        return Finish(left, right, frames, 0.8f);
    }

    /// <summary>A drop landing in water: the bubble it traps rings with a quickly rising pitch.</summary>
    static void AddPlink(
        ref StormRandom random,
        float[] left,
        float[] right,
        int start,
        float leftGain,
        float rightGain
    ) {
        float frequency = random.Range(1800, 5000);
        float durationSeconds = random.Range(0.008f, 0.02f);
        int length = Math.Min(left.Length - start, (int)(durationSeconds * SampleRate));
        double phase = 0;
        for (int offset = 0; offset < length; offset++) {
            float progress = offset / (float)length;
            phase += 2 * Math.PI * frequency * (1 + 0.4f * progress) / SampleRate;
            float attack = Math.Min(1, offset / (0.0003f * SampleRate));
            float value = (float)Math.Sin(phase) * attack * (float)Math.Exp(-progress * 4);
            left[start + offset] += value * leftGain;
            right[start + offset] += value * rightGain;
        }
    }

    /// <summary>A drop hitting ground or leaves: a tiny band-limited burst of noise.</summary>
    static void AddClick(
        ref StormRandom random,
        float[] left,
        float[] right,
        int start,
        float leftGain,
        float rightGain
    ) {
        var band = new Biquad(Biquad.Kind.BandPass, random.Range(2000, 7000), 1.2f, SampleRate);
        float decaySeconds = random.Range(0.001f, 0.004f);
        int length = Math.Min(left.Length - start, (int)(decaySeconds * 6 * SampleRate));
        for (int offset = 0; offset < length; offset++) {
            float time = offset / (float)SampleRate;
            float value = band.Process(Dsp.White(ref random)) * (float)Math.Exp(-time / decaySeconds) * 2.5f;
            left[start + offset] += value * leftGain;
            right[start + offset] += value * rightGain;
        }
    }

    /// <summary>
    /// Folds a loop's extra tail into its start (an equal-power crossfade, right for noise), so playback runs from the
    /// last sample straight back into the first without a seam, then normalises and interleaves it.
    /// </summary>
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
