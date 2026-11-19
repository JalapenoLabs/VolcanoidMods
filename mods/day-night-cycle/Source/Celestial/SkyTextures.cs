// Copyright © 2026 Jalapeno Labs

using JalapenoLabs.DayNightCycle.Lighting;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// Paints the night sky's textures at load: a soft star point, a glow, the Milky Way and the moon's face.
///
/// They are generated rather than shipped as images so the mod stays a single assembly with no asset bundle, which
/// would otherwise need rebuilding in a matching Unity editor, once per platform, after every game update.
/// </summary>
internal static class SkyTextures {
    const uint MilkyWaySeed = 0x4D696C6Bu;
    const uint MoonSeed = 0x4D6F6F6Eu;

    /// <summary>A white point with a soft Gaussian edge: one star.</summary>
    public static Texture2D StarPoint() {
        const int size = 32;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float offsetX = (x + 0.5f) / size * 2 - 1;
                float offsetY = (y + 0.5f) / size * 2 - 1;
                float distanceSquared = offsetX * offsetX + offsetY * offsetY;
                // A tight core inside a faint halo reads as a point of light rather than a fuzzy dot.
                float alpha = Mathf.Exp(-distanceSquared * 14) * 0.85f + Mathf.Exp(-distanceSquared * 4) * 0.15f;
                alpha *= Mathf.Clamp01((1 - distanceSquared) * 4);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }
        }
        return Create("DayNightCycle.StarPoint", size, size, pixels, TextureWrapMode.Clamp);
    }

    /// <summary>A soft radial glow fading to nothing at its edge, for the haze of light around the moon.</summary>
    public static Texture2D Glow() {
        const int size = 128;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float offsetX = (x + 0.5f) / size * 2 - 1;
                float offsetY = (y + 0.5f) / size * 2 - 1;
                float distance = Mathf.Sqrt(offsetX * offsetX + offsetY * offsetY);
                // A bright centre with a long tail, squared off to zero at the edge so the quad's corners never show.
                float edgeFade = Mathf.Clamp01(1 - distance);
                float alpha = edgeFade * edgeFade / (1 + distance * distance * 60);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255));
            }
        }
        return Create("DayNightCycle.Glow", size, size, pixels, TextureWrapMode.Clamp);
    }

    /// <summary>
    /// The Milky Way as a strip to wrap round the sky: u runs once round the galactic plane (and repeats seamlessly),
    /// v runs across the band. A bright, warm core near u = 0.15, cooler arms, and a dark dust lane down the middle.
    /// </summary>
    public static Texture2D MilkyWay() {
        const int width = 1024;
        const int height = 256;
        const int cloudPeriod = 14;
        var pixels = new Color32[width * height];
        var warmCore = new Color(1.0f, 0.88f, 0.74f);
        var coolArm = new Color(0.78f, 0.85f, 1.0f);

        for (int y = 0; y < height; y++) {
            float across = (y + 0.5f) / height - 0.5f;
            for (int x = 0; x < width; x++) {
                float along = (x + 0.5f) / width;

                // Distance round the band from the core, wrapping at 1.
                float fromCore = Mathf.Abs(along - 0.15f);
                fromCore = Mathf.Min(fromCore, 1 - fromCore);
                float core = Mathf.Exp(-fromCore * fromCore / 0.006f) * Mathf.Exp(-across * across / 0.012f);

                // Patchy star clouds rather than a smooth glow: the noise is pushed hard towards dark and bright.
                float clouds = SkyRandom.FractalNoise(
                    along * cloudPeriod,
                    across * 4 + 7,
                    5,
                    cloudPeriod,
                    MilkyWaySeed
                );
                float patches = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.32f, 0.72f, clouds));
                float grain = SkyRandom.FractalNoise(along * 110, across * 36, 3, 110, MilkyWaySeed + 51);
                float band = Mathf.Exp(-across * across / 0.05f) * (0.12f + 1.1f * patches) * (0.6f + 0.8f * grain);

                // The dust lane wanders and breaks up, so it reads as dark clouds, not a ruled line.
                float laneCentre = 0.03f * Mathf.Sin(along * Mathf.PI * 2 * 3);
                float laneNoise = SkyRandom.FractalNoise(along * 40, across * 12, 4, 40, MilkyWaySeed + 99);
                float laneBreaks = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.35f, 0.7f, laneNoise));
                float lane = Mathf.Exp(-(across - laneCentre) * (across - laneCentre) / 0.0025f) * laneBreaks;

                float density = (band + core * 1.2f) * (1 - 0.7f * lane);
                float alpha = Mathf.Clamp01(density * 0.32f);
                Color color = Color.Lerp(coolArm, warmCore, Mathf.Clamp01(core * 2));
                pixels[y * width + x] = new Color32(
                    (byte)(color.r * 255),
                    (byte)(color.g * 255),
                    (byte)(color.b * 255),
                    (byte)(alpha * 255)
                );
            }
        }
        return Create("DayNightCycle.MilkyWay", width, height, pixels, TextureWrapMode.Repeat);
    }

    /// <summary>
    /// The moon's albedo, 0 to 1 per pixel of a square map: bright highlands, dark maria, and craters with raised rims.
    /// Painted once; <see cref="PaintMoonPhase"/> lights it for each night's phase.
    /// </summary>
    public static float[] MoonAlbedo(int size) {
        var albedo = new float[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float u = (float)x / size;
                float v = (float)y / size;
                float maria = SkyRandom.FractalNoise(u * 3.2f + 5, v * 3.2f + 2, 4, 0, MoonSeed);
                float mariaMask = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.47f, 0.60f, maria));
                float grain = SkyRandom.FractalNoise(u * 24, v * 24, 3, 0, MoonSeed + 7) - 0.5f;
                albedo[y * size + x] = 0.66f - 0.27f * mariaMask + 0.10f * grain;
            }
        }

        var random = new SkyRandom(MoonSeed);
        for (int crater = 0; crater < 110; crater++) {
            float centreX = random.Range(0.05f, 0.95f) * size;
            float centreY = random.Range(0.05f, 0.95f) * size;
            // Many small craters, few large ones.
            float radius = (0.012f + 0.08f * Mathf.Pow(random.Next(), 3.5f)) * size;
            int reach = Mathf.CeilToInt(radius * 1.3f);
            for (int y = Mathf.Max(0, (int)centreY - reach); y < Mathf.Min(size, (int)centreY + reach); y++) {
                for (int x = Mathf.Max(0, (int)centreX - reach); x < Mathf.Min(size, (int)centreX + reach); x++) {
                    float distance = Mathf.Sqrt((x - centreX) * (x - centreX) + (y - centreY) * (y - centreY)) / radius;
                    float floor = distance < 0.85f ? -0.05f : 0;
                    float rim = Mathf.Exp(-(distance - 1) * (distance - 1) / 0.012f) * 0.09f;
                    albedo[y * size + x] += floor + rim;
                }
            }
        }
        return albedo;
    }

    /// <summary>
    /// Lights the moon's face for a phase, writing into <paramref name="texture"/>. The sunlit side is drawn towards +x
    /// (the caller turns the quad so +x points at the sun), so one picture serves both waxing and waning moons.
    /// The dark side keeps a faint earthshine and stays opaque, hiding the stars behind it as the real moon does.
    /// </summary>
    public static void PaintMoonPhase(Texture2D texture, float[] albedo, float illumination) {
        int size = texture.width;
        // Angle between the sun and the viewer as seen from the moon: 0 at new moon, 180 degrees at full.
        float phaseAngle = Mathf.Acos(Mathf.Clamp(1 - 2 * illumination, -1, 1));
        float lightX = Mathf.Sin(phaseAngle);
        float lightZ = -Mathf.Cos(phaseAngle);
        float pixelWidth = 2f / size;
        var tint = new Color(1.0f, 0.97f, 0.92f);

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float normalX = (x + 0.5f) / size * 2 - 1;
                float normalY = (y + 0.5f) / size * 2 - 1;
                float radiusSquared = normalX * normalX + normalY * normalY;
                float radius = Mathf.Sqrt(radiusSquared);
                float coverage = Mathf.Clamp01((0.98f - radius) / pixelWidth + 0.5f);
                if (coverage <= 0) {
                    pixels[y * size + x] = new Color32(0, 0, 0, 0);
                    continue;
                }

                float normalZ = Mathf.Sqrt(Mathf.Max(0, 1 - radiusSquared));
                float facing = normalX * lightX + normalZ * lightZ;
                // The moon's dusty surface looks flat when full; a narrow terminator and little falloff keep that.
                float terminator = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.03f, 0.07f, facing));
                float lit = terminator * (0.82f + 0.18f * Mathf.Clamp01(facing));
                float limbDarkening = 0.86f + 0.14f * normalZ;
                float brightness = albedo[y * size + x] * limbDarkening * (lit + NightLook.Earthshine * (1 - lit));
                Color color = tint * Mathf.Clamp01(brightness);
                pixels[y * size + x] = new Color32(
                    (byte)(color.r * 255),
                    (byte)(color.g * 255),
                    (byte)(color.b * 255),
                    (byte)(coverage * 255)
                );
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: true);
    }

    public static Texture2D CreateBlank(string name, int size) {
        return Create(name, size, size, new Color32[size * size], TextureWrapMode.Clamp);
    }

    static Texture2D Create(string name, int width, int height, Color32[] pixels, TextureWrapMode wrapMode) {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true) {
            name = name,
            wrapMode = wrapMode,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: true);
        return texture;
    }
}
