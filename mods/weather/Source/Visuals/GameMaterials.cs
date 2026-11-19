// Copyright © 2026 Jalapeno Labs

using System;
using System.Linq;
using JalapenoLabs.Weather.Art;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>
/// Materials and textures for the storm's effects, built on the game's own shaders.
///
/// Unity strips each shader down to the keyword variants the game's materials use, and a mod cannot compile shaders at
/// runtime, so a material only renders if it asks for a variant some game material already uses. Every storm material
/// therefore starts as a copy of a game material with exactly the right keywords (found by its keywords, not its
/// name, so a renamed asset doesn't matter), and changes only properties: textures, colours, blend factors.
/// </summary>
internal static class GameMaterials {
    public const string ParticlesSimpleLit = "Universal Render Pipeline/Particles/Simple Lit";
    public const string ParticlesUnlit = "Universal Render Pipeline/Particles/Unlit";
    public const string Lit = "Universal Render Pipeline/Lit";

    public static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    public static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    public static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
    public static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
    public static readonly int MetallicId = Shader.PropertyToID("_Metallic");
    public static readonly int SourceBlendId = Shader.PropertyToID("_SrcBlend");
    public static readonly int DestinationBlendId = Shader.PropertyToID("_DstBlend");

    /// <summary>
    /// A copy of a game material on <paramref name="shaderName"/> whose enabled shader keywords are exactly
    /// <paramref name="keywords"/> (order doesn't matter), or null with a warning if the game has none. The game
    /// material named <paramref name="preferredTemplate"/> is used if it qualifies, since its other properties (maps,
    /// scales) are the closest to what the storm wants; otherwise any game material with the same variant will do.
    ///
    /// Only keywords the shader declares count (Material.enabledKeywords): they alone pick the variant. Game materials
    /// also carry leftover keyword names the shader never declared (such as _ALPHABLEND_ON), which change nothing.
    /// </summary>
    public static Material CloneVariant(
        string shaderName,
        string preferredTemplate,
        string purpose,
        params string[] keywords
    ) {
        Material[] candidates = Resources.FindObjectsOfTypeAll<Material>()
            .Where(material => material != null
                && material.shader != null
                && material.shader.name == shaderName
                // The storm's own materials live in memory too; never build on one of those.
                && !material.name.StartsWith("Weather.")
                && material.enabledKeywords.Length == keywords.Length
                && material.enabledKeywords.All(keyword => keywords.Contains(keyword.name)))
            .ToArray();
        Material template = candidates.FirstOrDefault(material => material.name == preferredTemplate)
            ?? candidates.FirstOrDefault();
        if (template == null) {
            Debug.LogWarning(
                $"{WeatherMod.LogPrefix}No game material uses {shaderName} with [{string.Join(" ", keywords)}]; "
                + $"the storm's {purpose} stays off"
            );
            return null;
        }
        var material = new Material(template) { name = $"Weather.{purpose}" };
        // The template's own texture may sit in an atlas or flipbook of its own; the storm's textures fill theirs.
        foreach (int textureId in new[] { BaseMapId, BumpMapId }) {
            if (material.HasProperty(textureId)) {
                material.SetTextureScale(textureId, Vector2.one);
                material.SetTextureOffset(textureId, Vector2.zero);
            }
        }
        Debug.Log($"{WeatherMod.LogPrefix}The storm's {purpose} copies the game's '{template.name}' material");
        return material;
    }

    /// <summary>
    /// The storm's lit particles are built on the game's lit smoke ('SmokeLightParticle'): Simple Lit, normal-mapped,
    /// with flipbook blending, and its vertex colour applied as an overlay (so 0.5 grey leaves the texture as it is).
    /// Every light in the scene lights it, including the lightning, and it blends with plain alpha.
    /// </summary>
    public static Material CloneLitSmoke(string purpose) {
        return CloneVariant(
            ParticlesSimpleLit,
            "SmokeLightParticle",
            purpose,
            "_COLOROVERLAY_ON",
            "_FLIPBOOKBLENDING_ON",
            "_NORMALMAP",
            "_SURFACE_TYPE_TRANSPARENT"
        );
    }

    /// <summary>
    /// The vertex data the lit smoke variant reads: with flipbook blending it takes both flipbook frames' coordinates
    /// in TEXCOORD0 and their blend in TEXCOORD1, and the tangent for its normal map. A particle renderer must be told.
    /// </summary>
    public static void UseLitSmokeStreams(ParticleSystemRenderer renderer) {
        renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream> {
            ParticleSystemVertexStream.Position,
            ParticleSystemVertexStream.Normal,
            ParticleSystemVertexStream.Tangent,
            ParticleSystemVertexStream.Color,
            ParticleSystemVertexStream.UV,
            ParticleSystemVertexStream.UV2,
            ParticleSystemVertexStream.AnimBlend,
        });
    }

    /// <summary>Uploads a painted image as a texture, with mipmaps, clamped at its edges unless it tiles.</summary>
    public static Texture2D Upload(string name, ArtImage image, bool isLinear = false, bool isTiled = false) {
        var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, true, isLinear) {
            name = $"Weather.{name}",
            wrapMode = isTiled ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
        };
        texture.SetPixelData(image.Rgba, 0);
        // Keep no CPU copy: the pixels live on the GPU from here on.
        texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        return texture;
    }

    public static void Destroy(params Object[] objects) {
        foreach (Object item in objects) {
            if (item != null) {
                Object.Destroy(item);
            }
        }
    }
}
