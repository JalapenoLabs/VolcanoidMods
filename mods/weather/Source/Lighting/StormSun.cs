// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.Weather.Lighting;

/// <summary>
/// Dims and cools the island's sun under the storm's clouds, and softens its shadows.
///
/// Unlike the rest of the storm's light, the sun stays dimmed between frames rather than only while the game renders.
/// That is deliberate: Day/Night Cycle reads the sun's brightness each frame as "sky clarity" (how it notices eruption
/// ash), and dims its moonlight, stars and moon by it. Leaving the dimmed value in place lets a storm hide the night
/// sky too, with neither mod knowing about the other.
///
/// The game's eruption timeline rewrites the sun's intensity every LateUpdate. So this runs just before rendering
/// (Application.onBeforeRender, after every LateUpdate) and owns only the value it wrote: if the light no longer holds
/// that value, the game has set a new one, and that becomes the base to dim. The storm never compounds on itself, and
/// the game's own changes always come through.
/// </summary>
internal sealed class StormSun {
    readonly Light sun;
    float gameIntensity;
    Color gameColor;
    float gameShadowStrength;
    float writtenIntensity = float.NaN;
    Color writtenColor;
    float writtenShadowStrength = float.NaN;

    public StormSun(Light sun) {
        this.sun = sun;
    }

    /// <param name="darkness">The cloud darkness, already faded out underground.</param>
    /// <param name="cover">How much of the sky is cloud, already faded out underground.</param>
    public void Apply(float darkness, float cover) {
        if (sun.intensity != writtenIntensity) {
            gameIntensity = sun.intensity;
        }
        if (sun.color != writtenColor) {
            gameColor = sun.color;
        }
        if (sun.shadowStrength != writtenShadowStrength) {
            gameShadowStrength = sun.shadowStrength;
        }

        writtenIntensity = gameIntensity * (1 - StormLook.SunDimming * darkness);
        writtenColor = Color.Lerp(gameColor, gameColor * StormLook.OvercastTint, darkness);
        writtenShadowStrength = gameShadowStrength * (1 - StormLook.ShadowSoftening * cover);
        sun.intensity = writtenIntensity;
        sun.color = writtenColor;
        sun.shadowStrength = writtenShadowStrength;
    }

    /// <summary>Hands the sun back as the game last set it.</summary>
    public void Release() {
        if (sun == null) {
            return;
        }
        if (sun.intensity == writtenIntensity) {
            sun.intensity = gameIntensity;
        }
        if (sun.color == writtenColor) {
            sun.color = gameColor;
        }
        if (sun.shadowStrength == writtenShadowStrength) {
            sun.shadowStrength = gameShadowStrength;
        }
    }
}
