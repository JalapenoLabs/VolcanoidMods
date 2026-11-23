// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// Puddles that gather on flat ground while it rains, spread as the ground stays wet, ripple under the drops, and dry
/// up after.
///
/// Where puddles can form is fixed, like the hollows they fill: the ground is divided into cells, and each cell's own
/// seed decides whether it holds one, where, and how wet the ground must get before it appears. So every player sees
/// the same puddles, and the forecast's wetness (light rain only ever dampens the ground) decides how many there are
/// and how big. A spot only counts if the ground there is open, level, bare terrain: not grass (whose blades would
/// stand through it), not rock, not a building or a drillship, not under the sea, and well away from lava.
///
/// A puddle is a glossy, transparent quad laid on the ground, drawn with the game's transparent lit shader (its glass),
/// so it reflects the sky.
/// </summary>
internal sealed class Puddles : IDisposable {
    const float CellSize = 7;
    const float Reach = 70;
    const int MaxPuddles = 96;
    const float PuddleChance = 0.5f;
    const float SmallestPuddle = 1.5f;
    const float LargestPuddle = 5.5f;

    /// <summary>The most the ground may rise or fall under a puddle, or it would float or sink into a slope.</summary>
    const float Flatness = 0.15f;

    const float LavaClearance = 30;

    /// <summary>
    /// Each cell's survey costs five raycasts and a splat-map read, so only a few are done each frame: a camera racing
    /// over new ground (the cinematic camera) fills its surroundings in over a second or two instead of in a burst.
    /// </summary>
    const int CellsCheckedPerFrame = 16;

    /// <summary>Past this many surveyed cells, those far behind the camera are forgotten.</summary>
    const int MostRememberedCells = 2000;

    /// <summary>How high above the ground a puddle floats, against z-fighting.</summary>
    const float Lift = 0.025f;

    const float RipplesPerPuddlePerSecond = 3.5f;

    /// <summary>Puddles gather on dirt, sand and stone; where grass is painted this thickly, they don't.</summary>
    const float MostGrass = 0.55f;

    readonly struct Spot {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly float Size;
        /// <summary>The wetness at which this puddle starts to appear.</summary>
        public readonly float Threshold;
        public readonly int Tile;

        public Spot(Vector3 position, Quaternion rotation, float size, float threshold, int tile) {
            Position = position;
            Rotation = rotation;
            Size = size;
            Threshold = threshold;
            Tile = tile;
        }
    }

    readonly struct Showing {
        public readonly Spot Spot;
        public readonly float Appearance;
        public readonly float Distance;

        public Showing(Spot spot, float appearance, float distance) {
            Spot = spot;
            Appearance = appearance;
            Distance = distance;
        }
    }

    readonly GameObject host;
    readonly Material material;
    readonly Material rippleMaterial;
    readonly Mesh[] tileMeshes = new Mesh[Art.WeatherArt.PuddleAtlasTiles * Art.WeatherArt.PuddleAtlasTiles];
    readonly MeshFilter[] filters = new MeshFilter[MaxPuddles];
    readonly MeshRenderer[] renderers = new MeshRenderer[MaxPuddles];
    readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
    readonly ParticleSystem ripples;
    readonly Dictionary<long, Spot?> spots = new Dictionary<long, Spot?>();
    readonly List<Showing> showing = new List<Showing>();
    readonly List<LavaPool> lavaPools;
    readonly Terrain ground;
    readonly List<int> grassLayers = new List<int>();
    float rippleBudget;

    /// <summary>For developer logs: how many cells have been surveyed, held a puddle spot, and show one now.</summary>
    public string Describe() {
        int found = 0;
        foreach (Spot? spot in spots.Values) {
            if (spot.HasValue) {
                found++;
            }
        }
        return $"puddles: {Mathf.Min(showing.Count, MaxPuddles)} shown, {found} spots in {spots.Count} cells";
    }

    StormRandom rippleRandom = new StormRandom(0x2199_1E5UL);

    public Puddles(Transform parent, StormTextures textures, List<LavaPool> lavaPools) {
        this.lavaPools = lavaPools;
        foreach (Terrain terrain in Terrain.activeTerrains) {
            // The surface terrain; the caves' terrains lie hundreds of metres below.
            if (terrain.GetPosition().y > -200) {
                ground = terrain;
                break;
            }
        }
        if (ground != null) {
            TerrainLayer[] layers = ground.terrainData.terrainLayers;
            for (int index = 0; index < layers.Length; index++) {
                if (layers[index] != null && layers[index].name.ToLowerInvariant().Contains("grass")) {
                    grassLayers.Add(index);
                }
            }
        }
        // The game's 'GlassStations' configuration: transparent and lit.
        material = GameMaterials.CloneVariant(
            GameMaterials.Lit,
            "GlassStations",
            "puddles",
            "_SURFACE_TYPE_TRANSPARENT"
        );
        rippleMaterial = GameMaterials.CloneLitSmoke("puddle ripples");
        if (material == null || rippleMaterial == null) {
            throw new InvalidOperationException("the game has no transparent lit material to build on");
        }
        material.SetTexture(GameMaterials.BaseMapId, textures.Puddles);
        material.SetColor(GameMaterials.BaseColorId, new Color(0.14f, 0.13f, 0.12f, 0.9f));
        material.SetFloat(GameMaterials.SmoothnessId, 0.97f);
        material.SetFloat(GameMaterials.MetallicId, 0);
        // Plain alpha blending, so a puddle's reflection fades out with it at its edge.
        material.SetFloat(GameMaterials.SourceBlendId, (float)BlendMode.SrcAlpha);
        material.SetFloat(GameMaterials.DestinationBlendId, (float)BlendMode.OneMinusSrcAlpha);
        rippleMaterial.SetTexture(GameMaterials.BaseMapId, textures.Ripple);
        rippleMaterial.SetTexture(GameMaterials.BumpMapId, null);
        rippleMaterial.SetColor(GameMaterials.BaseColorId, new Color(1.3f, 1.35f, 1.45f, 0.4f));
        rippleMaterial.renderQueue = material.renderQueue + 2;

        host = new GameObject("Weather.Puddles") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        for (int tile = 0; tile < tileMeshes.Length; tile++) {
            tileMeshes[tile] = BuildTileMesh(tile);
        }
        for (int index = 0; index < MaxPuddles; index++) {
            var puddle = new GameObject("Puddle") { layer = Layers.MainZone };
            puddle.transform.SetParent(host.transform, false);
            filters[index] = puddle.AddComponent<MeshFilter>();
            renderers[index] = puddle.AddComponent<MeshRenderer>();
            renderers[index].sharedMaterial = material;
            renderers[index].shadowCastingMode = ShadowCastingMode.Off;
            renderers[index].enabled = false;
        }
        ripples = CreateRipples();
    }

    ParticleSystem CreateRipples() {
        var child = new GameObject("Ripples") { layer = Layers.MainZone };
        child.transform.SetParent(host.transform, false);
        var system = child.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = system.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = false;
        main.loop = true;
        main.maxParticles = 1500;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.8f);
        main.startSpeed = 0;
        main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.7f);
        main.startColor = new Color(0.5f, 0.5f, 0.5f, 1);
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0;
        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = false;
        // A ripple spreads from where the drop hit and fades.
        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.15f, 1, 1));
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) }
        );
        color.color = fade;
        var renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = rippleMaterial;
        renderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        GameMaterials.UseLitSmokeStreams(renderer);
        system.Play();
        return system;
    }

    /// <param name="wetness">How wet the ground is, 0 to 1 (the forecast's puddle depth).</param>
    /// <param name="rain">How hard it rains, 0 to 1, for the ripples.</param>
    /// <param name="centre">The camera's position; puddles are kept around it.</param>
    /// <param name="surface">0 underground, 1 on the surface.</param>
    public void Update(float wetness, float rain, Vector3 centre, float surface, float deltaTime) {
        wetness *= surface;
        showing.Clear();
        if (wetness > 0.005f) {
            SurveyCells(centre);
            GatherShowing(wetness, centre);
        }

        for (int index = 0; index < MaxPuddles; index++) {
            bool isShown = index < showing.Count;
            renderers[index].enabled = isShown;
            if (!isShown) {
                continue;
            }
            Showing puddle = showing[index];
            Transform transform = renderers[index].transform;
            // A puddle grows as the ground gets wetter, and shrinks as it dries.
            float size = puddle.Spot.Size * (0.4f + 0.6f * puddle.Appearance);
            transform.SetPositionAndRotation(puddle.Spot.Position, puddle.Spot.Rotation);
            transform.localScale = new Vector3(size, 1, size);
            filters[index].sharedMesh = tileMeshes[puddle.Spot.Tile];
            properties.SetColor(GameMaterials.BaseColorId, new Color(0.14f, 0.13f, 0.12f, 0.9f * puddle.Appearance));
            renderers[index].SetPropertyBlock(properties);
        }
        EmitRipples(rain * surface, deltaTime);
    }

    /// <summary>Works out a few more cells around the camera each frame, so moving never costs a burst.</summary>
    void SurveyCells(Vector3 centre) {
        int reachCells = Mathf.CeilToInt(Reach / CellSize);
        int centreX = Mathf.FloorToInt(centre.x / CellSize);
        int centreZ = Mathf.FloorToInt(centre.z / CellSize);
        if (spots.Count > MostRememberedCells) {
            ForgetFarCells(centreX, centreZ, reachCells * 2);
        }
        int checks = 0;
        for (int x = centreX - reachCells; x <= centreX + reachCells && checks < CellsCheckedPerFrame; x++) {
            for (int z = centreZ - reachCells; z <= centreZ + reachCells && checks < CellsCheckedPerFrame; z++) {
                long key = CellKey(x, z);
                if (spots.ContainsKey(key)) {
                    continue;
                }
                spots[key] = FindSpot(x, z);
                checks++;
            }
        }
    }

    /// <summary>Drops cells more than <paramref name="keepCells"/> from the camera's cell either way.</summary>
    void ForgetFarCells(int centreX, int centreZ, int keepCells) {
        var far = new List<long>();
        foreach (long key in spots.Keys) {
            int x = (int)(key >> 32);
            int z = (int)(uint)(key & 0xFFFFFFFF);
            if (Mathf.Abs(x - centreX) > keepCells || Mathf.Abs(z - centreZ) > keepCells) {
                far.Add(key);
            }
        }
        foreach (long key in far) {
            spots.Remove(key);
        }
    }

    void GatherShowing(float wetness, Vector3 centre) {
        int reachCells = Mathf.CeilToInt(Reach / CellSize);
        int centreX = Mathf.FloorToInt(centre.x / CellSize);
        int centreZ = Mathf.FloorToInt(centre.z / CellSize);
        for (int x = centreX - reachCells; x <= centreX + reachCells; x++) {
            for (int z = centreZ - reachCells; z <= centreZ + reachCells; z++) {
                if (!spots.TryGetValue(CellKey(x, z), out Spot? found) || !found.HasValue) {
                    continue;
                }
                Spot spot = found.Value;
                float filling = Mathf.InverseLerp(spot.Threshold, spot.Threshold + 0.15f, wetness);
                float appearance = Mathf.SmoothStep(0, 1, filling);
                float distance = Vector3.Distance(spot.Position, centre);
                if (appearance > 0.01f && distance < Reach) {
                    showing.Add(new Showing(spot, appearance, distance));
                }
            }
        }
        if (showing.Count > MaxPuddles) {
            showing.Sort((first, second) => first.Distance.CompareTo(second.Distance));
        }
    }

    /// <summary>
    /// The puddle a cell holds, if any: from the cell's own seed (so it is the same every storm and for everyone), on
    /// open, level terrain away from lava and above the sea.
    /// </summary>
    Spot? FindSpot(int cellX, int cellZ) {
        var random = new StormRandom(StormRandom.Mix((ulong)CellKey(cellX, cellZ)) ^ 0x9DD1_E5UL);
        if (random.NextFloat() > PuddleChance) {
            return null;
        }
        var point = new Vector3(
            (cellX + random.Range(0.2f, 0.8f)) * CellSize,
            0,
            (cellZ + random.Range(0.2f, 0.8f)) * CellSize
        );
        float size = random.Range(SmallestPuddle, LargestPuddle);
        float threshold = random.Range(0, 0.85f);
        int tile = (int)(random.NextFloat() * tileMeshes.Length);
        float turn = random.Range(0, 360);

        if (!TryGround(point, out RaycastHit ground) || ground.point.y < 0.5f || ground.normal.y < 0.975f) {
            return null;
        }
        // Level enough at its edges, too.
        float half = size * 0.45f;
        foreach (Vector3 corner in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back }) {
            if (!TryGround(ground.point + corner * half, out RaycastHit edge)
                || Mathf.Abs(edge.point.y - ground.point.y) > Flatness) {
                return null;
            }
        }
        if (GrassAt(ground.point) > MostGrass) {
            return null;
        }
        foreach (LavaPool pool in lavaPools) {
            Vector2 apart = new Vector2(pool.Centre.x - ground.point.x, pool.Centre.z - ground.point.z);
            if (apart.magnitude < pool.Radius + LavaClearance) {
                return null;
            }
        }
        Quaternion rotation = Quaternion.FromToRotation(Vector3.up, ground.normal) * Quaternion.Euler(0, turn, 0);
        return new Spot(ground.point + ground.normal * Lift, rotation, size, threshold, tile);
    }

    /// <summary>How much of the ground's paint at a point is grass, 0 to 1, from the terrain's splat map.</summary>
    float GrassAt(Vector3 point) {
        if (ground == null || grassLayers.Count == 0) {
            return 0;
        }
        TerrainData data = ground.terrainData;
        Vector3 local = point - ground.GetPosition();
        int resolution = data.alphamapResolution;
        int x = Mathf.Clamp((int)(local.x / data.size.x * resolution), 0, resolution - 1);
        int z = Mathf.Clamp((int)(local.z / data.size.z * resolution), 0, resolution - 1);
        float[,,] weights = data.GetAlphamaps(x, z, 1, 1);
        float grass = 0;
        foreach (int layer in grassLayers) {
            grass += weights[0, 0, layer];
        }
        return grass;
    }

    /// <summary>The terrain under a point, if the first thing a ray from above hits there is terrain.</summary>
    static bool TryGround(Vector3 point, out RaycastHit ground) {
        bool hasHit = Physics.Raycast(
            new Vector3(point.x, 1000, point.z),
            Vector3.down,
            out ground,
            2000,
            Layers.MaskToolRaycast,
            QueryTriggerInteraction.Ignore
        );
        return hasHit && ground.collider is TerrainCollider;
    }

    void EmitRipples(float rain, float deltaTime) {
        if (showing.Count == 0 || rain <= 0) {
            rippleBudget = 0;
            return;
        }
        int shown = Mathf.Min(showing.Count, MaxPuddles);
        rippleBudget += rain * shown * RipplesPerPuddlePerSecond * deltaTime;
        var emit = new ParticleSystem.EmitParams();
        while (rippleBudget >= 1) {
            rippleBudget--;
            Showing puddle = showing[(int)(rippleRandom.NextFloat() * shown)];
            float spread = puddle.Spot.Size * 0.3f * puddle.Appearance;
            emit.position = puddle.Spot.Position
                + new Vector3(rippleRandom.Range(-spread, spread), 0.01f, rippleRandom.Range(-spread, spread));
            ripples.Emit(emit, 1);
        }
    }

    static long CellKey(int x, int z) {
        return ((long)x << 32) ^ (uint)z;
    }

    /// <summary>A flat unit square facing up, showing one tile of the puddle atlas.</summary>
    static Mesh BuildTileMesh(int tile) {
        int tiles = Art.WeatherArt.PuddleAtlasTiles;
        float tileSize = 1f / tiles;
        var origin = new Vector2(tile % tiles, tile / tiles) * tileSize;
        return new Mesh {
            name = $"Weather.Puddle{tile}",
            vertices = new[] {
                new Vector3(-0.5f, 0, -0.5f),
                new Vector3(-0.5f, 0, 0.5f),
                new Vector3(0.5f, 0, 0.5f),
                new Vector3(0.5f, 0, -0.5f),
            },
            normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
            tangents = new[] {
                new Vector4(1, 0, 0, 1),
                new Vector4(1, 0, 0, 1),
                new Vector4(1, 0, 0, 1),
                new Vector4(1, 0, 0, 1),
            },
            uv = new[] {
                origin,
                origin + new Vector2(0, tileSize),
                origin + new Vector2(tileSize, tileSize),
                origin + new Vector2(tileSize, 0),
            },
            triangles = new[] { 0, 1, 2, 0, 2, 3 },
        };
    }

    public void Dispose() {
        foreach (Mesh mesh in tileMeshes) {
            Object.Destroy(mesh);
        }
        GameMaterials.Destroy(host, material, rippleMaterial);
    }
}
