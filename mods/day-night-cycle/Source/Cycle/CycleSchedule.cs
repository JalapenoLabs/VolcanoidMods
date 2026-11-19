// Copyright © 2026 Jalapeno Labs

using System;

namespace JalapenoLabs.DayNightCycle.Cycle;

/// <summary>
/// The fixed day/night schedule, as a pure function of the synced clock.
///
/// Every copy of the mod computes the sky from the same number (seconds of UTC wall time), so two players with the
/// mod see the same sunset, the same moon and the same stars without exchanging a single message. That is also why
/// nothing here is configurable: a setting is something two players could disagree on.
///
/// Sunrise falls on cycle second 0 and sunset on <see cref="DayLengthSeconds"/>. Dawn and dusk are centred on those
/// two moments, so measured from the middle of each twilight the day lasts 30 minutes and the night 20.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public static class CycleSchedule {
    public const double DayLengthSeconds = 30 * 60;
    public const double NightLengthSeconds = 20 * 60;
    public const double CycleLengthSeconds = DayLengthSeconds + NightLengthSeconds;

    /// <summary>How long dawn and dusk each last. Half falls on either side of sunrise and sunset.</summary>
    public const double TwilightSeconds = 4 * 60;

    /// <summary>How far either side of sunrise and sunset the warm golden-hour light reaches.</summary>
    public const double GoldenHourSeconds = 4 * 60;

    /// <summary>
    /// Days in one round of moon phases, new moon to new moon. Real months take 29.5 days; eight keeps every phase
    /// within reach of a few play sessions while each night still shows a visibly different moon.
    /// </summary>
    public const int LunarCycleDays = 8;

    /// <summary>How long the moon stays up once risen. Matching the sun gives both the same path.</summary>
    public const double MoonUpSeconds = DayLengthSeconds;

    /// <summary>When the sun is highest, halfway between sunrise and sunset.</summary>
    public const double SolarNoonSeconds = DayLengthSeconds / 2;

    public static CycleSample Sample(double syncedSeconds) {
        double cycleNumber = Math.Floor(syncedSeconds / CycleLengthSeconds);
        double cycleSeconds = syncedSeconds - cycleNumber * CycleLengthSeconds;

        double sinceSunrise = SecondsSinceRise(cycleSeconds, 0, DayLengthSeconds);
        double halfTwilight = TwilightSeconds / 2;
        double dawn = SmoothStep(-halfTwilight, halfTwilight, sinceSunrise);
        double dusk = SmoothStep(-halfTwilight, halfTwilight, sinceSunrise - DayLengthSeconds);
        double daylight = dawn * (1 - dusk);

        double goldenHour = Math.Max(
            1 - SmoothStep(0, GoldenHourSeconds, Math.Abs(sinceSunrise)),
            1 - SmoothStep(0, GoldenHourSeconds, Math.Abs(sinceSunrise - DayLengthSeconds))
        );

        // Phase 0 is new moon and 0.5 full moon. The moon culminates later each day by its phase: alongside the sun at
        // new moon (so it is up only by day), opposite the sun at full moon (so it is up all night).
        double moonPhase = Fraction(syncedSeconds / (CycleLengthSeconds * LunarCycleDays));
        double moonTransitSeconds = SolarNoonSeconds + moonPhase * CycleLengthSeconds;
        double sinceMoonrise = SecondsSinceRise(cycleSeconds, moonTransitSeconds - MoonUpSeconds / 2, MoonUpSeconds);

        return new CycleSample(
            cycleNumber: (long)cycleNumber,
            cycleSeconds: cycleSeconds,
            daylight: daylight,
            goldenHour: goldenHour,
            sunPathTurns: PathTurns(sinceSunrise, DayLengthSeconds),
            moonPhase: moonPhase,
            moonIllumination: (1 - Math.Cos(2 * Math.PI * moonPhase)) / 2,
            moonPathTurns: PathTurns(sinceMoonrise, MoonUpSeconds),
            starTurns: Fraction(syncedSeconds / CycleLengthSeconds)
        );
    }

    /// <summary>
    /// The synced time of a given minute after sunrise, in the cycle containing <paramref name="aroundSeconds"/>.
    /// Given a moon phase, it instead picks the day within the next lunar cycle whose moon is closest to that phase,
    /// so a developer can ask for "midnight under a full moon". Phases step 1/8 a day, so the match is within 1/16.
    /// </summary>
    public static double FindMoment(double aroundSeconds, double minutesSinceSunrise, double? moonPhase) {
        double cycleStart = Math.Floor(aroundSeconds / CycleLengthSeconds) * CycleLengthSeconds;
        double offsetSeconds = minutesSinceSunrise * 60;
        if (!moonPhase.HasValue) {
            return cycleStart + offsetSeconds;
        }

        double bestMoment = cycleStart + offsetSeconds;
        double bestError = double.MaxValue;
        for (int day = 0; day < LunarCycleDays; day++) {
            double candidate = cycleStart + day * CycleLengthSeconds + offsetSeconds;
            double phaseError = Math.Abs(Sample(candidate).MoonPhase - moonPhase.Value);
            // Phase wraps at 1, so 0.95 is as close to 0 as 0.05 is.
            phaseError = Math.Min(phaseError, 1 - phaseError);
            if (phaseError < bestError) {
                bestMoment = candidate;
                bestError = phaseError;
            }
        }
        return bestMoment;
    }

    /// <summary>
    /// Seconds since a body last rose, wrapped so the value runs continuously through both its rise and its set:
    /// from minus half its time below the horizon, through 0 at rise, to its time up plus half its time below.
    /// </summary>
    static double SecondsSinceRise(double cycleSeconds, double riseSeconds, double upSeconds) {
        double halfDownSeconds = (CycleLengthSeconds - upSeconds) / 2;
        double sinceRise = cycleSeconds - riseSeconds;
        double wraps = Math.Floor((sinceRise + halfDownSeconds) / CycleLengthSeconds);
        return sinceRise - wraps * CycleLengthSeconds;
    }

    /// <summary>
    /// Converts time since rise into a position on the body's circular path, as a fraction of a full turn:
    /// 0 rising, 0.25 highest, 0.5 setting, 0.75 lowest. The time up spans the upper half of the circle and the time
    /// down the lower half, so the position never jumps even though the body is up longer than it is down.
    /// </summary>
    static double PathTurns(double sinceRise, double upSeconds) {
        double downSeconds = CycleLengthSeconds - upSeconds;
        if (sinceRise < 0) {
            return 1 + 0.5 * sinceRise / downSeconds;
        }
        if (sinceRise <= upSeconds) {
            return 0.5 * sinceRise / upSeconds;
        }
        return 0.5 + 0.5 * (sinceRise - upSeconds) / downSeconds;
    }

    static double SmoothStep(double edge0, double edge1, double value) {
        double progress = Math.Min(1, Math.Max(0, (value - edge0) / (edge1 - edge0)));
        return progress * progress * (3 - 2 * progress);
    }

    static double Fraction(double value) {
        return value - Math.Floor(value);
    }
}
