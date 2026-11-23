// Copyright © 2026 Jalapeno Labs

using System;

namespace JalapenoLabs.DayNightCycle.Cycle;

/// <summary>
/// What stands between the island and the night sky: the game's eruption ash, and storm cloud from a weather mod.
///
/// Both hide what lies beyond the air: the stars, the moon's disc and its direct light. Only cloud also darkens the air
/// beneath it. The game paints its ash as bright dust that scatters the light it stops: by day the eruption drops the
/// sun to under a third while the sky turns a light grey and the ambient light holds. So under ash the moon's light
/// still glows in the night's haze and lights the world diffusely, and the night sky keeps its glow and its horizon.
/// Storm cloud is dark, and takes that glow with it.
///
/// No Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public readonly struct SkyVeil {
    /// <summary>
    /// How much of what lies beyond the air shows through, 0 to 1: the stars, the moon's disc and its direct light.
    /// </summary>
    public readonly double Clarity;

    /// <summary>How much of the moon's light still glows in the air and lights the world diffusely, 0 to 1.</summary>
    public readonly double GlowClarity;

    SkyVeil(double clarity, double glowClarity) {
        Clarity = clarity;
        GlowClarity = glowClarity;
    }

    /// <param name="sunIntensity">The game's sun as the eruption timeline sets it. Ash dims it.</param>
    /// <param name="clearSunIntensity">The game's sun under a clear sky.</param>
    /// <param name="cloudCover">The cloud cover a weather mod publishes, 0 clear to 1 overcast.</param>
    public static SkyVeil Measure(double sunIntensity, double clearSunIntensity, double cloudCover) {
        double ashClarity = Clamp01(sunIntensity / clearSunIntensity);
        double cloudClarity = 1 - Clamp01(cloudCover);
        return new SkyVeil(Math.Min(ashClarity, cloudClarity), cloudClarity);
    }

    static double Clamp01(double value) {
        return Math.Max(0, Math.Min(1, value));
    }
}
