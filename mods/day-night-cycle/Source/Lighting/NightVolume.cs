// Copyright © 2026 Jalapeno Labs

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// The night's fog and colour grading, as one more global post-processing volume beside the game's own.
///
/// The game drives fog through its own <see cref="Fog"/> volume component (FogRenderPass copies the blended result
/// into RenderSettings for each camera), so writing RenderSettings directly would simply be overwritten. A volume
/// layered over the island's dust and weather volumes blends with them the way the game's own effects do. Its
/// weight is the camera's darkness, set just before URP evaluates the volume stack for that camera.
/// </summary>
internal sealed class NightVolume : ICameraEffect, IDisposable {
    /// <summary>
    /// Above the island-wide weather volumes (priority 0 to 5), below the local lava and blast-wave volumes (10).
    /// Lava keeps its own red haze at night, and the deploy, stun and armour effects (20 and up) still sit on top.
    /// </summary>
    const float Priority = 9;

    readonly GameObject host;
    readonly Volume volume;
    readonly VolumeProfile profile;
    readonly Fog fog;
    readonly ColorAdjustments colorAdjustments;

    public NightVolume(Scene scene) {
        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "DayNightCycle.Night";
        fog = profile.Add<Fog>();
        fog.color.Override(NightLook.MoonlessFog);
        colorAdjustments = profile.Add<ColorAdjustments>();
        colorAdjustments.saturation.Override(NightLook.NightSaturation);

        host = new GameObject("DayNightCycle.NightVolume");
        SceneManager.MoveGameObjectToScene(host, scene);
        // Every surface camera's volume mask includes the surface zone layer; the game's weather volumes use it too.
        host.layer = Layers.MainZone;
        volume = host.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = Priority;
        volume.weight = 0;
        volume.sharedProfile = profile;
    }

    public void Apply(in NightFrame frame, Camera camera, float surface) {
        fog.color.value = frame.FogColor;
        volume.weight = frame.Darkness * surface;
    }

    public void Restore() {
        // Weight only matters while URP evaluates a camera, but other cameras must see the volume as off.
        volume.weight = 0;
    }

    public void Dispose() {
        Object.Destroy(host);
        Object.Destroy(profile);
    }
}
