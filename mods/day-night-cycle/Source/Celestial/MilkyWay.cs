// Copyright © 2026 Jalapeno Labs

using System;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>The Milky Way: a faint, cloudy band with a dust lane, round the star sphere's galactic plane.</summary>
internal sealed class MilkyWay : IDisposable {
    /// <summary>How far the band's glow reaches either side of the galactic plane.</summary>
    const float HalfWidthDegrees = 28;

    /// <summary>The band's opacity at full night; the texture's own alpha shapes it within this.</summary>
    const float Strength = 0.42f;

    readonly Texture2D texture;
    readonly Material material;
    readonly Mesh mesh;

    public readonly MeshRenderer Renderer;

    public MilkyWay(Transform starSphere) {
        texture = SkyTextures.MilkyWay();
        material = SkyRenderers.CreateMaterial("DayNightCycle.MilkyWay", texture, SkyRenderers.MilkyWayQueue);
        mesh = SkyMeshes.GreatCircleBand("DayNightCycle.MilkyWay", StarField.GalacticPole, HalfWidthDegrees, 128, 7);
        Renderer = SkyRenderers.CreateRenderer("MilkyWay", starSphere, mesh, material);
    }

    public void SetVisibility(float visibility) {
        material.color = new Color(1, 1, 1, visibility * Strength);
    }

    public void Dispose() {
        SkyRenderers.Destroy(mesh);
        SkyRenderers.Destroy(material);
        SkyRenderers.Destroy(texture);
    }
}
