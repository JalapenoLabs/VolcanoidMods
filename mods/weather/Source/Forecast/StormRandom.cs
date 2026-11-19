// Copyright © 2026 Jalapeno Labs

using System;

namespace JalapenoLabs.Weather.Forecast;

/// <summary>
/// A small seeded random generator (splitmix64), identical on every machine.
///
/// The weather must come out the same for every player who has the mod, with nothing sent over the network, so every
/// random choice (when a storm comes, what kind it is, where lightning strikes) is drawn from a generator seeded by the
/// synced clock. System.Random's sequence is not guaranteed across runtimes, so the mod carries its own.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public struct StormRandom {
    /// <summary>Keeps the weather's seeds apart from any other mod's use of the same clock.</summary>
    const ulong WeatherSalt = 0x5745_4154_4845_5221UL;

    ulong state;

    public StormRandom(ulong seed) {
        state = seed ^ WeatherSalt;
    }

    public ulong NextULong() {
        state += 0x9E37_79B9_7F4A_7C15UL;
        return Mix(state);
    }

    public uint NextUInt() {
        return (uint)(NextULong() >> 32);
    }

    /// <summary>Uniform in [0, 1), from the top 24 bits so it is exact in a float.</summary>
    public float NextFloat() {
        return (NextULong() >> 40) * (1f / (1 << 24));
    }

    /// <summary>Uniform in [0, 1), from the top 53 bits so it is exact in a double.</summary>
    public double NextDouble() {
        return (NextULong() >> 11) * (1.0 / (1UL << 53));
    }

    public float Range(float minimum, float maximum) {
        return minimum + (maximum - minimum) * NextFloat();
    }

    public double Range(double minimum, double maximum) {
        return minimum + (maximum - minimum) * NextDouble();
    }

    /// <summary>A stand-alone hash of one number, for seeding a generator from a slot or strike index.</summary>
    public static ulong Mix(ulong value) {
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>A smooth random curve over time (value noise with cosine easing), between -1 and 1.</summary>
    public static double Wander(ulong seed, double seconds, double periodSeconds) {
        double position = seconds / periodSeconds;
        double knot = Math.Floor(position);
        double blend = (1 - Math.Cos((position - knot) * Math.PI)) / 2;
        double from = KnotValue(seed, (long)knot);
        double to = KnotValue(seed, (long)knot + 1);
        return from + (to - from) * blend;
    }

    static double KnotValue(ulong seed, long knot) {
        var random = new StormRandom(Mix(seed ^ (ulong)knot));
        return random.NextDouble() * 2 - 1;
    }
}
