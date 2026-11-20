// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Art;
using UnityEngine;
// The game defines its own global Task type, which hides .NET's.
using Tasks = System.Threading.Tasks;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// Every texture the storm draws with. They are painted from noise on a worker thread when the island loads (see
/// <see cref="WeatherArt"/>), then uploaded to the GPU on the main thread the first frame they are ready. Until then
/// the storm's visible parts wait.
/// </summary>
internal sealed class StormTextures : IDisposable {
    sealed class Painted {
        public ArtImage CloudAlbedo;
        public ArtImage CloudNormals;
        public ArtImage RainStreak;
        public ArtImage Splash;
        public ArtImage Ripple;
        public ArtImage Puddles;
        public ArtImage LensDrops;
        public ArtImage Bolt;
        public ArtImage Leaves;
    }

    readonly Tasks.Task<Painted> painting;
    bool hasLoggedFailure;

    public StormTextures() {
        painting = Tasks.Task.Run(() => {
            (ArtImage cloudAlbedo, ArtImage cloudNormals) = WeatherArt.CloudAtlas(11);
            return new Painted {
                CloudAlbedo = cloudAlbedo,
                CloudNormals = cloudNormals,
                RainStreak = WeatherArt.RainStreak(),
                Splash = WeatherArt.Splash(13),
                Ripple = WeatherArt.RippleRing(),
                Puddles = WeatherArt.PuddleAtlas(14),
                LensDrops = WeatherArt.LensDropAtlas(15),
                Bolt = WeatherArt.BoltGlow(),
                Leaves = WeatherArt.LeafAtlas(16),
            };
        });
    }

    public bool IsReady { get; private set; }

    public Texture2D CloudAlbedo { get; private set; }
    public Texture2D CloudNormals { get; private set; }
    public Texture2D RainStreak { get; private set; }
    public Texture2D Splash { get; private set; }
    public Texture2D Ripple { get; private set; }
    public Texture2D Puddles { get; private set; }
    public Texture2D LensDrops { get; private set; }
    public Texture2D Bolt { get; private set; }
    public Texture2D Leaves { get; private set; }

    /// <summary>Uploads the textures once painting has finished. Returns whether they are ready to use.</summary>
    public bool TryFinish() {
        if (IsReady) {
            return true;
        }
        if (!painting.IsCompleted) {
            return false;
        }
        if (painting.IsFaulted) {
            if (!hasLoggedFailure) {
                Debug.LogError(
                    $"{WeatherMod.LogPrefix}The storm's textures could not be painted: {painting.Exception}"
                );
                hasLoggedFailure = true;
            }
            return false;
        }

        Painted painted = painting.Result;
        CloudAlbedo = GameMaterials.Upload("CloudAlbedo", painted.CloudAlbedo);
        CloudNormals = GameMaterials.Upload("CloudNormals", painted.CloudNormals, isLinear: true);
        RainStreak = GameMaterials.Upload("RainStreak", painted.RainStreak);
        Splash = GameMaterials.Upload("Splash", painted.Splash);
        Ripple = GameMaterials.Upload("Ripple", painted.Ripple);
        Puddles = GameMaterials.Upload("Puddles", painted.Puddles);
        LensDrops = GameMaterials.Upload("LensDrops", painted.LensDrops);
        Bolt = GameMaterials.Upload("Bolt", painted.Bolt);
        Leaves = GameMaterials.Upload("Leaves", painted.Leaves);
        IsReady = true;
        Debug.Log($"{WeatherMod.LogPrefix}Storm textures ready");
        return true;
    }

    public void Dispose() {
        GameMaterials.Destroy(
            CloudAlbedo,
            CloudNormals,
            RainStreak,
            Splash,
            Ripple,
            Puddles,
            LensDrops,
            Bolt,
            Leaves
        );
    }
}
