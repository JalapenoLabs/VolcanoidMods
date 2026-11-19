// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.Weather.Lighting;

/// <summary>
/// Every brightness and colour of the storm, in one place for tuning. "Darkness" below is the forecast's cloud
/// darkness: about 0.35 for light rain, 0.75 for heavy rain and 0.9 for a thunderstorm at its height.
/// </summary>
internal static class StormLook {
    /// <summary>How much of the sun the darkest clouds take away. Rain is dark; the world shows it.</summary>
    public const float SunDimming = 0.88f;

    /// <summary>Overcast light comes from everywhere at once, so shadows soften as the clouds close.</summary>
    public const float ShadowSoftening = 0.75f;

    /// <summary>Storm light is cool: the sun's colour drifts towards this grey-blue under dark cloud.</summary>
    public static readonly Color OvercastTint = new Color(0.78f, 0.84f, 0.95f);

    /// <summary>How much of the ambient (sky) light the darkest clouds take away.</summary>
    public const float AmbientDimming = 0.55f;

    /// <summary>And how grey it turns: the blue of a clear sky gives way to the flat grey of cloud.</summary>
    public static readonly Color AmbientStormTint = new Color(0.86f, 0.9f, 0.96f);

    /// <summary>
    /// How much of the game's painted storm sky shows at the storm's height, before its darkness adds the rest: about
    /// two thirds for light rain, all of it for heavy rain and thunderstorms.
    /// </summary>
    public const float StormSkyShare = 0.6f;

    /// <summary>The storm sky is dimmed and cooled a little more. The skybox tint is neutral at 0.5 grey.</summary>
    public static readonly Color SkyStormTint = new Color(0.42f, 0.44f, 0.48f);
    public const float SkyStormExposure = 0.8f;

    /// <summary>
    /// Fog thickens in rain: a downpour hides the far side of the island. The fog colour's alpha is how much of it
    /// veils the skybox (the game's sky shader reads it), kept low so the painted storm clouds show through the haze.
    /// </summary>
    public const float LightRainFogDensity = 0.0038f;
    public const float HeavyRainFogDensity = 0.0058f;
    public static readonly Color LightRainFog = new Color(0.48f, 0.51f, 0.55f, 0.4f);
    /// <summary>
    /// A heavy storm's haze is as dark as its sky's horizon, so distant hills and sea fade into it rather than out of
    /// it, and veils a little more of the sky so the two meet without a seam.
    /// </summary>
    public static readonly Color HeavyRainFog = new Color(0.2f, 0.22f, 0.26f, 0.5f);

    /// <summary>Colour drains out of the world under heavy cloud (URP saturation, -100 to 100).</summary>
    public const float StormSaturation = -28;

    /// <summary>
    /// A lightning flash adds this much to the ambient and sky light at full strength, on top of the flash light and
    /// the bolt's own point light, so the whole landscape blinks white for an instant.
    /// </summary>
    public const float FlashAmbientBoost = 2.6f;
    public const float FlashSkyBoost = 3.5f;
    public static readonly Color FlashColor = new Color(0.82f, 0.86f, 1f);
}
