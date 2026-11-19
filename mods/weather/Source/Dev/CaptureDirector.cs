// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace JalapenoLabs.Weather.Dev;

/// <summary>
/// Developer captures (-weather-capture): photographs chosen moments of chosen storms, then quits.
///
/// For each moment it holds the weather clock there and photographs each requested view: the island from above
/// looking towards the volcano, the same spot looking up into the clouds, a drillship close up, and the player's own
/// view. A view other than the player's lifts the player's own camera for the length of the frame's rendering
/// (applied before the first camera renders, undone after the last), so the game never sees it move, and anything
/// else that follows the camera while rendering (Day/Night Cycle's sky dome) sees where it really renders from. The
/// game's camera shake sets the view matrix from the player's head in Camera.onPreRender, so the lift sets it again
/// after that. The HUD and FPS counter are hidden.
/// </summary>
internal sealed class CaptureDirector : MonoBehaviour {
    readonly struct View {
        public readonly string Name;
        public readonly Func<Pose?> Pose;
        public readonly float? FieldOfView;

        public View(string name, Func<Pose?> pose, float? fieldOfView) {
            Name = name;
            Pose = pose;
            FieldOfView = fieldOfView;
        }
    }

    /// <summary>How long to wait after loading for streaming, shader warm-up and the loading screen to clear.</summary>
    const float LoadSettleSeconds = 15;

    /// <summary>
    /// How long each view settles before its photograph: rain has to fall from its spawn height to fill the view, and
    /// temporal anti-aliasing and auto exposure need a moment too.
    /// </summary>
    const float ViewSettleSeconds = 4;

    const float IslandViewHeight = 70;
    const float IslandViewFieldOfView = 62;
    const float DrillshipViewFieldOfView = 50;

    WeatherController controller;
    DevOptions devOptions;
    Camera screenCamera;
    Pose? liftedPose;
    float? liftedFieldOfView;
    Pose savedPose;
    float savedFieldOfView;
    Matrix4x4 savedViewMatrix;
    bool hadCustomViewMatrix;
    bool isPoseOverridden;
    bool wasHudShown;
    bool wasFpsShown;

    public void Begin(WeatherController weatherController, DevOptions options) {
        controller = weatherController;
        devOptions = options;
        StartCoroutine(Run());
    }

    void OnEnable() {
        RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
        RenderPipelineManager.endContextRendering += OnEndContextRendering;
        Camera.onPreRender += OnCameraPreRender;
    }

    void OnDisable() {
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
        RenderPipelineManager.endContextRendering -= OnEndContextRendering;
        Camera.onPreRender -= OnCameraPreRender;
    }

    IEnumerator Run() {
        Directory.CreateDirectory(devOptions.CaptureDirectory);
        yield return new WaitForSecondsRealtime(LoadSettleSeconds);

        screenCamera = CameraPriority.GetActiveCamera(0);
        if (screenCamera == null) {
            Debug.LogError($"{WeatherMod.LogPrefix}Capture found no active camera; quitting");
            Application.Quit();
            yield break;
        }
        // HudCanvas re-enables the HUD every frame from this option, so the option is what has to change.
        wasHudShown = Options.Instance.ShowHUD;
        wasFpsShown = Options.Instance.ShowFps;
        Options.Instance.ShowHUD = false;
        Options.Instance.ShowFps = false;

        List<View> views = BuildViews(screenCamera.transform.position);
        for (int shotIndex = 0; shotIndex < devOptions.Shots.Count; shotIndex++) {
            DevOptions.Moment shot = devOptions.Shots[shotIndex];
            controller.Clock.Hold(SyncedClock.FindMoment(controller.Clock.Now, shot));
            yield return null;

            foreach (View view in views) {
                Pose? pose = view.Pose();
                liftedPose = pose;
                liftedFieldOfView = view.FieldOfView;
                // A lifted camera stands out in the open, whatever the player is doing.
                controller.Location.Override = pose.HasValue ? Exposure.Outside : (Exposure?)null;
                controller.EyeOverride = pose?.position;
                if (pose.HasValue) {
                    Debug.Log($"{WeatherMod.LogPrefix}{view.Name} camera at {pose.Value.position}");
                }
                yield return new WaitForSecondsRealtime(ViewSettleSeconds);

                Pose shotPose = pose ?? new Pose(screenCamera.transform.position, screenCamera.transform.rotation);
                if (controller.StrikeInView(shotPose, view.FieldOfView ?? screenCamera.fieldOfView)) {
                    // A bolt is brightest a few frames after it strikes.
                    yield return new WaitForSecondsRealtime(0.06f);
                }
                string path = Path.Combine(devOptions.CaptureDirectory, $"{shotIndex:00}-{shot.Label}-{view.Name}.png");
                ScreenCapture.CaptureScreenshot(path);
                yield return null;
                controller.LogBaseline($"captured {Path.GetFileName(path)}");
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        Debug.Log($"{WeatherMod.LogPrefix}Capture finished; quitting");
        Application.Quit();
    }

    List<View> BuildViews(Vector3 playerPosition) {
        var views = new List<View>();
        foreach (string name in devOptions.Views) {
            switch (name) {
                case "island":
                    views.Add(new View(name, () => LookTowardsVolcano(playerPosition), IslandViewFieldOfView));
                    break;
                case "sky":
                    views.Add(new View(name, () => LookUpAtClouds(playerPosition), IslandViewFieldOfView));
                    break;
                case "drillship":
                    views.Add(new View(name, () => FrameNearestDrillship(playerPosition), DrillshipViewFieldOfView));
                    break;
                default:
                    views.Add(new View(name, () => null, null));
                    break;
            }
        }
        return views;
    }

    /// <summary>High above the player, looking across the island towards the volcano, its highest ground.</summary>
    static Pose LookTowardsVolcano(Vector3 playerPosition) {
        Vector3 eye = playerPosition + Vector3.up * IslandViewHeight;
        Vector3 peak = HighestGround();
        Vector3 heading = Vector3.ProjectOnPlane(peak - eye, Vector3.up);
        if (heading.sqrMagnitude < 1) {
            heading = Vector3.forward;
        }
        Vector3 forward = Quaternion.AngleAxis(4, Vector3.Cross(heading, Vector3.up)) * heading.normalized;
        return new Pose(eye, Quaternion.LookRotation(forward));
    }

    /// <summary>From the island view's spot, tilted up to look into the clouds.</summary>
    static Pose LookUpAtClouds(Vector3 playerPosition) {
        Pose island = LookTowardsVolcano(playerPosition);
        Vector3 forward = island.rotation * Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        // A negative turn about the camera's right axis tips its view up.
        Vector3 tilted = Quaternion.AngleAxis(-34, right) * forward;
        return new Pose(island.position, Quaternion.LookRotation(tilted));
    }

    static Vector3 HighestGround() {
        Vector3 highest = Vector3.zero;
        foreach (Terrain terrain in Terrain.activeTerrains) {
            TerrainData data = terrain.terrainData;
            int resolution = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, resolution, resolution);
            for (int y = 0; y < resolution; y += 4) {
                for (int x = 0; x < resolution; x += 4) {
                    float height = heights[y, x] * data.size.y + terrain.GetPosition().y;
                    if (height > highest.y) {
                        var normalized = new Vector3(x / (float)(resolution - 1), 0, y / (float)(resolution - 1));
                        highest = terrain.GetPosition() + Vector3.Scale(normalized, data.size);
                        highest.y = height;
                    }
                }
            }
        }
        return highest;
    }

    /// <summary>A three-quarter view of the drillship nearest the player, from a little above it.</summary>
    static Pose? FrameNearestDrillship(Vector3 playerPosition) {
        TrainWagons nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (TrainWagons wagons in TrainWagons.Instances) {
            float distance = Vector3.Distance(wagons.transform.position, playerPosition);
            if (distance < nearestDistance) {
                nearest = wagons;
                nearestDistance = distance;
            }
        }
        if (nearest == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}No drillship to photograph; using the player's view");
            return null;
        }
        var bounds = new Bounds(nearest.transform.position, Vector3.zero);
        foreach (Renderer renderer in nearest.GetComponentsInChildren<Renderer>()) {
            if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        float radius = bounds.extents.magnitude;
        Transform hull = nearest.transform;
        Vector3 offset = (hull.right * 1.3f + hull.forward * 0.7f).normalized * radius * 1.7f;
        Vector3 eye = bounds.center + offset + Vector3.up * radius * 0.25f;
        return new Pose(eye, Quaternion.LookRotation(bounds.center - eye));
    }

    void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
        if (screenCamera == null || !liftedPose.HasValue) {
            return;
        }
        Transform cameraTransform = screenCamera.transform;
        savedPose = new Pose(cameraTransform.position, cameraTransform.rotation);
        savedViewMatrix = screenCamera.worldToCameraMatrix;
        // Unity only keeps a script-set view matrix if something set one; otherwise the transform drives the view.
        hadCustomViewMatrix = !ApproximatelyEqual(savedViewMatrix, ViewMatrix(savedPose));
        savedFieldOfView = screenCamera.fieldOfView;

        cameraTransform.SetPositionAndRotation(liftedPose.Value.position, liftedPose.Value.rotation);
        screenCamera.worldToCameraMatrix = ViewMatrix(liftedPose.Value);
        if (liftedFieldOfView.HasValue) {
            screenCamera.fieldOfView = liftedFieldOfView.Value;
        }
        isPoseOverridden = true;
    }

    void OnCameraPreRender(Camera camera) {
        if (camera == screenCamera && isPoseOverridden) {
            camera.worldToCameraMatrix = ViewMatrix(liftedPose.Value);
            // ZoneSun switches the sun off for a camera inside the drillship; a lifted camera is out in the open.
            RenderSettings.sun.enabled = true;
        }
    }

    void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
        if (!isPoseOverridden) {
            return;
        }
        screenCamera.transform.SetPositionAndRotation(savedPose.position, savedPose.rotation);
        screenCamera.fieldOfView = savedFieldOfView;
        if (hadCustomViewMatrix) {
            screenCamera.worldToCameraMatrix = savedViewMatrix;
        } else {
            screenCamera.ResetWorldToCameraMatrix();
        }
        isPoseOverridden = false;
    }

    /// <summary>A camera's view matrix: the inverse of its pose, with Z flipped because cameras look down -Z.</summary>
    static Matrix4x4 ViewMatrix(Pose pose) {
        Matrix4x4 cameraToWorld = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
        return Matrix4x4.Scale(new Vector3(1, 1, -1)) * cameraToWorld.inverse;
    }

    static bool ApproximatelyEqual(Matrix4x4 first, Matrix4x4 second) {
        for (int index = 0; index < 16; index++) {
            if (Mathf.Abs(first[index] - second[index]) > 1e-3f) {
                return false;
            }
        }
        return true;
    }

    void OnDestroy() {
        if (Options.Instance == null) {
            return;
        }
        Options.Instance.ShowHUD |= wasHudShown;
        Options.Instance.ShowFps |= wasFpsShown;
    }
}
