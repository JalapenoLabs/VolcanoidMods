// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// Raindrops on the player's view: drops that land on the "lens", cling, sometimes slide down, and fade.
///
/// Only out in the open: never inside a drillship, a shelter or a mine, and never underground. More drops land when
/// the player looks up into the rain. They are drawn on a screen overlay canvas under the HUD, and follow the HUD's own
/// visibility rule (the player's Show HUD option and whatever screen is open), so hiding the HUD hides them too.
///
/// They are meant to be noticed, not looked at: each is mostly clear with a soft bright rim along its bottom, takes one
/// of sixteen shapes at random, is never more than about half opaque, and most are small. The occasional large one is
/// what sells it.
/// </summary>
internal sealed class LensRain : IDisposable {
    const int MaxDrops = 40;
    const float HeaviestDropsPerSecond = 6;

    /// <summary>The most opaque a drop ever gets, and the least: none is ever solid.</summary>
    const float MostOpacity = 0.55f;
    const float LeastOpacity = 0.2f;

    /// <summary>A drop's size, as a share of the screen's height, from the smallest to the largest.</summary>
    const float SmallestDrop = 0.008f;
    const float LargestDrop = 0.032f;

    /// <summary>Below the HUD's canvases, which sit at the default order.</summary>
    const int SortingOrder = -10;

    struct Drop {
        public float Age;
        public float Life;
        public float SlideSpeed;
        public float Opacity;
        public bool IsActive;
    }

    readonly GameObject host;
    readonly Canvas canvas;
    readonly RawImage[] images = new RawImage[MaxDrops];
    readonly Drop[] drops = new Drop[MaxDrops];
    float spawnBudget;

    /// <summary>For developer logs: how many drops are on the lens, and whether it is shown.</summary>
    public string Describe() {
        int active = Array.FindAll(drops, drop => drop.IsActive).Length;
        return $"lens: {active} drops, {(canvas.enabled ? "shown" : "hidden")}";
    }

    StormRandom random = new StormRandom(0x1E25_D20FUL);

    public LensRain(Transform parent, StormTextures textures) {
        host = new GameObject("Weather.LensRain");
        host.transform.SetParent(parent, false);
        canvas = host.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        for (int index = 0; index < MaxDrops; index++) {
            var dropObject = new GameObject("Drop");
            dropObject.transform.SetParent(host.transform, false);
            RawImage image = dropObject.AddComponent<RawImage>();
            image.texture = textures.LensDrops;
            image.raycastTarget = false;
            image.enabled = false;
            images[index] = image;
        }
    }

    /// <param name="rain">How hard it rains, 0 to 1.</param>
    /// <param name="outdoors">0 under a roof, 1 out in the open.</param>
    /// <param name="camera">The player's camera, to tell when they look up into the rain.</param>
    /// <param name="isForced">For developer captures: show the drops even though the HUD is hidden.</param>
    public void Update(float rain, float outdoors, Camera camera, float deltaTime, bool isForced) {
        bool isHudShown = isForced
            || (Options.Instance != null && Options.Instance.ShowHUD && InputManager.ShowHud);
        canvas.enabled = isHudShown;
        if (!isHudShown) {
            return;
        }

        float upward = camera != null ? Vector3.Dot(camera.transform.forward, Vector3.up) : 0;
        float exposure = rain * outdoors * Mathf.Clamp01(0.35f + upward * 1.3f);
        spawnBudget += HeaviestDropsPerSecond * exposure * deltaTime;
        while (spawnBudget >= 1) {
            spawnBudget--;
            Spawn();
        }

        float screenHeight = Screen.height;
        for (int index = 0; index < MaxDrops; index++) {
            if (!drops[index].IsActive) {
                continue;
            }
            ref Drop drop = ref drops[index];
            drop.Age += deltaTime;
            if (drop.Age >= drop.Life) {
                drop.IsActive = false;
                images[index].enabled = false;
                continue;
            }
            float fadeIn = Mathf.Clamp01(drop.Age / 0.15f);
            // A drop on a visor evaporates and thins gradually, over the second half of its life.
            float fadeOut = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(drop.Life * 0.45f, drop.Life, drop.Age));
            images[index].color = new Color(1, 1, 1, fadeIn * fadeOut * drop.Opacity);
            // Heavier drops gather and run down the lens after a moment.
            if (drop.Age > 0.6f && drop.SlideSpeed > 0) {
                RectTransform rect = images[index].rectTransform;
                rect.anchoredPosition += Vector2.down * (drop.SlideSpeed * screenHeight * deltaTime);
            }
        }
    }

    void Spawn() {
        int free = Array.FindIndex(drops, drop => !drop.IsActive);
        if (free < 0) {
            return;
        }
        float screenHeight = Screen.height;
        // Mostly small drops, now and then a big one.
        float size = Mathf.Lerp(SmallestDrop, LargestDrop, Mathf.Pow(random.NextFloat(), 2.2f)) * screenHeight;
        RawImage image = images[free];
        int tiles = Art.WeatherArt.LensDropAtlasTiles;
        int tile = (int)(random.NextFloat() * tiles * tiles);
        float tileSize = 1f / tiles;
        image.uvRect = new Rect(tile % tiles * tileSize, tile / tiles * tileSize, tileSize, tileSize);
        RectTransform rect = image.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        // Square: the shapes carry their own proportions, and stretching them is what made them look cartoonish.
        rect.sizeDelta = new Vector2(size, size);
        rect.anchoredPosition = new Vector2(random.Range(0, Screen.width), random.Range(0, screenHeight));
        drops[free] = new Drop {
            Age = 0,
            Life = random.Range(1.5f, 5),
            // Only the bigger drops gather enough water to run, and slowly.
            SlideSpeed = size > 0.018f * screenHeight && random.NextFloat() < 0.35f ? random.Range(0.01f, 0.04f) : 0,
            Opacity = random.Range(LeastOpacity, MostOpacity),
            IsActive = true,
        };
        image.enabled = true;
    }

    public void Dispose() {
        Object.Destroy(host);
    }
}
