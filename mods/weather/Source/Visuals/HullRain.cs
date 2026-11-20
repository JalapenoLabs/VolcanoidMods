// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// Rain on the drillships: drops splashing all over their roofs and modules, and water pouring off the edges of their
/// roofs in streams.
///
/// The rain's own drops only splash within a few dozen metres of the camera, where they collide with the world, so
/// from any farther off a drillship in a downpour would look dry. The hulls therefore get splashes of their own,
/// wherever the camera is: each lands on the topmost surface under a random spot over the hull, found by casting a ray
/// down from above the drillship, just where a raindrop would have struck.
///
/// A real splash is a hand's width across, a speck from fifty metres away; what shows of a downpour on a roof from
/// there is a pale fizz of spray over it. So splashes grow with their distance from the camera, keeping roughly the
/// size on screen they have up close, and a fine mist of spray hangs over the roofs in heavy rain.
/// </summary>
internal sealed class HullRain : IDisposable {
    /// <summary>Splashes per second on each square metre of hull, seen from above, in the heaviest rain.</summary>
    const float SplashesPerSquareMetre = 0.5f;

    /// <summary>Drillships farther than this from the camera are too far off for their rain to show.</summary>
    const float RainReach = 160;

    /// <summary>Splashes farther than this from the camera grow in proportion to their distance.</summary>
    const float TrueSizeDistance = 25;

    /// <summary>The share of splashes that also throw up a wisp of mist.</summary>
    const float MistChance = 0.06f;

    /// <summary>
    /// Run-off falls from this many places along each drillship's roof edges, each a steady stream of drops in heavy
    /// rain, like water pouring off a roof without gutters.
    /// </summary>
    const int DripPointsPerDrillship = 36;
    const float DripsPerPointPerSecond = 11;

    /// <summary>Only parts reaching this high above the drillship's base shed water from their tops.</summary>
    const float RoofHeight = 3;

    /// <summary>
    /// One drillship, prepared for the rain: its parts weighted by how much of the sky each covers, so every square
    /// metre of hull gets its share of splashes, and the points its run-off pours from.
    /// </summary>
    sealed class Hull {
        public readonly DrillshipExterior Exterior;
        public readonly float[] CumulativeArea;
        public readonly float Area;
        /// <summary>How far its highest part reaches above its root, so splash rays start above everything.</summary>
        public readonly float Rise;
        public readonly List<DripPoint> DripPoints = new List<DripPoint>();
        public float SplashBudget;
        public float DripBudget;

        public Hull(DrillshipExterior exterior) {
            Exterior = exterior;
            CumulativeArea = new float[exterior.Renderers.Count];
            float rootHeight = exterior.Root.position.y;
            for (int index = 0; index < exterior.Renderers.Count; index++) {
                Renderer renderer = exterior.Renderers[index];
                if (renderer != null) {
                    Bounds bounds = renderer.bounds;
                    Area += bounds.size.x * bounds.size.z;
                    Rise = Mathf.Max(Rise, bounds.max.y - rootHeight);
                }
                CumulativeArea[index] = Area;
            }
        }

        /// <summary>A part, chosen with a chance in proportion to its footprint.</summary>
        public Renderer PickPart(float chance) {
            int index = Array.BinarySearch(CumulativeArea, chance * Area);
            if (index < 0) {
                index = ~index;
            }
            return Exterior.Renderers[Mathf.Min(index, CumulativeArea.Length - 1)];
        }
    }

    readonly struct DripPoint {
        public readonly Transform Anchor;
        public readonly Vector3 LocalPosition;

        public DripPoint(Transform anchor, Vector3 localPosition) {
            Anchor = anchor;
            LocalPosition = localPosition;
        }
    }

    readonly DrillshipExteriors exteriors;
    readonly List<Hull> hulls = new List<Hull>();
    readonly GameObject host;
    readonly ParticleSystem splashes;
    readonly ParticleSystem mist;
    readonly ParticleSystem drips;
    readonly Material splashMaterial;
    readonly Material mistMaterial;
    readonly Material dripMaterial;
    int knownExteriorsVersion;
    StormRandom random = new StormRandom(0xD21B_5UL);

    public HullRain(Transform parent, StormTextures textures, DrillshipExteriors exteriors) {
        this.exteriors = exteriors;
        splashMaterial = GameMaterials.CloneLitSmoke("hull splashes");
        mistMaterial = GameMaterials.CloneLitSmoke("hull mist");
        dripMaterial = GameMaterials.CloneLitSmoke("hull drips");
        if (splashMaterial == null || mistMaterial == null || dripMaterial == null) {
            throw new InvalidOperationException("the game has no lit smoke material to build on");
        }
        splashMaterial.SetTexture(GameMaterials.BaseMapId, textures.Splash);
        splashMaterial.SetTexture(GameMaterials.BumpMapId, null);
        splashMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.6f, 1.65f, 1.75f, 0.75f));
        mistMaterial.SetTexture(GameMaterials.BaseMapId, textures.CloudAlbedo);
        mistMaterial.SetTexture(GameMaterials.BumpMapId, textures.CloudNormals);
        mistMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.3f, 1.33f, 1.4f, 0.22f));
        dripMaterial.SetTexture(GameMaterials.BaseMapId, textures.RainStreak);
        dripMaterial.SetTexture(GameMaterials.BumpMapId, null);
        dripMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.5f, 1.55f, 1.65f, 0.8f));

        host = new GameObject("Weather.HullRain") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        splashes = CreateSplashes();
        mist = CreateMist();
        drips = CreateDrips();
    }

    ParticleSystem CreateSplashes() {
        ParticleSystem system = CreateEmitter("Splashes", 1500);
        ParticleSystem.MainModule main = system.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.3f);
        main.startSpeed = 0;
        main.gravityModifier = 0;

        // A splash bursts open and fades.
        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.35f, 1, 1));
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0, 1) }
        );
        color.color = fade;

        var renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = splashMaterial;
        // Upright, turned to face the camera, as a crown of spray stands up from where the drop landed.
        renderer.renderMode = ParticleSystemRenderMode.VerticalBillboard;
        GameMaterials.UseLitSmokeStreams(renderer);
        return system;
    }

    /// <summary>Soft wisps of spray, thrown up where the rain beats down hardest, that rise and fade.</summary>
    ParticleSystem CreateMist() {
        ParticleSystem system = CreateEmitter("Mist", 1500);
        ParticleSystem.MainModule main = system.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1, 1.8f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(1.4f, 2.8f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        main.gravityModifier = 0;

        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.6f, 1, 1.3f));
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.25f), new GradientAlphaKey(0, 1) }
        );
        color.color = fade;
        // One puff of the cloud atlas each, chosen at random.
        ParticleSystem.TextureSheetAnimationModule sheet = system.textureSheetAnimation;
        sheet.enabled = true;
        sheet.numTilesX = Art.WeatherArt.CloudAtlasTiles;
        sheet.numTilesY = Art.WeatherArt.CloudAtlasTiles;
        sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0);
        sheet.startFrame = new ParticleSystem.MinMaxCurve(0, 0.9999f);

        var renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = mistMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        GameMaterials.UseLitSmokeStreams(renderer);
        return system;
    }

    ParticleSystem CreateDrips() {
        ParticleSystem system = CreateEmitter("Drips", 2000);
        ParticleSystem.MainModule main = system.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 1.8f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.07f);
        main.gravityModifier = 1;

        var renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = dripMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.12f;
        GameMaterials.UseLitSmokeStreams(renderer);
        return system;
    }

    /// <summary>A particle system that only ever emits when told to, at the spots this class picks.</summary>
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
        main.startColor = new Color(0.5f, 0.5f, 0.5f, 1);
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0;
        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = false;
        var renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        system.Play();
        return system;
    }

    /// <summary>For developer logs: how many drillships get rain, and how much of it is falling right now.</summary>
    public string Describe() {
        float area = 0;
        foreach (Hull hull in hulls) {
            area += hull.Area;
        }
        return $"hull rain: {hulls.Count} drillship(s), {area:0} m² seen from above, "
            + $"{splashes.particleCount} splashes, {mist.particleCount} mist, {drips.particleCount} drips";
    }

    /// <param name="rain">How hard it rains on the drillships, 0 to 1 (0 while the camera is underground).</param>
    /// <param name="centre">The camera's position; only drillships near it get rain on them.</param>
    public void Update(float rain, Vector3 centre, float deltaTime) {
        if (exteriors.Version != knownExteriorsVersion) {
            knownExteriorsVersion = exteriors.Version;
            RebuildHulls();
        }
        if (rain <= 0.01f) {
            foreach (Hull hull in hulls) {
                hull.SplashBudget = 0;
                hull.DripBudget = 0;
            }
            return;
        }
        foreach (Hull hull in hulls) {
            if (hull.Exterior.Root == null) {
                continue;
            }
            if ((hull.Exterior.Root.position - centre).sqrMagnitude > RainReach * RainReach) {
                continue;
            }
            hull.SplashBudget += rain * hull.Area * SplashesPerSquareMetre * deltaTime;
            hull.DripBudget += rain * hull.DripPoints.Count * DripsPerPointPerSecond * deltaTime;
            // Light rain patters; only a downpour beats up a mist.
            EmitSplashes(hull, centre, MistChance * rain * rain);
            EmitDrips(hull);
        }
    }

    void RebuildHulls() {
        hulls.Clear();
        foreach (DrillshipExterior exterior in exteriors.All) {
            if (exterior.Root == null || exterior.Renderers.Count == 0) {
                continue;
            }
            var hull = new Hull(exterior);
            AddDripPoints(hull);
            hulls.Add(hull);
        }
    }

    /// <param name="mistChance">The chance that a splash also throws up a wisp of mist.</param>
    void EmitSplashes(Hull hull, Vector3 centre, float mistChance) {
        var emit = new ParticleSystem.EmitParams();
        float rayStart = hull.Exterior.Root.position.y + hull.Rise + 1;
        while (hull.SplashBudget >= 1) {
            hull.SplashBudget--;
            Renderer part = hull.PickPart(random.NextFloat());
            if (part == null) {
                continue;
            }
            Bounds bounds = part.bounds;
            var origin = new Vector3(
                random.Range(bounds.min.x, bounds.max.x),
                rayStart,
                random.Range(bounds.min.z, bounds.max.z)
            );
            float reach = rayStart - bounds.min.y + 0.5f;
            bool isHit = Physics.Raycast(
                origin,
                Vector3.down,
                out RaycastHit hit,
                reach,
                Layers.MaskToolRaycast,
                QueryTriggerInteraction.Ignore
            );
            if (!isHit) {
                continue;
            }
            // A little larger than the rain's own splashes, which are only ever seen up close.
            float distance = Vector3.Distance(hit.point, centre);
            emit.position = hit.point + hit.normal * 0.05f;
            emit.startSize = random.Range(0.35f, 0.7f) * Mathf.Max(1, distance / TrueSizeDistance);
            splashes.Emit(emit, 1);
            if (random.NextFloat() < mistChance) {
                mist.Emit(new ParticleSystem.EmitParams { position = hit.point + hit.normal * 0.6f }, 1);
            }
        }
    }

    void EmitDrips(Hull hull) {
        var emit = new ParticleSystem.EmitParams();
        while (hull.DripBudget >= 1) {
            hull.DripBudget--;
            DripPoint point = hull.DripPoints[(int)(random.NextFloat() * hull.DripPoints.Count)];
            if (point.Anchor == null) {
                continue;
            }
            emit.position = point.Anchor.TransformPoint(point.LocalPosition);
            drips.Emit(emit, 1);
        }
    }

    /// <summary>
    /// Points along the upper edges of the hull's taller parts, just outside them, where the water on their roofs
    /// spills over and falls past their sides.
    /// </summary>
    void AddDripPoints(Hull hull) {
        float baseHeight = hull.Exterior.Root.position.y;
        var roofs = hull.Exterior.Renderers.FindAll(
            renderer => renderer != null && renderer.bounds.max.y > baseHeight + RoofHeight
        );
        if (roofs.Count == 0) {
            return;
        }
        for (int point = 0; point < DripPointsPerDrillship; point++) {
            Renderer renderer = roofs[(int)(random.NextFloat() * roofs.Count)];
            Bounds bounds = renderer.bounds;
            // A spot on one of the part's four upper edges, a hand's width outside it so the stream falls clear.
            bool isAlongX = random.NextFloat() < 0.5f;
            float along = random.Range(-0.9f, 0.9f);
            float side = random.NextFloat() < 0.5f ? -1 : 1;
            var offset = isAlongX
                ? new Vector3(along * bounds.extents.x, bounds.extents.y, side * (bounds.extents.z + 0.1f))
                : new Vector3(side * (bounds.extents.x + 0.1f), bounds.extents.y, along * bounds.extents.z);
            Vector3 world = bounds.center + offset;
            hull.DripPoints.Add(new DripPoint(renderer.transform, renderer.transform.InverseTransformPoint(world)));
        }
    }

    public void Dispose() {
        GameMaterials.Destroy(host, splashMaterial, mistMaterial, dripMaterial);
    }
}
