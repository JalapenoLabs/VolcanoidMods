// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;

namespace JalapenoLabs.Weather.Forecast;

/// <summary>
/// One lightning discharge: a bolt to the ground, or a flash that stays inside the clouds.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public readonly struct Strike {
    /// <summary>When it strikes, in synced seconds.</summary>
    public readonly double Time;
    /// <summary>True for a cloud-to-ground bolt, false for a flash inside the clouds.</summary>
    public readonly bool ReachesGround;
    /// <summary>Where it strikes (or flashes above), in world metres.</summary>
    public readonly float X;
    public readonly float Z;
    /// <summary>0.5 to 1: how bright, loud and damaging it is.</summary>
    public readonly float Power;
    /// <summary>The seed its shape (the bolt's path and branches, the flicker) is drawn from.</summary>
    public readonly ulong Seed;

    public Strike(double time, bool reachesGround, float x, float z, float power, ulong seed) {
        Time = time;
        ReachesGround = reachesGround;
        X = x;
        Z = z;
        Power = power;
        Seed = seed;
    }
}

/// <summary>The rectangle of the world, seen from above, that lightning can strike.</summary>
public readonly struct StrikeArea {
    public readonly float MinX;
    public readonly float MinZ;
    public readonly float MaxX;
    public readonly float MaxZ;

    public StrikeArea(float minX, float minZ, float maxX, float maxZ) {
        MinX = minX;
        MinZ = minZ;
        MaxX = maxX;
        MaxZ = maxZ;
    }
}

/// <summary>
/// Every lightning strike of a thunderstorm, worked out in advance from its seed, so every player sees the same bolt
/// hit the same spot at the same moment.
///
/// Strikes come every five seconds or so for the storm's ten minutes. Most land anywhere on the island at random;
/// some fall near the storm's core, which drifts with the wind, so the worst of the storm moves across the island.
/// Where exactly a bolt lands is therefore mostly luck, and a bolt close to a player or a cog is rare.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public static class LightningSchedule {
    public const double MeanGapSeconds = 5;
    public const double ShortestGapSeconds = 1.2;
    public const double LongestGapSeconds = 18;

    /// <summary>The share of discharges that reach the ground; the rest light the clouds from inside.</summary>
    public const float GroundShare = 0.6f;

    /// <summary>The share of discharges near the storm's core rather than anywhere on the island.</summary>
    public const float CoreShare = 0.3f;

    public const float CoreRadius = 300;

    /// <summary>How fast the storm's core drifts with the wind, in metres per second.</summary>
    public const float CoreDriftSpeed = 2;

    public static List<Strike> StrikesOf(Storm storm, StrikeArea area) {
        var strikes = new List<Strike>();
        if (!storm.Profile.HasLightning) {
            return strikes;
        }

        var random = new StormRandom(storm.Seed ^ 0x4C49_4748_544EUL);
        double start = storm.RainStart + StormSchedule.LightningMarginSeconds;
        double end = storm.RainEnd - StormSchedule.LightningMarginSeconds;
        float coreStartX = random.Range(area.MinX, area.MaxX);
        float coreStartZ = random.Range(area.MinZ, area.MaxZ);
        float driftX = (float)Math.Sin(storm.WindHeading) * CoreDriftSpeed;
        float driftZ = (float)Math.Cos(storm.WindHeading) * CoreDriftSpeed;

        double time = start;
        while (true) {
            // Exponential gaps make the strikes a random (Poisson) process: sometimes two in quick succession,
            // sometimes a long, tense wait. Clamped so neither extreme gets silly.
            double gap = -Math.Log(1 - random.NextDouble()) * MeanGapSeconds;
            time += Math.Max(ShortestGapSeconds, Math.Min(LongestGapSeconds, gap));
            if (time >= end) {
                return strikes;
            }

            bool reachesGround = random.NextFloat() < GroundShare;
            float x;
            float z;
            if (random.NextFloat() < CoreShare) {
                float elapsed = (float)(time - storm.RainStart);
                float angle = random.Range(0, 2 * (float)Math.PI);
                // The square root spreads the strikes evenly over the core's disc instead of bunching at its centre.
                float distance = CoreRadius * (float)Math.Sqrt(random.NextFloat());
                x = Clamp(coreStartX + driftX * elapsed + (float)Math.Sin(angle) * distance, area.MinX, area.MaxX);
                z = Clamp(coreStartZ + driftZ * elapsed + (float)Math.Cos(angle) * distance, area.MinZ, area.MaxZ);
            } else {
                x = random.Range(area.MinX, area.MaxX);
                z = random.Range(area.MinZ, area.MaxZ);
            }
            float power = random.Range(0.5f, 1f);
            strikes.Add(new Strike(time, reachesGround, x, z, power, random.NextULong()));
        }
    }

    static float Clamp(float value, float minimum, float maximum) {
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}
