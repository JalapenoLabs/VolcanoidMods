// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using JalapenoLabs.Shared.Showcase;
using UnityEngine;
using UnityEngine.Rendering;

namespace JalapenoLabs.Weather.Dev;

/// <summary>
/// Developer captures (-weather-capture): photographs chosen moments of chosen storms, then quits.
///
/// For each moment it holds the weather clock there and photographs each requested view: the island from above
/// looking towards the volcano, the same spot looking up into the clouds, a drillship close up, and the player's own
/// view. With -weather-showcase it first spawns a fully built drillship at an open landing site (the shared
/// <see cref="ShowcaseDrillship"/>), centres the views on it, and frames it with clear sightlines past rocks and trees
/// (<see cref="ShowcaseCamera"/>). A view other than the player's lifts the player's own camera for the length of
/// the frame's rendering (applied before the first camera renders, undone after the last), so the game never sees it
/// move, and anything else that follows the camera while rendering (Day/Night Cycle's sky dome) sees where it really
/// renders from. The
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

    /// <summary>How long a performance run measures each view.</summary>
    const float PerformanceSeconds = 6;

    /// <summary>How fast and how high the fly view travels, like a player flying the cinematic camera.</summary>
    const float FlySpeed = 25;
    const float FlyHeight = 30;

    /// <summary>How long a freshly spawned showcase drillship gets for its modules and lights to deploy.</summary>
    const float ShowcaseSettleSeconds = 8;

    const float IslandViewHeight = 70;
    const float IslandViewFieldOfView = 62;
    const float DrillshipViewFieldOfView = 50;
    const float HullViewFieldOfView = 58;

    /// <summary>How far from the hull the close-up stands, and how high above the ground.</summary>
    const float HullViewDistance = 16;
    const float HullViewHeight = 2.2f;

    /// <summary>
    /// The deck view stands farther off and higher, to see the rain on the roofs and the ground before it.
    /// </summary>
    const float DeckViewDistance = 34;
    const float DeckViewHeight = 9;

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
    bool hasTestedDamage;

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

        Train showcase = null;
        if (devOptions.ShowcaseDesign != null) {
            showcase = ShowcaseDrillship.Spawn(
                devOptions.ShowcaseDesign,
                devOptions.ShowcaseSite,
                screenCamera.transform.position
            );
            yield return new WaitForSecondsRealtime(ShowcaseSettleSeconds);
        }
        List<View> views = BuildViews(screenCamera.transform.position, showcase);
        for (int shotIndex = 0; shotIndex < devOptions.Shots.Count; shotIndex++) {
            DevOptions.Moment shot = devOptions.Shots[shotIndex];
            double moment = SyncedClock.FindMoment(controller.Clock.Now, shot);
            if (devOptions.IsSeekingGusts) {
                moment = NextFullGust(moment);
            }
            controller.Clock.Hold(moment);
            yield return null;
            if (devOptions.IsTestingDamage && shot.Kind == Forecast.StormKind.Thunderstorm && !hasTestedDamage) {
                hasTestedDamage = true;
                yield return TestDamage();
            }

            foreach (View view in views) {
                Pose? pose = view.Pose();
                liftedPose = pose;
                liftedFieldOfView = view.FieldOfView;
                // A lifted camera stands out in the open, whatever the player is doing.
                controller.Location.Override = pose.HasValue ? Exposure.Outside : (Exposure?)null;
                controller.EyeOverride = pose?.position;
                controller.IsLensRainForced = view.Name == "lens";
                if (pose.HasValue) {
                    Debug.Log($"{WeatherMod.LogPrefix}{view.Name} camera at {pose.Value.position}");
                }
                yield return new WaitForSecondsRealtime(ViewSettleSeconds);
                if (devOptions.IsMeasuringPerformance) {
                    yield return MeasureFrames(view.Name, shot.Label, isFlying: view.Name == "fly" && pose.HasValue);
                }

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

        controller.IsLensRainForced = false;
        Debug.Log($"{WeatherMod.LogPrefix}Capture finished; quitting");
        Application.Quit();
    }

    /// <summary>
    /// For performance runs (-weather-perf): logs a view's frame times over a few seconds. The fly view moves the camera
    /// steadily across the island meanwhile, as the cinematic camera does, so the costs of following a moving camera
    /// (the rain box, puddle surveys, the trees near the camera) show up in it.
    /// </summary>
    IEnumerator MeasureFrames(string viewName, string shotLabel, bool isFlying) {
        var frameMilliseconds = new List<float>();
        float until = Time.realtimeSinceStartup + PerformanceSeconds;
        while (Time.realtimeSinceStartup < until) {
            yield return null;
            frameMilliseconds.Add(Time.unscaledDeltaTime * 1000);
            if (isFlying) {
                Pose current = liftedPose.Value;
                Vector3 heading = Vector3.ProjectOnPlane(current.rotation * Vector3.forward, Vector3.up).normalized;
                Vector3 position = current.position + heading * (FlySpeed * Time.unscaledDeltaTime);
                liftedPose = new Pose(position, current.rotation);
                controller.EyeOverride = position;
            }
        }
        frameMilliseconds.Sort();
        int count = frameMilliseconds.Count;
        float median = frameMilliseconds[count / 2];
        float slow = frameMilliseconds[count * 95 / 100];
        Debug.Log($"{WeatherMod.LogPrefix}Frame times, {shotLabel} {viewName}: median {median:0.0} ms, "
            + $"95th percentile {slow:0.0} ms, worst {frameMilliseconds[count - 1]:0.0} ms over {count} frames");
    }

    /// <summary>The fly view starts above the focus, looking along the ground towards the volcano.</summary>
    static Pose FlyStart(Vector3 focus) {
        Pose towardsVolcano = LookTowardsVolcano(focus);
        Vector3 heading = Vector3.ProjectOnPlane(towardsVolcano.rotation * Vector3.forward, Vector3.up).normalized;
        Vector3 start = focus + Vector3.up * FlyHeight;
        return new Pose(start, Quaternion.LookRotation(heading + Vector3.down * 0.25f));
    }

    /// <summary>The first moment from <paramref name="from"/> on, within a minute, of a full gust.</summary>
    static double NextFullGust(double from) {
        const double Step = 0.25;
        const double Search = 60;
        for (double time = from; time < from + Search; time += Step) {
            if (Forecast.StormSchedule.Sample(time).Gust > 0.85f) {
                return time;
            }
        }
        Debug.Log($"{WeatherMod.LogPrefix}No full gust within {Search} s of the moment; capturing it as it is");
        return from;
    }

    /// <summary>
    /// Developer test (-weather-test-damage): strikes the player, treated as out in the open, and the nearest cog, and
    /// logs their health before and after. Damage is the host's to deal, as in a single-player game here.
    /// </summary>
    IEnumerator TestDamage() {
        controller.Location.Override = Exposure.Outside;
        yield return null;
        var targets = new List<Health>();
        if (Player.Local != null && Player.Local.TryGetComponent(out PlayerHealth playerHealth)) {
            targets.Add(playerHealth);
        }
        Health nearestCog = null;
        float nearestDistance = float.MaxValue;
        foreach (Agent agent in Agent.Instances) {
            if (agent == null || !(agent.Health is CogHealth cog) || cog.IsDead) {
                continue;
            }
            float distance = Vector3.Distance(agent.transform.position, screenCamera.transform.position);
            if (distance < nearestDistance) {
                nearestCog = cog;
                nearestDistance = distance;
            }
        }
        if (nearestCog != null) {
            targets.Add(nearestCog);
        } else {
            Debug.Log($"{WeatherMod.LogPrefix}Damage test: no cog on the island to strike");
        }
        foreach (Health target in targets) {
            // Armour takes a strike's damage before health does (the game's rule for all but heat and water), so an
            // armoured player's hit shows in the armour.
            Armor armor = target.GetComponentInChildren<Armor>();
            float healthBefore = target.CurrentHP;
            float armorBefore = armor != null ? armor.HP : 0;
            controller.StrikeAt(target.transform.position);
            yield return new WaitForSecondsRealtime(1);
            string armorReport = armor != null
                ? $", armour {armorBefore:0} to {armor.HP:0}"
                : "";
            Debug.Log($"{WeatherMod.LogPrefix}Damage test: {target.name} went from {healthBefore:0} to "
                + $"{target.CurrentHP:0} of {target.MaxHP} health{armorReport}");
        }
        controller.Location.Override = null;
    }

    List<View> BuildViews(Vector3 playerPosition, Train showcase) {
        var views = new List<View>();
        Vector3 focus = playerPosition;
        ShowcaseCamera framing = null;
        if (showcase != null) {
            Bounds bounds = ShowcaseDrillship.MeasureBounds(showcase);
            framing = new ShowcaseCamera(showcase, bounds, screenCamera.aspect);
            focus = bounds.center;
        }
        foreach (string name in devOptions.Views) {
            switch (name) {
                case "island":
                    views.Add(new View(name, () => LookTowardsVolcano(focus), IslandViewFieldOfView));
                    break;
                case "sky":
                    views.Add(new View(name, () => LookUpAtClouds(focus), IslandViewFieldOfView));
                    break;
                case "drillship" when framing != null:
                    views.Add(new View(name, () => framing.FrameAlongSide(), ShowcaseCamera.FieldOfView));
                    break;
                case "drillship":
                    views.Add(new View(name, () => FrameNearestDrillship(playerPosition), DrillshipViewFieldOfView));
                    break;
                case "lens":
                    views.Add(new View(name, () => LookTowardsVolcano(focus), IslandViewFieldOfView));
                    break;
                case "fly":
                    views.Add(new View(name, () => FlyStart(focus), IslandViewFieldOfView));
                    break;
                case "hull":
                    views.Add(new View(
                        name,
                        () => NearHull(showcase, playerPosition, HullViewDistance, HullViewHeight, 3),
                        HullViewFieldOfView
                    ));
                    break;
                case "deck":
                    views.Add(new View(
                        name,
                        () => NearHull(showcase, playerPosition, DeckViewDistance, DeckViewHeight, 6),
                        HullViewFieldOfView
                    ));
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

    /// <summary>
    /// Standing near a drillship (the showcase, or the one nearest the player), on its most open side, looking at its
    /// hull: where rain splashes off it, water pours from its edges and puddles gather around it.
    /// </summary>
    /// <param name="distance">How far out from the hull to stand.</param>
    /// <param name="height">How high above the ground the eye is.</param>
    /// <param name="aimHeight">How high up the hull to look, above its base.</param>
    static Pose? NearHull(Train showcase, Vector3 playerPosition, float distance, float height, float aimHeight) {
        Component drillship = showcase != null ? showcase : NearestDrillship(playerPosition);
        if (drillship == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}No drillship for the hull view; using the player's view");
            return null;
        }
        Bounds bounds = RendererBounds(drillship.gameObject);
        Pose? best = null;
        float bestOpenness = -1;
        for (int direction = 0; direction < 8; direction++) {
            float angle = direction * 45 * Mathf.Deg2Rad;
            var outward = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
            // From the bounds' edge in that direction, out to the standing spot.
            Vector3 edge = bounds.ClosestPoint(bounds.center + outward * bounds.extents.magnitude * 2);
            Vector3 spot = edge + outward * distance;
            bool hasGround = Physics.Raycast(
                spot + Vector3.up * 200,
                Vector3.down,
                out RaycastHit ground,
                400,
                Layers.MaskToolRaycast,
                QueryTriggerInteraction.Ignore
            );
            if (!hasGround || ground.point.y < 0.5f) {
                continue;
            }
            Vector3 eye = ground.point + Vector3.up * height;
            Vector3 target = new Vector3(edge.x, Mathf.Max(ground.point.y, bounds.min.y) + aimHeight, edge.z);
            // Open ground counts for more: a clear line to the hull, and level footing.
            bool isClear = !Physics.Linecast(
                eye,
                Vector3.Lerp(eye, target, 0.85f),
                Layers.MaskToolRaycast,
                QueryTriggerInteraction.Ignore
            );
            float openness = (isClear ? 1 : 0) + ground.normal.y;
            if (openness > bestOpenness) {
                bestOpenness = openness;
                best = new Pose(eye, Quaternion.LookRotation(target - eye));
            }
        }
        return best;
    }

    static TrainWagons NearestDrillship(Vector3 playerPosition) {
        TrainWagons nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (TrainWagons wagons in TrainWagons.Instances) {
            float distance = Vector3.Distance(wagons.transform.position, playerPosition);
            if (distance < nearestDistance) {
                nearest = wagons;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    static Bounds RendererBounds(GameObject root) {
        var bounds = new Bounds(root.transform.position, Vector3.zero);
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>()) {
            if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        return bounds;
    }

    /// <summary>A three-quarter view of the drillship nearest the player, from a little above it.</summary>
    static Pose? FrameNearestDrillship(Vector3 playerPosition) {
        TrainWagons nearest = NearestDrillship(playerPosition);
        if (nearest == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}No drillship to photograph; using the player's view");
            return null;
        }
        Bounds bounds = RendererBounds(nearest.gameObject);
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
