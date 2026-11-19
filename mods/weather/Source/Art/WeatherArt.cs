// Copyright © 2026 Jalapeno Labs

using System;

namespace JalapenoLabs.Weather.Art;

/// <summary>
/// Every texture of the weather, painted from noise at load.
///
/// The mod ships no image files and no asset bundle (which would have to be rebuilt in a matching Unity editor, once
/// per platform, after every game update). These painters run on a worker thread while the game loads; the controller
/// uploads their pixels to textures afterwards. Each is drawn from a fixed seed, so every player gets the same clouds.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public static class WeatherArt {
    /// <summary>Puffs per side of the cloud atlas: 16 different puffs.</summary>
    public const int CloudAtlasTiles = 4;

    /// <summary>Puddle shapes per side of the puddle atlas.</summary>
    public const int PuddleAtlasTiles = 2;

    /// <summary>Drop shapes per side of the lens-drop atlas.</summary>
    public const int LensDropAtlasTiles = 2;

    /// <summary>
    /// Cumulus puffs for the cloud particles, and the normal map that lets the lit particle shader shade them as
    /// volumes: a flat-bottomed blob with a billowing, cauliflower top, its density read as height for the normals.
    /// The albedo carries a little baked self-shadow (darker underneath) so the puffs read as solid even in flat light.
    /// </summary>
    public static (ArtImage Albedo, ArtImage Normals) CloudAtlas(ulong seed, int tileSize = 256) {
        int size = tileSize * CloudAtlasTiles;
        var albedo = new ArtImage(size, size);
        var normals = new ArtImage(size, size);
        var density = new float[size * size];

        for (int tileY = 0; tileY < CloudAtlasTiles; tileY++) {
            for (int tileX = 0; tileX < CloudAtlasTiles; tileX++) {
                ulong tileSeed = seed + (ulong)(tileY * CloudAtlasTiles + tileX) * 104729;
                Lobe[] lobes = PuffLobes(tileSeed);
                for (int y = 0; y < tileSize; y++) {
                    for (int x = 0; x < tileSize; x++) {
                        float u = (x + 0.5f) / tileSize * 2 - 1;
                        float v = (y + 0.5f) / tileSize * 2 - 1;
                        int index = (tileY * tileSize + y) * size + tileX * tileSize + x;
                        density[index] = PuffDensity(tileSeed, lobes, u, v);
                    }
                }
            }
        }

        // How steep the normals are: higher makes the billows read more strongly under the light.
        const float Relief = 2;
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float value = density[y * size + x];
                // A long, soft ramp from the edge inwards: vapour, not a solid with a hard outline.
                float alpha = Math.Max(0, Math.Min(1, value * 1.5f));
                float v = (y % tileSize + 0.5f) / tileSize * 2 - 1;
                float shade = 0.72f + 0.2f * (v + 1) / 2 + 0.12f * value;
                albedo.Set(x, y, shade, shade, shade * 1.02f, alpha);

                float left = density[y * size + Math.Max(0, x - 1)];
                float right = density[y * size + Math.Min(size - 1, x + 1)];
                float below = density[Math.Max(0, y - 1) * size + x];
                float above = density[Math.Min(size - 1, y + 1) * size + x];
                float normalX = -(right - left) * Relief;
                float normalY = -(above - below) * Relief;
                float length = (float)Math.Sqrt(normalX * normalX + normalY * normalY + 1);
                // Alpha 1, so the shader's normal unpacking reads x from red whether it expects RG or AG.
                normals.Set(
                    x,
                    y,
                    normalX / length * 0.5f + 0.5f,
                    normalY / length * 0.5f + 0.5f,
                    1 / length * 0.5f + 0.5f,
                    1
                );
            }
        }
        return (albedo, normals);
    }

    /// <summary>One rounded swelling of a cumulus heap.</summary>
    readonly struct Lobe {
        public readonly float X;
        public readonly float Y;
        public readonly float Radius;

        public Lobe(float x, float y, float radius) {
            X = x;
            Y = y;
            Radius = radius;
        }
    }

    /// <summary>
    /// A cumulus heap as overlapping lobes: broad ones low down and to the middle, smaller ones piled above and to the
    /// sides, so each puff has its own lumpy outline.
    /// </summary>
    static Lobe[] PuffLobes(ulong seed) {
        var random = new Forecast.StormRandom(seed);
        var lobes = new Lobe[5 + (int)(random.NextFloat() * 4)];
        for (int index = 0; index < lobes.Length; index++) {
            float height = random.NextFloat();
            float spread = 0.6f - 0.3f * height;
            lobes[index] = new Lobe(
                random.Range(-spread, spread),
                -0.3f + 0.65f * height,
                random.Range(0.28f, 0.5f) * (1 - 0.35f * height)
            );
        }
        return lobes;
    }

    /// <summary>The density of one puff at (u, v) in [-1, 1]: above zero inside the cloud.</summary>
    static float PuffDensity(ulong seed, Lobe[] lobes, float u, float v) {
        // A soft union of the lobes: each contributes a rounded dome, and the strongest wins.
        float body = 0;
        foreach (Lobe lobe in lobes) {
            float distanceX = (u - lobe.X) / lobe.Radius;
            float distanceY = (v - lobe.Y) / lobe.Radius;
            body = Math.Max(body, 1 - (distanceX * distanceX + distanceY * distanceY));
        }
        // Cumulus have flat bases: cut the underside off smoothly.
        body *= SmoothStep(-0.62f, -0.4f, v);
        // Billow the surface (cauliflower texture) and erode the outline, but only on and near the cloud itself, so
        // the empty corners of the tile stay clear.
        float billows = Noise.Fractal(seed, u * 3.2f + 10, v * 3.2f + 10, 5);
        float nearCloud = SmoothStep(-0.25f, 0.3f, body);
        return body * (0.7f + 0.6f * billows) + (billows - 0.5f) * 0.4f * nearCloud - 0.12f;
    }

    /// <summary>A rain streak: a thin vertical line, brightest at its leading (lower) end.</summary>
    public static ArtImage RainStreak(int width = 8, int height = 128) {
        var image = new ArtImage(width, height);
        for (int y = 0; y < height; y++) {
            float along = y / (float)(height - 1);
            float fade = SmoothStep(0, 0.15f, along) * (1 - SmoothStep(0.55f, 1, along));
            for (int x = 0; x < width; x++) {
                float across = Math.Abs((x + 0.5f) / width * 2 - 1);
                float alpha = (1 - SmoothStep(0.2f, 1, across)) * fade;
                image.Set(x, y, 1, 1, 1, alpha);
            }
        }
        return image;
    }

    /// <summary>
    /// A splash seen from the side, as the upright splash particles show it: a crown of water thrown up from where the
    /// drop struck, a thin sheet at its foot breaking into jets, some ending in a droplet, over a bright skirt.
    ///
    /// The point of impact is the image's centre and the splash rises into its upper half, leaving the lower half
    /// empty: a particle is centred on where it lands, so this way the splash stands on the surface instead of being
    /// half buried in it.
    /// </summary>
    public static ArtImage Splash(ulong seed, int size = 128) {
        const int JetCount = 9;
        const float JetWidth = 0.028f;
        const float DropletRadius = 0.035f;
        // The crown opens upward like a cup: its jets lean out at most this far from upright, in radians.
        const float CrownSpread = 0.75f;
        var directionsX = new float[JetCount];
        var directionsY = new float[JetCount];
        var lengths = new float[JetCount];
        var hasDroplet = new bool[JetCount];
        for (int jet = 0; jet < JetCount; jet++) {
            float slot = (jet + 0.15f + 0.7f * Noise.Value(seed, jet * 3.1f + 0.5f, 0.5f)) / JetCount;
            float lean = (slot * 2 - 1) * CrownSpread;
            directionsX[jet] = (float)Math.Sin(lean);
            directionsY[jet] = (float)Math.Cos(lean);
            lengths[jet] = 0.4f + 0.55f * Noise.Value(seed + 1, jet * 3.1f + 0.5f, 0.5f);
            hasDroplet[jet] = Noise.Value(seed + 2, jet * 3.1f + 0.5f, 0.5f) > 0.4f;
        }

        var image = new ArtImage(size, size);
        for (int y = size / 2; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float u = (x + 0.5f) / size * 2 - 1;
                float v = (y + 0.5f) / size * 2 - 1;
                // The skirt of water at the foot, and the thin cup of the crown's wall rising from it.
                float alpha = (float)Math.Exp(-(u * u / 0.06f + v * v / 0.003f));
                float wallEdge = 0.06f + v * (float)Math.Tan(CrownSpread);
                if (v < 0.3f && Math.Abs(u) < wallEdge) {
                    alpha = Math.Max(alpha, 0.35f * (1 - v / 0.3f) * (0.5f + 0.5f * Math.Abs(u) / wallEdge));
                }
                for (int jet = 0; jet < JetCount; jet++) {
                    float along = u * directionsX[jet] + v * directionsY[jet];
                    float across = Math.Abs(u * directionsY[jet] - v * directionsX[jet]);
                    float length = lengths[jet];
                    if (along > 0 && along < length) {
                        // Thick and bright at the foot, thinning out towards the tip.
                        float thinning = 1 - along / length;
                        float width = JetWidth * (0.4f + 0.6f * thinning);
                        float core = (float)Math.Exp(-Math.Pow(across / width, 2));
                        alpha = Math.Max(alpha, core * (0.25f + 0.65f * thinning));
                    }
                    if (hasDroplet[jet]) {
                        float tipX = u - directionsX[jet] * (length + DropletRadius * 2);
                        float tipY = v - directionsY[jet] * (length + DropletRadius * 2);
                        float droplet = (float)Math.Exp(-(tipX * tipX + tipY * tipY) / (DropletRadius * DropletRadius));
                        alpha = Math.Max(alpha, droplet * 0.75f);
                    }
                }
                image.Set(x, y, 1, 1, 1, Math.Min(1, alpha));
            }
        }
        return image;
    }

    /// <summary>A ripple ring for raindrops landing in puddles.</summary>
    public static ArtImage RippleRing(int size = 128) {
        var image = new ArtImage(size, size);
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float u = (x + 0.5f) / size * 2 - 1;
                float v = (y + 0.5f) / size * 2 - 1;
                float radius = (float)Math.Sqrt(u * u + v * v);
                float ring = (float)Math.Exp(-Math.Pow((radius - 0.8f) / 0.06f, 2))
                    + 0.35f * (float)Math.Exp(-Math.Pow((radius - 0.55f) / 0.05f, 2));
                image.Set(x, y, 1, 1, 1, Math.Min(1, ring));
            }
        }
        return image;
    }

    /// <summary>
    /// Puddle shapes: irregular, soft-edged blobs (alpha), slightly deeper and darker at their middle (red channel
    /// carries depth, so the material can darken the middle more than the shallow edge).
    /// </summary>
    public static ArtImage PuddleAtlas(ulong seed, int tileSize = 128) {
        int size = tileSize * PuddleAtlasTiles;
        var image = new ArtImage(size, size);
        for (int tileY = 0; tileY < PuddleAtlasTiles; tileY++) {
            for (int tileX = 0; tileX < PuddleAtlasTiles; tileX++) {
                ulong tileSeed = seed + (ulong)(tileY * PuddleAtlasTiles + tileX) * 7907;
                for (int y = 0; y < tileSize; y++) {
                    for (int x = 0; x < tileSize; x++) {
                        float u = (x + 0.5f) / tileSize * 2 - 1;
                        float v = (y + 0.5f) / tileSize * 2 - 1;
                        float radius = (float)Math.Sqrt(u * u + v * v);
                        float angle = (float)Math.Atan2(v, u);
                        float lobes = 0.12f * (float)Math.Sin(angle * 3 + tileSeed % 7);
                        float edge = 0.45f + lobes + 0.45f * Noise.Fractal(tileSeed, u * 2.5f + 5, v * 2.5f + 5, 4);
                        float inside = 1 - SmoothStep(edge - 0.12f, edge, radius);
                        float depth = 1 - SmoothStep(0, edge, radius);
                        image.Set(tileX * tileSize + x, tileY * tileSize + y, depth, depth, depth, inside);
                    }
                }
            }
        }
        return image;
    }

    /// <summary>
    /// Water drops on the lens: each focuses light like a little lens, so it is brighter low down and has a dark rim
    /// above and a bright highlight near its top.
    /// </summary>
    public static ArtImage LensDropAtlas(ulong seed, int tileSize = 128) {
        int size = tileSize * LensDropAtlasTiles;
        var image = new ArtImage(size, size);
        for (int tileY = 0; tileY < LensDropAtlasTiles; tileY++) {
            for (int tileX = 0; tileX < LensDropAtlasTiles; tileX++) {
                ulong tileSeed = seed + (ulong)(tileY * LensDropAtlasTiles + tileX) * 6007;
                for (int y = 0; y < tileSize; y++) {
                    for (int x = 0; x < tileSize; x++) {
                        float u = (x + 0.5f) / tileSize * 2 - 1;
                        float v = (y + 0.5f) / tileSize * 2 - 1;
                        float wobble = 0.85f + 0.15f * Noise.Value(tileSeed, u * 2 + 3, v * 2 + 3);
                        float radius = (float)Math.Sqrt(u * u + v * v) / wobble;
                        float inside = 1 - SmoothStep(0.8f, 0.92f, radius);
                        float rim = (float)Math.Exp(-Math.Pow((radius - 0.82f) / 0.08f, 2))
                            * SmoothStep(-0.2f, 0.6f, v);
                        float focus = SmoothStep(0.2f, -0.7f, v) * (1 - radius);
                        float highlight = (float)Math.Exp(-(Math.Pow(u + 0.25f, 2) + Math.Pow(v - 0.4f, 2)) / 0.015f);
                        float brightness = Math.Min(1, 0.55f + 0.5f * focus + highlight - 0.45f * rim);
                        float alpha = inside * Math.Min(1, 0.35f + 0.4f * rim + 0.3f * focus + highlight);
                        image.Set(
                            tileX * tileSize + x,
                            tileY * tileSize + y,
                            brightness,
                            brightness,
                            brightness,
                            alpha
                        );
                    }
                }
            }
        }
        return image;
    }

    /// <summary>
    /// The cross-section of a lightning bolt, for its line renderer: a white-hot core inside a wider violet glow.
    /// Constant along the bolt (u), shaped across it (v).
    /// </summary>
    public static ArtImage BoltGlow(int width = 4, int height = 64) {
        var image = new ArtImage(width, height);
        for (int y = 0; y < height; y++) {
            float across = (y + 0.5f) / height * 2 - 1;
            float core = (float)Math.Exp(-Math.Pow(across / 0.12f, 2));
            float glow = (float)Math.Exp(-Math.Pow(across / 0.5f, 2));
            float red = Math.Min(1, core + glow * 0.55f);
            float green = Math.Min(1, core + glow * 0.5f);
            float blue = Math.Min(1, core + glow * 0.95f);
            float alpha = Math.Min(1, core + glow * 0.6f);
            for (int x = 0; x < width; x++) {
                image.Set(x, y, red, green, blue, alpha);
            }
        }
        return image;
    }

    static float SmoothStep(float edge0, float edge1, float value) {
        float position = Math.Max(0, Math.Min(1, (value - edge0) / (edge1 - edge0)));
        return position * position * (3 - 2 * position);
    }
}
