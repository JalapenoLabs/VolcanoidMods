// Copyright © 2026 Jalapeno Labs

using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The enclosed spaces near the camera where no rain or wind-blown debris belongs: drillship cabins and the blast
/// shelters and mines. Particle systems registered here kill any particle that finds its way inside one.
///
/// The rain's own collision cannot be trusted to keep a cabin dry: the far rain has no collision at all (it costs too
/// much), and the near rain's approximate collision lets drops through thin roofs. So the spaces themselves are made
/// kill volumes, from the shapes the game already gives them: an interior zone's precise area (the colliders the game
/// uses to decide that a player is inside), and a shelter's safe collider. Particles outside them, such as the rain
/// seen through a cabin's windows, are untouched.
/// </summary>
internal sealed class ShelterVolumes {
    /// <summary>Only spaces this close to the camera are watched; the rain never reaches farther.</summary>
    const float Reach = 160;

    const float RefreshSeconds = 2;

    static readonly int InteriorZoneMask = 1 << Layers.InteriorZone;

    readonly List<Collider> volumes = new List<Collider>();
    readonly List<ParticleSystem> guarded = new List<ParticleSystem>();
    float nextRefresh;

    public int Count => volumes.Count;

    /// <summary>Has every particle of this system that strays into a sheltered space die there.</summary>
    public void Guard(ParticleSystem system) {
        guarded.Add(system);
        Apply(system);
    }

    /// <param name="centre">The camera's position.</param>
    public void Update(Vector3 centre) {
        if (Time.time < nextRefresh) {
            return;
        }
        nextRefresh = Time.time + RefreshSeconds;

        var found = new List<Collider>();
        foreach (Zone zone in TrackingBehaviour<Zone>.Instances) {
            if (zone == null || (zone.LayerMask.value & InteriorZoneMask) == 0) {
                continue;
            }
            foreach (Collider area in zone.PreciseArea) {
                AddIfNear(area, centre, found);
            }
        }
        foreach (BlastWaveSafeZone shelter in TrackingBehaviour<BlastWaveSafeZone>.Instances) {
            if (shelter != null) {
                AddIfNear(shelter.SafeCollider, centre, found);
            }
        }
        if (IsSameAs(found)) {
            return;
        }
        volumes.Clear();
        volumes.AddRange(found);
        foreach (ParticleSystem system in guarded) {
            Apply(system);
        }
    }

    /// <summary>
    /// For developer logs: how many of a system's particles sit inside a sheltered space's bounds right now. With the
    /// kill volumes working this stays near zero (bounds are a little larger than the spaces they hold).
    /// </summary>
    public int CountInside(ParticleSystem system) {
        var particles = new ParticleSystem.Particle[system.particleCount];
        int count = system.GetParticles(particles);
        int inside = 0;
        for (int index = 0; index < count; index++) {
            foreach (Collider volume in volumes) {
                if (volume != null && volume.bounds.Contains(particles[index].position)) {
                    inside++;
                    break;
                }
            }
        }
        return inside;
    }

    static void AddIfNear(Collider collider, Vector3 centre, List<Collider> found) {
        if (collider != null && (collider.ClosestPointOnBounds(centre) - centre).sqrMagnitude < Reach * Reach) {
            found.Add(collider);
        }
    }

    bool IsSameAs(List<Collider> found) {
        if (found.Count != volumes.Count) {
            return false;
        }
        for (int index = 0; index < found.Count; index++) {
            if (found[index] != volumes[index]) {
                return false;
            }
        }
        return true;
    }

    void Apply(ParticleSystem system) {
        if (system == null) {
            return;
        }
        ParticleSystem.TriggerModule trigger = system.trigger;
        for (int index = trigger.colliderCount - 1; index >= 0; index--) {
            trigger.RemoveCollider(index);
        }
        foreach (Collider volume in volumes) {
            trigger.AddCollider(volume);
        }
        trigger.enabled = volumes.Count > 0;
        trigger.inside = ParticleSystemOverlapAction.Kill;
        trigger.enter = ParticleSystemOverlapAction.Kill;
        trigger.outside = ParticleSystemOverlapAction.Ignore;
        trigger.exit = ParticleSystemOverlapAction.Ignore;
    }
}
