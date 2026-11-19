// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.Linq;
using JalapenoLabs.Weather.Forecast;
using Xunit;

namespace JalapenoLabs.Weather.Tests;

public class ForecastTests {
    const double Hour = StormSchedule.SlotSeconds;

    /// <summary>An hour in November 2026, so the tests run on realistic Unix-time magnitudes.</summary>
    static readonly long FirstSlot = (long)Math.Floor(1_795_000_000 / Hour);

    static readonly StrikeArea Island = new StrikeArea(-900, -800, 900, 900);

    static IEnumerable<Storm> Storms(int count) {
        return Enumerable.Range(0, count).Select(offset => StormSchedule.StormInSlot(FirstSlot + offset));
    }

    [Fact]
    public void Every_hour_has_one_storm_that_lives_and_dies_inside_it() {
        foreach (Storm storm in Storms(500)) {
            double hourStart = storm.Slot * Hour;
            Assert.InRange(storm.CloudsStart, hourStart + StormSchedule.EarliestStartSeconds, hourStart + Hour);
            Assert.InRange(storm.CloudsEnd, storm.CloudsStart, hourStart + Hour);
        }
    }

    [Fact]
    public void Clouds_take_two_to_three_minutes_to_roll_in_and_clear_and_it_rains_for_ten() {
        foreach (Storm storm in Storms(50)) {
            Assert.InRange(storm.RainStart - storm.CloudsStart, 120, 180);
            Assert.InRange(storm.CloudsEnd - storm.RainEnd, 120, 180);
            Assert.Equal(10 * 60, storm.RainEnd - storm.RainStart, 6);
        }
    }

    [Fact]
    public void The_same_hour_always_brings_the_same_storm() {
        Storm first = StormSchedule.StormInSlot(FirstSlot + 7);
        Storm second = StormSchedule.StormInSlot(FirstSlot + 7);
        Assert.Equal(first.Kind, second.Kind);
        Assert.Equal(first.CloudsStart, second.CloudsStart);
        Assert.Equal(first.Seed, second.Seed);
    }

    [Fact]
    public void A_third_of_storms_are_thunderstorms_and_the_rest_split_light_and_heavy() {
        Storm[] storms = Storms(6000).ToArray();
        double thunder = storms.Count(storm => storm.Kind == StormKind.Thunderstorm) / (double)storms.Length;
        double heavy = storms.Count(storm => storm.Kind == StormKind.HeavyRain) / (double)storms.Length;
        double light = storms.Count(storm => storm.Kind == StormKind.LightRain) / (double)storms.Length;
        Assert.InRange(thunder, 0.30, 0.36);
        Assert.InRange(heavy, 0.30, 0.36);
        Assert.InRange(light, 0.30, 0.36);
    }

    [Fact]
    public void Storms_come_almost_on_a_timer() {
        Storm[] storms = Storms(200).ToArray();
        for (int index = 1; index < storms.Length; index++) {
            double gap = storms[index].RainStart - storms[index - 1].RainStart;
            Assert.InRange(gap, 35 * 60, 85 * 60);
        }
    }

    [Fact]
    public void Skies_are_clear_and_dry_between_storms() {
        foreach (Storm storm in Storms(50)) {
            WeatherSample before = StormSchedule.Sample(storm.CloudsStart - 1);
            WeatherSample after = StormSchedule.Sample(storm.CloudsEnd + 1);
            Assert.Equal(0, before.Cover);
            Assert.Equal(0, before.Rain);
            Assert.Equal(0, after.Cover);
            Assert.Equal(0, after.Rain);
        }
    }

    [Fact]
    public void Clouds_close_in_before_the_rain_and_rain_stops_before_they_clear() {
        foreach (Storm storm in Storms(50)) {
            WeatherSample rollingIn = StormSchedule.Sample(storm.CloudsStart + StormSchedule.RollInSeconds / 2);
            WeatherSample clearing = StormSchedule.Sample(storm.RainEnd + StormSchedule.ClearingSeconds / 2);
            Assert.InRange(rollingIn.Cover, 0.2f, 0.8f);
            Assert.Equal(0, rollingIn.Rain);
            Assert.InRange(clearing.Cover, 0.2f, 0.8f);
            Assert.Equal(0, clearing.Rain);
        }
    }

    [Fact]
    public void Light_rain_is_paler_and_gentler_than_heavy_rain() {
        Storm light = StormSchedule.NextStormOfKind(FirstSlot * Hour, StormKind.LightRain);
        Storm heavy = StormSchedule.NextStormOfKind(FirstSlot * Hour, StormKind.HeavyRain);
        WeatherSample lightPeak = StormSchedule.Sample(StormSchedule.MomentIn(light, 0.5));
        WeatherSample heavyPeak = StormSchedule.Sample(StormSchedule.MomentIn(heavy, 0.5));
        Assert.True(lightPeak.Darkness < heavyPeak.Darkness);
        Assert.True(lightPeak.Rain < heavyPeak.Rain * 0.6f);
        Assert.True(StormSchedule.Sample(light.RainEnd).Wetness < StormSchedule.Sample(heavy.RainEnd).Wetness);
    }

    [Fact]
    public void Only_thunderstorms_are_electric_and_they_rain_heavily() {
        foreach (Storm storm in Storms(60)) {
            WeatherSample peak = StormSchedule.Sample(StormSchedule.MomentIn(storm, 0.5));
            Assert.Equal(storm.Kind == StormKind.Thunderstorm, peak.Electric > 0);
            if (storm.Kind == StormKind.Thunderstorm) {
                Assert.True(peak.Rain > 0.75f);
            }
        }
    }

    [Fact]
    public void Puddles_fill_while_it_rains_and_dry_after() {
        Storm storm = StormSchedule.NextStormOfKind(FirstSlot * Hour, StormKind.HeavyRain);
        float early = StormSchedule.Sample(StormSchedule.MomentIn(storm, 0.1)).Wetness;
        float late = StormSchedule.Sample(StormSchedule.MomentIn(storm, 0.95)).Wetness;
        float drying = StormSchedule.Sample(storm.CloudsEnd + 300).Wetness;
        // The last hour's puddles have all but dried by the time the next storm arrives.
        Assert.True(StormSchedule.Sample(storm.RainStart - 1).Wetness < 0.001f);
        Assert.True(early < late);
        Assert.True(drying < late);
        Assert.True(drying > 0);
    }

    [Fact]
    public void Lightning_strikes_only_in_thunderstorms_while_it_rains() {
        foreach (Storm storm in Storms(60)) {
            List<Strike> strikes = LightningSchedule.StrikesOf(storm, Island);
            if (storm.Kind != StormKind.Thunderstorm) {
                Assert.Empty(strikes);
                continue;
            }
            Assert.InRange(strikes.Count, 60, 200);
            foreach (Strike strike in strikes) {
                Assert.InRange(strike.Time, storm.RainStart, storm.RainEnd);
                Assert.InRange(strike.X, Island.MinX, Island.MaxX);
                Assert.InRange(strike.Z, Island.MinZ, Island.MaxZ);
                Assert.InRange(strike.Power, 0.5f, 1f);
            }
            Assert.Equal(strikes.OrderBy(strike => strike.Time), strikes);
        }
    }

    [Fact]
    public void Every_player_works_out_the_same_strikes() {
        Storm storm = StormSchedule.NextStormOfKind(FirstSlot * Hour, StormKind.Thunderstorm);
        List<Strike> first = LightningSchedule.StrikesOf(storm, Island);
        List<Strike> second = LightningSchedule.StrikesOf(storm, Island);
        Assert.Equal(first.Count, second.Count);
        for (int index = 0; index < first.Count; index++) {
            Assert.Equal(first[index].Time, second[index].Time);
            Assert.Equal(first[index].X, second[index].X);
            Assert.Equal(first[index].Seed, second[index].Seed);
        }
    }

    [Fact]
    public void Most_strikes_reach_the_ground_and_land_all_over_the_island() {
        Storm storm = StormSchedule.NextStormOfKind(FirstSlot * Hour, StormKind.Thunderstorm);
        List<Strike> strikes = LightningSchedule.StrikesOf(storm, Island);
        double groundShare = strikes.Count(strike => strike.ReachesGround) / (double)strikes.Count;
        Assert.InRange(groundShare, 0.4, 0.8);
        // Each quarter of the island sees some lightning.
        Assert.Contains(strikes, strike => strike.X < 0 && strike.Z < 0);
        Assert.Contains(strikes, strike => strike.X > 0 && strike.Z < 0);
        Assert.Contains(strikes, strike => strike.X < 0 && strike.Z > 0);
        Assert.Contains(strikes, strike => strike.X > 0 && strike.Z > 0);
    }

    [Fact]
    public void A_capture_moment_lands_where_asked() {
        Storm storm = StormSchedule.StormInSlot(FirstSlot);
        Assert.Equal(storm.CloudsStart, StormSchedule.MomentIn(storm, -1), 6);
        Assert.Equal(storm.RainStart, StormSchedule.MomentIn(storm, 0), 6);
        Assert.Equal(storm.RainEnd, StormSchedule.MomentIn(storm, 1), 6);
        Assert.Equal(storm.CloudsEnd, StormSchedule.MomentIn(storm, 2), 6);
    }
}
