// Copyright © 2026 Jalapeno Labs

using JalapenoLabs.DayNightCycle.Cycle;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// Turns the schedule's path positions into directions in the island's world.
///
/// The sun and moon share one tilted great circle. It rises opposite the game's sun and sets under it, so the
/// afternoon sun sinks straight down along the azimuth the island was lit for, stretching the shadows the level
/// designers already placed. Between mid-morning and late afternoon the light is exactly the game's own sun.
/// The stars turn about the circle's pole, so they wheel across the sky in step with the moon.
/// </summary>
internal sealed class CelestialGeometry {
    /// <summary>
    /// How high the sun and moon climb. Kept low so the moon spends its night inside a level-looking player's view;
    /// a moon riding overhead would be one nobody sees.
    /// </summary>
    const float HighestElevationDegrees = 40;

    /// <summary>The fraction of the day over which the light blends from the sun's path into the game's sun.</summary>
    const float MorningBlendEnd = 0.25f;

    /// <summary>The fraction of the day from which the light leaves the game's sun and sinks to the horizon.</summary>
    const float EveningBlendStart = 0.85f;

    readonly Vector3 gameSunDirection;
    readonly Vector3 riseDirection;
    readonly Vector3 highestDirection;
    readonly Vector3 pole;

    /// <param name="gameSunDirection">The direction towards the game's sun, as the island was lit.</param>
    public CelestialGeometry(Vector3 gameSunDirection) {
        this.gameSunDirection = gameSunDirection.normalized;

        Vector3 setDirection = Vector3.ProjectOnPlane(this.gameSunDirection, Vector3.up).normalized;
        if (setDirection.sqrMagnitude < 0.5f) {
            // A sun straight overhead has no azimuth to set along; any horizontal direction will do.
            setDirection = Vector3.forward;
        }
        riseDirection = -setDirection;

        float highest = HighestElevationDegrees * Mathf.Deg2Rad;
        Vector3 culminationSide = Vector3.Cross(Vector3.up, riseDirection).normalized;
        highestDirection = culminationSide * Mathf.Cos(highest) + Vector3.up * Mathf.Sin(highest);
        pole = Vector3.Cross(riseDirection, highestDirection).normalized;
    }

    /// <summary>Where the sun and moon rise, on the horizon. They set opposite, under the game's sun.</summary>
    public Vector3 RiseDirection => riseDirection;

    /// <summary>The highest point of the sun's and moon's path.</summary>
    public Vector3 HighestDirection => highestDirection;

    /// <param name="veil">See <see cref="NightFrame.Veil"/>.</param>
    public NightFrame Evaluate(CycleSample sample, SkyVeil veil) {
        Vector3 sunPathDirection = PathDirection(sample.SunPathTurns);

        // While the sun is up, its path position runs from 0 at sunrise to 1 at sunset.
        float dayProgress = (float)(sample.SunPathTurns * 2);
        float gameSunWeight = dayProgress <= 1
            ? Mathf.SmoothStep(0, 1, dayProgress / MorningBlendEnd)
                * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(EveningBlendStart, 1, dayProgress)))
            : 0;
        Vector3 sunDirection = Vector3.Slerp(sunPathDirection, gameSunDirection, gameSunWeight);

        return new NightFrame(
            sample,
            sunDirection,
            sunPathDirection,
            PathDirection(sample.MoonPathTurns),
            StarRotation(sample.StarTurns),
            veil
        );
    }

    /// <summary>A point on the shared path: rising at 0 turns, highest at 0.25, setting at 0.5, lowest 0.75.</summary>
    Vector3 PathDirection(double pathTurns) {
        float angle = (float)(pathTurns * 2 * System.Math.PI);
        return riseDirection * Mathf.Cos(angle) + highestDirection * Mathf.Sin(angle);
    }

    /// <summary>
    /// Turns the star sphere about the pole. Its local up axis follows the same path the sun and moon take, so the
    /// stars always drift the same way as the moon.
    /// </summary>
    Quaternion StarRotation(double starTurns) {
        float angle = (float)(starTurns * 2 * System.Math.PI);
        Vector3 pathUp = riseDirection * Mathf.Cos(angle) + highestDirection * Mathf.Sin(angle);
        return Quaternion.LookRotation(pole, pathUp);
    }
}
