// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Shared;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.Rendering;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// What the wind carries past the player: leaves torn from the trees around them, mostly in the gusts, and low sheets
/// of spray that each gust drives racing across the ground.
///
/// The island's trees cannot sway (see <see cref="StormWind"/>), so their leaves show the wind instead: each is torn
/// from the crown of a real tree near the camera and tumbles away downwind, so a gust visibly strips the trees. Out on
/// open ground there is nothing to strip, and only the spray shows the gusts.
/// </summary>
internal sealed class WindDebris : IDisposable {
    const float MostLeavesPerSecond = 60;
    const float MostSheetsPerSecond = 6;

    /// <summary>Leaves are torn from the crowns of trees this close to the camera.</summary>
    const float LeafReach = 70;

    /// <summary>How often, or after how far a move, the trees around the camera are looked up again.</summary>
    const float TreeSurveySeconds = 3;
    const float TreeSurveyDistance = 20;

    /// <summary>Spray starts this far upwind of the camera, so it blows past it rather than appearing on it.</summary>
    const float UpwindStart = 25;

    static readonly int CullId = Shader.PropertyToID("_Cull");

    readonly GameObject host;
    readonly ParticleSystem leaves;
    readonly ParticleSystem sheets;
    readonly Material leafMaterial;
    readonly Material sheetMaterial;
    bool isClear = true;
    List<TerrainTrees.Tree> nearbyTrees = new List<TerrainTrees.Tree>();
    Vector3 surveyedAt;
    float nextTreeSurvey;
    float leafBudget;
    StormRandom random = new StormRandom(0x1EAF_5UL);

    public WindDebris(Transform parent, StormTextures textures) {
        leafMaterial = GameMaterials.CloneLitSmoke("wind-blown leaves");
        sheetMaterial = GameMaterials.CloneLitSmoke("wind-blown spray");
        if (leafMaterial == null || sheetMaterial == null) {
            throw new InvalidOperationException("the game has no lit smoke material to build on");
        }
        leafMaterial.SetTexture(GameMaterials.BaseMapId, textures.Leaves);
        leafMaterial.SetTexture(GameMaterials.BumpMapId, null);
        // The particle shader overlays each particle's colour on the texture; on a white leaf that overlay is lost,
        // so the leaf colour comes from the material, and the particles only nudge it greener or browner. Bright,
        // since the storm's gloom darkens whatever it lights: a darker leaf reads as a black scrap.
        leafMaterial.SetColor(GameMaterials.BaseColorId, new Color(0.8f, 0.82f, 0.42f, 1));
        // Seen from either side as they tumble.
        if (leafMaterial.HasProperty(CullId)) {
            leafMaterial.SetFloat(CullId, (float)CullMode.Off);
        }
        sheetMaterial.SetTexture(GameMaterials.BaseMapId, textures.CloudAlbedo);
        sheetMaterial.SetTexture(GameMaterials.BumpMapId, textures.CloudNormals);
        sheetMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.2f, 1.22f, 1.28f, 0.16f));

        host = new GameObject("Weather.WindDebris") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        leaves = CreateLeaves();
        sheets = CreateSheets();
    }

    ParticleSystem CreateLeaves() {
        ParticleSystem system = CreateEmitter("Leaves", 500);
        // Emitted one by one from the crowns of the trees around the camera.
        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = false;
        ParticleSystem.MainModule main = system.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(3, 6);
        // Two or three times a real leaf, which would be a speck a few metres away.
        main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.3f);
        main.startRotation3D = true;
        main.startRotationX = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        main.startRotationY = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        main.startRotationZ = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        // Overlay colours around the neutral 0.5: some leaves greener, some browner.
        main.startColor = new ParticleSystem.MinMaxGradient(
            new Color(0.42f, 0.56f, 0.44f, 1),
            new Color(0.64f, 0.5f, 0.38f, 1)
        );
        main.gravityModifier = 0.08f;

        // Tumbling end over end on every axis, and fluttering about the wind's line.
        ParticleSystem.RotationOverLifetimeModule rotation = system.rotationOverLifetime;
        rotation.enabled = true;
        rotation.separateAxes = true;
        rotation.x = new ParticleSystem.MinMaxCurve(-5, 5);
        rotation.y = new ParticleSystem.MinMaxCurve(-5, 5);
        rotation.z = new ParticleSystem.MinMaxCurve(-3, 3);
        ParticleSystem.NoiseModule noise = system.noise;
        noise.enabled = true;
        noise.strength = 2.5f;
        noise.frequency = 0.6f;
        noise.scrollSpeed = 0.5f;
        noise.damping = true;
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        color.color = FadeInAndOut(0.05f, 0.85f);
        // Skittering along the ground and off walls instead of sailing through them.
        ParticleSystem.CollisionModule collision = system.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.mode = ParticleSystemCollisionMode.Collision3D;
        collision.quality = ParticleSystemCollisionQuality.Low;
        collision.collidesWith = Layers.MaskToolRaycast;
        collision.bounce = 0.15f;
        collision.dampen = 0.6f;
        collision.lifetimeLoss = 0.2f;
        collision.radiusScale = 0.5f;
        ParticleSystem.TextureSheetAnimationModule sheet = system.textureSheetAnimation;
        sheet.enabled = true;
        sheet.numTilesX = Art.WeatherArt.LeafAtlasTiles;
        sheet.numTilesY = Art.WeatherArt.LeafAtlasTiles;
        sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0);
        sheet.startFrame = new ParticleSystem.MinMaxCurve(0, 0.9999f);

        var renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = leafMaterial;
        // Flat quads that turn in three dimensions, so a leaf flashes edge-on and face-on as it tumbles.
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        renderer.enableGPUInstancing = false;
        GameMaterials.UseLitSmokeStreams(renderer);
        return system;
    }

    /// <summary>Long, low veils of spray that a gust drives across the ground.</summary>
    ParticleSystem CreateSheets() {
        ParticleSystem system = CreateEmitter("Spray", 150);
        ParticleSystem.MainModule main = system.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2, 3.2f);
        // Far wider than tall: a racing streak of mist, not a puff.
        main.startSize3D = true;
        main.startSizeX = new ParticleSystem.MinMaxCurve(9, 16);
        main.startSizeY = new ParticleSystem.MinMaxCurve(2, 4);
        main.startSizeZ = 1;
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        color.color = FadeInAndOut(0.25f, 0.6f);
        ParticleSystem.TextureSheetAnimationModule sheet = system.textureSheetAnimation;
        sheet.enabled = true;
        sheet.numTilesX = Art.WeatherArt.CloudAtlasTiles;
        sheet.numTilesY = Art.WeatherArt.CloudAtlasTiles;
        sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0);
        sheet.startFrame = new ParticleSystem.MinMaxCurve(0, 0.9999f);

        var renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = sheetMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        GameMaterials.UseLitSmokeStreams(renderer);
        return system;
    }

    /// <summary>A particle system that drifts with the wind, emitting where this class puts it.</summary>
    ParticleSystem CreateEmitter(string name, int maxParticles) {
        var child = new GameObject(name) { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        var system = child.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = system.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = false;
        main.loop = true;
        main.maxParticles = maxParticles;
        main.startSpeed = 0;
        main.startColor = new Color(0.5f, 0.5f, 0.5f, 1);
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0;
        ParticleSystem.ShapeModule shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        SetVelocity(velocity, Vector3.zero);
        var renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        system.Play();
        return system;
    }

    static Gradient FadeInAndOut(float fadeInEnd, float fadeOutStart) {
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] {
                new GradientAlphaKey(0, 0),
                new GradientAlphaKey(1, fadeInEnd),
                new GradientAlphaKey(1, fadeOutStart),
                new GradientAlphaKey(0, 1),
            }
        );
        return fade;
    }

    /// <summary>For developer logs: what the wind has in the air.</summary>
    public string Describe() {
        return $"{nearbyTrees.Count} trees near, {leaves.particleCount} leaves, {sheets.particleCount} spray sheets";
    }

    /// <param name="wind">The wind's velocity this moment, gusts included, in metres per second.</param>
    /// <param name="gust">How hard it is gusting, 0 to 1.</param>
    /// <param name="rain">How hard it rains, 0 to 1: gusts drive spray only when there is rain to drive.</param>
    /// <param name="eye">The camera's position; the leaves and spray blow past it.</param>
    /// <param name="exposure">How much wind reaches the camera: 0 underground or under a roof, 1 outside.</param>
    public void Update(Vector3 wind, float gust, float rain, Vector3 eye, float exposure, float deltaTime) {
        // Inside, nothing blows through the room: what was in the air is gone, as it would be past the door.
        if (exposure < 0.5f) {
            if (!isClear) {
                leaves.Clear();
                sheets.Clear();
                isClear = true;
            }
            leafBudget = 0;
            SetRate(sheets, 0);
            return;
        }
        isClear = false;
        float speed = wind.magnitude;
        float strength = Mathf.Clamp01(speed / StormWind.GaleSpeed);
        Vector3 upwind = speed > 0.01f
            ? -wind / speed
            : Vector3.zero;

        // Mostly in the gusts, which strip the trees in bursts.
        leafBudget += MostLeavesPerSecond * strength * (0.25f + 0.75f * gust) * exposure * deltaTime;
        TearLeaves(eye);
        SetVelocity(leaves.velocityOverLifetime, wind * 0.85f);

        SetRate(sheets, MostSheetsPerSecond * gust * strength * (0.4f + 0.6f * rain) * exposure);
        float ground = GroundBelow(eye);
        PlaceSource(sheets, new Vector3(eye.x, ground + 1.5f, eye.z) + upwind * UpwindStart, new Vector3(60, 2, 60));
        SetVelocity(sheets.velocityOverLifetime, wind);
    }

    /// <summary>Leaves torn from the crowns of the trees around the camera, as many as the wind has earned.</summary>
    void TearLeaves(Vector3 eye) {
        if (leafBudget < 1) {
            return;
        }
        bool hasMoved = (eye - surveyedAt).sqrMagnitude > TreeSurveyDistance * TreeSurveyDistance;
        if (Time.time >= nextTreeSurvey || hasMoved) {
            nearbyTrees = TerrainTrees.Around(eye, LeafReach);
            surveyedAt = eye;
            nextTreeSurvey = Time.time + TreeSurveySeconds;
        }
        if (nearbyTrees.Count == 0) {
            // Open ground: nothing to strip.
            leafBudget = 0;
            return;
        }
        var emit = new ParticleSystem.EmitParams();
        while (leafBudget >= 1) {
            leafBudget--;
            TerrainTrees.Tree tree = nearbyTrees[(int)(random.NextFloat() * nearbyTrees.Count)];
            // Somewhere in the upper part of the crown.
            float angle = random.Range(0, Mathf.PI * 2);
            float reach = Mathf.Sqrt(random.NextFloat()) * tree.Radius * 0.7f;
            emit.position = tree.Base + new Vector3(
                Mathf.Sin(angle) * reach,
                random.Range(0.5f, 0.95f) * tree.Height,
                Mathf.Cos(angle) * reach
            );
            leaves.Emit(emit, 1);
        }
    }

    /// <summary>The ground under the camera, so spray skims it even when a capture lifts the camera high.</summary>
    static float GroundBelow(Vector3 eye) {
        bool isHit = Physics.Raycast(
            eye + Vector3.up,
            Vector3.down,
            out RaycastHit hit,
            400,
            Layers.MaskToolRaycast,
            QueryTriggerInteraction.Ignore
        );
        if (isHit) {
            return hit.point.y;
        }
        return eye.y - 1.7f;
    }

    static void SetRate(ParticleSystem system, float perSecond) {
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = perSecond;
    }

    static void PlaceSource(ParticleSystem system, Vector3 centre, Vector3 size) {
        system.transform.position = centre;
        ParticleSystem.ShapeModule shape = system.shape;
        shape.scale = size;
    }

    /// <summary>Unity needs all three velocity axes in one curve mode.</summary>
    static void SetVelocity(ParticleSystem.VelocityOverLifetimeModule velocity, Vector3 wind) {
        velocity.x = new ParticleSystem.MinMaxCurve(wind.x * 0.8f, wind.x * 1.1f);
        velocity.y = new ParticleSystem.MinMaxCurve(-0.3f, 0.6f);
        velocity.z = new ParticleSystem.MinMaxCurve(wind.z * 0.8f, wind.z * 1.1f);
    }

    public void Dispose() {
        GameMaterials.Destroy(host, leafMaterial, sheetMaterial);
    }
}
