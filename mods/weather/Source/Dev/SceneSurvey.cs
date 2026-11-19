// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Dev;

/// <summary>
/// Developer aid (-weather-survey): logs the parts of the island the storm builds on.
///
/// The game's shaders are stripped to the keyword variants its own materials use, so a mod's material only renders if
/// it asks for a variant some game material already does. The survey lists the materials of the shaders the storm
/// could use, with their keywords and render settings, and the scene's particle effects, water and lava.
/// </summary>
internal static class SceneSurvey {
    static readonly string[] InterestingShaders = { "Particles", "Lit", "Water", "Additive", "Smoke", "Lightning" };
    static readonly string[] InterestingNames = { "steam", "smoke", "lava", "water", "rain", "splash", "lightning",
        "tesla", "fog", "dust", "puddle", "ocean", "sea", "wave", "bolt", "spark", "vapor", "geyser", "smokelight" };
    static readonly string[] ProbedFloats = { "_Surface", "_Blend", "_SrcBlend", "_DstBlend", "_ZWrite",
        "_SoftParticlesEnabled", "_Smoothness", "_Glossiness", "_Metallic", "_ColorMode", "_Cull" };

    public static void Log() {
        var report = new StringBuilder($"{WeatherMod.LogPrefix}Scene survey\n");
        LogMaterials(report);
        LogParticleSystems(report);
        LogNamedObjects(report);
        LogSkyboxes(report);
        Debug.Log(report.ToString());
    }

    static void LogMaterials(StringBuilder report) {
        report.AppendLine("Materials of interest:");
        IEnumerable<Material> materials = Resources.FindObjectsOfTypeAll<Material>()
            .Where(material => material != null && material.shader != null)
            .Where(material => InterestingShaders.Any(part => material.shader.name.Contains(part)))
            .OrderBy(material => material.shader.name)
            .ThenBy(material => material.name);
        foreach (Material material in materials) {
            string floats = string.Join(" ", ProbedFloats
                .Where(material.HasProperty)
                .Select(property => $"{property}={material.GetFloat(property):0.##}"));
            string mainTexture = material.mainTexture != null ? material.mainTexture.name : "-";
            report.AppendLine(
                $"  '{material.name}' [{material.shader.name}] queue {material.renderQueue} tex {mainTexture} "
                + $"kw [{string.Join(" ", material.shaderKeywords)}] {floats}"
            );
        }
    }

    static void LogParticleSystems(StringBuilder report) {
        report.AppendLine("Particle systems of interest:");
        var renderers = Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        foreach (ParticleSystemRenderer renderer in renderers) {
            string path = PathOf(renderer.transform);
            string material = renderer.sharedMaterial != null
                ? $"{renderer.sharedMaterial.name} [{renderer.sharedMaterial.shader.name}]"
                : "-";
            if (!InterestingNames.Any(name => path.ToLowerInvariant().Contains(name)
                || material.ToLowerInvariant().Contains(name))) {
                continue;
            }
            var streams = new List<ParticleSystemVertexStream>();
            renderer.GetActiveVertexStreams(streams);
            report.AppendLine($"  {path} at {renderer.transform.position}: {material}, mode {renderer.renderMode}, "
                + $"streams [{string.Join(" ", streams)}]");
        }
    }

    static void LogNamedObjects(StringBuilder report) {
        report.AppendLine("Lava, water and steam objects (first 60):");
        var transforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        int count = 0;
        foreach (Transform transform in transforms) {
            string name = transform.name.ToLowerInvariant();
            bool isInteresting = name.Contains("lava") || name.Contains("water") || name.Contains("steam")
                || name.Contains("ocean");
            if (!isInteresting) {
                continue;
            }
            string components = string.Join(",", transform.GetComponents<Component>()
                .Where(component => component != null && !(component is Transform))
                .Select(component => component.GetType().Name));
            report.AppendLine($"  {PathOf(transform)} at {transform.position} scale {transform.lossyScale}: "
                + components);
            if (++count >= 60) {
                break;
            }
        }
    }

    /// <summary>The cubemaps the eruption timeline blends the skybox between.</summary>
    static void LogSkyboxes(StringBuilder report) {
        Material skybox = RenderSettings.skybox;
        report.AppendLine($"Skybox '{skybox?.name}': _Tex {skybox?.GetTexture("_Tex")?.name}, "
            + $"_Tex2 {skybox?.GetTexture("_Tex2")?.name}, _Blend {skybox?.GetFloat("_Blend")}");
        FieldInfo elementsField = typeof(IslandTimeSkybox).GetField(
            "m_elements",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        foreach (IslandTimeSkybox timeline in Object.FindObjectsByType<IslandTimeSkybox>(FindObjectsSortMode.None)) {
            if (!(elementsField?.GetValue(timeline) is Array elements)) {
                report.AppendLine("  (could not read the skybox timeline's elements)");
                continue;
            }
            foreach (object element in elements) {
                var texture = element.GetType().GetField("Texture")?.GetValue(element) as Texture;
                report.AppendLine($"  timeline cubemap '{texture?.name}' ({texture?.width} px)");
            }
        }
    }

    static string PathOf(Transform transform) {
        string path = transform.name;
        for (Transform parent = transform.parent; parent != null; parent = parent.parent) {
            path = parent.name + "/" + path;
        }
        return path;
    }
}
