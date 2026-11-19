// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Lighting;

/// <summary>
/// The storm's sky and sky light: the sky itself rolls over into heavy storm cloud, the ambient light and default
/// reflections turn a darker grey, and all of it blinks white with each lightning flash.
///
/// The storm sky is the game's own: the island's skybox already blends between painted cubemaps along the eruption
/// timeline, and one of them, 'Afterblast', is a dark, heavy overcast. As the storm's clouds close in, the skybox
/// cross-fades from whichever sky the timeline is showing into that one, so the painted clouds thicken and darken
/// overhead with the game's own art (and, unlike particles, the skybox takes no distance fog).
///
/// These are scene-wide settings and shared assets the game expects to keep (the timeline rewrites the skybox's
/// cubemaps every few frames), so they change only while the game renders: applied once per frame before the first
/// camera renders (beginContextRendering) and put back after the last one (endContextRendering). Day/Night Cycle
/// changes the same values per camera, inside that window, reading whatever it finds as its starting point; so its
/// night darkens the storm's grey, in whichever order the two mods load, and each puts back exactly what it found.
/// </summary>
internal sealed class StormAmbience {
    /// <summary>The painted storm sky among the skybox timeline's cubemaps.</summary>
    const string StormSkyName = "Afterblast";

    static readonly int ExposureId = Shader.PropertyToID("_Exposure");
    static readonly int TintId = Shader.PropertyToID("_Tint");
    static readonly int TextureId = Shader.PropertyToID("_Tex");
    static readonly int SecondTextureId = Shader.PropertyToID("_Tex2");
    static readonly int BlendId = Shader.PropertyToID("_Blend");

    readonly struct SavedSky {
        public readonly Material Material;
        public readonly float Exposure;
        public readonly Color Tint;
        public readonly Texture Texture;
        public readonly Texture SecondTexture;
        public readonly float Blend;

        public SavedSky(Material material) {
            Material = material;
            Exposure = material.GetFloat(ExposureId);
            Tint = material.GetColor(TintId);
            bool isBlended = material.HasProperty(BlendId);
            Texture = isBlended ? material.GetTexture(TextureId) : null;
            SecondTexture = isBlended ? material.GetTexture(SecondTextureId) : null;
            Blend = isBlended ? material.GetFloat(BlendId) : 0;
        }

        public void Restore() {
            if (Material == null) {
                return;
            }
            Material.SetFloat(ExposureId, Exposure);
            Material.SetColor(TintId, Tint);
            if (Texture != null || SecondTexture != null) {
                Material.SetTexture(TextureId, Texture);
                Material.SetTexture(SecondTextureId, SecondTexture);
                Material.SetFloat(BlendId, Blend);
            }
        }
    }

    readonly List<Material> waterMaterials = new List<Material>();
    readonly List<SavedSky> savedSkies = new List<SavedSky>();
    readonly Texture stormSky;
    SphericalHarmonicsL2 savedProbe;
    float savedReflectionIntensity;
    bool isApplied;

    public StormAmbience() {
        var waterSkies = Object.FindObjectsByType<WaterMaterialSkybox>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
        foreach (WaterMaterialSkybox waterSky in waterSkies) {
            if (waterSky.sharedMaterial != null) {
                waterMaterials.Add(waterSky.sharedMaterial);
            }
        }
        stormSky = FindStormSky();
    }

    /// <summary>The storm cubemap, from the skybox timeline's own list (its field is private).</summary>
    static Texture FindStormSky() {
        FieldInfo elementsField = typeof(IslandTimeSkybox).GetField(
            "m_elements",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        foreach (IslandTimeSkybox timeline in Object.FindObjectsByType<IslandTimeSkybox>(FindObjectsSortMode.None)) {
            if (!(elementsField?.GetValue(timeline) is Array elements)) {
                continue;
            }
            foreach (object element in elements) {
                if (element.GetType().GetField("Texture")?.GetValue(element) is Texture texture
                    && texture.name == StormSkyName) {
                    return texture;
                }
            }
        }
        Debug.LogWarning(
            $"{WeatherMod.LogPrefix}The skybox timeline has no '{StormSkyName}' sky; storms only darken the sky"
        );
        return null;
    }

    /// <param name="darkness">The cloud darkness, already faded out underground.</param>
    /// <param name="cover">How much of the sky is cloud, already faded out underground.</param>
    /// <param name="flash">The lightning's brightness this frame, 0 to 1, already faded out underground.</param>
    public void Apply(float darkness, float cover, float flash) {
        if (cover <= 0 && flash <= 0) {
            return;
        }
        savedProbe = ReadStoredProbe();
        savedReflectionIntensity = RenderSettings.reflectionIntensity;
        isApplied = true;

        Color stormScale = Color.Lerp(Color.white, StormLook.AmbientStormTint, darkness)
            * (1 - StormLook.AmbientDimming * darkness);
        Color channelScale = stormScale + StormLook.FlashColor * (StormLook.FlashAmbientBoost * flash);
        SphericalHarmonicsL2 probe = savedProbe;
        for (int channel = 0; channel < 3; channel++) {
            for (int coefficient = 0; coefficient < 9; coefficient++) {
                probe[channel, coefficient] *= channelScale[channel];
            }
        }
        RenderSettings.ambientProbe = probe;
        float reflectionScale = (1 - StormLook.AmbientDimming * darkness) + StormLook.FlashAmbientBoost * flash;
        RenderSettings.reflectionIntensity = savedReflectionIntensity * reflectionScale;

        // Light rain thins the storm sky; heavier storms close it completely.
        float darkShare = Mathf.Clamp01(darkness / 0.9f);
        float stormSkyShare = Mathf.Clamp01(
            cover * (StormLook.StormSkyShare + (1 - StormLook.StormSkyShare) * darkShare)
        );
        Darken(RenderSettings.skybox, darkness, stormSkyShare, flash);
        foreach (Material waterMaterial in waterMaterials) {
            Darken(waterMaterial, darkness, stormSkyShare, flash);
        }
    }

    void Darken(Material material, float darkness, float stormSkyShare, float flash) {
        if (material == null || !material.HasProperty(ExposureId) || !material.HasProperty(TintId)) {
            return;
        }
        var saved = new SavedSky(material);
        savedSkies.Add(saved);

        if (stormSky != null && material.HasProperty(BlendId) && stormSkyShare > 0) {
            // The shader blends two cubemaps; keep whichever the timeline mostly shows and blend it into the storm.
            Texture shown = saved.Blend < 0.5f ? saved.Texture : saved.SecondTexture;
            material.SetTexture(TextureId, shown);
            material.SetTexture(SecondTextureId, stormSky);
            material.SetFloat(BlendId, stormSkyShare);
        }
        Color tint = Color.Lerp(saved.Tint, StormLook.SkyStormTint, darkness);
        float exposure = saved.Exposure * Mathf.Lerp(1, StormLook.SkyStormExposure, darkness)
            * (1 + StormLook.FlashSkyBoost * flash);
        material.SetColor(TintId, tint);
        material.SetFloat(ExposureId, exposure);
    }

    public void Restore() {
        if (!isApplied) {
            return;
        }
        RenderSettings.ambientProbe = savedProbe;
        RenderSettings.reflectionIntensity = savedReflectionIntensity;
        // In reverse, so a material saved twice ends with its first, untouched values.
        for (int index = savedSkies.Count - 1; index >= 0; index--) {
            savedSkies[index].Restore();
        }
        savedSkies.Clear();
        isApplied = false;
    }

    /// <summary>
    /// The ambient probe as Unity stores it. The <c>RenderSettings.ambientProbe</c> getter returns the stored probe
    /// already multiplied by the ambient intensity (in linear space), while the setter stores exactly what it is given,
    /// so writing back a value straight from the getter would darken the ambient light on every frame. Reading with
    /// the intensity at 1 returns the stored probe itself, so writes round-trip exactly.
    /// </summary>
    internal static SphericalHarmonicsL2 ReadStoredProbe() {
        float intensity = RenderSettings.ambientIntensity;
        RenderSettings.ambientIntensity = 1;
        SphericalHarmonicsL2 stored = RenderSettings.ambientProbe;
        RenderSettings.ambientIntensity = intensity;
        return stored;
    }
}
