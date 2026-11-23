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
    /// Eruption ash and storm cloud. Read from the game's sun brightness and a weather mod's cloud cover, so the night
    /// follows eruptions with no knowledge of them.
    /// </summary>
    public readonly SkyVeil Veil;

    public NightFrame(
        CycleSample sample,
        Vector3 sunDirection,
        Vector3 sunPathDirection,
        Vector3 moonDirection,
        Quaternion starRotation,
        SkyVeil veil
    ) {
        Sample = sample;
        Darkness = (float)sample.Darkness;
        GoldenHour = (float)sample.GoldenHour;
        SunDirection = sunDirection;
        SunPathDirection = sunPathDirection;
        MoonDirection = moonDirection;
        StarRotation = starRotation;
        Veil = veil;
        SunVisibility = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.02f, 0.10f, sunDirection.y));
        MoonVisibility = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.02f, 0.12f, moonDirection.y));
    }

    public float MoonIllumination => (float)Sample.MoonIllumination;

    /// <summary>
    /// 1 under a clear sky, less under ash or cloud: how much of the stars, the moon's disc and its direct light shows.
    /// </summary>
    public float SkyClarity => (float)Veil.Clarity;

    /// <summary>
    /// The moon's light before anything veils it, 0 to 1: its lit fraction (eased, so a half moon gives well under half
    /// a full moon's light, as in life), times how high it stands.
    /// </summary>
    float MoonShare => Mathf.Pow(MoonIllumination, 1.5f) * MoonVisibility;

    /// <summary>How much the moon lights the world directly, 0 to 1: dimmed by ash and cloud alike.</summary>
    public float Moonlight => MoonShare * SkyClarity;

    /// <summary>
    /// How much the moon's light glows in the air and lights the world diffusely, 0 to 1. Ash scatters the moonlight
    /// it stops rather than swallowing it (see <see cref="SkyVeil"/>), so only storm cloud dims this.
    /// </summary>
    public float MoonGlow => MoonShare * (float)Veil.GlowClarity;

    /// <summary>
    /// How much of the day's ambient light the night keeps right now: starlight always, plus the moon's glow when it is
    /// up. Shared by every effect that dims indirect light.
    /// </summary>
    public float NightAmbientLevel => NightLook.MoonlessAmbient + NightLook.FullMoonExtraAmbient * MoonGlow;

    /// <summary>
    /// The colour the night's fog and horizon haze aim for: moonlit or not, warmed at golden hour. The warmth drains
    /// as the dark deepens, so the afterglow fades to night blue a minute or two after sunset rather than lingering.
    /// </summary>
    public Color FogColor => Color.Lerp(
        Color.Lerp(NightLook.MoonlessFog, NightLook.MoonlitFog, MoonGlow),
        NightLook.DuskFog,
        GoldenHour * Mathf.Sqrt(1 - Darkness)
    );
}
