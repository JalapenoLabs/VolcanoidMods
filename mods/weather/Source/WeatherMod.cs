// Copyright © 2026 Jalapeno Labs
//
// Weather, a Volcanoids mod by Jalapeno Labs.
//
// Once an hour, almost on a timer, clouds roll in over the island and it rains for ten minutes: sometimes a light
// shower, sometimes a downpour, and a third of the time a thunderstorm whose lightning can strike players and cogs. It
// is client-side only: nothing goes over the network and players without the mod are unaffected. Players who both have
// it see the same storms, because every copy reads the weather from the same clock (UTC, checked against Steam's
// servers). It runs alongside Day/Night Cycle, and neither needs the other.

using UnityEngine;
using UnityEngine.SceneManagement;

namespace JalapenoLabs.Weather;

/// <summary>The game's entry point into the mod. Volcanoids creates it once at startup.</summary>
public sealed class WeatherMod : GameMod {
    internal const string LogPrefix = "[Weather] ";

    DevOptions devOptions;
    WeatherSettings settings;

    public override void Load() {
        devOptions = DevOptions.FromCommandLine();
        settings = WeatherSettings.Load(PersistentDataDir);
        Debug.Log($"{LogPrefix}Loaded version {Owner.Manifest.Version}, weather volume {settings.Volume:0.00}. "
            + devOptions.Describe());
        PrepareCaptureRun();
    }

    public override void OnMainMenuLoaded(Scene mainMenuScene) {
        PrepareCaptureRun();
        WeatherVolumeSlider.AddToOptionsScreens(settings);
    }

    public override void OnGameLoading(Scene gameScene, AsyncOperation gameSceneLoadOperation) {
        PrepareCaptureRun();
    }

    public override void OnGameLoaded(Scene gameScene) {
        PrepareCaptureRun();
        WeatherVolumeSlider.AddToOptionsScreens(settings);
        // The recorded rain ships in the mod's own folder, beside its assembly.
        string soundsDirectory = System.IO.Path.Combine(Owner.Path, "Sounds");
        WeatherController.Create(gameScene, new SyncedClock(devOptions), devOptions, settings, soundsDirectory);
    }

    /// <summary>
    /// Developer captures run unattended, usually on a hidden workspace. They stay silent, and they drop vsync: a
    /// hidden window gets few or no presents, so a vsync wait would crawl the loading screen. Re-applied at each step
    /// because the game applies the player's own vsync and volume options during startup.
    /// </summary>
    void PrepareCaptureRun() {
        if (!devOptions.IsCapturing) {
            return;
        }
        AudioListener.volume = 0;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 30;
    }
}
