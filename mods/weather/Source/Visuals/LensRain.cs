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
/// </summary>
internal sealed class LensRain : IDisposable {
    const int MaxDrops = 48;
    const float HeaviestDropsPerSecond = 9;

    /// <summary>Below the HUD's canvases, which sit at the default order.</summary>
    const int SortingOrder = -10;

    struct Drop {
        public float Age;
        public float Life;
        public float SlideSpeed;
        public bool IsActive;
    }

    readonly GameObject host;
    readonly Canvas canvas;
    readonly RawImage[] images = new RawImage[MaxDrops];
    readonly Drop[] drops = new Drop[MaxDrops];
    float spawnBudget;

    /// <summary>For developer logs.</summary>
    public string Describe() {
        int active = Array.FindAll(drops, drop => drop.IsActive).Length;
        int first = Array.FindIndex(drops, drop => drop.IsActive);
        string sample = first >= 0
            ? $" first at {images[first].rectTransform.anchoredPosition} size {images[first].rectTransform.sizeDelta} "
                + $"colour {images[first].color} scale {images[first].rectTransform.lossyScale}"
            : "";
        return $"lens: {active} drops, canvas {(canvas.enabled ? "on" : "off")} on {Screen.width}x{Screen.height}"
            + $" ({canvas.renderMode}, order {canvas.sortingOrder}){sample}";
    }

    StormRandom random = new StormRandom(0x1E25_D20FUL);

    public LensRain(Transform parent, StormTextures textures) {
        host = new GameObject("Weather.LensRain");
        host.transform.SetParent(parent, false);
        canvas = host.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        int tiles = Art.WeatherArt.LensDropAtlasTiles;
        for (int index = 0; index < MaxDrops; index++) {
            var dropObject = new GameObject("Drop");
            dropObject.transform.SetParent(host.transform, false);
            RawImage image = dropObject.AddComponent<RawImage>();
            image.texture = textures.LensDrops;
            image.raycastTarget = false;
            int tile = index % (tiles * tiles);
            float tileSize = 1f / tiles;
            image.uvRect = new Rect(tile % tiles * tileSize, tile / tiles * tileSize, tileSize, tileSize);
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
            float fadeIn = Mathf.Clamp01(drop.Age / 0.08f);
            float fadeOut = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(drop.Life * 0.55f, drop.Life, drop.Age));
            images[index].color = new Color(1, 1, 1, fadeIn * fadeOut * 0.85f);
            // Heavier drops gather and run down the lens after a moment.
            if (drop.Age > 0.5f && drop.SlideSpeed > 0) {
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
        float size = random.Range(0.016f, 0.05f) * screenHeight;
        RectTransform rect = images[free].rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.sizeDelta = new Vector2(size, size * random.Range(1, 1.25f));
        rect.anchoredPosition = new Vector2(random.Range(0, Screen.width), random.Range(0, screenHeight));
        drops[free] = new Drop {
            Age = 0,
            Life = random.Range(1.2f, 3.6f),
            SlideSpeed = random.NextFloat() < 0.3f ? random.Range(0.02f, 0.07f) : 0,
            IsActive = true,
        };
        images[free].enabled = true;
    }

    public void Dispose() {
        Object.Destroy(host);
    }
}
