// Copyright © 2026 Jalapeno Labs

using System;

namespace JalapenoLabs.Weather.Forecast;

/// <summary>
/// The weather at one moment: how cloudy, dark, wet and windy it is, and how electric the storm is.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public readonly struct WeatherSample {
    /// <summary>This hour's storm, whether it is coming, under way or past.</summary>
    public readonly Storm Storm;
    /// <summary>How much of the sky is cloud, 0 to 1.</summary>
    public readonly float Cover;
    /// <summary>How dark the clouds are, 0 (white) to 1 (near black). Already scaled by <see cref="Cover"/>.</summary>
    public readonly float Darkness;
    /// <summary>How hard it is raining, 0 to 1.</summary>
    public readonly float Rain;
    /// <summary>How deep the puddles are, 0 to 1. They fill while it rains and dry slowly after.</summary>
    public readonly float Wetness;
    /// <summary>How active the lightning is, 0 to 1. Only thunderstorms have any.</summary>
    public readonly float Electric;
    /// <summary>Wind speed in metres per second.</summary>
    public readonly float WindSpeed;
    /// <summary>Where the wind blows towards, in radians clockwise from north (+z).</summary>
    public readonly float WindHeading;

    public WeatherSample(
        Storm storm,
        float cover,
        float darkness,
        float rain,
        float wetness,
        float electric,
        float windSpeed,
        float windHeading
    ) {
        Storm = storm;
        Cover = cover;
        Darkness = darkness;
        Rain = rain;
        Wetness = wetness;
        Electric = electric;
        WindSpeed = windSpeed;
        WindHeading = windHeading;
    }
}

/// <summary>
/// The fixed weather schedule, as a pure function of the synced clock.
///
/// Every copy of the mod computes the weather from the same number (seconds of UTC wall time, checked against Steam),
/// so two players with the mod see the same storm at the same moment without exchanging a single message. Nothing is
/// configurable, because a setting is something two players could disagree on.
///
/// Each UTC hour has exactly one storm, "almost on a timer": its clouds start rolling in somewhere between 10 and 35
/// minutes past the hour, take two and a half minutes to close in and darken, rain for ten minutes, and take two and a
/// half minutes to clear. A third of storms are thunderstorms, a third heavy rain and a third light rain. A storm's
/// whole life fits inside its hour, so a moment only ever belongs to one storm.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public static class StormSchedule {
    public const double SlotSeconds = 60 * 60;
    public const double RollInSeconds = 150;
    public const double RainSeconds = 10 * 60;
    public const double ClearingSeconds = 150;
    public const double EarliestStartSeconds = 10 * 60;
    public const double LatestStartSeconds = 35 * 60;

    /// <summary>How long rain takes to build to its full strength, and to die away at the end.</summary>
    public const double RainRampSeconds = 45;

    /// <summary>How long after the rain starts, and before it stops, the lightning holds off.</summary>
    public const double LightningMarginSeconds = 30;

    /// <summary>Puddles fill towards their depth with this time constant while it rains.</summary>
    const double PuddleFillSeconds = 180;

    /// <summary>And dry with this one once it stops.</summary>
    const double PuddleDrySeconds = 240;

    public static Storm StormInSlot(long slot) {
        var random = new StormRandom(StormRandom.Mix((ulong)slot));
        double cloudsStart = slot * SlotSeconds + random.Range(EarliestStartSeconds, LatestStartSeconds);
        double rainStart = cloudsStart + RollInSeconds;
        double rainEnd = rainStart + RainSeconds;

        float kindRoll = random.NextFloat();
        StormKind kind;
        if (kindRoll < 1 / 3f) {
            kind = StormKind.Thunderstorm;
        } else if (kindRoll < 2 / 3f) {
            kind = StormKind.HeavyRain;
        } else {
            kind = StormKind.LightRain;
        }

        ulong seed = random.NextULong();
        float windHeading = random.Range(0, 2 * (float)Math.PI);
        return new Storm(slot, kind, cloudsStart, rainStart, rainEnd, rainEnd + ClearingSeconds, seed, windHeading);
    }

    public static Storm StormAt(double syncedSeconds) {
        return StormInSlot((long)Math.Floor(syncedSeconds / SlotSeconds));
    }

    public static WeatherSample Sample(double syncedSeconds) {
        Storm storm = StormAt(syncedSeconds);
        StormProfile profile = storm.Profile;

        // The clouds close in over the roll-in and open again over the clearing; they darken late in the roll-in, so
        // white cloud gathers first and then turns a heavy grey.
        double gathering = SmoothStep(storm.CloudsStart, storm.RainStart, syncedSeconds)
            * (1 - SmoothStep(storm.RainEnd, storm.CloudsEnd, syncedSeconds));
        double darkening = SmoothStep(0.35, 1, gathering);

        double raining = SmoothStep(storm.RainStart, storm.RainStart + RainRampSeconds, syncedSeconds)
            * (1 - SmoothStep(storm.RainEnd - RainRampSeconds, storm.RainEnd, syncedSeconds));
        double gusts = 1 + 0.18 * StormRandom.Wander(storm.Seed, syncedSeconds, 25);

        double electric = 0;
        if (profile.HasLightning) {
            electric = SmoothStep(storm.RainStart, storm.RainStart + LightningMarginSeconds, syncedSeconds)
                * (1 - SmoothStep(storm.RainEnd - LightningMarginSeconds, storm.RainEnd, syncedSeconds));
        }

        double windSpeed = profile.Wind * (0.25 + 0.75 * gathering)
            * (1 + 0.25 * StormRandom.Wander(storm.Seed + 1, syncedSeconds, 12));
        double windHeading = storm.WindHeading + 0.35 * StormRandom.Wander(storm.Seed + 2, syncedSeconds, 90);

        // The previous hour's puddles may not have dried completely.
        double wetness = Math.Max(Wetness(storm, syncedSeconds), Wetness(StormInSlot(storm.Slot - 1), syncedSeconds));

        return new WeatherSample(
            storm,
            (float)(profile.Cover * gathering),
            (float)(profile.Darkness * darkening),
            (float)Clamp01(profile.Rain * raining * gusts),
            (float)wetness,
            (float)electric,
            (float)windSpeed,
            (float)windHeading
        );
    }

    /// <summary>
    /// A moment within a storm, for developer captures: progress -1 is when its clouds start rolling in, 0 when the
    /// rain starts, 1 when it stops and 2 when the sky is clear again.
    /// </summary>
    public static double MomentIn(Storm storm, double progress) {
        if (progress < 0) {
            return storm.RainStart + progress * RollInSeconds;
        }
        if (progress <= 1) {
            return storm.RainStart + progress * RainSeconds;
        }
        return storm.RainEnd + (progress - 1) * ClearingSeconds;
    }

    /// <summary>The first storm of a kind in the hours from <paramref name="syncedSeconds"/> on.</summary>
    public static Storm NextStormOfKind(double syncedSeconds, StormKind kind) {
        long slot = (long)Math.Floor(syncedSeconds / SlotSeconds);
        while (true) {
            Storm storm = StormInSlot(slot);
            if (storm.Kind == kind) {
                return storm;
            }
            slot++;
        }
    }

    /// <summary>
    /// How deep one storm's puddles are: filling towards the storm's depth while it rains, drying after. A closed
    /// form of the time integral, so every player computes the same puddles whenever they joined.
    /// </summary>
    static double Wetness(Storm storm, double syncedSeconds) {
        if (syncedSeconds <= storm.RainStart) {
            return 0;
        }
        double depth = storm.Profile.Puddles;
        double filledFor = Math.Min(syncedSeconds, storm.RainEnd) - storm.RainStart;
        double filled = depth * (1 - Math.Exp(-filledFor / PuddleFillSeconds));
        if (syncedSeconds <= storm.RainEnd) {
            return filled;
        }
        return filled * Math.Exp(-(syncedSeconds - storm.RainEnd) / PuddleDrySeconds);
    }

    internal static double SmoothStep(double edge0, double edge1, double value) {
        double position = Clamp01((value - edge0) / (edge1 - edge0));
        return position * position * (3 - 2 * position);
    }

    static double Clamp01(double value) {
        return Math.Max(0, Math.Min(1, value));
    }
}
