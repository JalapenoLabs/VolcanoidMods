// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// How much open sky a point can see, from 0 (deep in a cave) to 1 (an open field).
///
/// The island's light probes and reflection probes were baked once, in daylight, for surface and caves alike. Night
/// only takes away light that came from the sky, so each probe is dimmed in proportion to how much sky it sees:
/// surface probes go dark while cave probes, lit by lava and lamps, keep their light.
/// </summary>
internal static class SkyExposure {
    /// <summary>Straight up, then four directions 40 degrees above the horizon.</summary>
    static readonly Vector3[] Directions = {
        Vector3.up,
        new Vector3(0.766f, 0.643f, 0),
        new Vector3(-0.766f, 0.643f, 0),
        new Vector3(0, 0.643f, 0.766f),
        new Vector3(0, 0.643f, -0.766f),
    };

    const float RayLength = 400;

    /// <summary>Lifts the ray start clear of the floor a probe may sit on.</summary>
    const float StartOffset = 0.5f;

    public static float Measure(Vector3 position) {
        Vector3 start = position + Vector3.up * StartOffset;
        int openDirections = 0;
        foreach (Vector3 direction in Directions) {
            // The game's own mask for "solid things a tool would hit": terrain, rock and buildings; not agents,
            // triggers, water or UI.
            if (!Physics.Raycast(start, direction, RayLength, Layers.MaskToolRaycast, QueryTriggerInteraction.Ignore)) {
                openDirections++;
            }
        }
        return (float)openDirections / Directions.Length;
    }
}
