// Copyright © 2026 Jalapeno Labs

using JalapenoLabs.DayNightCycle.Cycle;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>This frame's sky, worked out once in Update and shared by every effect and camera.</summary>
internal readonly struct NightFrame {
    public readonly CycleSample Sample;

    /// <summary>0 in full day, 1 in full night.</summary>
    public readonly float Darkness;

    /// <summary>1 at sunrise and sunset, for the warm low-sun colouring.</summary>
    public readonly float GoldenHour;

    /// <summary>Where the sun's light comes from: the game's own sun by day, sinking to the horizon at dusk.</summary>
    public readonly Vector3 SunDirection;

    /// <summary>Where the sun really is on its path, below the horizon at night. Lights the moon's disc.</summary>
    public readonly Vector3 SunPathDirection;

    /// <summary>0 when the sun is below the horizon, rising to 1 once it is a few degrees up.</summary>
    public readonly float SunVisibility;

    public readonly Vector3 MoonDirection;

    /// <summary>0 when the moon is below the horizon, rising to 1 once it is a few degrees up.</summary>
    public readonly float MoonVisibility;

    /// <summary>How the star sphere is turned about the celestial pole.</summary>
    public readonly Quaternion StarRotation;

    /// <summary>
    /// 1 under a clear sky, less under eruption ash: the game's sun brightness against its usual noon value. Ash that
    /// dims the game's sun dims the moon and stars too, with no knowledge of eruptions needed.
    /// </summary>
    public readonly float SkyClarity;

    public NightFrame(
        CycleSample sample,
        Vector3 sunDirection,
        Vector3 sunPathDirection,
        Vector3 moonDirection,
        Quaternion starRotation,
        float skyClarity
    ) {
        Sample = sample;
        Darkness = (float)sample.Darkness;
        GoldenHour = (float)sample.GoldenHour;
        SunDirection = sunDirection;
        SunPathDirection = sunPathDirection;
        MoonDirection = moonDirection;
        StarRotation = starRotation;
        SkyClarity = skyClarity;
        SunVisibility = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.02f, 0.10f, sunDirection.y));
        MoonVisibility = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.02f, 0.12f, moonDirection.y));
    }

    public float MoonIllumination => (float)Sample.MoonIllumination;

    /// <summary>
    /// How much the moon lights the world, 0 to 1: its lit fraction (eased, so a half moon gives well under half a
    /// full moon's light, as in life), times how high it stands, times how clear the sky is.
    /// </summary>
    public float Moonlight => Mathf.Pow(MoonIllumination, 1.5f) * MoonVisibility * SkyClarity;

    /// <summary>
    /// How much of the day's ambient light the night keeps right now: starlight always, plus moonlight when the moon
    /// is up. Shared by every effect that dims indirect light.
    /// </summary>
    public float NightAmbientLevel => NightLook.MoonlessAmbient + NightLook.FullMoonExtraAmbient * Moonlight;

    /// <summary>
    /// The colour the night's fog and horizon haze aim for: moonlit or not, warmed at golden hour. The warmth drains
    /// as the dark deepens, so the afterglow fades to night blue a minute or two after sunset rather than lingering.
    /// </summary>
    public Color FogColor => Color.Lerp(
        Color.Lerp(NightLook.MoonlessFog, NightLook.MoonlitFog, Moonlight),
        NightLook.DuskFog,
        GoldenHour * Mathf.Sqrt(1 - Darkness)
    );
}
