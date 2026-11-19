// Copyright © 2026 Jalapeno Labs

namespace JalapenoLabs.Weather.Forecast;

public enum StormKind {
    LightRain,
    HeavyRain,
    /// <summary>Always heavy rain, with lightning and thunder.</summary>
    Thunderstorm,
}

/// <summary>
/// How a kind of storm looks at its height. The schedule eases every value in and out with the storm's clouds.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public readonly struct StormProfile {
    /// <summary>How much of the sky the clouds cover at the storm's height, 0 to 1.</summary>
    public readonly float Cover;
    /// <summary>How dark and grey the clouds turn, 0 (white) to 1 (near black).</summary>
    public readonly float Darkness;
    /// <summary>Rain at its height, 0 to 1, where 1 is a downpour.</summary>
    public readonly float Rain;
    /// <summary>How deep puddles grow if it rains long enough, 0 to 1.</summary>
    public readonly float Puddles;
    /// <summary>Wind speed at the storm's height, in metres per second.</summary>
    public readonly float Wind;
    public readonly bool HasLightning;

    StormProfile(float cover, float darkness, float rain, float puddles, float wind, bool hasLightning) {
        Cover = cover;
        Darkness = darkness;
        Rain = rain;
        Puddles = puddles;
        Wind = wind;
        HasLightning = hasLightning;
    }

    /// <summary>
    /// Light rain falls from pale, broken cloud and leaves only small puddles; heavy rain brings a dark, closed sky;
    /// a thunderstorm is heavy rain under the darkest sky, with lightning.
    /// </summary>
    public static StormProfile Of(StormKind kind) {
        return kind switch {
            StormKind.LightRain => new StormProfile(0.8f, 0.35f, 0.3f, 0.3f, 3, false),
            StormKind.HeavyRain => new StormProfile(1, 0.75f, 1, 1, 7, false),
            _ => new StormProfile(1, 0.9f, 1, 1, 10, true),
        };
    }
}

/// <summary>
/// One hour's storm: when its clouds roll in, when it rains, when the sky has cleared, what kind it is, and the seed
/// every random detail of it (its wind, its lightning, its cloud shapes) is drawn from.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public readonly struct Storm {
    /// <summary>The UTC hour it belongs to, counted from the Unix epoch.</summary>
    public readonly long Slot;
    public readonly StormKind Kind;
    /// <summary>When the first clouds start rolling in, in synced seconds.</summary>
    public readonly double CloudsStart;
    public readonly double RainStart;
    public readonly double RainEnd;
    /// <summary>When the last clouds have gone.</summary>
    public readonly double CloudsEnd;
    public readonly ulong Seed;
    /// <summary>Where the wind blows towards, in radians clockwise from north (+z), as Unity's yaw is.</summary>
    public readonly float WindHeading;

    public Storm(
        long slot,
        StormKind kind,
        double cloudsStart,
        double rainStart,
        double rainEnd,
        double cloudsEnd,
        ulong seed,
        float windHeading
    ) {
        Slot = slot;
        Kind = kind;
        CloudsStart = cloudsStart;
        RainStart = rainStart;
        RainEnd = rainEnd;
        CloudsEnd = cloudsEnd;
        Seed = seed;
        WindHeading = windHeading;
    }

    public StormProfile Profile => StormProfile.Of(Kind);
}
