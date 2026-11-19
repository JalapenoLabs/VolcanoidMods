// Copyright © 2026 Jalapeno Labs

using UnityEngine;
using UnityEngine.Rendering;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// Dims the environment light: the ambient probe (what lights the terrain and anything outside the light-probe grid)
/// and the default skybox reflection. Both are scene-wide settings the game expects to stay at their daytime values,
/// so they only change while a surface camera renders.
/// </summary>
internal sealed class AmbientLight : ICameraEffect {
    SphericalHarmonicsL2 savedProbe;
    float savedReflectionIntensity;
    bool isApplied;

    public void Apply(in NightFrame frame, Camera camera, float surface) {
        float darkness = frame.Darkness * surface;
        if (darkness <= 0) {
            return;
        }
        savedProbe = ReadStoredProbe();
        savedReflectionIntensity = RenderSettings.reflectionIntensity;
        isApplied = true;

        float nightLevel = frame.NightAmbientLevel;
        Color channelScale = Color.Lerp(Color.white, NightLook.AmbientTint * nightLevel, darkness);

        SphericalHarmonicsL2 probe = savedProbe;
        for (int channel = 0; channel < 3; channel++) {
            for (int coefficient = 0; coefficient < 9; coefficient++) {
                probe[channel, coefficient] *= channelScale[channel];
            }
        }
        RenderSettings.ambientProbe = probe;
        RenderSettings.reflectionIntensity = savedReflectionIntensity * Mathf.Lerp(1, nightLevel, darkness);
    }

    public void Restore() {
        if (!isApplied) {
            return;
        }
        RenderSettings.ambientProbe = savedProbe;
        RenderSettings.reflectionIntensity = savedReflectionIntensity;
        isApplied = false;
    }

    /// <summary>
    /// The ambient probe as Unity stores it. The <c>RenderSettings.ambientProbe</c> getter returns the stored probe
    /// already multiplied by the ambient intensity (in linear space: the island's 0.66 reads back as 0.39), while the
    /// setter stores exactly what it is given. Writing back a value straight from the getter therefore darkens the
    /// ambient light by that factor on every write, and within seconds of nightfall it reaches black and stays there.
    /// Reading with the intensity at 1 returns the stored probe itself, so writes round-trip exactly.
    /// </summary>
    static SphericalHarmonicsL2 ReadStoredProbe() {
        float intensity = RenderSettings.ambientIntensity;
        RenderSettings.ambientIntensity = 1;
        SphericalHarmonicsL2 stored = RenderSettings.ambientProbe;
        RenderSettings.ambientIntensity = intensity;
        return stored;
    }
}
