// Copyright © 2026 Jalapeno Labs

using System;
using UnityEngine;
using WaterSystem;
// The game has its own type named Data, which hides the water system's.
using WaveSettings = WaterSystem.Data.BasicWaves;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The sea rises with the storm: taller, longer swells that run faster before the wind, whitecaps of foam and a choppy
/// surface, settling again as the storm passes.
///
/// The island's ocean is Boat Attack's water system (WaterSystem.Ocean). Its waves are generated from a few basic
/// settings (amplitude, wavelength, direction) whenever it is initialised, so the storm edits those settings and
/// initialises it again. That rebuilds its wave buffers and mesh, so it happens in coarse steps as the storm builds,
/// not every frame. Nothing in the game reads the waves' height (the drillship runs on its track, nothing floats, and
/// drowning is a fixed zone), so rough seas are only seen, never felt. The calm sea's settings come back when the storm
/// is over or the island unloads.
/// </summary>
internal sealed class StormSea : IDisposable {
    /// <summary>How much taller the waves stand at a storm's height.</summary>
    const float HeightGain = 2.2f;

    /// <summary>
    /// How much longer they grow: a longer wave also travels faster (deep-water waves run at the square root of their
    /// length), so storm swells roll in quicker as well as bigger.
    /// </summary>
    const float LengthGain = 0.8f;

    /// <summary>The sea is re-initialised each time the storm's strength moves this far.</summary>
    const float Step = 0.1f;

    readonly Ocean ocean;
    readonly float calmAmplitude;
    readonly float calmWavelength;
    readonly float calmDirection;
    readonly float calmFoam;
    readonly float calmChop;
    float appliedStrength;

    public StormSea() {
        ocean = Ocean.Instance;
        if (ocean == null || ocean.settingsData?._basicWaveSettings == null) {
            throw new InvalidOperationException("the island has no WaterSystem ocean to roughen");
        }
        WaveSettings waves = ocean.settingsData._basicWaveSettings;
        calmAmplitude = waves.amplitude;
        calmWavelength = waves.wavelength;
        calmDirection = waves.direction;
        calmFoam = ocean.settingsData._foamIntensity;
        calmChop = ocean.settingsData._microWaveIntensity;
    }

    /// <param name="strength">How rough the storm makes the sea, 0 (calm) to 1 (a thunderstorm's gale).</param>
    /// <param name="windHeading">Where the wind blows towards, in radians clockwise from north.</param>
    public void Update(float strength, float windHeading) {
        float stepped = Mathf.Round(strength / Step) * Step;
        if (Mathf.Approximately(stepped, appliedStrength)) {
            return;
        }
        appliedStrength = stepped;
        WaveSettings waves = ocean.settingsData._basicWaveSettings;
        waves.amplitude = calmAmplitude * (1 + HeightGain * stepped);
        waves.wavelength = calmWavelength * (1 + LengthGain * stepped);
        // The swell swings round to run before the wind as the storm takes hold.
        float windDegrees = windHeading * Mathf.Rad2Deg;
        waves.direction = Mathf.LerpAngle(calmDirection, windDegrees, stepped);
        ocean.settingsData._foamIntensity = Mathf.Lerp(calmFoam, 1, stepped);
        ocean.settingsData._microWaveIntensity = Mathf.Lerp(calmChop, 1, stepped);
        ocean.Init();
    }

    public void Dispose() {
        if (ocean == null) {
            return;
        }
        WaveSettings waves = ocean.settingsData._basicWaveSettings;
        waves.amplitude = calmAmplitude;
        waves.wavelength = calmWavelength;
        waves.direction = calmDirection;
        ocean.settingsData._foamIntensity = calmFoam;
        ocean.settingsData._microWaveIntensity = calmChop;
        if (appliedStrength != 0) {
            ocean.Init();
        }
    }
}
