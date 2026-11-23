// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.DayNightCycle.Lighting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// The night sky drawn over the game's darkened skybox: Milky Way, stars, moon and horizon haze.
///
/// Everything hangs off one root that follows the camera and is scaled almost out to its far plane, so the sky reads
/// as infinitely far away yet is still hidden by mountains. The star sphere inside it turns about the celestial pole;
/// the moon and haze stay put relative to the horizon. The renderers are enabled only inside a surface camera's
/// render, so no other camera (maps, previews, cave views) ever sees them.
/// </summary>
internal sealed class CelestialSky : ICameraEffect, IDisposable {
    /// <summary>How far out the sky sits, as a fraction of the camera's far plane.</summary>
    const float FarPlaneFraction = 0.95f;

    readonly GameObject root;
    readonly Transform starSphere;
    readonly MilkyWay milkyWay;
    readonly StarField stars;
    readonly Moon moon;
    readonly HorizonHaze haze;
    /// <summary>Everything beyond the air, which ash and cloud hide: Milky Way, stars and moon. Not the haze.</summary>
    readonly List<MeshRenderer> skyRenderers = new List<MeshRenderer>();
    int lastAnimatedFrame = -1;
    bool isApplied;

    public CelestialSky(Scene scene) {
        root = new GameObject("DayNightCycle.Sky");
        SceneManager.MoveGameObjectToScene(root, scene);
        starSphere = new GameObject("StarSphere").transform;
        starSphere.SetParent(root.transform, worldPositionStays: false);

        float started = Time.realtimeSinceStartup;
        milkyWay = new MilkyWay(starSphere);
        stars = new StarField(starSphere);
        moon = new Moon(root.transform);
        haze = new HorizonHaze(root.transform);
        float elapsedMilliseconds = (Time.realtimeSinceStartup - started) * 1000;
        Debug.Log($"{DayNightCycleMod.LogPrefix}Night sky generated in {elapsedMilliseconds:0} ms");

        skyRenderers.Add(milkyWay.Renderer);
        skyRenderers.AddRange(stars.Renderers);
        skyRenderers.AddRange(moon.Renderers);
    }

    public void Apply(in NightFrame frame, Camera camera, float surface) {
        // The night comes out once the dusk sky has gone dark, and fades early in the dawn. The haze is the air's own
        // glow, so storm cloud darkens it but ash does not (see SkyVeil): under ash it keeps the horizon glowing.
        float nightVisibility = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.5f, 1.0f, frame.Darkness)) * surface;
        float hazeVisibility = nightVisibility * (float)frame.Veil.GlowClarity;
        if (hazeVisibility <= 0.001f) {
            return;
        }

        Transform rootTransform = root.transform;
        rootTransform.position = camera.transform.position;
        rootTransform.localScale = Vector3.one * (camera.farClipPlane * FarPlaneFraction);

        haze.SetColor(frame.FogColor, hazeVisibility);
        haze.Renderer.enabled = true;
        isApplied = true;

        // Never more than the haze's visibility: whatever veils the air veils the sky beyond it too.
        float skyVisibility = nightVisibility * frame.SkyClarity;
        if (skyVisibility <= 0.001f) {
            return;
        }

        if (lastAnimatedFrame != Time.frameCount) {
            stars.Twinkle(Time.time);
            moon.Update(in frame, skyVisibility);
            lastAnimatedFrame = Time.frameCount;
        }
        starSphere.localRotation = frame.StarRotation;
        milkyWay.SetVisibility(skyVisibility);
        stars.SetVisibility(skyVisibility);
        foreach (MeshRenderer renderer in skyRenderers) {
            renderer.enabled = true;
        }
    }

    public void Restore() {
        if (!isApplied) {
            return;
        }
        haze.Renderer.enabled = false;
        foreach (MeshRenderer renderer in skyRenderers) {
            renderer.enabled = false;
        }
        isApplied = false;
    }

    public void Dispose() {
        milkyWay.Dispose();
        stars.Dispose();
        moon.Dispose();
        haze.Dispose();
        Object.Destroy(root);
    }
}
