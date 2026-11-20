// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The storm's wind on the world: grass rippling, and the drillships' chimney smoke streaming off downwind.
///
/// The island's trees cannot sway: they are SpeedTree trees built without wind (every tree material uses SpeedTree's
/// no-wind setting, and switching it on at runtime changes nothing, as the game ships no wind shader for them), so the
/// wind shows in the leaves it tears from them instead (<see cref="WindDebris"/>). Terrain grass waves by the
/// terrain's own settings, which the storm turns up and puts back afterwards. Smoke takes the wind from a directional
/// wind zone, turned to the storm's wind and blowing harder in its gusts: particles feel wind zones once their external
/// forces are on, which the storm switches on for the smoke on drillships' outsides (never inside a cabin), and off
/// again after. Between storms all of it is still: the storm's wind is zero, and the game's own calm comes back.
/// </summary>
internal sealed class StormWind : IDisposable {
    /// <summary>A wind this fast, gusts included, waves grass and drives smoke and debris as far as they go.</summary>
    public const float GaleSpeed = 14;

    /// <summary>The wind zone's main strength in a gale.</summary>
    const float GaleZoneWind = 1.4f;

    /// <summary>How strongly drillship smoke answers the wind zone: in a gale it streams off nearly flat.</summary>
    const float SmokeWindResponse = 2.5f;

    /// <summary>How much the grass's waving settings must change before the terrain is told again.</summary>
    const float GrassStep = 0.02f;

    readonly GameObject host;
    readonly DrillshipExteriors exteriors;
    readonly WindZone windZone;
    /// <summary>The game's smoke particle systems whose response to the wind the storm switched on.</summary>
    readonly List<ParticleSystem> windBlownSmoke = new List<ParticleSystem>();
    readonly TerrainData groundData;
    readonly float calmGrassStrength;
    readonly float calmGrassSpeed;
    readonly float calmGrassAmount;
    float appliedGrass = -1;
    int knownExteriorsVersion;

    public StormWind(Transform parent, DrillshipExteriors exteriors) {
        this.exteriors = exteriors;
        host = new GameObject("Weather.Wind") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        windZone = host.AddComponent<WindZone>();
        windZone.mode = WindZoneMode.Directional;
        SetZoneWind(0);

        foreach (Terrain terrain in Terrain.activeTerrains) {
            // The surface terrain; the caves' terrains lie hundreds of metres below.
            if (terrain.GetPosition().y > -200) {
                groundData = terrain.terrainData;
                break;
            }
        }
        if (groundData == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}Found no surface terrain; the grass won't wave in the wind");
            return;
        }
        calmGrassStrength = groundData.wavingGrassStrength;
        calmGrassSpeed = groundData.wavingGrassSpeed;
        calmGrassAmount = groundData.wavingGrassAmount;
    }

    /// <summary>For developer logs: the wind's strength and the smoke it is pushing.</summary>
    public string Describe() {
        return $"wind {windZone.windMain:0.00} main, {windBlownSmoke.Count} smoke plumes in the wind";
    }

    /// <param name="wind">The wind's velocity this moment, gusts included, in metres per second.</param>
    public void Update(Vector3 wind) {
        float speed = wind.magnitude;
        float strength = Mathf.Clamp01(speed / GaleSpeed);
        if (speed > 0.01f) {
            host.transform.rotation = Quaternion.LookRotation(wind / speed);
        }
        SetZoneWind(strength);
        WaveGrass(strength);
        CatchSmoke();
    }

    void SetZoneWind(float strength) {
        windZone.windMain = GaleZoneWind * strength;
        windZone.windTurbulence = 1.2f * strength;
        windZone.windPulseMagnitude = 0.8f * strength;
        windZone.windPulseFrequency = 0.05f + 0.25f * strength;
    }

    /// <summary>
    /// Switches on wind for the smoke on every drillship's outside: chimneys, vents, deploy smoke. Found again whenever
    /// the drillships change, as modules come and go.
    /// </summary>
    void CatchSmoke() {
        if (exteriors.Version == knownExteriorsVersion) {
            return;
        }
        knownExteriorsVersion = exteriors.Version;
        ReleaseSmoke();
        foreach (DrillshipExterior exterior in exteriors.All) {
            if (exterior.Root == null) {
                continue;
            }
            foreach (ParticleSystem system in exterior.Root.GetComponentsInChildren<ParticleSystem>(true)) {
                ParticleSystem.ExternalForcesModule forces = system.externalForces;
                // Smoke that already feels the wind is the game's to keep as it is.
                if (forces.enabled || !IsOutdoorSmoke(system)) {
                    continue;
                }
                forces.enabled = true;
                forces.multiplier = SmokeWindResponse;
                windBlownSmoke.Add(system);
            }
        }
    }

    /// <summary>Smoke drifting free in the world, on a drillship's outside.</summary>
    static bool IsOutdoorSmoke(ParticleSystem system) {
        var renderer = system.GetComponent<ParticleSystemRenderer>();
        Material material = renderer != null ? renderer.sharedMaterial : null;
        bool isSmoke = material != null && material.name.IndexOf("Smoke", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isSmoke || system.main.simulationSpace != ParticleSystemSimulationSpace.World) {
            return false;
        }
        // Smoke inside the cabin (a cog's gun, a drone) stays out of the wind; the cabins are the InteriorZone objects.
        for (Transform parent = system.transform; parent != null; parent = parent.parent) {
            if (parent.name.StartsWith("InteriorZone", StringComparison.Ordinal)) {
                return false;
            }
        }
        return true;
    }

    void ReleaseSmoke() {
        foreach (ParticleSystem system in windBlownSmoke) {
            if (system != null) {
                ParticleSystem.ExternalForcesModule forces = system.externalForces;
                forces.enabled = false;
            }
        }
        windBlownSmoke.Clear();
    }

    void WaveGrass(float strength) {
        if (groundData == null || Mathf.Abs(strength - appliedGrass) < GrassStep) {
            return;
        }
        appliedGrass = strength;
        groundData.wavingGrassStrength = Mathf.Lerp(calmGrassStrength, 1, strength);
        groundData.wavingGrassSpeed = Mathf.Lerp(calmGrassSpeed, 1, strength);
        groundData.wavingGrassAmount = Mathf.Lerp(calmGrassAmount, 1, strength);
    }

    public void Dispose() {
        ReleaseSmoke();
        if (groundData != null) {
            groundData.wavingGrassStrength = calmGrassStrength;
            groundData.wavingGrassSpeed = calmGrassSpeed;
            groundData.wavingGrassAmount = calmGrassAmount;
        }
        Object.Destroy(host);
    }
}
