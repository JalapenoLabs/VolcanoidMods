// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Weather.Synth;
using Xunit;

namespace JalapenoLabs.Weather.Tests;

public class SynthTests {
    /// <summary>Each loop with its length in seconds.</summary>
    static readonly Dictionary<string, float> LoopSeconds = new Dictionary<string, float> {
        ["wind"] = 16,
    };

    public static IEnumerable<object[]> Loops() {
        yield return new object[] { "wind", WeatherSounds.Wind(4) };
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void Loops_are_the_right_length_and_level(string name, SoundBuffer sound) {
        Assert.Equal((int)(LoopSeconds[name] * WeatherSounds.SampleRate), sound.Frames);
        AssertCleanAudio(name, sound);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void Loops_repeat_without_a_seam(string name, SoundBuffer sound) {
        // The jump from the last sample back to the first should be no bigger than the sound's ordinary steps.
        float[] samples = sound.Samples;
        double typicalStep = 0;
        for (int index = 2; index < samples.Length; index += 2) {
            typicalStep += Math.Abs(samples[index] - samples[index - 2]);
        }
        typicalStep /= sound.Frames - 1;
        float seam = Math.Abs(samples[0] - samples[samples.Length - 2]);
        Assert.True(seam < typicalStep * 8, $"{name}: seam {seam} against a typical step of {typicalStep}");
    }

    [Fact]
    public void Thunder_near_cracks_at_once_and_far_thunder_swells_in() {
        SoundBuffer near = WeatherSounds.Thunder(5, isNear: true);
        SoundBuffer far = WeatherSounds.Thunder(5, isNear: false);
        AssertCleanAudio("near thunder", near);
        AssertCleanAudio("far thunder", far);
        Assert.True(Peak(near, 0, 0.1f) > 0.4f, "a close strike should open with its crack");
        Assert.True(Peak(far, 0, 0.1f) < Peak(far, 0, far.Frames / (float)WeatherSounds.SampleRate) * 0.5f);
    }

    [Fact]
    public void A_seed_always_makes_the_same_sound() {
        Assert.Equal(WeatherSounds.Thunder(7, isNear: true).Samples, WeatherSounds.Thunder(7, isNear: true).Samples);
    }

    static void AssertCleanAudio(string name, SoundBuffer sound) {
        float peak = 0;
        foreach (float sample in sound.Samples) {
            Assert.False(float.IsNaN(sample) || float.IsInfinity(sample), $"{name} has a non-finite sample");
            peak = Math.Max(peak, Math.Abs(sample));
        }
        Assert.InRange(peak, 0.5f, 1f);
    }

    static float Peak(SoundBuffer sound, float fromSeconds, float toSeconds) {
        float peak = 0;
        int from = (int)(fromSeconds * sound.SampleRate) * 2;
        int to = Math.Min(sound.Samples.Length, (int)(toSeconds * sound.SampleRate) * 2);
        for (int index = from; index < to; index++) {
            peak = Math.Max(peak, Math.Abs(sound.Samples[index]));
        }
        return peak;
    }
}
