// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.Weather;

/// <summary>Where the local player is, as far as the weather cares.</summary>
internal enum Exposure {
    /// <summary>Out under the open sky: rain on the lens, rain loud and clear.</summary>
    Outside,
    /// <summary>
    /// Under a roof on the surface: in a drillship, a building, a bunker, a mineshaft entrance, under an overhang. The
    /// storm is still there outside, but its rain is heard muffled and never reaches the lens.
    /// </summary>
    Sheltered,
    /// <summary>Underground, in caves and tunnels: no weather at all, not even its light.</summary>
    Underground,
}

/// <summary>
/// Works out where the local player is each frame, and eases between the answers so nothing snaps.
///
/// The game has a drillship-interior system (InteriorBehaviour) and cave zones whose masks carry the underground
/// layer, both of which this reads directly. The surface's buildings (the blast shelters and blast mines) are the
/// game's blast-wave safe zones, which this asks too. Anything else overhead (an overhang, a structure) is found by a
/// ray up from the camera against the game's solid geometry; trees have no colliders, so they never count as a roof.
/// </summary>
internal sealed class PlayerLocation {
    /// <summary>How long the storm takes to fade in or out when the player goes underground or comes up.</summary>
    const float SurfaceFadeSeconds = 1.5f;

    /// <summary>How long the rain takes to go from loud to muffled stepping under a roof (and back).</summary>
    const float ShelterFadeSeconds = 0.6f;

    /// <summary>How high above the head a roof can be and still count as shelter.</summary>
    const float RoofSearchHeight = 120;

    static readonly int SurfaceZoneMask = 1 << Layers.MainZone;
    static readonly int UndergroundZoneMask = 1 << Layers.UndergroundZone;
    static readonly int InteriorZoneMask = 1 << Layers.InteriorZone;

    Exposure lastExposure = Exposure.Outside;
    bool hasLoggedMissingPlayer;

    public Exposure Exposure { get; private set; } = Exposure.Outside;

    /// <summary>
    /// Developer captures set this while a lifted camera looks at the storm from out in the open; null otherwise.
    /// An override takes effect at once rather than easing in.
    /// </summary>
    public Exposure? Override { get; set; }

    /// <summary>0 underground, 1 on the surface (sheltered or not), eased.</summary>
    public float Surface { get; private set; } = 1;

    /// <summary>0 sheltered or underground, 1 out in the open, eased.</summary>
    public float Outdoors { get; private set; } = 1;

    public void Update(Camera camera, float deltaTime) {
        if (Override.HasValue) {
            Exposure = Override.Value;
            deltaTime = float.MaxValue;
        } else {
            Exposure = Classify(camera);
        }
        if (Exposure != lastExposure) {
            Debug.Log($"{WeatherMod.LogPrefix}Player is now {Exposure}");
            lastExposure = Exposure;
        }
        float surfaceTarget = Exposure == Exposure.Underground ? 0 : 1;
        float outdoorsTarget = Exposure == Exposure.Outside ? 1 : 0;
        Surface = Mathf.MoveTowards(Surface, surfaceTarget, deltaTime / SurfaceFadeSeconds);
        Outdoors = Mathf.MoveTowards(Outdoors, outdoorsTarget, deltaTime / ShelterFadeSeconds);
    }

    Exposure Classify(Camera camera) {
        GameObject player = Player.Local;
        if (player == null) {
            if (!hasLoggedMissingPlayer) {
                Debug.Log($"{WeatherMod.LogPrefix}No local player yet; judging shelter from the camera alone");
                hasLoggedMissingPlayer = true;
            }
            return camera != null && HasRoof(camera.transform.position) ? Exposure.Sheltered : Exposure.Outside;
        }

        if (player.TryGetComponent(out BoardedDrillship boarded) && boarded.IsInside) {
            Train train = boarded.TrainWagons != null ? boarded.TrainWagons.Train : null;
            bool isDrilling = train != null && (train.State & (TrainState.Underground | TrainState.Travel)) != 0;
            return isDrilling ? Exposure.Underground : Exposure.Sheltered;
        }

        if (player.TryGetComponent(out ZoneDetector zones)) {
            // The game's own sky rule (ZoneSun): the sun reaches a player in no zone, or in at least one surface zone.
            bool hasZones = false;
            bool hasSky = false;
            bool isUnderground = false;
            foreach (Zone zone in zones.ActiveZones) {
                hasZones = true;
                int mask = zone.LayerMask.value;
                hasSky |= (mask & SurfaceZoneMask) != 0;
                isUnderground |= (mask & UndergroundZoneMask) != 0;
                if ((mask & InteriorZoneMask) != 0) {
                    return Exposure.Sheltered;
                }
            }
            if (hasZones && !hasSky) {
                return isUnderground ? Exposure.Underground : Exposure.Sheltered;
            }
        }

        Vector3 head = camera != null ? camera.transform.position : player.transform.position + Vector3.up * 1.7f;
        foreach (BlastWaveSafeZone shelter in TrackingBehaviour<BlastWaveSafeZone>.Instances) {
            if (shelter.SafeCollider != null && shelter.IsSafe(head)) {
                return Exposure.Sheltered;
            }
        }
        return HasRoof(head) ? Exposure.Sheltered : Exposure.Outside;
    }

    static bool HasRoof(Vector3 head) {
        return Physics.Raycast(
            head + Vector3.up * 0.5f,
            Vector3.up,
            RoofSearchHeight,
            Layers.MaskExplosionVisibility,
            QueryTriggerInteraction.Ignore
        );
    }
}
