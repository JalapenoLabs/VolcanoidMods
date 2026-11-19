// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The world getting wet: the ground takes on a sheen and drillship hulls turn glossy as the rain soaks them, and both
/// dry off again after the storm.
///
/// Both the ground's terrain layers and the drillships' paint materials are assets the game keeps (each painted
/// renderer has its own material copy), so the storm changes only their smoothness, a value nothing else in the game
/// writes, and puts back exactly what it found when the ground dries or the island unloads. Only a drillship's
/// exterior gets wet (see <see cref="DrillshipExteriors"/>).
/// </summary>
internal sealed class WetSurfaces : IDisposable {
    /// <summary>How glossy soaked ground gets (terrain layers are matte, 0, when dry).</summary>
    const float WetGroundSmoothness = 0.5f;

    /// <summary>How glossy a soaked hull gets.</summary>
    const float WetHullSmoothness = 0.97f;

    /// <summary>How much wetness must change before the surfaces are updated again.</summary>
    const float WetnessStep = 0.03f;

    static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

    readonly DrillshipExteriors exteriors;
    readonly TerrainData groundData;
    readonly TerrainLayer[] groundLayers;
    readonly float[] dryGroundSmoothness;
    readonly Dictionary<Material, float> dryHullSmoothness = new Dictionary<Material, float>();
    readonly List<Material> hullMaterials = new List<Material>();
    int knownExteriorsVersion;
    float appliedWetness;

    public WetSurfaces(DrillshipExteriors exteriors) {
        this.exteriors = exteriors;
        foreach (Terrain terrain in Terrain.activeTerrains) {
            // The surface terrain; the caves' terrains lie hundreds of metres below.
            if (terrain.GetPosition().y > -200) {
                groundData = terrain.terrainData;
                break;
            }
        }
        if (groundData == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}Found no surface terrain; the ground won't look wet");
            return;
        }
        groundLayers = groundData.terrainLayers;
        dryGroundSmoothness = Array.ConvertAll(groundLayers, layer => layer != null ? layer.smoothness : 0);
    }

    /// <param name="wetness">How wet the world is, 0 to 1 (the forecast's puddle depth).</param>
    public void Update(float wetness) {
        if (exteriors.Version != knownExteriorsVersion) {
            knownExteriorsVersion = exteriors.Version;
            CollectHullMaterials();
            // Newly built modules catch up with the current wetness.
            WetHulls();
        }
        bool hasChanged = Mathf.Abs(wetness - appliedWetness) >= WetnessStep
            || (wetness <= 0.001f && appliedWetness > 0);
        if (!hasChanged) {
            return;
        }
        appliedWetness = wetness <= 0.001f ? 0 : wetness;
        WetGround();
        WetHulls();
    }

    void CollectHullMaterials() {
        hullMaterials.Clear();
        foreach (DrillshipExterior exterior in exteriors.All) {
            foreach (Renderer renderer in exterior.Renderers) {
                if (renderer == null) {
                    continue;
                }
                foreach (Material material in renderer.sharedMaterials) {
                    if (material == null || !material.HasProperty(SmoothnessId)) {
                        continue;
                    }
                    if (!dryHullSmoothness.ContainsKey(material)) {
                        dryHullSmoothness[material] = material.GetFloat(SmoothnessId);
                    }
                    hullMaterials.Add(material);
                }
            }
        }
    }

    void WetGround() {
        if (groundLayers == null) {
            return;
        }
        for (int index = 0; index < groundLayers.Length; index++) {
            if (groundLayers[index] != null) {
                groundLayers[index].smoothness = Mathf.Lerp(
                    dryGroundSmoothness[index],
                    WetGroundSmoothness,
                    appliedWetness
                );
            }
        }
        // The terrain copies its layers' values into its material when its layer list is set.
        groundData.terrainLayers = groundLayers;
    }

    void WetHulls() {
        foreach (Material material in hullMaterials) {
            if (material != null && dryHullSmoothness.TryGetValue(material, out float dry)) {
                float wet = Mathf.Max(dry, WetHullSmoothness);
                material.SetFloat(SmoothnessId, Mathf.Lerp(dry, wet, appliedWetness));
            }
        }
    }

    public void Dispose() {
        // Dry everything, exactly as it was.
        if (groundLayers != null) {
            for (int index = 0; index < groundLayers.Length; index++) {
                if (groundLayers[index] != null) {
                    groundLayers[index].smoothness = dryGroundSmoothness[index];
                }
            }
            if (groundData != null) {
                groundData.terrainLayers = groundLayers;
            }
        }
        foreach (KeyValuePair<Material, float> entry in dryHullSmoothness) {
            if (entry.Key != null) {
                entry.Key.SetFloat(SmoothnessId, entry.Value);
            }
        }
    }
}
