// Copyright © 2026 Jalapeno Labs

using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// Creates the sky's renderers and materials.
///
/// Every part of the night sky draws with Unity's built-in Sprites/Default shader, which the game ships in its
/// built-in shader bundle. It is unlit, alpha-blended and depth-tested, and unlike the game's own particle shaders it
/// takes no fog, so stars at the far plane stay crisp instead of vanishing into the fog colour. Mountains still hide
/// them, because the sky draws after the game's opaque terrain and tests against its depth.
///
/// Render queues start just past the opaque range, so the sky draws right after the skybox and before every
/// transparent effect the game has, each part over the ones behind it.
/// </summary>
internal static class SkyRenderers {
    public const int MilkyWayQueue = 2501;
    public const int StarQueue = 2502;
    public const int MoonQueue = 2503;
    /// <summary>After the moon: the glow is air lit in front of it, and must not be cut out by its dark side.</summary>
    public const int MoonHaloQueue = 2504;
    public const int HorizonHazeQueue = 2505;

    const string ShaderName = "Sprites/Default";

    public static Material CreateMaterial(string name, Texture texture, int renderQueue) {
        Shader shader = Shader.Find(ShaderName);
        if (shader == null) {
            throw new InvalidOperationException(
                $"The game no longer ships the {ShaderName} shader the night sky draws with"
            );
        }
        return new Material(shader) {
            name = name,
            mainTexture = texture,
            renderQueue = renderQueue,
            color = new Color(1, 1, 1, 0),
        };
    }

    /// <summary>A disabled renderer for a sky part, enabled only while a surface camera renders at night.</summary>
    public static MeshRenderer CreateRenderer(string name, Transform parent, Mesh mesh, Material material) {
        var host = new GameObject(name);
        host.transform.SetParent(parent, worldPositionStays: false);
        host.AddComponent<MeshFilter>().sharedMesh = mesh;

        var renderer = host.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        // The island has baked occlusion data; a camera-centred sky must never be culled as "behind" a hill.
        renderer.allowOcclusionWhenDynamic = false;
        renderer.enabled = false;
        return renderer;
    }

    public static void Destroy(Object asset) {
        if (asset != null) {
            Object.Destroy(asset);
        }
    }
}
