// Copyright © 2026 Jalapeno Labs

using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>One lava pool on the island's surface, seen from above.</summary>
internal readonly struct LavaPool {
    public readonly Vector3 Centre;
    public readonly float Radius;

    public LavaPool(Vector3 centre, float radius) {
        Centre = centre;
        Radius = radius;
    }
}

/// <summary>
/// Finds the lava pools on the island's surface, for the steam that boils off them in the rain and for keeping puddles
/// well clear of them.
///
/// The game has no lava-pool class: a pool is a prefab hierarchy whose deadly surface is a trigger collider carrying a
/// DamageTrigger of type Lava. Those exist for every player (only their effect is the host's), and the cave pools lie
/// far below the surface, so the triggers above <see cref="SurfaceFloor"/> are the surface pools. Small ones (lava
/// cracks) are left out.
/// </summary>
internal static class LavaPools {
    /// <summary>Everything below this height is underground.</summary>
    const float SurfaceFloor = -50;

    /// <summary>Lava smaller than this across is a crack, not a pool.</summary>
    const float SmallestPoolRadius = 8;

    public static List<LavaPool> FindOnSurface() {
        var pools = new List<LavaPool>();
        var triggers = Object.FindObjectsByType<DamageTrigger>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (DamageTrigger trigger in triggers) {
            if (trigger.DamageType != DamageType.Lava || !trigger.TryGetComponent(out Collider collider)) {
                continue;
            }
            Bounds bounds = collider.bounds;
            float radius = Mathf.Max(bounds.extents.x, bounds.extents.z);
            if (bounds.center.y < SurfaceFloor || radius < SmallestPoolRadius) {
                continue;
            }
            pools.Add(new LavaPool(bounds.center, radius));
        }
        Debug.Log($"{WeatherMod.LogPrefix}Found {pools.Count} lava pool(s) on the surface");
        return pools;
    }
}
