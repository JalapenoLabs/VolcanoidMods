// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// Great clouds of steam over the island's lava pools, billowing up high and filling the ground around them, and
/// surging when rain falls on the lava.
///
/// Each pool gets a column of huge puffs, drawn with the game's lit smoke shader and the storm's own cloud shapes, so
/// they catch the sun, the pool's glow and the lightning; they rise on the heat and lean with the wind. Around each
/// pool a local fog volume (the game's own fog component) thickens the air, so walking through the steam is genuinely
/// hard going: the far side of a pool disappears. The steam is always there, gently, and rain pouring onto the lava
/// boils off far more.
/// </summary>
internal sealed class LavaSteam : IDisposable {
    /// <summary>How strong the steam is with no rain, 0 to 1; rain adds the rest.</summary>
    const float CalmStrength = 0.4f;

    const float HeaviestPuffsPerSecond = 5.5f;
    const float RiseSpeed = 3.2f;

    /// <summary>Pools farther than this from the camera stop steaming, to keep the cost to the pools in view.</summary>
    const float SteamReach = 1100;

    /// <summary>How far past the pool's edge its steam fog reaches, and over how far it fades in.</summary>
    const float FogMargin = 22;
    const float FogBlend = 18;

    /// <summary>Thick enough that the far side of a 40 m pool is lost in it.</summary>
    const float SteamFogDensity = 0.055f;

    static readonly Color SteamFogColor = new Color(0.6f, 0.58f, 0.56f, 0.9f);

    /// <summary>
    /// Above the game's lava and blast-wave volumes (10), whose red haze the steam's white fog covers, and below its
    /// deploy, stun and armour effects (20 and up).
    /// </summary>
    const float FogPriority = 11;

    readonly struct Vent {
        public readonly LavaPool Pool;
        public readonly ParticleSystem Puffs;
        public readonly Volume Fog;

        public Vent(LavaPool pool, ParticleSystem puffs, Volume fog) {
            Pool = pool;
            Puffs = puffs;
            Fog = fog;
        }
    }

    readonly GameObject host;
    readonly Material material;
    readonly VolumeProfile profile;
    readonly List<Vent> vents = new List<Vent>();

    public LavaSteam(Scene scene, StormTextures textures, List<LavaPool> pools) {
        material = GameMaterials.CloneLitSmoke("lava steam");
        if (material == null) {
            throw new InvalidOperationException("the game has no lit smoke material to build on");
        }
        material.SetTexture(GameMaterials.BaseMapId, textures.CloudAlbedo);
        material.SetTexture(GameMaterials.BumpMapId, textures.CloudNormals);
        material.SetColor(GameMaterials.BaseColorId, new Color(1.05f, 1.02f, 1, 0.55f));

        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "Weather.LavaSteam";
        Fog fog = profile.Add<Fog>();
        fog.density.Override(SteamFogDensity);
        fog.color.Override(SteamFogColor);

        host = new GameObject("Weather.LavaSteam") { layer = Layers.MainZone };
        SceneManager.MoveGameObjectToScene(host, scene);
        foreach (LavaPool pool in pools) {
            vents.Add(new Vent(pool, CreatePuffs(pool), CreateFog(pool)));
        }
    }

    ParticleSystem CreatePuffs(LavaPool pool) {
        var child = new GameObject("Steam") { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        child.transform.position = pool.Centre;
        // The emitter's circle lies flat, across the pool.
        child.transform.rotation = Quaternion.Euler(90, 0, 0);
        var system = child.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = system.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = false;
        main.loop = true;
        main.maxParticles = 200;
        main.startLifetime = new ParticleSystem.MinMaxCurve(15, 24);
        main.startSpeed = 0;
        main.startSize = new ParticleSystem.MinMaxCurve(pool.Radius * 0.3f, pool.Radius * 0.55f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        main.startColor = new Color(0.55f, 0.55f, 0.55f, 1);
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0;
        ParticleSystem.ShapeModule shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = pool.Radius * 0.8f;

        ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        SetDrift(velocity, Vector3.zero);
        // Steam swells as it rises and cools, and thins out at the top.
        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.6f, 1, 2.2f));
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.15f), new GradientAlphaKey(0, 1) }
        );
        color.color = fade;
        ParticleSystem.RotationOverLifetimeModule rotation = system.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);
        ParticleSystem.TextureSheetAnimationModule sheet = system.textureSheetAnimation;
        sheet.enabled = true;
        sheet.numTilesX = Art.WeatherArt.CloudAtlasTiles;
        sheet.numTilesY = Art.WeatherArt.CloudAtlasTiles;
        sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0, 0.9999f);

        var renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        // Facing the camera's position and level with the world, so turning the head does not turn the steam (as for
        // the cloud puffs).
        renderer.alignment = ParticleSystemRenderSpace.Facing;
        renderer.allowRoll = false;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        renderer.maxParticleSize = 100;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        GameMaterials.UseLitSmokeStreams(renderer);
        system.Play();
        return system;
    }

    Volume CreateFog(LavaPool pool) {
        var child = new GameObject("SteamFog") { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        child.transform.position = pool.Centre + Vector3.up * 10;
        var sphere = child.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = pool.Radius + FogMargin;
        var volume = child.AddComponent<Volume>();
        volume.isGlobal = false;
        volume.blendDistance = FogBlend;
        volume.priority = FogPriority;
        volume.sharedProfile = profile;
        volume.weight = 0;
        return volume;
    }

    /// <param name="rain">How hard it rains, 0 to 1: rain on lava boils off into far more steam.</param>
    /// <param name="wind">The wind's velocity, metres per second; the steam leans with it.</param>
    /// <param name="centre">The camera's position; only pools within reach steam.</param>
    /// <param name="surface">0 underground, 1 on the surface.</param>
    public void Update(float rain, Vector3 wind, Vector3 centre, float surface) {
        float strength = Mathf.Lerp(CalmStrength, 1, rain);
        foreach (Vent vent in vents) {
            bool isNear = (vent.Pool.Centre - centre).sqrMagnitude < SteamReach * SteamReach;
            ParticleSystem.EmissionModule emission = vent.Puffs.emission;
            emission.rateOverTime = isNear ? HeaviestPuffsPerSecond * strength * surface : 0;
            SetDrift(vent.Puffs.velocityOverLifetime, wind * 0.6f);
            vent.Fog.weight = strength * surface;
        }
    }

    /// <summary>Rising on the heat, drifting with the wind; Unity needs all three axes in one curve mode.</summary>
    static void SetDrift(ParticleSystem.VelocityOverLifetimeModule velocity, Vector3 wind) {
        velocity.x = new ParticleSystem.MinMaxCurve(wind.x, wind.x);
        velocity.y = new ParticleSystem.MinMaxCurve(RiseSpeed * 0.7f, RiseSpeed * 1.3f);
        velocity.z = new ParticleSystem.MinMaxCurve(wind.z, wind.z);
    }

    public void Dispose() {
        GameMaterials.Destroy(host, material, profile);
    }
}
