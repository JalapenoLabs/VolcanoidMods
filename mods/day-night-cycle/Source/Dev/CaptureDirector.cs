// Copyright © 2026 Jalapeno Labs

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JalapenoLabs.DayNightCycle.Cycle;
using JalapenoLabs.DayNightCycle.Lighting;
using UnityEngine;
using UnityEngine.Rendering;

namespace JalapenoLabs.DayNightCycle.Dev;

/// <summary>
/// Developer captures (-daynight-capture): photographs the island at each requested time of day, then quits.
///
/// A save often leaves the player inside the drillship, so most views lift the player's own camera 45 m and aim it
/// relative to the sky's path: once towards sunset, once up towards the moon's arc. The lift happens the same way the
/// night itself works, inside the camera's render and undone afterwards, so the game never sees the camera move. The
/// game's camera shake (ShakeListener) sets the view matrix from the player's head in Camera.onPreRender, so the lift
/// sets it again after that. The player's real view is captured too, with the HUD and FPS counter off.
/// </summary>
internal sealed class CaptureDirector : MonoBehaviour {
    /// <summary>One framing, captured at every requested moment.</summary>
    readonly struct View {
        public readonly string Name;
        /// <summary>False for the player's real view; true to lift and aim the camera.</summary>
        public readonly bool IsLifted;
        public readonly bool FacesSunset;
        public readonly float PitchDegrees;

        public View(string name, bool isLifted, bool facesSunset, float pitchDegrees) {
            Name = name;
            IsLifted = isLifted;
            FacesSunset = facesSunset;
            PitchDegrees = pitchDegrees;
        }
    }

    static readonly View[] Views = {
        new View("sunset", isLifted: true, facesSunset: true, pitchDegrees: -6),
        new View("moon", isLifted: true, facesSunset: false, pitchDegrees: 14),
        new View("player", isLifted: false, facesSunset: false, pitchDegrees: 0),
    };

    const float LiftHeight = 45;

    /// <summary>How long to wait after loading for streaming, shader warm-up and the loading screen to clear.</summary>
    const float LoadSettleSeconds = 15;

    /// <summary>How long each new view settles (temporal anti-aliasing, auto exposure) before capturing.</summary>
    const float ViewSettleSeconds = 2;

    NightController controller;
    DevOptions devOptions;
    Camera screenCamera;
    Pose? liftedPose;
    Pose savedPose;
    bool isPoseOverridden;
    Matrix4x4 savedViewMatrix;
    bool hadCustomViewMatrix;
    bool hasLoggedLift;
    bool wasHudShown;
    bool wasFpsShown;

    public void Begin(NightController nightController, DevOptions options) {
        controller = nightController;
        devOptions = options;
        StartCoroutine(Run());
    }

    void OnEnable() {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        Camera.onPreRender += OnCameraPreRender;
    }

    void OnDisable() {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        Camera.onPreRender -= OnCameraPreRender;
    }

    IEnumerator Run() {
        Directory.CreateDirectory(devOptions.CaptureDirectory);
        yield return new WaitForSecondsRealtime(LoadSettleSeconds);

        screenCamera = FindScreenCamera();
        if (screenCamera == null) {
            Debug.LogError($"{DayNightCycleMod.LogPrefix}Capture found no camera drawing to the screen; quitting");
            Application.Quit();
            yield break;
        }
        // HudCanvas re-enables the HUD every frame from this option, so the option is what has to change.
        wasHudShown = Options.Instance.ShowHUD;
        wasFpsShown = Options.Instance.ShowFps;
        Options.Instance.ShowHUD = false;
        Options.Instance.ShowFps = false;

        for (int shotIndex = 0; shotIndex < devOptions.Shots.Count; shotIndex++) {
            DevOptions.Shot shot = devOptions.Shots[shotIndex];
            controller.Clock.Hold(CycleSchedule.FindMoment(controller.Clock.Now, shot.Minute, shot.MoonPhase));

            foreach (View view in Views) {
                if (view.IsLifted) {
                    liftedPose = Aim(view, screenCamera.transform.position);
                    // Lifted 45 m, the camera is on the surface whatever zone the player stands in.
                    controller.TreatAsSurfaceCamera(screenCamera);
                } else {
                    liftedPose = null;
                    controller.ForgetCamera(screenCamera);
                }
                yield return new WaitForSecondsRealtime(ViewSettleSeconds);

                string path = Path.Combine(devOptions.CaptureDirectory, $"{shotIndex:00}-{shot.Label}-{view.Name}.png");
                ScreenCapture.CaptureScreenshot(path);
                LogCapture(path);
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        Debug.Log($"{DayNightCycleMod.LogPrefix}Capture finished; quitting");
        Application.Quit();
    }

    /// <summary>The world camera that draws to the screen: a ZoneSun camera without a target texture.</summary>
    Camera FindScreenCamera() {
        var inventory = new StringBuilder($"{DayNightCycleMod.LogPrefix}Capture cameras:\n");
        Camera best = null;
        foreach (Camera camera in Camera.allCameras) {
            var zoneSun = camera.GetComponent<ZoneSun>();
            inventory.AppendLine(
                $"  '{camera.name}': depth {camera.depth}, "
                + $"target {(camera.targetTexture != null ? camera.targetTexture.name : "screen")}, "
                + $"ZoneSun {(zoneSun != null ? DescribeZones(zoneSun) : "none")}, at {camera.transform.position}"
            );
            if (zoneSun != null && camera.targetTexture == null && (best == null || camera.depth > best.depth)) {
                best = camera;
            }
        }
        if (best != null) {
            var shakeListener = best.GetComponentInParent<ShakeListener>();
            inventory.AppendLine(
                $"  '{best.name}' path: {HierarchyPath(best.transform)}; ShakeListener on "
                + $"{(shakeListener != null ? HierarchyPath(shakeListener.transform) : "no parent")}"
            );
        }
        inventory.Append($"  Capturing through '{(best != null ? best.name : "nothing")}'");
        Debug.Log(inventory.ToString());
        return best;
    }

    static string HierarchyPath(Transform transform) {
        string path = transform.name;
        for (Transform parent = transform.parent; parent != null; parent = parent.parent) {
            path = parent.name + "/" + path;
        }
        return path;
    }

    static string DescribeZones(ZoneSun zoneSun) {
        var names = new List<string>();
        foreach (Zone zone in zoneSun.ActiveZones) {
            names.Add($"{zone.name} ({LayerMask.LayerToName(zone.gameObject.layer)})");
        }
        return names.Count > 0 ? string.Join(", ", names) : "no zones";
    }

    Pose Aim(in View view, Vector3 playerPosition) {
        Vector3 horizontal = view.FacesSunset
            ? -controller.Geometry.RiseDirection
            : Vector3.ProjectOnPlane(controller.Geometry.HighestDirection, Vector3.up).normalized;
        float pitch = view.PitchDegrees * Mathf.Deg2Rad;
        Vector3 forward = horizontal * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
        return new Pose(playerPosition + Vector3.up * LiftHeight, Quaternion.LookRotation(forward));
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera) {
        if (camera != screenCamera || !liftedPose.HasValue) {
            return;
        }
        Transform cameraTransform = camera.transform;
        savedPose = new Pose(cameraTransform.position, cameraTransform.rotation);
        savedViewMatrix = camera.worldToCameraMatrix;
        // Unity only keeps a script-set view matrix if something set one; otherwise the transform drives the view.
        hadCustomViewMatrix = !ApproximatelyEqual(savedViewMatrix, ViewMatrix(savedPose));

        cameraTransform.SetPositionAndRotation(liftedPose.Value.position, liftedPose.Value.rotation);
        camera.worldToCameraMatrix = ViewMatrix(liftedPose.Value);
        // ZoneSun switches the sun off for a camera inside the drillship; a lifted camera is out in the open.
        RenderSettings.sun.enabled = true;
        isPoseOverridden = true;
    }

    void OnCameraPreRender(Camera camera) {
        if (camera == screenCamera && isPoseOverridden) {
            camera.worldToCameraMatrix = ViewMatrix(liftedPose.Value);
        }
    }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera camera) {
        if (camera != screenCamera || !isPoseOverridden) {
            return;
        }
        Vector3 renderedFrom = camera.cameraToWorldMatrix.GetColumn(3);
        if (!hasLoggedLift) {
            Debug.Log(
                $"{DayNightCycleMod.LogPrefix}Lifted render: wanted {liftedPose.Value.position}, rendered from "
                + $"{renderedFrom}; the game had {(hadCustomViewMatrix ? "a custom" : "no custom")} view matrix"
            );
            hasLoggedLift = true;
        }
        camera.transform.SetPositionAndRotation(savedPose.position, savedPose.rotation);
        if (hadCustomViewMatrix) {
            camera.worldToCameraMatrix = savedViewMatrix;
        } else {
            camera.ResetWorldToCameraMatrix();
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

    void LogCapture(string path) {
        NightFrame frame = controller.Frame;
        Debug.Log(
            $"{DayNightCycleMod.LogPrefix}Captured {Path.GetFileName(path)}: darkness {frame.Darkness:0.00}, "
            + $"{frame.Sample.MoonPhaseName}, moon height {frame.Sample.MoonHeight:0.00}, "
            + $"clarity {frame.SkyClarity:0.00}"
        );
    }

    void OnDestroy() {
        if (Options.Instance == null) {
            return;
        }
        Options.Instance.ShowHUD |= wasHudShown;
        Options.Instance.ShowFps |= wasFpsShown;
    }
}
