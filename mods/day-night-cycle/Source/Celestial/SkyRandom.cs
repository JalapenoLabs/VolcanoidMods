// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// A tiny deterministic random-number generator (xorshift) with seedable value noise.
///
/// The stars, the Milky Way and the moon's face are generated, not shipped as art. Generating them from a fixed seed
/// with our own generator, rather than System.Random or UnityEngine.Random, guarantees every player's copy of the mod
/// draws the identical sky, star for star.
/// </summary>
internal struct SkyRandom {
    uint state;

    public SkyRandom(uint seed) {
        state = seed == 0 ? 0x9E3779B9u : seed;
    }

    /// <summary>A number in [0, 1).</summary>
    public float Next() {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state >> 8) * (1f / 16777216f);
    }

    public float Range(float minimum, float maximum) {
        return minimum + (maximum - minimum) * Next();
    }

    /// <summary>A uniformly distributed direction on the unit sphere.</summary>
    public Vector3 Direction() {
        float height = Range(-1, 1);
        float angle = Range(0, 2 * Mathf.PI);
        float radius = Mathf.Sqrt(1 - height * height);
        return new Vector3(radius * Mathf.Cos(angle), height, radius * Mathf.Sin(angle));
    }

    /// <summary>A normally distributed number (mean 0, deviation 1), by the Box-Muller transform.</summary>
    public float Gaussian() {
        float first = Mathf.Max(Next(), 1e-7f);
        float second = Next();
        return Mathf.Sqrt(-2 * Mathf.Log(first)) * Mathf.Cos(2 * Mathf.PI * second);
    }

    /// <summary>
    /// Smooth value noise in [0, 1], repeating every <paramref name="periodX"/> cells along x so a texture wrapped
    /// round the sky has no seam.
    /// </summary>
    public static float ValueNoise(float x, float y, int periodX, uint seed) {
        int cellX = Mathf.FloorToInt(x);
        int cellY = Mathf.FloorToInt(y);
        float fractionX = x - cellX;
        float fractionY = y - cellY;
        float easeX = fractionX * fractionX * (3 - 2 * fractionX);
        float easeY = fractionY * fractionY * (3 - 2 * fractionY);

        float bottom = Mathf.Lerp(
            Lattice(cellX, cellY, periodX, seed),
            Lattice(cellX + 1, cellY, periodX, seed),
            easeX
        );
        float top = Mathf.Lerp(
            Lattice(cellX, cellY + 1, periodX, seed),
            Lattice(cellX + 1, cellY + 1, periodX, seed),
            easeX
        );
        return Mathf.Lerp(bottom, top, easeY);
    }

    /// <summary>Fractal noise: octaves of value noise, each finer and fainter. Result in about [0, 1].</summary>
    public static float FractalNoise(float x, float y, int octaves, int periodX, uint seed) {
        float total = 0;
        float amplitude = 0.5f;
        float frequency = 1;
        float amplitudeSum = 0;
        for (int octave = 0; octave < octaves; octave++) {
            int octavePeriod = periodX * (int)frequency;
            total += amplitude * ValueNoise(x * frequency, y * frequency, octavePeriod, seed + (uint)octave);
            amplitudeSum += amplitude;
            amplitude *= 0.5f;
            frequency *= 2;
        }
        return total / amplitudeSum;
    }

    static float Lattice(int x, int y, int periodX, uint seed) {
        int wrappedX = periodX > 0 ? ((x % periodX) + periodX) % periodX : x;
        uint hash = (uint)wrappedX * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ seed * 0xCB1AB31Fu;
        hash ^= hash >> 13;
        hash *= 0x5BD1E995u;
        hash ^= hash >> 15;
        return (hash & 0xFFFFFF) * (1f / 16777215f);
    }
}
