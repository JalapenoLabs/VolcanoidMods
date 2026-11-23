// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using JalapenoLabs.DayNightCycle.Celestial;
using JalapenoLabs.DayNightCycle.Cycle;
using JalapenoLabs.DayNightCycle.Dev;
using JalapenoLabs.DayNightCycle.Lighting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace JalapenoLabs.DayNightCycle;

/// <summary>
/// Runs the night for one loaded game. It lives in the island scene, so it goes away with it.
///
/// Each frame it samples the sky once. Then, around every surface camera's render, it applies each effect and undoes
/// it again (see <see cref="ICameraEffect"/>). Cameras are recognised by the game's own <see cref="ZoneSun"/>, the
/// component that already decides which cameras see the sun, and its zone rule decides whether a camera is on the
/// surface, so the night stops exactly where the game's sunlight stops.
/// </summary>
internal sealed class NightController : MonoBehaviour {
    enum Period {
        Day,
        Dusk,
        Night,
        Dawn,
    }

    /// <summary>Per-camera state: whether it is a world camera, and how far onto the surface it is.</summary>
    sealed class CameraState {
        public ZoneSun ZoneSun;
        /// <summary>A camera of the mod's own (developer captures) that always counts as on the surface.</summary>
        public bool IsAlwaysSurface;
        public float Surface;
        public int LastUpdatedFrame;
    }

    /// <summary>How long the night takes to fade when a camera crosses between surface and underground.</summary>
    const float SurfaceFadeSeconds = 1.5f;

    static readonly int SurfaceZoneMask = Layers.GetMask(Layers.MainZone);

    /// <summary>
    /// Cloud cover a weather mod (such as Jalapeno Labs' Rain &amp; Thunderstorms) may publish, 0 clear to 1
    /// overcast, as a global shader value. It is the only link between the night and a weather mod, so neither needs
    /// the other; unset, it reads 0.
    /// </summary>
    static readonly int CloudCoverId = Shader.PropertyToID("_JalapenoLabs_CloudCover");

    /// <summary>How far the sky clarity must move before the log reports it again.</summary>
    const float ClarityLogStep = 0.1f;

    /// <summary>
    /// The clarity that counts as a clear sky. The game's noon sun is a float a hair under its nominal 1.8, so a
    /// clear sky reads 0.99999 rather than 1.
    /// </summary>
    const float ClearClarity = 0.999f;

    readonly List<ICameraEffect> effects = new List<ICameraEffect>();
    readonly Dictionary<Camera, CameraState> cameras = new Dictionary<Camera, CameraState>();

    SyncedClock clock;
    DevOptions devOptions;
    Light sun;
    CelestialGeometry geometry;
    NightFrame frame;
    Camera renderingCamera;
    Period? lastPeriod;
    /// <summary>The sky clarity the log last reported. A game loaded under ash logs its first frame.</summary>
    float loggedClarity = 1;
    /// <summary>The developer capture rig, while capturing. Its camera lift wraps the night's camera changes.</summary>
    CaptureDirector captureDirector;

    public SyncedClock Clock => clock;

    public NightFrame Frame => frame;

    public CelestialGeometry Geometry => geometry;

    public static NightController Create(Scene gameScene, SyncedClock clock, DevOptions devOptions) {
        var host = new GameObject("DayNightCycle");
        SceneManager.MoveGameObjectToScene(host, gameScene);
        var controller = host.AddComponent<NightController>();
        controller.clock = clock;
        controller.devOptions = devOptions;
        return controller;
    }

    void Start() {
        Light sun = RenderSettings.sun;
        if (sun == null) {
            Debug.LogWarning($"{DayNightCycleMod.LogPrefix}The island has no sun light, so the night stays off");
            enabled = false;
            return;
        }
        SceneReport.Log();

        this.sun = sun;
        geometry = new CelestialGeometry(-sun.transform.forward);
        frame = SampleFrame();

        Scene scene = gameObject.scene;
        AddEffect(() => new SunMoonLight(sun));
        AddEffect(() => new AmbientLight());
        AddEffect(() => new SkyMaterials());
        AddEffect(() => new ReflectionProbeDimmer());
        AddEffect(() => new NightVolume(scene));
        AddEffect(() => new CelestialSky(scene));
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

        if (devOptions.IsCapturing) {
            captureDirector = gameObject.AddComponent<CaptureDirector>();
            captureDirector.Begin(this, devOptions);
        }
    }

    void Update() {
        frame = SampleFrame();
        LogPeriodChange();
        LogClarityChange();
    }

    NightFrame SampleFrame() {
        // Read in Update, after the night has put the sun back: this is the eruption timeline's own brightness.
        SkyVeil veil = SkyVeil.Measure(
            sun.intensity,
            NightLook.GameSunIntensity,
            Shader.GetGlobalFloat(CloudCoverId)
        );
        return geometry.Evaluate(CycleSchedule.Sample(clock.Now), veil);
    }

    /// <summary>
    /// Creates one effect. A failure (say, a game update removed something it needs) costs only that effect.
    /// </summary>
    void AddEffect(Func<ICameraEffect> create) {
        try {
            effects.Add(create());
        } catch (Exception exception) {
            Debug.LogError($"{DayNightCycleMod.LogPrefix}An effect could not start and stays off: {exception}");
        }
    }

    void OnDestroy() {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        RestoreEffects();
        foreach (ICameraEffect effect in effects) {
            (effect as IDisposable)?.Dispose();
        }
        effects.Clear();
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera) {
        // A capture's lifted camera must be in place before the night applies: the sky dome centres on the camera.
        if (captureDirector != null) {
            captureDirector.LiftForRender(camera);
        }
        float? surface = SurfaceOf(camera);
        if (!surface.HasValue) {
            return;
        }
        if (renderingCamera != null) {
            Debug.LogWarning(
                $"{DayNightCycleMod.LogPrefix}'{camera.name}' began rendering inside '{renderingCamera.name}'; "
                + "restoring the outer camera first"
            );
            RestoreEffects();
        }

        renderingCamera = camera;
        for (int index = 0; index < effects.Count; index++) {
            ICameraEffect effect = effects[index];
            try {
                effect.Apply(in frame, camera, surface.Value);
            } catch (Exception exception) {
                // One broken effect must not break rendering, or spam the log every frame: drop it and carry on.
                Debug.LogError(
                    $"{DayNightCycleMod.LogPrefix}{effect.GetType().Name} failed and is now off: {exception}"
                );
                effect.Restore();
                effects.RemoveAt(index);
                index--;
            }
        }
    }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera camera) {
        if (camera == renderingCamera) {
            RestoreEffects();
        }
        if (captureDirector != null) {
            captureDirector.LowerAfterRender(camera);
        }
    }

    void RestoreEffects() {
        // Undo in reverse, so effects that build on another's change unwind cleanly.
        for (int index = effects.Count - 1; index >= 0; index--) {
            effects[index].Restore();
        }
        renderingCamera = null;
    }

    /// <summary>
    /// How far onto the surface a camera is, or null for cameras the night leaves alone (UI, map, previews).
    /// Mirrors ZoneSun: the sun shines when the camera is in no zone, or in at least one surface zone.
    /// </summary>
    float? SurfaceOf(Camera camera) {
        if (!cameras.TryGetValue(camera, out CameraState state)) {
            state = new CameraState { ZoneSun = camera.GetComponent<ZoneSun>() };
            state.Surface = state.ZoneSun != null && IsOnSurface(state.ZoneSun) ? 1 : 0;
            state.LastUpdatedFrame = Time.frameCount;
            cameras[camera] = state;
        }
        if (state.IsAlwaysSurface) {
            return 1;
        }
        if (state.ZoneSun == null) {
            return null;
        }
        if (state.LastUpdatedFrame != Time.frameCount) {
            float target = IsOnSurface(state.ZoneSun) ? 1 : 0;
            state.Surface = Mathf.MoveTowards(state.Surface, target, Time.unscaledDeltaTime / SurfaceFadeSeconds);
            state.LastUpdatedFrame = Time.frameCount;
        }
        return state.Surface;
    }

    static bool IsOnSurface(ZoneSun zoneSun) {
        ListReader<Zone> activeZones = zoneSun.ActiveZones;
        if (activeZones.Count == 0) {
            return true;
        }
        foreach (Zone zone in activeZones) {
            if ((zone.LayerMask.value & SurfaceZoneMask) != 0) {
                return true;
            }
        }
        return false;
    }

    void LogPeriodChange() {
        Period period;
        if (frame.Darkness < 0.001f) {
            period = Period.Day;
        } else if (frame.Darkness > 0.999f) {
            period = Period.Night;
        } else {
            // Mid-twilight with the sun on its way down (around 0.5 turns) is dusk; on its way up is dawn.
            double sunTurns = frame.Sample.SunPathTurns;
            period = sunTurns > 0.25 && sunTurns < 0.75
                ? Period.Dusk
                : Period.Dawn;
        }
        if (period == lastPeriod) {
            return;
        }
        lastPeriod = period;
        Debug.Log(
            $"{DayNightCycleMod.LogPrefix}{period} (cycle {frame.Sample.CycleNumber}, "
            + $"minute {frame.Sample.CycleSeconds / 60:0.0}, {frame.Sample.MoonPhaseName})"
        );
    }

    /// <summary>
    /// Logs the sky clarity in steps as ash or storm cloud comes and goes, with what drove it, so a player's log shows
    /// why a night looked the way it did.
    /// </summary>
    void LogClarityChange() {
        float clarity = frame.SkyClarity;
        bool hasCleared = clarity > ClearClarity && loggedClarity <= ClearClarity;
        if (Mathf.Abs(clarity - loggedClarity) < ClarityLogStep && !hasCleared) {
            return;
        }
        loggedClarity = clarity;
        IslandState islandState = Island.CurrentState;
        string islandStateName = islandState != null
            ? islandState.name
            : "unknown";
        Debug.Log(
            $"{DayNightCycleMod.LogPrefix}Sky clarity {clarity:0.00}: sun {sun.intensity:0.00} of "
            + $"{NightLook.GameSunIntensity:0.00}, cloud cover {Shader.GetGlobalFloat(CloudCoverId):0.00}, "
            + $"island {islandStateName}"
        );
    }

    /// <summary>Makes the night treat a camera as on the surface whatever its zones say (developer captures).</summary>
    public void TreatAsSurfaceCamera(Camera camera) {
        cameras[camera] = new CameraState { IsAlwaysSurface = true, Surface = 1 };
    }

    /// <summary>Drops what the night knows about a camera, so it is judged afresh by its zones next frame.</summary>
    public void ForgetCamera(Camera camera) {
        cameras.Remove(camera);
    }
}
