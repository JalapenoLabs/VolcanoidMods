// Copyright © 2026 Jalapeno Labs

using System;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// A band of night-fog colour round the horizon, opaque at and below it and fading out a few degrees up.
///
/// Stars and the moon draw without fog, so on their own they would shine right down to the horizon and through the
/// sea. The band dims them through the thick air low in the sky, as real stars fade near the horizon, and lets a
/// setting moon sink into haze before the hills hide it.
/// </summary>
internal sealed class HorizonHaze : IDisposable {
    static readonly float[] ElevationsDegrees = { -20, -2, 1, 4, 8, 13, 19 };
    static readonly float[] Opacities = { 1, 1, 0.9f, 0.62f, 0.32f, 0.1f, 0 };

    readonly Material material;
    readonly Mesh mesh;

    public readonly MeshRenderer Renderer;

    public HorizonHaze(Transform skyRoot) {
        material = SkyRenderers.CreateMaterial(
            "DayNightCycle.HorizonHaze",
            Texture2D.whiteTexture,
            SkyRenderers.HorizonHazeQueue
        );
        mesh = SkyMeshes.HorizonBand("DayNightCycle.HorizonHaze", ElevationsDegrees, Opacities, 96);
        Renderer = SkyRenderers.CreateRenderer("HorizonHaze", skyRoot, mesh, material);
    }

    public void SetColor(Color fogColor, float visibility) {
        material.color = new Color(fogColor.r, fogColor.g, fogColor.b, visibility);
    }

    public void Dispose() {
        SkyRenderers.Destroy(mesh);
        SkyRenderers.Destroy(material);
    }
}
