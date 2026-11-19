// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Forecast;

namespace JalapenoLabs.Weather.Art;

/// <summary>
/// A generated image: 8-bit RGBA pixels, rows from the bottom up (as Unity's textures store them).
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public sealed class ArtImage {
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Rgba;

    public ArtImage(int width, int height) {
        Width = width;
        Height = height;
        Rgba = new byte[width * height * 4];
    }

    public void Set(int x, int y, float red, float green, float blue, float alpha) {
        int index = (y * Width + x) * 4;
        Rgba[index] = ToByte(red);
        Rgba[index + 1] = ToByte(green);
        Rgba[index + 2] = ToByte(blue);
        Rgba[index + 3] = ToByte(alpha);
    }

    public float Alpha(int x, int y) {
        return Rgba[(y * Width + x) * 4 + 3] / 255f;
    }

    static byte ToByte(float value) {
        return (byte)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);
    }
}

/// <summary>
/// Smooth value noise and its fractal sum, the raw material of every cloud, puddle and drop shape.
///
/// This file deliberately uses no Unity types, so the unit tests compile it as plain .NET.
/// </summary>
public static class Noise {
    /// <summary>
    /// Value noise in [0, 1]. With a period, the lattice wraps, so a texture made from it tiles seamlessly.
    /// </summary>
    public static float Value(ulong seed, float x, float y, int period = 0) {
        int cellX = (int)Math.Floor(x);
        int cellY = (int)Math.Floor(y);
        float fractionX = x - cellX;
        float fractionY = y - cellY;
        float easeX = fractionX * fractionX * (3 - 2 * fractionX);
        float easeY = fractionY * fractionY * (3 - 2 * fractionY);

        float bottom = Lerp(Lattice(seed, cellX, cellY, period), Lattice(seed, cellX + 1, cellY, period), easeX);
        float top = Lerp(Lattice(seed, cellX, cellY + 1, period), Lattice(seed, cellX + 1, cellY + 1, period), easeX);
        return Lerp(bottom, top, easeY);
    }

    /// <summary>Fractal (fBm) noise in about [0, 1]: octaves of value noise, each finer and fainter.</summary>
    public static float Fractal(ulong seed, float x, float y, int octaves, int period = 0) {
        float sum = 0;
        float amplitude = 0.5f;
        float total = 0;
        float frequency = 1;
        for (int octave = 0; octave < octaves; octave++) {
            int octavePeriod = period > 0 ? period * (int)frequency : 0;
            sum += amplitude * Value(seed + (ulong)octave * 7919, x * frequency, y * frequency, octavePeriod);
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2;
        }
        return sum / total;
    }

    static float Lattice(ulong seed, int x, int y, int period) {
        if (period > 0) {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
        }
        ulong hash = StormRandom.Mix(seed ^ StormRandom.Mix((ulong)(uint)x | ((ulong)(uint)y << 32)));
        return (hash >> 40) * (1f / (1 << 24));
    }

    static float Lerp(float from, float to, float amount) {
        return from + (to - from) * amount;
    }
}
