// Copyright © 2026 Jalapeno Labs
//
// Day/Night Cycle, a Volcanoids mod by Jalapeno Labs.
//
// Volcanoids is lit by one sun that never moves. This mod adds a 30-minute day and a 20-minute night on the surface:
// dusk, a starry sky, a moon that runs through its phases every eight days, and a dark world where lamps and the
// flashlight matter. It is client-side only. Each player's copy darkens only their own view, nothing goes over the
// network, and players without the mod are unaffected. Players who both have it see the same sky, because every copy
// reads the time of day from the same clock (UTC, checked against Steam's servers).

using JalapenoLabs.DayNightCycle.Lighting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JalapenoLabs.DayNightCycle;

/// <summary>The game's entry point into the mod. Volcanoids creates it once at startup.</summary>
public sealed class DayNightCycleMod : GameMod {
    internal const string LogPrefix = "[DayNightCycle] ";

    DevOptions devOptions;

    public override void Load() {
        devOptions = DevOptions.FromCommandLine();
        Debug.Log($"{LogPrefix}Loaded version {Owner.Manifest.Version}. {devOptions.Describe()}");
        PrepareCaptureRun();
    }

    public override void OnMainMenuLoaded(Scene mainMenuScene) {
        PrepareCaptureRun();
    }

    public override void OnGameLoading(Scene gameScene, AsyncOperation gameSceneLoadOperation) {
        PrepareCaptureRun();
    }

    public override void OnGameLoaded(Scene gameScene) {
        PrepareCaptureRun();
        NightController.Create(gameScene, new SyncedClock(devOptions), devOptions);
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
