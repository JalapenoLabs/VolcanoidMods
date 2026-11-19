// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.DayNightCycle.Cycle;
using Xunit;

namespace JalapenoLabs.DayNightCycle.Tests;

public class CycleScheduleTests {
    const double Cycle = CycleSchedule.CycleLengthSeconds;
    const double Sunset = CycleSchedule.DayLengthSeconds;
    const double HalfTwilight = CycleSchedule.TwilightSeconds / 2;

    /// <summary>The start of a cycle in November 2026, so the tests run on realistic Unix-time magnitudes.</summary>
    static readonly double CycleStart = Math.Floor(1_795_000_000 / Cycle) * Cycle;

    static CycleSample At(double secondsIntoCycle, int cycleOffset = 0) {
        return CycleSchedule.Sample(CycleStart + cycleOffset * Cycle + secondsIntoCycle);
    }

    [Fact]
    public void Day_lasts_thirty_minutes_and_night_twenty() {
        Assert.Equal(30 * 60, CycleSchedule.DayLengthSeconds);
        Assert.Equal(20 * 60, CycleSchedule.NightLengthSeconds);
        Assert.Equal(50 * 60, CycleSchedule.CycleLengthSeconds);
    }

    [Fact]
    public void Daylight_is_full_at_noon_and_gone_at_midnight() {
        Assert.Equal(1, At(CycleSchedule.SolarNoonSeconds).Daylight, 9);
        Assert.Equal(0, At(Sunset + CycleSchedule.NightLengthSeconds / 2).Daylight, 9);
    }

    [Fact]
    public void Sunrise_and_sunset_fall_halfway_through_twilight() {
        Assert.Equal(0.5, At(0).Daylight, 9);
        Assert.Equal(0.5, At(Sunset).Daylight, 9);
    }

    [Fact]
    public void Twilight_starts_and_ends_two_minutes_either_side_of_sunrise_and_sunset() {
        Assert.Equal(1, At(Sunset - HalfTwilight - 1).Daylight, 9);
        Assert.Equal(0, At(Sunset + HalfTwilight + 1).Daylight, 9);
        Assert.Equal(0, At(Cycle - HalfTwilight - 1).Daylight, 9);
        Assert.Equal(1, At(HalfTwilight + 1).Daylight, 9);

        Assert.InRange(At(Sunset - HalfTwilight + 30).Daylight, 0.5, 0.99);
        Assert.InRange(At(Sunset + HalfTwilight - 30).Daylight, 0.01, 0.5);
    }

    [Fact]
    public void Daylight_never_jumps_anywhere_in_two_cycles() {
        // The steepest point of a 4-minute smoothstep changes by 1.5 / 240 per second.
        double previous = At(0).Daylight;
        for (double second = 1; second < 2 * Cycle; second++) {
            double current = At(second).Daylight;
            Assert.True(Math.Abs(current - previous) < 0.0065, $"Daylight jumped at second {second}");
            previous = current;
        }
    }

    [Fact]
    public void Golden_hour_peaks_at_sunrise_and_sunset_and_is_absent_at_noon_and_midnight() {
        Assert.Equal(1, At(0).GoldenHour, 9);
        Assert.Equal(1, At(Sunset).GoldenHour, 9);
        Assert.Equal(0, At(CycleSchedule.SolarNoonSeconds).GoldenHour, 9);
        Assert.Equal(0, At(Sunset + CycleSchedule.NightLengthSeconds / 2).GoldenHour, 9);
    }

    [Fact]
    public void Sun_is_above_the_horizon_exactly_while_it_is_day() {
        Assert.True(At(1).SunHeight > 0);
        Assert.True(At(CycleSchedule.SolarNoonSeconds).SunHeight > 0.999);
        Assert.True(At(Sunset - 1).SunHeight > 0);
        Assert.True(At(Sunset + 1).SunHeight < 0);
        Assert.True(At(Cycle - 1).SunHeight < 0);
    }

    [Fact]
    public void Sun_path_moves_continuously_through_rise_set_and_midnight() {
        double previous = At(0).SunPathTurns;
        for (double second = 1; second < 2 * Cycle; second++) {
            double current = At(second).SunPathTurns;
            double step = current - previous;
            // Wrapping from just under 1 back to 0 at sunrise is a continuation, not a jump.
            step -= Math.Round(step);
            Assert.True(Math.Abs(step) < 0.001, $"Sun path jumped at second {second}");
            previous = current;
        }
    }

    [Fact]
    public void Moon_steps_an_eighth_of_its_cycle_each_day() {
        double midnight = Sunset + CycleSchedule.NightLengthSeconds / 2;
        for (int day = 0; day < 16; day++) {
            double step = At(midnight, day + 1).MoonPhase - At(midnight, day).MoonPhase;
            step -= Math.Floor(step);
            Assert.Equal(1.0 / CycleSchedule.LunarCycleDays, step, 9);
        }
    }

    [Fact]
    public void Moon_shows_all_eight_phases_in_their_real_order_on_consecutive_nights() {
        string[] realOrder = {
            "New moon", "Waxing crescent", "First quarter", "Waxing gibbous",
            "Full moon", "Waning gibbous", "Last quarter", "Waning crescent",
        };
        double midnight = Sunset + CycleSchedule.NightLengthSeconds / 2;
        var seen = new List<string>();
        for (int day = 0; day < CycleSchedule.LunarCycleDays; day++) {
            seen.Add(At(midnight, day).MoonPhaseName);
        }

        int start = Array.IndexOf(realOrder, seen[0]);
        Assert.True(start >= 0);
        for (int day = 0; day < seen.Count; day++) {
            Assert.Equal(realOrder[(start + day) % realOrder.Length], seen[day]);
        }
    }

    [Fact]
    public void Lit_fraction_follows_the_phase() {
        Assert.Equal(0, IlluminationAtPhase(0), 6);
        Assert.Equal(0.5, IlluminationAtPhase(0.25), 6);
        Assert.Equal(1, IlluminationAtPhase(0.5), 6);
        Assert.Equal(0.5, IlluminationAtPhase(0.75), 6);
    }

    [Fact]
    public void Full_moon_stays_up_all_night() {
        double fullMoonNight = CycleSchedule.FindMoment(CycleStart, 40, moonPhase: 0.5);
        double nightStart = fullMoonNight - 10 * 60;
        for (double second = 0; second <= CycleSchedule.NightLengthSeconds; second += 10) {
            double moonHeight = CycleSchedule.Sample(nightStart + second).MoonHeight;
            Assert.True(moonHeight > 0, $"Moon down {second}s into the night");
        }
    }

    [Fact]
    public void New_moon_stays_below_the_horizon_through_the_dark_of_the_night() {
        // FindMoment lands within a sixteenth of new moon. That much either way lets the moon set as late as
        // 7 minutes before midnight, or rise as early as 7 minutes after it, so the 12 minutes around midnight are
        // moonless whichever night it picks.
        double newMoonMidnight = CycleSchedule.FindMoment(CycleStart, 40, moonPhase: 0);
        for (double second = -6 * 60; second <= 6 * 60; second += 10) {
            Assert.True(CycleSchedule.Sample(newMoonMidnight + second).MoonHeight < 0, $"Moon up at {second}s");
        }
    }

    [Fact]
    public void Two_players_whose_clocks_differ_by_a_second_see_the_same_sky() {
        for (double moment = 0; moment < Cycle; moment += 7) {
            CycleSample earlier = At(moment);
            CycleSample later = At(moment + 1);
            Assert.True(Math.Abs(earlier.Daylight - later.Daylight) < 0.0065);
            Assert.True(Math.Abs(earlier.MoonPhase - later.MoonPhase) < 1e-4);
            Assert.True(Math.Abs(earlier.StarTurns - later.StarTurns) < 1e-3);
        }
    }

    [Fact]
    public void Same_moment_always_gives_the_same_sky() {
        double moment = CycleStart + 1234.5;
        Assert.Equal(CycleSchedule.Sample(moment).MoonPathTurns, CycleSchedule.Sample(moment).MoonPathTurns);
        Assert.Equal(CycleSchedule.Sample(moment).Daylight, CycleSchedule.Sample(moment).Daylight);
    }

    [Fact]
    public void FindMoment_lands_on_the_requested_minute_after_sunrise() {
        CycleSample sample = CycleSchedule.Sample(CycleSchedule.FindMoment(CycleStart + 100, 40, null));
        Assert.Equal(40 * 60, sample.CycleSeconds, 6);
    }

    [Fact]
    public void FindMoment_with_a_moon_phase_picks_a_night_within_a_sixteenth_of_it() {
        foreach (double wanted in new[] { 0.0, 0.25, 0.5, 0.75 }) {
            CycleSample sample = CycleSchedule.Sample(CycleSchedule.FindMoment(CycleStart, 40, wanted));
            double error = Math.Abs(sample.MoonPhase - wanted);
            error = Math.Min(error, 1 - error);
            Assert.True(error <= 1.0 / 16 + 1e-9, $"Wanted phase {wanted}, got {sample.MoonPhase}");
            Assert.Equal(40 * 60, sample.CycleSeconds, 6);
        }
    }

    static double IlluminationAtPhase(double phase) {
        // Phase 0 falls at a multiple of the lunar cycle; step the clock to the requested phase from there.
        double lunarCycle = Cycle * CycleSchedule.LunarCycleDays;
        double newMoon = Math.Floor(CycleStart / lunarCycle) * lunarCycle;
        return CycleSchedule.Sample(newMoon + phase * lunarCycle).MoonIllumination;
    }
}
