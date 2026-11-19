// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// The island's one directional light, carried round the clock: the game's own sun by day, a warm sun sinking to the
/// horizon at dusk, and the moon by night, shining from wherever it stands and as bright as its phase allows.
///
/// One light rather than a second "moon" light, because the game's shadows, interior masking and underground switch-off
/// (ZoneSun) are all built around this one.
/// </summary>
internal sealed class SunMoonLight : ICameraEffect {
    readonly Light light;
    float savedIntensity;
    Color savedColor;
    Quaternion savedRotation;
    bool isApplied;

    public SunMoonLight(Light light) {
        this.light = light;
    }

    public void Apply(in NightFrame frame, Camera camera, float surface) {
        savedIntensity = light.intensity;
        savedColor = light.color;
        savedRotation = light.transform.rotation;
        isApplied = true;

        // The game's own value already carries eruption ash; the moon's share is dimmed by the same clouds through
        // Moonlight's sky clarity.
        float sunIntensity = savedIntensity * frame.SunVisibility;
        float moonIntensity = NightLook.FullMoonIntensity * frame.Moonlight * frame.Darkness;
        float totalIntensity = sunIntensity + moonIntensity;
        if (totalIntensity <= 0.0001f) {
            light.intensity = 0;
            return;
        }

        // Blend direction and colour by each body's share of the light. They cross over while both are faint, so the
        // shadows swing round in the dusk rather than jumping.
        float sunShare = sunIntensity / totalIntensity;
        Vector3 towardsLight = Vector3.Slerp(frame.MoonDirection, frame.SunDirection, sunShare);
        Color sunColor = Color.Lerp(
            savedColor,
            savedColor * NightLook.GoldenHourTint,
            frame.GoldenHour * NightLook.GoldenHourStrength
        );

        light.intensity = totalIntensity;
        light.color = Color.Lerp(NightLook.MoonlightColor, sunColor, sunShare);
        light.transform.rotation = Quaternion.LookRotation(-towardsLight);
    }

    public void Restore() {
        if (!isApplied) {
            return;
        }
        light.intensity = savedIntensity;
        light.color = savedColor;
        light.transform.rotation = savedRotation;
        isApplied = false;
    }
}
