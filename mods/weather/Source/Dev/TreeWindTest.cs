// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace JalapenoLabs.Weather.Dev;

/// <summary>
/// Developer experiment (-weather-tree-wind QUALITY): can the island's trees sway after all?
///
/// Every tree material is SpeedTree 8 with its wind switched off (_WINDQUALITY_NONE), and switching a wind quality on
/// alone changed nothing. That test could not tell a shader variant missing from the game's build from wind inputs
/// nobody was feeding: Unity computes a tree's wind values only when its asset has wind, so with the keyword forced on,
/// the shader still read zeros. This feeds the global sway inputs itself, as shader globals (direction and strength,
/// time, sway distance, how the sway grows with height, direction adherence), which is all the "fastest" quality reads:
/// it bends the whole tree, more towards its top, and needs no branch data in the mesh. If a frame difference then
/// shows the canopies moving, the variant exists and the trees can be given real wind; if not, it does not.
/// </summary>
internal sealed class TreeWindTest : IDisposable {
    static readonly int WindVectorId = Shader.PropertyToID("_ST_WindVector");
    static readonly int WindGlobalId = Shader.PropertyToID("_ST_WindGlobal");
    static readonly int WindBranchAdherencesId = Shader.PropertyToID("_ST_WindBranchAdherences");

    /// <summary>Each tree material whose wind was switched on, with the keywords to switch back.</summary>
    readonly List<(Material Material, LocalKeyword None, LocalKeyword Tried)> switched =
        new List<(Material Material, LocalKeyword None, LocalKeyword Tried)>();
    float windTime;

    public TreeWindTest(string quality) {
        var materials = new HashSet<Material>();
        foreach (Terrain terrain in Terrain.activeTerrains) {
            foreach (TreePrototype prototype in terrain.terrainData.treePrototypes) {
                if (prototype.prefab == null) {
                    continue;
                }
                foreach (Renderer renderer in prototype.prefab.GetComponentsInChildren<Renderer>(true)) {
                    foreach (Material material in renderer.sharedMaterials) {
                        if (material == null || !materials.Add(material)) {
                            continue;
                        }
                        string[] windKeywords = material.shader.keywordSpace.keywordNames
                            .Where(name => name.IndexOf("WIND", StringComparison.Ordinal) >= 0)
                            .ToArray();
                        Debug.Log($"{WeatherMod.LogPrefix}Tree wind test: '{material.name}' ({material.shader.name}) "
                            + $"declares [{string.Join(" ", windKeywords)}]");
                        var noneKeyword = new LocalKeyword(material.shader, "_WINDQUALITY_NONE");
                        var triedKeyword = new LocalKeyword(material.shader, $"_WINDQUALITY_{quality}");
                        if (!triedKeyword.isValid) {
                            Debug.LogWarning($"{WeatherMod.LogPrefix}Tree wind test: '{material.shader.name}' has no "
                                + $"_WINDQUALITY_{quality}");
                            continue;
                        }
                        material.DisableKeyword(noneKeyword);
                        material.EnableKeyword(triedKeyword);
                        switched.Add((material, noneKeyword, triedKeyword));
                    }
                }
            }
        }
        Debug.Log($"{WeatherMod.LogPrefix}Tree wind test: _WINDQUALITY_{quality} on {switched.Count} of "
            + $"{materials.Count} tree materials");
    }

    /// <param name="wind">The wind's velocity, metres per second.</param>
    public void Update(Vector3 wind, float deltaTime) {
        float strength = Mathf.Clamp01(wind.magnitude / Visuals.StormWind.GaleSpeed);
        Vector3 direction = wind.sqrMagnitude > 0.0001f
            ? wind.normalized
            : Vector3.forward;
        // SpeedTree's wind clock runs faster in a stronger wind.
        windTime += deltaTime * (0.6f + 1.4f * strength);
        var windVector = new Vector4(direction.x, direction.y, direction.z, strength);
        // (time, sway distance, 1 / height the sway is measured over, how sharply the sway grows with height)
        var windGlobal = new Vector4(windTime, 0.1f + 0.6f * strength, 0.05f, 1.8f);
        // (how far the tree leans downwind regardless of the sway, unused here)
        var adherences = new Vector4(0.3f * strength, 0, 0, 0);
        Shader.SetGlobalVector(WindVectorId, windVector);
        Shader.SetGlobalVector(WindGlobalId, windGlobal);
        Shader.SetGlobalVector(WindBranchAdherencesId, adherences);
        // And on each material, in case the shader reads them per material rather than globally.
        foreach ((Material material, LocalKeyword _, LocalKeyword _) in switched) {
            if (material != null) {
                material.SetVector(WindVectorId, windVector);
                material.SetVector(WindGlobalId, windGlobal);
                material.SetVector(WindBranchAdherencesId, adherences);
            }
        }
    }

    public void Dispose() {
        foreach ((Material material, LocalKeyword none, LocalKeyword tried) in switched) {
            if (material != null) {
                material.DisableKeyword(tried);
                material.EnableKeyword(none);
            }
        }
    }
}
