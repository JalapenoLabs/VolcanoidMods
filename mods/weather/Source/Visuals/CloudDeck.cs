// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// The storm's clouds: heaped cumulus that roll in from upwind beneath the darkening storm sky.
///
/// Each cloud is a cluster of large puffs drawn with the game's lit smoke shader, so they are shaded by the real sun
/// (or Day/Night Cycle's moon) and by every lightning flash inside them: real light falling on them, not painted-on
/// brightness. The clouds live in a square field that drifts with the storm's wind and wraps around the camera, so the
/// sky is full wherever the player goes. Above and beyond them, the skybox rolls over into the game's painted storm
/// sky (<see cref="Lighting.StormAmbience"/>); the puffs give that sky depth and parallax, and catch the lightning.
///
/// The clouds roll in rather than appear: each fades and swells in as the forecast's cloud cover passes its own
/// threshold, and the thresholds rise from upwind to downwind, so the cloud front sweeps in over the player. Light
/// rain never reaches full cover, which leaves its sky broken.
/// </summary>
internal sealed class CloudDeck : IDisposable {
    const int CloudCount = 96;
    const int PuffsPerCloud = 7;

    /// <summary>The cloud field tiles every this many metres, centred on the camera.</summary>
    const float FieldSize = 3200;

    /// <summary>Clouds fade out towards the field's edge, so its wrap never shows.</summary>
    const float FadeStart = 0.72f;

    /// <summary>
    /// The cloud base: this far above the camera, and never below <see cref="LowestCloudBase"/>. The game's own fog
    /// thins everything by distance (particles cannot opt out of it), so clouds kept within a few hundred metres stay
    /// clearly visible overhead while distant ones melt into the haze, as real ones do.
    /// </summary>
    const float CloudHeightAboveCamera = 120;
    const float LowestCloudBase = 170;

    /// <summary>How far a cloud's fade-in reaches either side of its threshold, in units of cloud cover.</summary>
    const float ArrivalSoftness = 0.14f;

    /// <summary>Fair-weather cloud is brilliant white in the sun; storm cloud a heavy blue-grey.</summary>
    static readonly Color FairCloudTint = new Color(1.15f, 1.15f, 1.18f);
    static readonly Color StormCloudTint = new Color(0.45f, 0.47f, 0.52f);

    /// <summary>
    /// Puffs are drawn wider than they are tall, so the heaps read as banks of drifting cloud rather than balls.
    /// </summary>
    const float PuffWidth = 1.5f;
    const float PuffHeight = 0.8f;

    /// <summary>How opaque a fully arrived puff is: thin enough that the painted sky shows through it.</summary>
    const float PuffOpacity = 0.75f;

    readonly struct Puff {
        public readonly Vector3 Offset;
        public readonly float Size;
        public readonly byte Shade;
        public readonly uint Seed;

        public Puff(Vector3 offset, float size, byte shade, uint seed) {
            Offset = offset;
            Size = size;
            Shade = shade;
            Seed = seed;
        }
    }

    readonly struct Cloud {
        /// <summary>Where it sits in the field at the storm's start, from the field's corner.</summary>
        public readonly Vector2 Origin;
        /// <summary>Its own share of the arrival threshold (the rest comes from its place in the front).</summary>
        public readonly float Lateness;
        public readonly Puff[] Puffs;

        public Cloud(Vector2 origin, float lateness, Puff[] puffs) {
            Origin = origin;
            Lateness = lateness;
            Puffs = puffs;
        }
    }

    readonly GameObject host;
    readonly ParticleSystem puffSystem;
    readonly ParticleSystemRenderer puffRenderer;
    readonly ParticleSystem.Particle[] particles = new ParticleSystem.Particle[CloudCount * PuffsPerCloud];
    readonly Material puffMaterial;
    Cloud[] clouds;
    ulong layoutSeed = ulong.MaxValue;

    public CloudDeck(Transform parent, StormTextures textures) {
        puffMaterial = GameMaterials.CloneLitSmoke("cloud puffs");
        if (puffMaterial == null) {
            throw new InvalidOperationException("the game has no lit smoke material to build on");
        }
        puffMaterial.SetTexture(GameMaterials.BaseMapId, textures.CloudAlbedo);
        puffMaterial.SetTexture(GameMaterials.BumpMapId, textures.CloudNormals);
        puffMaterial.renderQueue = (int)RenderQueue.Transparent;

        // On the surface zone layer, which underground cameras don't render.
        host = new GameObject("Weather.Clouds") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        puffSystem = host.AddComponent<ParticleSystem>();
        puffSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = puffSystem.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = particles.Length;
        main.playOnAwake = false;
        main.loop = true;
        main.startLifetime = 1e6f;
        main.startSpeed = 0;
        main.startSize3D = true;
        ParticleSystem.EmissionModule emission = puffSystem.emission;
        emission.enabled = false;
        ParticleSystem.ShapeModule shape = puffSystem.shape;
        shape.enabled = false;
        // Each puff shows one of the atlas's sixteen shapes, picked by its seed and held for its whole life.
        ParticleSystem.TextureSheetAnimationModule sheet = puffSystem.textureSheetAnimation;
        sheet.enabled = true;
        sheet.numTilesX = Art.WeatherArt.CloudAtlasTiles;
        sheet.numTilesY = Art.WeatherArt.CloudAtlasTiles;
        sheet.animation = ParticleSystemAnimationType.WholeSheet;
        sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0, 0.9999f);
        sheet.cycleCount = 1;

        puffRenderer = host.GetComponent<ParticleSystemRenderer>();
        puffRenderer.sharedMaterial = puffMaterial;
        puffRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        puffRenderer.sortMode = ParticleSystemSortMode.Distance;
        puffRenderer.minParticleSize = 0;
        puffRenderer.maxParticleSize = 100;
        puffRenderer.shadowCastingMode = ShadowCastingMode.Off;
        puffRenderer.receiveShadows = false;
        GameMaterials.UseLitSmokeStreams(puffRenderer);
        puffSystem.Play();
    }

    /// <param name="weather">This moment's forecast.</param>
    /// <param name="now">The synced time, which drifts the clouds with the wind.</param>
    /// <param name="centre">Where the camera is; the cloud field wraps around it.</param>
    /// <param name="surface">0 underground (no sky to see), 1 on the surface.</param>
    public void Update(in WeatherSample weather, double now, Vector3 centre, float surface) {
        bool isVisible = surface > 0 && weather.Cover > 0.001f;
        puffRenderer.enabled = isVisible;
        if (!isVisible) {
            return;
        }
        if (layoutSeed != weather.Storm.Seed) {
            clouds = Layout(weather.Storm.Seed);
            layoutSeed = weather.Storm.Seed;
        }

        float stormDarkness = Mathf.Clamp01(weather.Darkness / 0.9f);
        puffMaterial.SetColor(GameMaterials.BaseColorId, Color.Lerp(FairCloudTint, StormCloudTint, stormDarkness));

        var windDirection = new Vector2(Mathf.Sin(weather.WindHeading), Mathf.Cos(weather.WindHeading));
        // Drift from the storm's own start, so every player's clouds sit in the same place.
        float drifted = (float)(now - weather.Storm.CloudsStart) * Mathf.Max(weather.WindSpeed, 2) * 0.8f;
        Vector2 drift = windDirection * drifted;
        var camera = new Vector2(centre.x, centre.z);
        float cloudBase = Mathf.Max(LowestCloudBase, centre.y + CloudHeightAboveCamera);
        float half = FieldSize / 2;
        int count = 0;
        foreach (Cloud cloud in clouds) {
            // The cloud's place relative to the camera, wrapped into the field around it.
            Vector2 world = cloud.Origin + drift;
            var relative = new Vector2(Wrap(world.x - camera.x, half), Wrap(world.y - camera.y, half));
            float distance = Mathf.Max(Mathf.Abs(relative.x), Mathf.Abs(relative.y)) / half;
            float edgeFade = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(FadeStart, 1, distance));

            // Upwind clouds arrive first: the front's position runs 0 (upwind edge) to 1 (downwind edge).
            float front = 0.5f + 0.5f * Vector2.Dot(relative, windDirection) / half;
            float threshold = 0.06f + 0.55f * front + 0.3f * cloud.Lateness;
            float arrival = Mathf.SmoothStep(
                0,
                1,
                Mathf.InverseLerp(threshold - ArrivalSoftness, threshold + ArrivalSoftness, weather.Cover)
            );
            float visibility = arrival * edgeFade * PuffOpacity;
            if (visibility <= 0.001f) {
                continue;
            }

            var cloudCentre = new Vector3(camera.x + relative.x, cloudBase, camera.y + relative.y);
            foreach (Puff puff in cloud.Puffs) {
                // Puffs swell as their cloud billows in.
                float size = puff.Size * (0.55f + 0.45f * arrival);
                particles[count++] = new ParticleSystem.Particle {
                    position = cloudCentre + puff.Offset * (0.7f + 0.3f * arrival),
                    startSize3D = new Vector3(size * PuffWidth, size * PuffHeight, size),
                    startColor = new Color32(puff.Shade, puff.Shade, puff.Shade, (byte)(255 * visibility)),
                    remainingLifetime = 1e6f,
                    startLifetime = 1e6f,
                    randomSeed = puff.Seed,
                };
            }
        }
        puffSystem.SetParticles(particles, count);
    }

    /// <summary>The storm's clouds, scattered through the field from its seed, each a heap of puffs.</summary>
    static Cloud[] Layout(ulong seed) {
        var random = new StormRandom(seed ^ 0xC10D_5EEDUL);
        var layout = new Cloud[CloudCount];
        for (int index = 0; index < CloudCount; index++) {
            var origin = new Vector2(random.Range(0, FieldSize), random.Range(0, FieldSize));
            float scale = random.Range(0.75f, 1.35f);
            var puffs = new Puff[PuffsPerCloud];
            for (int puff = 0; puff < PuffsPerCloud; puff++) {
                // The first puffs form the broad base; later ones pile up higher and nearer the middle.
                float height = puff / (float)(PuffsPerCloud - 1);
                float spread = (170 - 90 * height) * scale;
                var offset = new Vector3(
                    random.Range(-spread, spread),
                    height * 110 * scale + random.Range(-15, 15),
                    random.Range(-spread, spread)
                );
                float size = random.Range(170, 280) * scale * (1 - 0.3f * height);
                // The lit smoke applies vertex colour as an overlay, neutral at mid grey (128): the base of a heap
                // sits a little darker, its sunlit top a little brighter.
                var shade = (byte)Mathf.Clamp(random.Range(104, 124) + 30 * height, 0, 255);
                puffs[puff] = new Puff(offset, size, shade, random.NextUInt());
            }
            layout[index] = new Cloud(origin, random.NextFloat(), puffs);
        }
        return layout;
    }

    static float Wrap(float value, float half) {
        float span = half * 2;
        value = (value + half) % span;
        if (value < 0) {
            value += span;
        }
        return value - half;
    }

    public void Dispose() {
        Object.Destroy(host);
        Object.Destroy(puffMaterial);
    }
}
