// Copyright © 2026 Jalapeno Labs

using JalapenoLabs.DayNightCycle.Cycle;
using Xunit;

namespace JalapenoLabs.DayNightCycle.Tests;

/// <summary>
/// Regression tests for the night going black after every eruption. The eruption timeline holds the game's sun at 0.5
/// (of its usual 1.8) through the first part of Afterblast, and the night once read that as one clarity that also
/// dimmed the moon's glow in the fog and the horizon haze: the sky turned black from zenith to horizon wherever the
/// player stood, so it looked like a dark patch of the island.
/// </summary>
public class SkyVeilTests {
    const double ClearSun = 1.8;

    /// <summary>The sun through the heaviest ash, from the eruption timeline's 'Sun:m_Intensity' curve.</summary>
    const double AshSun = 0.5;

    [Fact]
    public void Eruption_ash_hides_the_stars_and_moon_but_keeps_the_night_air_glowing() {
        SkyVeil veil = SkyVeil.Measure(AshSun, ClearSun, cloudCover: 0);

        Assert.Equal(AshSun / ClearSun, veil.Clarity, 6);
        Assert.Equal(1, veil.GlowClarity, 6);
    }

    [Fact]
    public void A_clear_sky_veils_nothing() {
        // The game's noon sun is the float nearest 1.8, a hair under it.
        SkyVeil veil = SkyVeil.Measure(1.7999999523162842, ClearSun, cloudCover: 0);

        Assert.Equal(1, veil.Clarity, 6);
        Assert.Equal(1, veil.GlowClarity, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void Storm_cloud_dims_the_glow_as_well_as_the_sky(double cloudCover) {
        SkyVeil veil = SkyVeil.Measure(ClearSun, ClearSun, cloudCover);

        Assert.Equal(1 - cloudCover, veil.Clarity, 6);
        Assert.Equal(1 - cloudCover, veil.GlowClarity, 6);
    }

    [Fact]
    public void Ash_and_cloud_together_hide_the_sky_as_much_as_the_thicker_one_and_only_cloud_dims_the_glow() {
        SkyVeil thinCloud = SkyVeil.Measure(AshSun, ClearSun, cloudCover: 0.5);
        Assert.Equal(AshSun / ClearSun, thinCloud.Clarity, 6);
        Assert.Equal(0.5, thinCloud.GlowClarity, 6);

        SkyVeil thickCloud = SkyVeil.Measure(AshSun, ClearSun, cloudCover: 0.9);
        Assert.Equal(0.1, thickCloud.Clarity, 6);
        Assert.Equal(0.1, thickCloud.GlowClarity, 6);
    }

    [Fact]
    public void Readings_outside_their_range_are_clamped() {
        SkyVeil brightSun = SkyVeil.Measure(2.5, ClearSun, cloudCover: -0.3);
        Assert.Equal(1, brightSun.Clarity, 6);
        Assert.Equal(1, brightSun.GlowClarity, 6);

        SkyVeil overcast = SkyVeil.Measure(ClearSun, ClearSun, cloudCover: 1.4);
        Assert.Equal(0, overcast.Clarity, 6);
        Assert.Equal(0, overcast.GlowClarity, 6);
    }
}
