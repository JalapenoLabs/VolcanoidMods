// Copyright © 2026 Jalapeno Labs

using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// Darkens and tints the game's day sky, and the ocean's copy of it, while a surface camera renders.
///
/// The skybox is a blend of two cubemaps that the eruption timeline keeps re-choosing, so the night is laid over it
/// through the shader's exposure and tint rather than by swapping textures: eruption skies still show through at
/// dusk, just dimmed. The ocean copies these properties from the skybox in its own Update (WaterMaterialSkybox),
/// before rendering, so it would reflect a daytime sky at night unless it is darkened alongside.
/// </summary>
internal sealed class SkyMaterials : ICameraEffect {
    readonly struct SavedState {
        public readonly Material Material;
        public readonly float Exposure;
        public readonly Color Tint;

        public SavedState(Material material) {
            Material = material;
            Exposure = material.GetFloat(ExposureId);
            Tint = material.GetColor(TintId);
        }
    }

    static readonly int ExposureId = Shader.PropertyToID("_Exposure");
    static readonly int TintId = Shader.PropertyToID("_Tint");

    readonly List<Material> waterMaterials = new List<Material>();
    readonly List<SavedState> saved = new List<SavedState>();

    public SkyMaterials() {
        var waterSkies = Object.FindObjectsByType<WaterMaterialSkybox>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
        foreach (WaterMaterialSkybox waterSky in waterSkies) {
            if (waterSky.sharedMaterial != null) {
                waterMaterials.Add(waterSky.sharedMaterial);
            }
        }
    }

    public void Apply(in NightFrame frame, Camera camera, float surface) {
        float darkness = frame.Darkness * surface;
        float golden = frame.GoldenHour * surface * NightLook.GoldenSkyStrength;
        if (darkness <= 0 && golden <= 0) {
            return;
        }

        Darken(RenderSettings.skybox, darkness, golden);
        foreach (Material waterMaterial in waterMaterials) {
            Darken(waterMaterial, darkness, golden);
        }
    }

    void Darken(Material material, float darkness, float golden) {
        if (material == null || !material.HasProperty(ExposureId) || !material.HasProperty(TintId)) {
            return;
        }
        var state = new SavedState(material);
        saved.Add(state);

        Color tint = Color.Lerp(state.Tint, NightLook.GoldenSkyTint, golden);
        tint = Color.Lerp(tint, NightLook.NightSkyTint, darkness);
        material.SetColor(TintId, tint);
        material.SetFloat(ExposureId, state.Exposure * Mathf.Lerp(1, NightLook.NightSkyExposure, darkness));
    }

    public void Restore() {
        foreach (SavedState state in saved) {
            if (state.Material == null) {
                continue;
            }
            state.Material.SetFloat(ExposureId, state.Exposure);
            state.Material.SetColor(TintId, state.Tint);
        }
        saved.Clear();
    }
}
