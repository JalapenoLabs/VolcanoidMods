// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Weather.Audio;
using JalapenoLabs.Weather.Forecast;
using JalapenoLabs.Weather.Visuals;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Lightning;

/// <summary>
/// A thunderstorm's lightning: it brings each strike of the storm's schedule down where and when the schedule says,
/// lights the world with it, sends its thunder, and (on the host) hurts what it hits.
///
/// Every player computes the same schedule from the synced clock (<see cref="LightningSchedule"/>), so a bolt hits the
/// same spot at the same moment for everyone with the mod. Where a bolt lands is mostly luck; lightning prefers tall
/// things, so a strike close to a player, a cog or a drillship jumps to it.
///
/// Damage follows the game's rules: only the host deals damage (the game's Health ignores damage anywhere else), so a
/// strike hurts cogs and the host's own player, out in the open. Other players see the same bolts but take no damage
/// from them: the host can't know whether their copy of the game shows the lightning, and a hit from a bolt nobody
/// could see would be unfair.
/// </summary>
internal sealed class LightningStrikes : IDisposable {
    /// <summary>How close a strike must land to a player, cog or drillship to jump to it.</summary>
    const float AttractionRadius = 25;

    /// <summary>How close to a bolt's landing point the middle of a player or cog must be for it to be hurt.</summary>
    const float DamageRadius = 9;

    /// <summary>A strike takes up to this share of the struck player's or cog's health, scaled by its power.</summary>
    const float DamageShare = 0.6f;

    const float CloudBase = 250;
    const int MaxBolts = 5;

    /// <summary>A later lightning flash outshines an earlier one this far away, in metres.</summary>
    const float FlashReach = 900;

    /// <summary>
    /// How much of a flash's light reaches a sheltered player: a flicker at the windows, not daylight. Lightning's
    /// lights cast no shadows (URP gives shadows to one directional light, the sun), so at full strength they would
    /// light a drillship's cabin straight through its hull.
    /// </summary>
    const float ShelteredFlashShare = 0.15f;

    /// <summary>Unity's built-in sprite shader, which takes no fog (see <see cref="CreateBoltMaterial"/>).</summary>
    const string BoltShaderName = "Sprites/Default";

    readonly GameObject host;
    readonly Material boltMaterial;
    readonly Material sparkMaterial;
    readonly List<BoltEffect> bolts = new List<BoltEffect>();
    readonly Light flashLight;
    readonly StrikeArea area;
    List<Strike> strikes = new List<Strike>();
    ulong scheduledSeed = ulong.MaxValue;
    int nextStrike;
    double lastNow = double.NaN;

    public LightningStrikes(Transform parent, StormTextures textures) {
        // The sparks: the game's 'ParticleGlow' configuration, unlit and additive, far brighter than white so the bloom
        // catches them. They only ever burst where a bolt lands nearby, so the game's fog on them does no harm.
        sparkMaterial = GameMaterials.CloneVariant(
            GameMaterials.ParticlesUnlit,
            "ParticleGlow",
            "lightning sparks",
            "_SURFACE_TYPE_TRANSPARENT"
        );
        if (sparkMaterial == null) {
            throw new InvalidOperationException("the game has no transparent unlit particle material to build on");
        }
        sparkMaterial.SetTexture(GameMaterials.BaseMapId, textures.RainStreak);
        sparkMaterial.SetFloat(GameMaterials.SourceBlendId, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        sparkMaterial.SetFloat(GameMaterials.DestinationBlendId, (float)UnityEngine.Rendering.BlendMode.One);
        sparkMaterial.SetColor(GameMaterials.BaseColorId, new Color(6, 5, 3.5f, 1));
        boltMaterial = CreateBoltMaterial(textures, sparkMaterial);

        host = new GameObject("Weather.Lightning") { layer = Layers.MainZone };
        host.transform.SetParent(parent, false);
        var flashObject = new GameObject("Flash") { layer = Layers.MainZone };
        flashObject.transform.SetParent(host.transform, false);
        flashLight = flashObject.AddComponent<Light>();
        flashLight.type = LightType.Directional;
        flashLight.color = new Color(0.8f, 0.85f, 1f);
        flashLight.shadows = LightShadows.None;
        flashLight.intensity = 0;
        flashLight.enabled = false;
        area = IslandArea();
        Debug.Log($"{WeatherMod.LogPrefix}Lightning can strike from ({area.MinX:0}, {area.MinZ:0}) "
            + $"to ({area.MaxX:0}, {area.MaxZ:0})");
    }

    /// <summary>
    /// The bolts' channels. They draw with Unity's built-in Sprites/Default shader, which the game ships and which
    /// takes no fog: through storm fog every particle shader fades a bolt a kilometre off to nothing (the game's fog
    /// at storm density hides 98% of what lies that far), yet real lightning shows through rain for kilometres, and
    /// strikes land anywhere on the island. The shader still tests depth, so hills hide what is behind them. Its colour is far
    /// brighter than white, so the bloom catches the bolt.
    /// </summary>
    static Material CreateBoltMaterial(StormTextures textures, Material fallback) {
        Shader shader = Shader.Find(BoltShaderName);
        if (shader == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}The game no longer ships {BoltShaderName}; lightning draws with "
                + "a particle shader, and the fog will hide distant bolts");
            var foggy = new Material(fallback) { name = "Weather.Lightning" };
            foggy.SetTexture(GameMaterials.BaseMapId, textures.Bolt);
            foggy.SetColor(GameMaterials.BaseColorId, new Color(7, 7.5f, 11, 1));
            return foggy;
        }
        return new Material(shader) {
            name = "Weather.Lightning",
            mainTexture = textures.Bolt,
            color = new Color(7, 7.5f, 11, 1),
            renderQueue = (int)RenderQueue.Transparent,
        };
    }

    /// <summary>The flash's brightness this frame, 0 to 1, as seen from the camera.</summary>
    public float Flash { get; private set; }

    /// <summary>Where this frame's brightest flash shines from, as a direction from the camera.</summary>
    public Vector3 FlashDirection { get; private set; } = Vector3.up;

    public void Update(
        in WeatherSample weather,
        double now,
        Camera camera,
        PlayerLocation location,
        WeatherAudio audio
    ) {
        if (weather.Storm.Seed != scheduledSeed) {
            strikes = LightningSchedule.StrikesOf(weather.Storm, area);
            scheduledSeed = weather.Storm.Seed;
            nextStrike = FirstStrikeAfter(now);
        } else if (double.IsNaN(lastNow) || now < lastNow || now - lastNow > 5) {
            // The clock jumped (a developer hold, or the game stalled): strikes in between are skipped, not replayed.
            nextStrike = FirstStrikeAfter(now);
        }
        lastNow = now;

        while (nextStrike < strikes.Count && strikes[nextStrike].Time <= now) {
            Fire(strikes[nextStrike++], camera, location, audio);
        }
        UpdateFlash(camera, location);
    }

    int FirstStrikeAfter(double now) {
        int index = 0;
        while (index < strikes.Count && strikes[index].Time <= now) {
            index++;
        }
        return index;
    }

    /// <summary>Brings one strike down: its bolt, its light, its thunder, and its damage.</summary>
    public void Fire(Strike strike, Camera camera, PlayerLocation location, WeatherAudio audio) {
        BoltEffect bolt = FreeBolt();
        if (bolt == null) {
            Debug.Log($"{WeatherMod.LogPrefix}Lightning skipped a strike: {MaxBolts} bolts already on screen");
            return;
        }
        var random = new StormRandom(strike.Seed ^ 0xB017UL);
        Vector3 skyPoint = new Vector3(strike.X, CloudBase + random.Range(20, 70), strike.Z);
        // The cloud deck's base where this camera sees it (the same rule as CloudDeck), for the light under it.
        float eyeHeight = camera != null ? camera.transform.position.y : 0;
        float cloudDeckBase = Mathf.Max(
            CloudDeck.LowestCloudBase,
            eyeHeight + CloudDeck.CloudHeightAboveCamera
        );
        if (!strike.ReachesGround) {
            // A flash in the clouds: if it shows a channel at all, it crawls along the cloud base.
            float angle = random.Range(0, Mathf.PI * 2);
            var crawl = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * random.Range(250, 800);
            Vector3 crawlStart = skyPoint - crawl / 2 + Vector3.down * 15;
            bolt.Fire(strike, crawlStart, crawlStart + crawl, isInCloud: true, cloudDeckBase);
            audio?.Thunder(skyPoint, strike.Power, strike.Seed, false, camera, location.Surface, location.Outdoors);
            return;
        }

        Vector3 ground = LandingPoint(strike, out Transform struck);
        Vector3 top = new Vector3(ground.x + random.Range(-120, 120), skyPoint.y, ground.z + random.Range(-120, 120));
        bolt.Fire(strike, top, ground, isInCloud: false, cloudDeckBase);
        audio?.Thunder(ground, strike.Power, strike.Seed, true, camera, location.Surface, location.Outdoors);
        if (struck != null) {
            Debug.Log($"{WeatherMod.LogPrefix}Lightning struck {struck.name} at {ground}");
        }
        if (NetworkServer.active) {
            DealDamage(ground, strike.Power, location);
        }
    }

    /// <summary>
    /// Where a bolt meets the ground (or the sea), or the top of the player, cog or drillship it jumps to.
    /// </summary>
    static Vector3 LandingPoint(Strike strike, out Transform struck) {
        var column = new Vector2(strike.X, strike.Z);
        Transform nearestTarget = null;
        float nearest = AttractionRadius;
        Vector3 target = Vector3.zero;

        foreach (Player player in Player.Players) {
            if (player != null) {
                Consider(player.transform, player.transform.position + Vector3.up * 1.9f);
            }
        }
        foreach (Agent agent in Agent.Instances) {
            if (agent != null && agent.Health is CogHealth cog && !cog.IsDead) {
                Consider(agent.transform, TopOf(agent.gameObject, agent.transform.position + Vector3.up * 2));
            }
        }
        foreach (TrainWagons wagons in TrainWagons.Instances) {
            if (wagons != null) {
                Consider(wagons.transform, TopOf(wagons.gameObject, wagons.transform.position + Vector3.up * 10));
            }
        }
        struck = nearestTarget;
        if (struck != null) {
            return target;
        }

        bool hasGround = Physics.Raycast(
            new Vector3(strike.X, 1200, strike.Z),
            Vector3.down,
            out RaycastHit hit,
            2000,
            Layers.MaskExplosionVisibility,
            QueryTriggerInteraction.Ignore
        );
        // Over the sea there is nothing to hit but the water, at sea level.
        return hasGround && hit.point.y > 0 ? hit.point : new Vector3(strike.X, 0, strike.Z);

        void Consider(Transform candidate, Vector3 top) {
            float distance = Vector2.Distance(column, new Vector2(top.x, top.z));
            if (distance < nearest) {
                nearest = distance;
                nearestTarget = candidate;
                target = top;
            }
        }
    }

    /// <summary>The highest point of an object's colliders, above its middle.</summary>
    static Vector3 TopOf(GameObject target, Vector3 fallback) {
        bool hasBounds = false;
        var bounds = new Bounds();
        foreach (Collider collider in target.GetComponentsInChildren<Collider>()) {
            if (collider.isTrigger) {
                continue;
            }
            if (hasBounds) {
                bounds.Encapsulate(collider.bounds);
            } else {
                bounds = collider.bounds;
                hasBounds = true;
            }
        }
        return hasBounds ? new Vector3(bounds.center.x, bounds.max.y, bounds.center.z) : fallback;
    }

    /// <summary>
    /// Hurts the host's own player (unless sheltered: the roof takes the strike) and every cog near where the bolt
    /// landed. Runs on the host only; the game ignores damage anywhere else.
    /// </summary>
    static void DealDamage(Vector3 point, float power, PlayerLocation location) {
        GameObject source = Island.Current != null ? Island.Current.gameObject : null;
        GameObject local = Player.Local;
        if (local != null && location.Exposure == Exposure.Outside
            && Vector3.Distance(local.transform.position, point) < DamageRadius
            && local.TryGetComponent(out PlayerHealth playerHealth) && !playerHealth.IsDead) {
            Hurt(playerHealth, point, power, source);
        }
        foreach (Agent agent in Agent.Instances) {
            if (agent == null || !(agent.Health is CogHealth cog) || cog.IsDead) {
                continue;
            }
            // Cogs inside a drillship are sheltered by its hull, as players are.
            if (agent.gameObject.layer == Layers.AgentInside) {
                continue;
            }
            if (Vector3.Distance(agent.transform.position, point) < DamageRadius) {
                Hurt(cog, point, power, source);
            }
        }
    }

    static void Hurt(Health health, Vector3 point, float power, GameObject source) {
        float damage = health.MaxHP * DamageShare * power;
        // A non-null source matters: the game's damage log dereferences it.
        health.Events.Raise(new DamageEvent(
            damage,
            DamageType.Electricity,
            point,
            Vector3.up,
            source != null ? source : health.gameObject,
            null
        ));
        Debug.Log($"{WeatherMod.LogPrefix}Lightning hit {health.name} for {damage:0} of {health.MaxHP} health");
    }

    BoltEffect FreeBolt() {
        foreach (BoltEffect bolt in bolts) {
            if (!bolt.IsActive) {
                return bolt;
            }
        }
        if (bolts.Count >= MaxBolts) {
            return null;
        }
        var created = new BoltEffect(host.transform, boltMaterial, sparkMaterial);
        bolts.Add(created);
        return created;
    }

    /// <summary>
    /// The sky-wide flash: each bolt lights the whole landscape for an instant, brighter the closer it is. A
    /// directional light from the brightest bolt's direction does the lighting; <see cref="Flash"/> lets the sky and
    /// ambient light join in.
    /// </summary>
    void UpdateFlash(Camera camera, PlayerLocation location) {
        // None of the storm's light reaches underground, and only a little gets under a roof.
        float lightShare = location.Surface * Mathf.Lerp(ShelteredFlashShare, 1, location.Outdoors);
        float flash = 0;
        float brightest = 0;
        Vector3 direction = Vector3.up;
        Vector3 eye = camera != null ? camera.transform.position : Vector3.zero;
        foreach (BoltEffect bolt in bolts) {
            if (!bolt.IsActive || !bolt.Update(lightShare, eye)) {
                continue;
            }
            Vector3 middle = (bolt.Top + bolt.Ground) / 2;
            float distance = Vector3.Distance(eye, middle);
            float seen = bolt.Brightness / (1 + (distance / FlashReach) * (distance / FlashReach));
            flash += seen;
            if (seen > brightest) {
                brightest = seen;
                direction = (middle - eye).normalized;
            }
        }
        Flash = Mathf.Clamp01(flash) * lightShare;
        FlashDirection = direction;

        flashLight.enabled = Flash > 0.001f;
        flashLight.intensity = Flash * 1.8f;
        flashLight.transform.rotation = Quaternion.LookRotation(-direction);
    }

    /// <summary>For developer captures: brings a bolt down now, at a chosen spot.</summary>
    public void StrikeNow(Vector3 ground, double now, Camera camera, PlayerLocation location, WeatherAudio audio) {
        Fire(new Strike(now, true, ground.x, ground.z, 1, 0x5EED_B017UL), camera, location, audio);
    }

    /// <summary>
    /// The island's extent from above, from its surface terrain, so every player's strikes fall over the same area.
    /// </summary>
    static StrikeArea IslandArea() {
        foreach (Terrain terrain in Terrain.activeTerrains) {
            Vector3 position = terrain.GetPosition();
            // The surface terrain; the caves' terrains lie hundreds of metres below.
            if (position.y < -200) {
                continue;
            }
            Vector3 size = terrain.terrainData.size;
            return new StrikeArea(position.x, position.z, position.x + size.x, position.z + size.z);
        }
        Debug.LogWarning(
            $"{WeatherMod.LogPrefix}Found no surface terrain; lightning strikes the island's usual extent"
        );
        return new StrikeArea(-1000, -1000, 1048, 1048);
    }

    public void Dispose() {
        foreach (BoltEffect bolt in bolts) {
            bolt.Dispose();
        }
        GameMaterials.Destroy(host, boltMaterial, sparkMaterial);
    }
}
