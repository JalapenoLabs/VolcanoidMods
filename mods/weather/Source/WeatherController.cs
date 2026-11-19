// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.Weather.Audio;
using JalapenoLabs.Weather.Forecast;
using JalapenoLabs.Weather.Lighting;
using JalapenoLabs.Weather.Lightning;
using JalapenoLabs.Weather.Visuals;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace JalapenoLabs.Weather;

/// <summary>
/// Runs the weather for one loaded game. It lives in the island scene, so it goes away with it.
///
/// Each frame it reads the forecast from the synced clock, works out where the player is, and hands both to every part
/// of the storm. Parts are independent: one that fails to start (say, a game update removed something it needs) costs
/// only itself.
/// </summary>
internal sealed class WeatherController : MonoBehaviour {
    enum Phase {
        Clear,
        RollingIn,
        Raining,
        Clearing,
    }

    readonly PlayerLocation location = new PlayerLocation();
    readonly List<IDisposable> parts = new List<IDisposable>();

    SyncedClock clock;
    DevOptions devOptions;
    WeatherSample weather;
    Camera viewCamera;
    StormSun sun;
    StormAmbience ambience;
    StormVolume volume;
    WeatherAudio audio;
    StormTextures textures;
    CloudDeck clouds;
    RainFall rain;
    LightningStrikes lightning;
    Puddles puddles;
    LensRain lensRain;
    WetSurfaces wetSurfaces;
    StormSea sea;
    LavaSteam steam;
    bool hasStartedVisuals;
    Phase? lastPhase;

    public SyncedClock Clock => clock;

    public WeatherSample Weather => weather;

    public PlayerLocation Location => location;

    /// <summary>
    /// Developer captures set this to where a lifted camera really renders from, so the rain falls and the clouds
    /// centre around it rather than around the player far below; null otherwise.
    /// </summary>
    public Vector3? EyeOverride { get; set; }

    public static WeatherController Create(Scene gameScene, SyncedClock clock, DevOptions devOptions) {
        var host = new GameObject("Weather");
        SceneManager.MoveGameObjectToScene(host, gameScene);
        var controller = host.AddComponent<WeatherController>();
        controller.clock = clock;
        controller.devOptions = devOptions;
        return controller;
    }

    void Start() {
        Scene scene = gameObject.scene;
        weather = StormSchedule.Sample(clock.Now);
        LogBaseline("at load");

        Light sunLight = RenderSettings.sun;
        if (sunLight != null) {
            sun = Start(() => new StormSun(sunLight));
        } else {
            Debug.LogWarning($"{WeatherMod.LogPrefix}The island has no sun light; storms won't dim it");
        }
        ambience = Start(() => new StormAmbience());
        volume = Own(Start(() => new StormVolume(scene)));
        audio = Own(Start(() => new WeatherAudio(transform, devOptions.SoundDumpDirectory)));
        sea = Own(Start(() => new StormSea()));
        textures = Own(Start(() => new StormTextures()));

        if (devOptions.IsSurveying) {
            Dev.SceneSurvey.Log();
        }
        if (devOptions.IsCapturing) {
            gameObject.AddComponent<Dev.CaptureDirector>().Begin(this, devOptions);
        }

        Application.onBeforeRender += OnBeforeRender;
        RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
        RenderPipelineManager.endContextRendering += OnEndContextRendering;
    }

    /// <summary>Creates one part of the storm, logging (not throwing) if it can't start.</summary>
    static TPart Start<TPart>(Func<TPart> create) where TPart : class {
        try {
            return create();
        } catch (Exception exception) {
            Debug.LogError($"{WeatherMod.LogPrefix}{typeof(TPart).Name} could not start and stays off: {exception}");
            return null;
        }
    }

    TPart Own<TPart>(TPart part) where TPart : class, IDisposable {
        if (part != null) {
            parts.Add(part);
        }
        return part;
    }

    /// <summary>
    /// Starts the storm's visible parts once their textures are painted (on a worker thread, so loading doesn't stall).
    /// </summary>
    void StartVisuals() {
        if (hasStartedVisuals || textures == null || !textures.TryFinish()) {
            return;
        }
        clouds = Own(Start(() => new CloudDeck(transform, textures)));
        rain = Own(Start(() => new RainFall(transform, textures)));
        lightning = Own(Start(() => new LightningStrikes(transform, textures)));
        List<LavaPool> lavaPools = LavaPools.FindOnSurface();
        puddles = Own(Start(() => new Puddles(transform, textures, lavaPools)));
        lensRain = Own(Start(() => new LensRain(transform, textures)));
        wetSurfaces = Own(Start(() => new WetSurfaces(transform, textures)));
        steam = Own(Start(() => new LavaSteam(gameObject.scene, textures, lavaPools)));
        hasStartedVisuals = true;
    }

    void Update() {
        double now = clock.Now;
        weather = StormSchedule.Sample(now);
        viewCamera = CameraPriority.GetActiveCamera(0);
        location.Update(viewCamera, Time.unscaledDeltaTime);
        float surface = location.Surface;
        StartVisuals();

        volume?.Update(weather.Cover * surface, weather.Darkness, weather.Rain);
        if (viewCamera != null) {
            Vector3 eye = EyeOverride ?? viewCamera.transform.position;
            Vector3 wind = new Vector3(Mathf.Sin(weather.WindHeading), 0, Mathf.Cos(weather.WindHeading))
                * weather.WindSpeed;
            audio?.Update(weather.Rain, weather.WindSpeed, surface, location.Outdoors, eye);
            clouds?.Update(in weather, now, eye, surface);
            rain?.Update(weather.Rain, wind, eye, surface);
            lightning?.Update(in weather, now, viewCamera, location, audio);
            puddles?.Update(weather.Wetness, weather.Rain, eye, surface, Time.deltaTime);
            lensRain?.Update(weather.Rain * surface, location.Outdoors, viewCamera, Time.unscaledDeltaTime);
            wetSurfaces?.Update(weather.Wetness, weather.Rain * surface, eye, Time.deltaTime);
            steam?.Update(weather.Rain, wind, eye, surface);
        }
        // The sea answers the storm's wind, not the player's whereabouts: it is the same sea underground or not.
        sea?.Update(weather.Cover * Mathf.Clamp01(weather.WindSpeed / 9), weather.WindHeading);
        LogPhaseChange();
    }

    /// <summary>
    /// For developer captures: if a thunderstorm is under way, brings a bolt down where the given view can see it, so
    /// its photograph shows lightning. Returns whether it did.
    /// </summary>
    public bool StrikeInView(Pose view, float fieldOfView) {
        if (lightning == null || weather.Electric <= 0) {
            return false;
        }
        // A few hundred metres ahead and a little to the side, so the bolt stands clear of the frame's centre.
        Vector3 ahead = Vector3.ProjectOnPlane(view.rotation * Vector3.forward, Vector3.up).normalized;
        Vector3 side = Vector3.Cross(Vector3.up, ahead);
        Vector3 spot = view.position + ahead * 380 + side * 90;
        lightning.StrikeNow(spot, clock.Now, viewCamera, location, audio);
        return true;
    }

    void OnBeforeRender() {
        float surface = location.Surface;
        sun?.Apply(weather.Darkness * surface, weather.Cover * surface);
    }

    void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
        float surface = location.Surface;
        float flash = lightning != null ? lightning.Flash : 0;
        ambience?.Apply(weather.Darkness * surface, weather.Cover * surface, flash);
    }

    void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
        ambience?.Restore();
    }

    void OnDestroy() {
        Application.onBeforeRender -= OnBeforeRender;
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
        RenderPipelineManager.endContextRendering -= OnEndContextRendering;
        ambience?.Restore();
        sun?.Release();
        foreach (IDisposable part in parts) {
            part.Dispose();
        }
        parts.Clear();
    }

    /// <summary>
    /// Logs the game's own ambient light, read between renders. The smoke test compares these: if the value ever moves,
    /// the storm has leaked into the game's state instead of only changing what the game renders. It is read through
    /// the getter (scaled by the ambient intensity), the same quantity Day/Night Cycle logs, so the two compare.
    /// </summary>
    public void LogBaseline(string when) {
        float baseline = RenderSettings.ambientProbe[0, 0];
        string puddleReport = puddles != null ? $", {puddles.Describe()}" : "";
        Debug.Log($"{WeatherMod.LogPrefix}Storm {when}: {weather.Storm.Kind}, cover {weather.Cover:0.00}, "
            + $"rain {weather.Rain:0.00}, wetness {weather.Wetness:0.00}{puddleReport}, "
            + $"baseline ambient {baseline:0.000}");
    }

    void LogPhaseChange() {
        Phase phase;
        double now = clock.Now;
        Storm storm = weather.Storm;
        if (now < storm.CloudsStart || now >= storm.CloudsEnd) {
            phase = Phase.Clear;
        } else if (now < storm.RainStart) {
            phase = Phase.RollingIn;
        } else if (now < storm.RainEnd) {
            phase = Phase.Raining;
        } else {
            phase = Phase.Clearing;
        }
        if (phase == lastPhase) {
            return;
        }
        lastPhase = phase;
        DateTime rainStart = DateTimeOffset.FromUnixTimeSeconds((long)storm.RainStart).UtcDateTime;
        Debug.Log($"{WeatherMod.LogPrefix}{phase}: this hour's storm is {storm.Kind}, raining from "
            + $"{rainStart:HH:mm:ss} UTC for {StormSchedule.RainSeconds / 60:0} minutes");
    }
}
