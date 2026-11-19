// Copyright © 2026 Jalapeno Labs

using System;

namespace JalapenoLabs.DayNightCycle.Cycle;

/// <summary>
/// Where the cycle stands at one moment: how light it is, and where the sun, moon and stars are.
/// Produced by <see cref="CycleSchedule.Sample"/>; every value is a pure function of the synced clock.
/// </summary>
public readonly struct CycleSample {
    static readonly string[] PhaseNames = {
        "New moon", "Waxing crescent", "First quarter", "Waxing gibbous",
        "Full moon", "Waning gibbous", "Last quarter", "Waning crescent",
    };

    /// <summary>How many whole day/night cycles have passed since the clock's epoch.</summary>
    public readonly long CycleNumber;

    /// <summary>
    /// Seconds into the current cycle: 0 at sunrise, <see cref="CycleSchedule.DayLengthSeconds"/> at sunset.
    /// </summary>
    public readonly double CycleSeconds;

    /// <summary>1 in full day, 0 in full night, easing between the two through dawn and dusk.</summary>
    public readonly double Daylight;

    /// <summary>1 at sunrise and sunset, fading to 0 a few minutes either side: the warm low-sun colouring.</summary>
    public readonly double GoldenHour;

    /// <summary>
    /// The sun's place on its path as a fraction of a turn: 0 rising, 0.25 highest, 0.5 setting, 0.75 lowest.
    /// </summary>
    public readonly double SunPathTurns;

    /// <summary>0 new moon, 0.25 first quarter, 0.5 full moon, 0.75 last quarter.</summary>
    public readonly double MoonPhase;

    /// <summary>The lit fraction of the moon's disc: 0 at new moon, 1 at full moon.</summary>
    public readonly double MoonIllumination;

    /// <summary>The moon's place on its path, measured like <see cref="SunPathTurns"/>.</summary>
    public readonly double MoonPathTurns;

    /// <summary>How far the star field has turned about the celestial pole, as a fraction of a turn.</summary>
    public readonly double StarTurns;

    public CycleSample(
        long cycleNumber,
        double cycleSeconds,
        double daylight,
        double goldenHour,
        double sunPathTurns,
        double moonPhase,
        double moonIllumination,
        double moonPathTurns,
        double starTurns
    ) {
        CycleNumber = cycleNumber;
        CycleSeconds = cycleSeconds;
        Daylight = daylight;
        GoldenHour = goldenHour;
        SunPathTurns = sunPathTurns;
        MoonPhase = moonPhase;
        MoonIllumination = moonIllumination;
        MoonPathTurns = moonPathTurns;
        StarTurns = starTurns;
    }

    /// <summary>The inverse of <see cref="Daylight"/>: how deep into the night the cycle is.</summary>
    public double Darkness => 1 - Daylight;

    /// <summary>True from new moon to full moon, when the lit side grows.</summary>
    public bool IsWaxing => MoonPhase < 0.5;

    /// <summary>The sine of the sun's angle around its path: positive while it is above the horizon.</summary>
    public double SunHeight => Math.Sin(2 * Math.PI * SunPathTurns);

    /// <summary>The sine of the moon's angle around its path: positive while it is above the horizon.</summary>
    public double MoonHeight => Math.Sin(2 * Math.PI * MoonPathTurns);

    /// <summary>The traditional name of the nearest of the eight moon phases, for logs and the README.</summary>
    public string MoonPhaseName => PhaseNames[(int)Math.Round(MoonPhase * PhaseNames.Length) % PhaseNames.Length];
}
