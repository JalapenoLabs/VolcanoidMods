// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Lightning;

/// <summary>
/// One lightning discharge on screen: its glowing channel and branches, the light it throws into the clouds and onto
/// the ground, and its sparks. Kept and reused, since a storm only ever has a few on screen at once.
///
/// A bolt's shape and flicker are drawn from its strike's seed, so every player sees the same bolt. Real lightning
/// flickers because one channel carries several return strokes in quick succession; each stroke here is a sharp flash
/// that decays in a few hundredths of a second, over a dim glow that lingers until the channel fades.
/// </summary>
internal sealed class BoltEffect : IDisposable {
    /// <summary>How long a bolt stays on screen, start to finish.</summary>
    public const float Lifetime = 0.75f;

    const int MainSubdivisions = 6;
    const int BranchSubdivisions = 4;
    const int MaxBranches = 6;
    const float MainWidth = 3.6f;
    const float BranchWidth = 1.6f;

    /// <summary>How bright a stroke's peak light is, before the strike's power and the stroke's share.</summary>
    const float CloudLightIntensity = 900000;
    const float GroundLightIntensity = 120000;

    static readonly Color BoltColor = new Color(0.78f, 0.82f, 1f);

    readonly GameObject host;
    readonly LineRenderer mainLine;
    readonly LineRenderer[] branchLines = new LineRenderer[MaxBranches];
    readonly Light cloudLight;
    readonly Light groundLight;
    readonly ParticleSystem sparks;
    readonly List<Vector3> points = new List<Vector3>();
    readonly float[] strokeTimes = new float[4];
    readonly float[] strokeLevels = new float[4];
    int strokeCount;
    float startTime;
    float power;
    bool reachesGround;

    public BoltEffect(Transform parent, Material boltMaterial, Material sparkMaterial) {
        host = new GameObject("Weather.Bolt") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        mainLine = CreateLine("Channel", boltMaterial, MainWidth);
        for (int index = 0; index < MaxBranches; index++) {
            branchLines[index] = CreateLine("Branch", boltMaterial, BranchWidth);
        }
        cloudLight = CreateLight("CloudLight", 1300);
        groundLight = CreateLight("GroundLight", 260);
        sparks = CreateSparks(sparkMaterial);
        host.SetActive(false);
    }

    public bool IsActive => host.activeSelf;

    public Vector3 Top { get; private set; }

    public Vector3 Ground { get; private set; }

    /// <summary>The bolt's current brightness, 0 to 1, times its strike's power.</summary>
    public float Brightness { get; private set; }

    LineRenderer CreateLine(string name, Material material, float width) {
        var child = new GameObject(name) { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        var line = child.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.widthMultiplier = width;
        line.textureMode = LineTextureMode.Stretch;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.numCapVertices = 2;
        return line;
    }

    Light CreateLight(string name, float range) {
        var child = new GameObject(name) { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        var light = child.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = range;
        light.color = BoltColor;
        light.shadows = LightShadows.None;
        light.intensity = 0;
        // Above the game's lamps, so a flash isn't the light URP drops when an object has too many.
        light.renderMode = LightRenderMode.ForcePixel;
        return light;
    }

    ParticleSystem CreateSparks(Material material) {
        var child = new GameObject("Sparks") { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        var system = child.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = system.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = false;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.8f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(5, 18);
        main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.25f);
        main.startColor = new Color(1, 0.85f, 0.6f);
        main.gravityModifier = 1.2f;
        main.maxParticles = 200;
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0;
        ParticleSystem.ShapeModule shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.5f;
        var renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.04f;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        return system;
    }

    /// <summary>Starts the bolt from <paramref name="top"/> (in the cloud) to <paramref name="ground"/>.</summary>
    /// <param name="isInCloud">A flash that stays in the clouds: a faint crawler along the cloud base, if any.</param>
    public void Fire(Strike strike, Vector3 top, Vector3 ground, bool isInCloud) {
        var random = new StormRandom(strike.Seed);
        Top = top;
        Ground = ground;
        power = strike.Power;
        reachesGround = !isInCloud;
        startTime = Time.time;
        host.SetActive(true);

        // Return strokes: the first is the brightest; later ones follow within a fifth of a second.
        strokeCount = 1 + (int)(random.NextFloat() * (isInCloud ? 3 : 4));
        float time = 0;
        for (int stroke = 0; stroke < strokeCount; stroke++) {
            strokeTimes[stroke] = time;
            strokeLevels[stroke] = stroke == 0 ? 1 : random.Range(0.45f, 0.9f);
            time += random.Range(0.05f, isInCloud ? 0.2f : 0.14f);
        }

        bool hasChannel = !isInCloud || random.NextFloat() < 0.5f;
        mainLine.enabled = hasChannel;
        if (hasChannel) {
            BuildChannel(ref random, top, ground, MainSubdivisions, isInCloud ? 0.18f : 0.22f);
            mainLine.positionCount = points.Count;
            mainLine.SetPositions(points.ToArray());
            mainLine.widthMultiplier = isInCloud ? MainWidth * 0.6f : MainWidth;
        }

        int branchCount = hasChannel ? 2 + (int)(random.NextFloat() * (MaxBranches - 1)) : 0;
        Vector3[] channel = points.ToArray();
        for (int branch = 0; branch < MaxBranches; branch++) {
            LineRenderer line = branchLines[branch];
            line.enabled = branch < branchCount;
            if (!line.enabled) {
                continue;
            }
            // Branches fork off the upper two thirds of the channel and reach down and outwards.
            Vector3 start = channel[(int)(random.Range(0.08f, 0.66f) * (channel.Length - 1))];
            Vector3 toGround = ground - top;
            Vector3 sideways = new Vector3(random.Range(-1f, 1f), 0, random.Range(-1f, 1f)).normalized;
            float length = random.Range(0.12f, 0.35f) * toGround.magnitude;
            Vector3 end = start + (toGround.normalized * 0.6f + sideways * 0.8f).normalized * length;
            BuildChannel(ref random, start, end, BranchSubdivisions, 0.3f);
            line.positionCount = points.Count;
            line.SetPositions(points.ToArray());
        }

        cloudLight.transform.position = top + Vector3.down * 25;
        groundLight.transform.position = ground + Vector3.up * 25;
        groundLight.enabled = reachesGround;
        if (reachesGround) {
            sparks.transform.position = ground;
            sparks.Emit(60 + (int)(power * 80));
        }
    }

    /// <summary>
    /// A jagged channel from one point to another, by midpoint displacement: each level splits every segment and pushes
    /// its middle sideways by a share of the segment's length, giving lightning's self-similar zigzag.
    /// </summary>
    void BuildChannel(ref StormRandom random, Vector3 from, Vector3 to, int levels, float roughness) {
        points.Clear();
        points.Add(from);
        points.Add(to);
        for (int level = 0; level < levels; level++) {
            for (int index = points.Count - 1; index > 0; index--) {
                Vector3 start = points[index - 1];
                Vector3 end = points[index];
                float length = Vector3.Distance(start, end);
                var push = new Vector3(random.Range(-1f, 1f), random.Range(-0.4f, 0.4f), random.Range(-1f, 1f));
                points.Insert(index, (start + end) / 2 + push * (length * roughness));
            }
        }
    }

    /// <summary>Advances the flicker. Returns false once the bolt has faded.</summary>
    public bool Update() {
        float age = Time.time - startTime;
        if (age > Lifetime) {
            host.SetActive(false);
            Brightness = 0;
            return false;
        }

        float level = 0;
        for (int stroke = 0; stroke < strokeCount; stroke++) {
            float sinceStroke = age - strokeTimes[stroke];
            if (sinceStroke >= 0) {
                level = Mathf.Max(level, strokeLevels[stroke] * Mathf.Exp(-sinceStroke / 0.035f));
            }
        }
        // The channel stays faintly lit between strokes, then fades out.
        float glow = 0.18f * (1 - Mathf.SmoothStep(Lifetime * 0.55f, Lifetime, age));
        level = Mathf.Max(level, glow);
        Brightness = level * power;

        Color lineColor = new Color(1, 1, 1, Mathf.Clamp01(level * 1.4f));
        mainLine.startColor = lineColor;
        mainLine.endColor = lineColor;
        Color branchColor = new Color(1, 1, 1, Mathf.Clamp01(level * 0.9f));
        foreach (LineRenderer branch in branchLines) {
            branch.startColor = branchColor;
            branch.endColor = new Color(1, 1, 1, 0);
        }
        cloudLight.intensity = CloudLightIntensity * Brightness;
        groundLight.intensity = reachesGround ? GroundLightIntensity * Brightness : 0;
        return true;
    }

    public void Dispose() {
        Object.Destroy(host);
    }
}
