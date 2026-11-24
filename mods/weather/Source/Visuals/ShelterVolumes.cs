// Copyright © 2026 Jalapeno Labs

using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The enclosed spaces near the camera, where no rain belongs: drillship cabins and the blast shelters and mines. While
/// the camera is inside one, the rain is not drawn.
///
/// The rain's collision cannot keep a cabin dry: the far rain has no collision at all (it costs too much), and the near
/// rain's approximate collision lets drops through thin roofs, so it rained in the drillship. Making the spaces kill
/// volumes for the rain did not help: the game's zone shapes are trigger colliders, and Unity's particle trigger left
/// the drops inside them (measured: some twenty drops inside the cabin the camera stood in, with the trigger set up on
/// every space). So while the camera is in an enclosed space the rain is simply not drawn; it keeps falling, unseen, so
/// stepping out of the door shows it at once. The spaces are the shapes the game already gives them: an interior zone's
/// precise area (the colliders the game uses to decide that a player is inside), and a shelter's safe collider. An open
/// overhang is not one, so rain still shows past its edge.
///
/// Collider.ClosestPoint, the usual test for a point inside a shape, cannot test a concave mesh: it hands back the
/// point it was given, so every point anywhere counted as inside, and a drillship whose cabin is such a mesh hid the
/// rain all over the island. A concave mesh is tested against its own bounding box instead, held in the mesh's own
/// space so it turns with the drillship and stays tight around a box-like cabin whichever way the ship faces.
/// </summary>
internal sealed class ShelterVolumes {
    /// <summary>Only spaces this close to the camera are looked at.</summary>
    const float Reach = 160;

    const float RefreshSeconds = 2;

    static readonly int InteriorZoneMask = 1 << Layers.InteriorZone;

    readonly List<Collider> volumes = new List<Collider>();
    float nextRefresh;

    public int Count => volumes.Count;

    /// <param name="centre">The camera's position.</param>
    public void Update(Vector3 centre) {
        if (Time.time < nextRefresh) {
            return;
        }
        nextRefresh = Time.time + RefreshSeconds;
        volumes.Clear();
        foreach (Zone zone in TrackingBehaviour<Zone>.Instances) {
            if (zone == null || (zone.LayerMask.value & InteriorZoneMask) == 0) {
                continue;
            }
            foreach (Collider area in zone.PreciseArea) {
                AddIfNear(area, centre);
            }
        }
        foreach (BlastWaveSafeZone shelter in TrackingBehaviour<BlastWaveSafeZone>.Instances) {
            if (shelter != null) {
                AddIfNear(shelter.SafeCollider, centre);
            }
        }
    }

    void AddIfNear(Collider collider, Vector3 centre) {
        if (collider != null && collider.bounds.SqrDistance(centre) < Reach * Reach) {
            volumes.Add(collider);
        }
    }

    /// <summary>The enclosed space holding a point (the camera), or null when it is out under the sky.</summary>
    public Collider Holding(Vector3 point) {
        foreach (Collider volume in volumes) {
            if (volume != null && volume.enabled && Contains(volume, point)) {
                return volume;
            }
        }
        return null;
    }

    static bool Contains(Collider volume, Vector3 point) {
        if (volume is MeshCollider mesh && !mesh.convex) {
            if (mesh.sharedMesh == null) {
                return false;
            }
            return mesh.sharedMesh.bounds.Contains(mesh.transform.InverseTransformPoint(point));
        }
        // For every other shape, ClosestPoint gives back the point itself when it lies inside.
        return (volume.ClosestPoint(point) - point).sqrMagnitude < 0.0001f;
    }
}
