// Copyright © 2026 Jalapeno Labs

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Lighting;

/// <summary>
/// The storm's fog and colour, as one more global post-processing volume beside the game's own.
///
/// The game drives fog through its own <see cref="Fog"/> volume component: for each camera, FogRenderPass copies the
/// blended volume stack into RenderSettings, so fog written straight to RenderSettings would simply be overwritten.
/// A volume blends with the game's the way its own effects do. It sits on the surface zone layer, which only surface
/// cameras include in their volume masks, so caves never see it.
/// </summary>
internal sealed class StormVolume : IDisposable {
    /// <summary>
    /// Above the island's eruption-dust volumes (priority 0 to 5) and below Day/Night Cycle's night volume (9), so at
    /// night the night's dark fog colour lies over the storm's thick fog. The local lava and blast-wave volumes (10)
    /// and the deploy, stun and armour effects (20 and up) stay on top.
    /// </summary>
    const float Priority = 7;

    readonly GameObject host;
    readonly Volume volume;
    readonly VolumeProfile profile;
    readonly Fog fog;

    public StormVolume(Scene scene) {
        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "Weather.Storm";
        fog = profile.Add<Fog>();
        fog.color.Override(StormLook.LightRainFog);
        fog.density.Override(StormLook.HeavyRainFogDensity);
        ColorAdjustments colorAdjustments = profile.Add<ColorAdjustments>();
        colorAdjustments.saturation.Override(StormLook.StormSaturation);

        host = new GameObject("Weather.StormVolume");
        SceneManager.MoveGameObjectToScene(host, scene);
        host.layer = Layers.MainZone;
        volume = host.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = Priority;
        volume.weight = 0;
        volume.sharedProfile = profile;
    }

    /// <param name="cover">How much of the sky is cloud, already faded out underground.</param>
    /// <param name="darkness">How dark the clouds are, 0 to 1.</param>
    /// <param name="rain">How hard it rains, 0 to 1.</param>
    public void Update(float cover, float darkness, float rain) {
        fog.color.value = Color.Lerp(StormLook.LightRainFog, StormLook.HeavyRainFog, Mathf.Clamp01(darkness / 0.9f));
        // Rain is what thickens the air; cloud alone only greys it a little.
        fog.density.value = Mathf.Lerp(StormLook.LightRainFogDensity, StormLook.HeavyRainFogDensity, rain);
        // Weighted by the cloud cover alone, so under full cloud the storm's grey replaces the game's blue haze
        // outright rather than mixing with it; the density itself only climbs with the rain.
        volume.weight = Mathf.Clamp01(cover);
    }

    public void Dispose() {
        Object.Destroy(host);
        Object.Destroy(profile);
    }
}
