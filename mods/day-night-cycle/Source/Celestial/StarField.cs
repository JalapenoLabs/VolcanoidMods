// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.DayNightCycle.Lighting;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// The stars: a fixed, seeded catalogue drawn as camera-facing points on the star sphere. Most are faint; they
/// crowd along the Milky Way; their colours run from blue-white to orange; and the brightest twinkle.
/// </summary>
internal sealed class StarField : IDisposable {
    /// <summary>
    /// The galactic plane's axis in the star sphere's frame, whose +Z is the celestial pole. Tilted about 62 degrees
    /// from the pole, as the real Milky Way is, so the band arcs across the sky at a slant.
    /// </summary>
    public static readonly Vector3 GalacticPole = new Vector3(0.883f, 0, 0.469f);

    const uint Seed = 0x53746172u;

    /// <summary>The share of stars placed along the Milky Way rather than anywhere.</summary>
    const float BandShare = 0.38f;

    /// <summary>The typical spread of band stars either side of the galactic plane.</summary>
    const float BandSpreadDegrees = 9;

    /// <summary>Angular radius of the faintest star, in radians: about two pixels at 1440p.</summary>
    const float SmallestRadius = 0.0014f;

    static readonly Color[] StarColors = {
        new Color(0.72f, 0.82f, 1.0f),
        new Color(0.86f, 0.91f, 1.0f),
        new Color(1.0f, 1.0f, 1.0f),
        new Color(1.0f, 0.95f, 0.84f),
        new Color(1.0f, 0.84f, 0.64f),
        new Color(1.0f, 0.72f, 0.52f),
    };

    readonly Material material;
    readonly Texture2D texture;
    readonly Mesh steadyMesh;
    readonly Mesh twinklingMesh;
    readonly Color[] twinklingBaseColors;
    readonly Color[] twinklingColors;
    readonly float[] twinklePhase;
    readonly float[] twinkleSpeed;

    public readonly MeshRenderer[] Renderers;

    public StarField(Transform starSphere) {
        var random = new SkyRandom(Seed);
        var stars = new List<(Vector3 direction, float brightness, Color color)>(NightLook.StarCount);
        Vector3 bandAxisA = Vector3.Cross(GalacticPole, Vector3.up).normalized;
        Vector3 bandAxisB = Vector3.Cross(GalacticPole, bandAxisA);

        for (int index = 0; index < NightLook.StarCount; index++) {
            Vector3 direction;
            if (random.Next() < BandShare) {
                float angle = random.Range(0, 2 * Mathf.PI);
                float latitude = random.Gaussian() * BandSpreadDegrees * Mathf.Deg2Rad;
                Vector3 around = bandAxisA * Mathf.Cos(angle) + bandAxisB * Mathf.Sin(angle);
                direction = (around * Mathf.Cos(latitude) + GalacticPole * Mathf.Sin(latitude)).normalized;
            } else {
                direction = random.Direction();
            }
            // A steep power curve: a sky of faint stars with a few bright ones, as the eye sees it.
            float brightness = 0.12f + 0.88f * Mathf.Pow(random.Next(), 5);
            Color color = StarColors[Mathf.Min(StarColors.Length - 1, (int)(random.Next() * StarColors.Length))];
            stars.Add((direction, brightness, color));
        }
        stars.Sort((first, second) => second.brightness.CompareTo(first.brightness));

        texture = SkyTextures.StarPoint();
        material = SkyRenderers.CreateMaterial("DayNightCycle.Stars", texture, SkyRenderers.StarQueue);

        int twinklingCount = Mathf.Min(NightLook.TwinklingStarCount, stars.Count);
        twinklingMesh = BuildMesh("DayNightCycle.TwinklingStars", stars.GetRange(0, twinklingCount));
        steadyMesh = BuildMesh("DayNightCycle.Stars", stars.GetRange(twinklingCount, stars.Count - twinklingCount));

        twinklingBaseColors = twinklingMesh.colors;
        twinklingColors = new Color[twinklingBaseColors.Length];
        twinklePhase = new float[twinklingCount];
        twinkleSpeed = new float[twinklingCount];
        for (int index = 0; index < twinklingCount; index++) {
            twinklePhase[index] = random.Range(0, 2 * Mathf.PI);
            twinkleSpeed[index] = random.Range(1.5f, 4.0f);
        }

        Renderers = new[] {
            SkyRenderers.CreateRenderer("Stars", starSphere, steadyMesh, material),
            SkyRenderers.CreateRenderer("TwinklingStars", starSphere, twinklingMesh, material),
        };
    }

    public void SetVisibility(float visibility) {
        float brightness = NightLook.StarBrightness;
        material.color = new Color(brightness, brightness, brightness, visibility);
    }

    /// <summary>Flickers the brightest stars. Two sine waves per star at unrelated rates never visibly loop.</summary>
    public void Twinkle(float time) {
        for (int star = 0; star < twinklePhase.Length; star++) {
            float speed = twinkleSpeed[star];
            float phase = twinklePhase[star];
            float flicker = 0.78f
                + 0.22f * Mathf.Sin(time * speed + phase) * Mathf.Sin(time * speed * 2.37f + phase * 1.7f);
            for (int corner = 0; corner < 4; corner++) {
                int vertex = star * 4 + corner;
                Color color = twinklingBaseColors[vertex];
                color.a *= flicker;
                twinklingColors[vertex] = color;
            }
        }
        twinklingMesh.colors = twinklingColors;
    }

    public void Dispose() {
        SkyRenderers.Destroy(steadyMesh);
        SkyRenderers.Destroy(twinklingMesh);
        SkyRenderers.Destroy(material);
        SkyRenderers.Destroy(texture);
    }

    static Mesh BuildMesh(string name, List<(Vector3 direction, float brightness, Color color)> stars) {
        var directions = new List<Vector3>(stars.Count);
        var radii = new List<float>(stars.Count);
        var colors = new List<Color>(stars.Count);
        foreach ((Vector3 direction, float brightness, Color color) in stars) {
            directions.Add(direction);
            // Brighter stars are drawn larger as well as more opaque, as bright stars bloom on the eye.
            radii.Add(SmallestRadius * (0.8f + 2.2f * brightness));
            colors.Add(new Color(color.r, color.g, color.b, brightness));
        }
        return SkyMeshes.Points(name, directions, radii, colors);
    }
}
