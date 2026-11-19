// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// The art direction of the night in one place: every brightness and colour the effects aim for.
///
/// The night darkens the light falling on the world (sun, sky, ambient and reflections), never the finished image.
/// That keeps lamps, the flashlight, lava and the glow of cogs and drillships at full strength, so they are what the
/// player navigates by. Brightness levels are fractions of the game's own daytime values unless noted.
/// </summary>
internal static class NightLook {
    // ── Directional light ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The game's sun at noon, in Unity light units. Sky clarity compares the sun against it.</summary>
    public const float GameSunIntensity = 1.8f;

    /// <summary>A full moon at its highest, in Unity light units: soft shadows, too little to work by.</summary>
    public const float FullMoonIntensity = 0.3f;

    public static readonly Color MoonlightColor = new Color(0.62f, 0.72f, 1.0f);

    /// <summary>What the sun's colour is multiplied towards at sunrise and sunset.</summary>
    public static readonly Color GoldenHourTint = new Color(1.0f, 0.62f, 0.36f);

    /// <summary>How far the sun's colour moves towards <see cref="GoldenHourTint"/> at golden hour's peak.</summary>
    public const float GoldenHourStrength = 0.85f;

    // ── Ambient light and reflections ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ambient light a moonless night keeps: starlight and airglow. Enough to make out the ground nearby and
    /// hills against the sky, the base light that keeps the surface playable without a lamp.
    /// </summary>
    public const float MoonlessAmbient = 0.13f;

    /// <summary>The ambient light a full moon high in the sky adds to <see cref="MoonlessAmbient"/>.</summary>
    public const float FullMoonExtraAmbient = 0.10f;

    /// <summary>Night light is bluer than daylight; the ambient light's colour channels are scaled by this.</summary>
    public static readonly Color AmbientTint = new Color(0.70f, 0.82f, 1.0f);

    // ── Day sky (the game's skybox, and the ocean that mirrors it) ─────────────────────────────────────────────────

    /// <summary>How much of the day sky's brightness survives into full night: a faint glow under the stars.</summary>
    public const float NightSkyExposure = 0.035f;

    /// <summary>The skybox tint at night. The game's skybox treats 0.5 grey as "no tint".</summary>
    public static readonly Color NightSkyTint = new Color(0.30f, 0.38f, 0.60f, 0.5f);

    /// <summary>The skybox tint at the peak of golden hour, warming the whole sky at sunrise and sunset.</summary>
    public static readonly Color GoldenSkyTint = new Color(0.66f, 0.46f, 0.38f, 0.5f);

    /// <summary>How far the sky moves towards <see cref="GoldenSkyTint"/> at the peak of golden hour.</summary>
    public const float GoldenSkyStrength = 0.75f;

    // ── Fog and grading (a volume layered over the game's own) ─────────────────────────────────────────────────────

    /// <summary>
    /// Fog colour on a moonless night. Alpha is how much the fog covers the sky, as the game's fog defines it. It
    /// reaches well above the horizon, so it stays below the day's 0.69: enough to melt distant hills into the dark,
    /// little enough for the darkened sky and its clouds to show through.
    /// </summary>
    public static readonly Color MoonlessFog = new Color(0.010f, 0.014f, 0.026f, 0.6f);

    /// <summary>Fog colour under a full moon high in the sky: moonlit haze.</summary>
    public static readonly Color MoonlitFog = new Color(0.040f, 0.052f, 0.085f, 0.6f);

    /// <summary>Fog colour at golden hour's peak, blended in by the same weight that brings in the night.</summary>
    public static readonly Color DuskFog = new Color(0.32f, 0.20f, 0.16f, 0.5f);

    /// <summary>Colour saturation at night (URP's -100 to 100 scale). Eyes lose colour in the dark.</summary>
    public const float NightSaturation = -18;

    // ── Stars, Milky Way and moon ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>How many stars the sky holds. Most are faint; a few dozen are bright enough to twinkle.</summary>
    public const int StarCount = 5500;

    /// <summary>How many of the brightest stars twinkle.</summary>
    public const int TwinklingStarCount = 160;

    /// <summary>Overall brightness of the star field and Milky Way. Above 1 lets the brightest stars bloom.</summary>
    public const float StarBrightness = 1.5f;

    /// <summary>The moon's apparent diameter. The real one is half a degree; enlarged so it reads on screen.</summary>
    public const float MoonDiameterDegrees = 4.2f;

    /// <summary>The moon's brightness on screen. Above 1 lets it bloom.</summary>
    public const float MoonBrightness = 1.7f;

    /// <summary>How bright the moon's unlit side glows from earthshine, as a fraction of full sunlight.</summary>
    public const float Earthshine = 0.045f;

    /// <summary>The glow around the moon, as a multiple of its diameter.</summary>
    public const float MoonHaloScale = 9;
}
