// Copyright © 2026 Jalapeno Labs

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The rain itself: drops falling around the camera and splashing where they land, and a wider veil of rain beyond.
///
/// Near drops are stretched particles that fall from a box above the camera, blown by the storm's wind. They collide
/// with the world (terrain, rocks, buildings, drillships), so a roof keeps them off whoever is under it, and burst into
/// a splash wherever they hit: rain spatters across the ground and drums visibly on every drillship's roof and modules.
/// Farther out, a wider, sparser layer of longer drops fills the distance; it skips collision, since anything that
/// falls through the ground there is hidden by the ground itself, and the fog softens it. Everything is drawn with the
/// game's lit smoke shader, so the rain is dark under a dark sky and sparkles in a lamp's light or a lightning flash.
/// </summary>
internal sealed class RainFall : IDisposable {
    const float FallSpeed = 11;

    /// <summary>The share of drop impacts that throw up a wisp of spray.</summary>
    const float SprayChance = 0.07f;

    /// <summary>How much of the wind's speed the drops take on.</summary>
    const float WindDrag = 0.55f;

    const float NearDropsPerSecond = 4500;
    const float NearLifetime = 2.4f;
    const float NearArea = 56;
    const float NearHeight = 22;

    const float FarDropsPerSecond = 2600;
    const float FarLifetime = 5.5f;
    const float FarArea = 260;
    const float FarHeight = 50;

    readonly GameObject host;
    readonly ParticleSystem nearDrops;
    readonly ParticleSystem farDrops;
    readonly ParticleSystem splashes;
    readonly Material dropMaterial;
    readonly Material splashMaterial;
    readonly ParticleSystem spray;
    readonly Material sprayMaterial;

    public RainFall(Transform parent, StormTextures textures, ShelterVolumes shelters) {
        dropMaterial = GameMaterials.CloneLitSmoke("rain");
        if (dropMaterial == null) {
            throw new InvalidOperationException("the game has no lit smoke material to build on");
        }
        dropMaterial.SetTexture(GameMaterials.BaseMapId, textures.RainStreak);
        dropMaterial.SetTexture(GameMaterials.BumpMapId, null);
        // Brighter than white: in dim storm light a lit drop would otherwise be the same grey as the fog behind it,
        // where real rain catches the light and glints.
        dropMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.25f, 1.3f, 1.4f, 0.22f));
        splashMaterial = new Material(dropMaterial) { name = "Weather.Splash" };
        splashMaterial.SetTexture(GameMaterials.BaseMapId, textures.Splash);
        splashMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.5f, 1.55f, 1.65f, 0.65f));
        sprayMaterial = new Material(dropMaterial) { name = "Weather.Spray" };
        sprayMaterial.SetTexture(GameMaterials.BaseMapId, textures.CloudAlbedo);
        sprayMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.25f, 1.3f, 1.38f, 0.2f));

        host = new GameObject("Weather.Rain") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        // Wider than real drops: on screen a real 3 mm drop is a sub-pixel blur, so games draw the streak it smears
        // into instead. Far drops are wider again, so they still show at a distance.
        nearDrops = CreateDrops("NearDrops", NearDropsPerSecond, NearLifetime, NearArea, 0.012f, 0.022f, 0.06f);
        farDrops = CreateDrops("FarDrops", FarDropsPerSecond, FarLifetime, FarArea, 0.04f, 0.07f, 0.09f);
        ConfigureCollision(nearDrops);

        var splashObject = new GameObject("Splashes") { layer = Layers.MainZone };
        splashObject.transform.SetParent(nearDrops.transform, false);
        splashes = splashObject.AddComponent<ParticleSystem>();
        splashes.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ConfigureSplashes();
        var sprayObject = new GameObject("Spray") { layer = Layers.MainZone };
        sprayObject.transform.SetParent(nearDrops.transform, false);
        spray = sprayObject.AddComponent<ParticleSystem>();
        spray.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ConfigureSpray();
        ParticleSystem.SubEmittersModule subEmitters = nearDrops.subEmitters;
        subEmitters.enabled = true;
        subEmitters.AddSubEmitter(
            splashes,
            ParticleSystemSubEmitterType.Collision,
            ParticleSystemSubEmitterProperties.InheritNothing
        );
        // Now and then a drop's impact leaves a wisp of spray: in a downpour, every surface it hits smokes with it.
        subEmitters.AddSubEmitter(
            spray,
            ParticleSystemSubEmitterType.Collision,
            ParticleSystemSubEmitterProperties.InheritNothing,
            SprayChance
        );
        // However the drops get in (the far rain has no collision, the near rain's is approximate), none survive inside
        // a cabin or a shelter.
        shelters.Guard(nearDrops);
        shelters.Guard(farDrops);
        nearDrops.Play();
        farDrops.Play();
    }

    /// <summary>For developer logs: drops inside sheltered spaces right now, which should be none.</summary>
    public int CountDropsInside(ShelterVolumes shelters) {
        return shelters.CountInside(nearDrops) + shelters.CountInside(farDrops);
    }

    ParticleSystem CreateDrops(
        string name,
        float heaviestRate,
        float lifetime,
        float area,
        float smallest,
        float largest,
        float streakLength
    ) {
        var child = new GameObject(name) { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        var system = child.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = system.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = (int)(heaviestRate * lifetime * 1.1f);
        main.playOnAwake = false;
        main.loop = true;
        main.startLifetime = lifetime;
        main.startSpeed = 0;
        main.startSize = new ParticleSystem.MinMaxCurve(smallest, largest);
        // Neutral grey under the lit smoke shader's overlay colouring; the material's colour tints the rain.
        main.startColor = new Color(0.5f, 0.5f, 0.5f, 1);
        main.scalingMode = ParticleSystemScalingMode.Shape;
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0;

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(area, 1, area);

        ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        SetVelocity(velocity, Vector3.zero);

        var renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = dropMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        // Each streak is as long as the distance its drop falls in this many seconds.
        renderer.velocityScale = streakLength;
        renderer.lengthScale = 1;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        GameMaterials.UseLitSmokeStreams(renderer);
        return system;
    }

    static void ConfigureCollision(ParticleSystem system) {
        ParticleSystem.CollisionModule collision = system.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.mode = ParticleSystemCollisionMode.Collision3D;
        collision.quality = ParticleSystemCollisionQuality.Medium;
        collision.collidesWith = Layers.MaskToolRaycast;
        collision.lifetimeLoss = 1;
        collision.bounce = 0;
        collision.dampen = 1;
        collision.radiusScale = 0.2f;
        collision.enableDynamicColliders = true;
        // Medium quality only weighs this many nearby collision shapes, terrain first; a fully built drillship alone
        // has hundreds, and past the limit the rain would fall straight through its hull.
        collision.maxCollisionShapes = 2048;
    }

    void ConfigureSplashes() {
        ParticleSystem.MainModule main = splashes.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 6000;
        main.playOnAwake = false;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.16f, 0.28f);
        main.startSpeed = 0;
        main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
        main.startColor = new Color(0.5f, 0.5f, 0.5f, 1);

        ParticleSystem.EmissionModule emission = splashes.emission;
        emission.enabled = true;
        emission.rateOverTime = 0;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0, 1) });
        ParticleSystem.ShapeModule shape = splashes.shape;
        shape.enabled = false;

        // A splash bursts open and fades.
        ParticleSystem.SizeOverLifetimeModule size = splashes.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.35f, 1, 1));
        ParticleSystem.ColorOverLifetimeModule color = splashes.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0, 1) }
        );
        color.color = fade;

        var renderer = splashes.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = splashMaterial;
        // Upright, turned to face the camera, as a crown of spray stands up from where the drop landed.
        renderer.renderMode = ParticleSystemRenderMode.VerticalBillboard;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        GameMaterials.UseLitSmokeStreams(renderer);
    }

    /// <summary>Soft, low wisps of spray that rise a little where the rain beats down, and drift.</summary>
    void ConfigureSpray() {
        ParticleSystem.MainModule main = spray.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 1500;
        main.playOnAwake = false;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
        main.startSize = new ParticleSystem.MinMaxCurve(1.2f, 2.6f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        main.startColor = new Color(0.5f, 0.5f, 0.5f, 1);
        ParticleSystem.EmissionModule emission = spray.emission;
        emission.enabled = true;
        emission.rateOverTime = 0;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0, 1) });
        ParticleSystem.ShapeModule shape = spray.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.3f;
        ParticleSystem.ColorOverLifetimeModule color = spray.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.25f), new GradientAlphaKey(0, 1) }
        );
        color.color = fade;
        ParticleSystem.SizeOverLifetimeModule size = spray.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.6f, 1, 1.4f));
        // One puff of the cloud atlas each, chosen at random; the whole atlas on one particle is a cluster of puffs.
        ParticleSystem.TextureSheetAnimationModule sheet = spray.textureSheetAnimation;
        sheet.enabled = true;
        sheet.numTilesX = Art.WeatherArt.CloudAtlasTiles;
        sheet.numTilesY = Art.WeatherArt.CloudAtlasTiles;
        sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0);
        sheet.startFrame = new ParticleSystem.MinMaxCurve(0, 0.9999f);
        var renderer = spray.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = sprayMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        GameMaterials.UseLitSmokeStreams(renderer);
    }

    /// <param name="rain">How hard it rains, 0 to 1.</param>
    /// <param name="wind">The wind's velocity, metres per second.</param>
    /// <param name="centre">The camera's position; the rain falls around it.</param>
    /// <param name="surface">0 underground, 1 on the surface.</param>
    public void Update(float rain, Vector3 wind, Vector3 centre, float surface) {
        float strength = rain * surface;
        // Drops are heavy for their size; gusts lean them over, but less than the air moves.
        wind *= WindDrag;
        Follow(nearDrops, NearDropsPerSecond * strength, NearHeight, wind, centre);
        Follow(farDrops, FarDropsPerSecond * strength, FarHeight, wind, centre);
    }

    static void Follow(ParticleSystem system, float rate, float height, Vector3 wind, Vector3 centre) {
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = rate;
        // Spawn upwind, so drops blown sideways while they fall still land around the camera.
        float fallTime = height / FallSpeed;
        system.transform.position = centre + Vector3.up * height - wind * fallTime;
        SetVelocity(system.velocityOverLifetime, wind);
    }

    /// <summary>
    /// Falling with the wind. Unity insists the three axes share one curve mode, so all three are a range of two
    /// constants: the fall speed varies drop to drop, the wind is the same for all.
    /// </summary>
    static void SetVelocity(ParticleSystem.VelocityOverLifetimeModule velocity, Vector3 wind) {
        velocity.x = new ParticleSystem.MinMaxCurve(wind.x, wind.x);
        velocity.y = new ParticleSystem.MinMaxCurve(-FallSpeed * 1.1f, -FallSpeed * 0.9f);
        velocity.z = new ParticleSystem.MinMaxCurve(wind.z, wind.z);
    }

    public void Dispose() {
        GameMaterials.Destroy(host, dropMaterial, splashMaterial, sprayMaterial);
    }
}
