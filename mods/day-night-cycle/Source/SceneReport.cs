// Copyright © 2026 Jalapeno Labs

using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace JalapenoLabs.DayNightCycle;

/// <summary>
/// Writes the island's lighting setup to the player log once per game load. The night is built on assumptions about
/// how the island is lit; when a game update changes that, this report is where it shows first.
/// </summary>
internal static class SceneReport {
    static readonly int ExposureId = Shader.PropertyToID("_Exposure");
    static readonly int TintId = Shader.PropertyToID("_Tint");

    public static void Log() {
        var report = new StringBuilder($"{DayNightCycleMod.LogPrefix}Island lighting:\n");

        Light sun = RenderSettings.sun;
        report.AppendLine(sun != null
            ? $"  Sun '{sun.name}': intensity {sun.intensity:0.00}, colour {sun.color}, "
                + $"towards {-sun.transform.forward}"
            : "  No sun");

        SphericalHarmonicsL2 probe = RenderSettings.ambientProbe;
        report.AppendLine(
            $"  Ambient: {RenderSettings.ambientMode}, probe DC ({probe[0, 0]:0.000}, {probe[1, 0]:0.000}, "
            + $"{probe[2, 0]:0.000}), reflections x{RenderSettings.reflectionIntensity:0.00}"
        );

        Material skybox = RenderSettings.skybox;
        report.AppendLine(skybox != null
            ? $"  Skybox '{skybox.name}' ({skybox.shader.name}): exposure {ReadFloat(skybox, ExposureId)}, "
                + $"tint {ReadColor(skybox, TintId)}"
            : "  No skybox");

        LightProbes lightProbes = LightmapSettings.lightProbes;
        LightProbes sceneProbes = LightProbes.GetSharedLightProbesForScene(SceneManager.GetActiveScene());
        report.AppendLine(
            $"  Light probes: {(lightProbes != null ? lightProbes.count : 0)} "
            + $"({(lightProbes != null ? lightProbes.bakedProbes.Length : 0)} readable); active scene's own: "
            + $"{(sceneProbes != null ? $"{sceneProbes.count} ({sceneProbes.bakedProbes.Length} readable)" : "none")}"
        );
        report.AppendLine(
            $"  Reflection probes: {FindAll<ReflectionProbe>().Length}, "
            + $"water sky materials: {FindAll<WaterMaterialSkybox>().Length}"
        );

        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline) {
            report.AppendLine($"  URP: volume update {pipeline.volumeFrameworkUpdateMode}, HDR {pipeline.supportsHDR}");
        }

        foreach (ZoneSun zoneSun in FindAll<ZoneSun>()) {
            Camera camera = zoneSun.GetComponent<Camera>();
            if (camera == null) {
                continue;
            }
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            report.AppendLine(
                $"  Camera '{camera.name}': far {camera.farClipPlane}, culling {camera.cullingMask:X8}, "
                + $"volume layers {(cameraData != null ? cameraData.volumeLayerMask.value.ToString("X8") : "?")}"
            );
        }

        foreach (Volume volume in FindAll<Volume>()) {
            string components = volume.sharedProfile != null
                ? string.Join(", ", volume.sharedProfile.components.ConvertAll(component => component.GetType().Name))
                : "no profile";
            report.AppendLine(
                $"  Volume '{volume.name}': {(volume.isGlobal ? "global" : "local")}, priority {volume.priority}, "
                + $"weight {volume.weight:0.00}, layer {LayerMask.LayerToName(volume.gameObject.layer)}: {components}"
            );
        }

        report.Append($"  Sprites/Default shader: {(Shader.Find("Sprites/Default") != null ? "found" : "MISSING")}");
        Debug.Log(report.ToString());
    }

    static ComponentType[] FindAll<ComponentType>() where ComponentType : Object {
        return Object.FindObjectsByType<ComponentType>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    }

    static string ReadFloat(Material material, int propertyId) {
        return material.HasProperty(propertyId)
            ? material.GetFloat(propertyId).ToString("0.00")
            : "-";
    }

    static string ReadColor(Material material, int propertyId) {
        return material.HasProperty(propertyId)
            ? material.GetColor(propertyId).ToString()
            : "-";
    }
}
