// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.DayNightCycle.Lighting;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// The moon: a lit disc showing tonight's phase, turned so its bright side faces the sun below the horizon, with a
/// soft glow around it that grows with the phase.
/// </summary>
internal sealed class Moon : IDisposable {
    const int TextureSize = 256;

    /// <summary>Repaint the phase when the lit fraction has moved this much; about four times a night.</summary>
    const float RepaintThreshold = 0.005f;

    /// <summary>The glow's colour and its opacity at full moon.</summary>
    static readonly Color HaloColor = new Color(0.62f, 0.72f, 1.0f, 0.45f);

    readonly float[] albedo;
    readonly Texture2D faceTexture;
    readonly Texture2D haloTexture;
    readonly Material faceMaterial;
    readonly Material haloMaterial;
    readonly Mesh quad;
    readonly Transform face;
    readonly Transform halo;
    float paintedIllumination = -1;

    public readonly MeshRenderer[] Renderers;

    public Moon(Transform skyRoot) {
        albedo = SkyTextures.MoonAlbedo(TextureSize);
        faceTexture = SkyTextures.CreateBlank("DayNightCycle.MoonFace", TextureSize);
        haloTexture = SkyTextures.Glow();
        faceMaterial = SkyRenderers.CreateMaterial("DayNightCycle.Moon", faceTexture, SkyRenderers.MoonQueue);
        haloMaterial = SkyRenderers.CreateMaterial("DayNightCycle.MoonHalo", haloTexture, SkyRenderers.MoonHaloQueue);
        quad = SkyMeshes.Quad("DayNightCycle.MoonQuad");

        MeshRenderer faceRenderer = SkyRenderers.CreateRenderer("Moon", skyRoot, quad, faceMaterial);
        MeshRenderer haloRenderer = SkyRenderers.CreateRenderer("MoonHalo", skyRoot, quad, haloMaterial);
        halo = haloRenderer.transform;
        face = faceRenderer.transform;
        Renderers = new[] { faceRenderer, haloRenderer };
    }

    public void Update(in NightFrame frame, float visibility) {
        if (Mathf.Abs(frame.MoonIllumination - paintedIllumination) >= RepaintThreshold) {
            SkyTextures.PaintMoonPhase(faceTexture, albedo, frame.MoonIllumination);
            paintedIllumination = frame.MoonIllumination;
        }

        // Point the painted bright side (+x) at the sun, wherever it is below the horizon.
        Vector3 direction = frame.MoonDirection;
        Vector3 towardsSun = Vector3.ProjectOnPlane(frame.SunPathDirection, direction);
        if (towardsSun.sqrMagnitude < 1e-6f) {
            towardsSun = Vector3.ProjectOnPlane(Vector3.up, direction);
        }
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.Cross(direction, towardsSun.normalized));

        float diameter = 2 * Mathf.Tan(NightLook.MoonDiameterDegrees * 0.5f * Mathf.Deg2Rad);
        face.localPosition = direction;
        face.localRotation = rotation;
        face.localScale = Vector3.one * diameter;
        halo.localPosition = direction * 0.999f;
        halo.localRotation = rotation;
        halo.localScale = Vector3.one * diameter * NightLook.MoonHaloScale;

        float alpha = visibility * frame.MoonVisibility;
        float brightness = NightLook.MoonBrightness;
        faceMaterial.color = new Color(brightness, brightness, brightness, alpha);
        float haloAlpha = alpha * HaloColor.a * frame.MoonIllumination;
        haloMaterial.color = new Color(HaloColor.r, HaloColor.g, HaloColor.b, haloAlpha);
    }

    public void Dispose() {
        SkyRenderers.Destroy(quad);
        SkyRenderers.Destroy(faceMaterial);
        SkyRenderers.Destroy(haloMaterial);
        SkyRenderers.Destroy(faceTexture);
        SkyRenderers.Destroy(haloTexture);
    }
}
