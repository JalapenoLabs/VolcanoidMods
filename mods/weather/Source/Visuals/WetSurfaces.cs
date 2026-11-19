// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The world getting wet: the ground takes on a sheen, drillship hulls turn glossy, and rainwater pours off the edges
/// of the drillships' roofs and modules in streams while it rains.
///
/// Both the ground's terrain layers and the drillships' paint materials are assets the game keeps (each painted
/// renderer has its own material copy), so the storm changes only their smoothness, a value nothing else in the game
/// writes, and puts back exactly what it found when the ground dries or the island unloads. Only a drillship's exterior
/// gets wet: its paint system already separates the exterior case and hull from the interior, and that is what this
/// uses.
/// </summary>
internal sealed class WetSurfaces : IDisposable {
    /// <summary>How glossy soaked ground gets (terrain layers are matte, 0, when dry).</summary>
    const float WetGroundSmoothness = 0.5f;

    /// <summary>How glossy a soaked hull gets.</summary>
    const float WetHullSmoothness = 0.97f;

    /// <summary>How much wetness must change before the surfaces are updated again.</summary>
    const float WetnessStep = 0.03f;

    const float HullRefreshSeconds = 8;
    const float DripReach = 90;

    /// <summary>
    /// Run-off falls from this many places along each drillship's roof edges, each a steady stream of drops in heavy
    /// rain, like water pouring off a roof without gutters.
    /// </summary>
    const int DripPointsPerDrillship = 36;
    const float DripsPerPointPerSecond = 11;

    /// <summary>Only parts reaching this high above the drillship's base shed water from their tops.</summary>
    const float RoofHeight = 3;

    static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

    readonly struct DripPoint {
        public readonly Transform Anchor;
        public readonly Vector3 LocalPosition;

        public DripPoint(Transform anchor, Vector3 localPosition) {
            Anchor = anchor;
            LocalPosition = localPosition;
        }
    }

    readonly TerrainData groundData;
    readonly TerrainLayer[] groundLayers;
    readonly float[] dryGroundSmoothness;
    readonly Dictionary<Material, float> dryHullSmoothness = new Dictionary<Material, float>();
    readonly List<Material> hullMaterials = new List<Material>();
    readonly List<DripPoint> dripPoints = new List<DripPoint>();
    readonly GameObject host;
    readonly ParticleSystem drips;
    readonly Material dripMaterial;
    float appliedWetness;
    float nextHullRefresh;
    float dripBudget;
    StormRandom random = new StormRandom(0xD21B_5UL);

    public WetSurfaces(Transform parent, StormTextures textures) {
        foreach (Terrain terrain in Terrain.activeTerrains) {
            // The surface terrain; the caves' terrains lie hundreds of metres below.
            if (terrain.GetPosition().y > -200) {
                groundData = terrain.terrainData;
                break;
            }
        }
        if (groundData != null) {
            groundLayers = groundData.terrainLayers;
            dryGroundSmoothness = Array.ConvertAll(groundLayers, layer => layer != null ? layer.smoothness : 0);
        } else {
            Debug.LogWarning($"{WeatherMod.LogPrefix}Found no surface terrain; the ground won't look wet");
        }

        dripMaterial = GameMaterials.CloneLitSmoke("hull drips");
        if (dripMaterial == null) {
            throw new InvalidOperationException("the game has no lit smoke material to build on");
        }
        dripMaterial.SetTexture(GameMaterials.BaseMapId, textures.RainStreak);
        dripMaterial.SetTexture(GameMaterials.BumpMapId, null);
        dripMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.5f, 1.55f, 1.65f, 0.7f));
        host = new GameObject("Weather.HullDrips") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        drips = CreateDrips();
    }

    ParticleSystem CreateDrips() {
        var system = host.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = system.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = false;
        main.loop = true;
        main.maxParticles = 2000;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 1.8f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.05f);
        main.startColor = new Color(0.5f, 0.5f, 0.5f, 1);
        main.gravityModifier = 1;
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0;
        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = false;
        var renderer = host.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = dripMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.12f;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        GameMaterials.UseLitSmokeStreams(renderer);
        system.Play();
        return system;
    }

    /// <param name="wetness">How wet the world is, 0 to 1 (the forecast's puddle depth).</param>
    /// <param name="rain">How hard it rains, 0 to 1, for the drips.</param>
    /// <param name="centre">The camera's position; only nearby drillships drip.</param>
    public void Update(float wetness, float rain, Vector3 centre, float deltaTime) {
        if (Time.time >= nextHullRefresh) {
            RefreshHulls();
            nextHullRefresh = Time.time + HullRefreshSeconds;
        }
        bool hasChanged = Mathf.Abs(wetness - appliedWetness) >= WetnessStep
            || (wetness <= 0.001f && appliedWetness > 0);
        if (hasChanged) {
            Apply(wetness);
        }
        EmitDrips(rain, centre, deltaTime);
    }

    void Apply(float wetness) {
        appliedWetness = wetness <= 0.001f ? 0 : wetness;
        if (groundLayers != null) {
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
        foreach (Material material in hullMaterials) {
            if (material != null && dryHullSmoothness.TryGetValue(material, out float dry)) {
                float wet = Mathf.Max(dry, WetHullSmoothness);
                material.SetFloat(SmoothnessId, Mathf.Lerp(dry, wet, appliedWetness));
            }
        }
    }

    /// <summary>Finds every drillship's exterior materials and drip points again, as modules come and go.</summary>
    void RefreshHulls() {
        hullMaterials.Clear();
        dripPoints.Clear();
        var exterior = new List<Renderer>();
        foreach (TrainWagons wagons in TrainWagons.Instances) {
            if (wagons == null) {
                continue;
            }
            exterior.Clear();
            foreach (PaintGridModule module in wagons.GetComponentsInChildren<PaintGridModule>()) {
                AddRenderers(module.ExteriorCase, exterior);
            }
            foreach (PaintSegment segment in wagons.GetComponentsInChildren<PaintSegment>()) {
                foreach (PaintSegment.NamedItem item in segment.Items) {
                    if ((item.DecalLayer & PaintDecalLayer.Interior) == 0) {
                        AddRenderers(item.Element as PaintElement, exterior);
                    }
                }
            }
            foreach (Renderer renderer in exterior) {
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
            AddDripPoints(exterior, wagons.transform.position.y);
        }
        // Newly found materials catch up with the current wetness.
        Apply(appliedWetness);
    }

    static void AddRenderers(PaintElement element, List<Renderer> renderers) {
        if (element == null) {
            return;
        }
        foreach (PaintElement.Element part in element.Renderers) {
            if (part.Renderer != null) {
                renderers.Add(part.Renderer);
            }
        }
    }

    /// <summary>
    /// Points along the upper edges of the hull's taller parts, just outside them, where the water on their roofs
    /// spills over and falls past their sides.
    /// </summary>
    void AddDripPoints(List<Renderer> exterior, float baseHeight) {
        var roofs = exterior.FindAll(renderer => renderer.bounds.max.y > baseHeight + RoofHeight);
        if (roofs.Count == 0) {
            return;
        }
        for (int point = 0; point < DripPointsPerDrillship; point++) {
            Renderer renderer = roofs[(int)(random.NextFloat() * roofs.Count)];
            Bounds bounds = renderer.bounds;
            // A spot on one of the part's four upper edges, a hand's width outside it so the stream falls clear.
            bool isAlongX = random.NextFloat() < 0.5f;
            float along = random.Range(-0.9f, 0.9f);
            float side = random.NextFloat() < 0.5f ? -1 : 1;
            var offset = isAlongX
                ? new Vector3(along * bounds.extents.x, bounds.extents.y, side * (bounds.extents.z + 0.1f))
                : new Vector3(side * (bounds.extents.x + 0.1f), bounds.extents.y, along * bounds.extents.z);
            Vector3 world = bounds.center + offset;
            dripPoints.Add(new DripPoint(renderer.transform, renderer.transform.InverseTransformPoint(world)));
        }
    }

    void EmitDrips(float rain, Vector3 centre, float deltaTime) {
        if (rain <= 0.01f || dripPoints.Count == 0) {
            dripBudget = 0;
            return;
        }
        dripBudget += rain * dripPoints.Count * DripsPerPointPerSecond * deltaTime;
        var emit = new ParticleSystem.EmitParams();
        while (dripBudget >= 1) {
            dripBudget--;
            DripPoint point = dripPoints[(int)(random.NextFloat() * dripPoints.Count)];
            if (point.Anchor == null) {
                continue;
            }
            Vector3 position = point.Anchor.TransformPoint(point.LocalPosition);
            if ((position - centre).sqrMagnitude > DripReach * DripReach) {
                continue;
            }
            emit.position = position;
            drips.Emit(emit, 1);
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
        Object.Destroy(host);
        Object.Destroy(dripMaterial);
    }
}
