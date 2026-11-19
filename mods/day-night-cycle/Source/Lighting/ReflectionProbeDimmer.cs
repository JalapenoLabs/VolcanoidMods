// Copyright © 2026 Jalapeno Labs

using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// Dims the island's baked reflection probes, so metal and wet rock stop mirroring a daytime sky at night. Each probe
/// is dimmed by how much sky it sees, so cave probes keep their lava glow.
///
/// Applied per camera because the eruption timeline sets the surface probes' intensities every frame (it cross-fades
/// between "normal" and "after the blast" probes), and the night multiplies whatever it set.
/// </summary>
internal sealed class ReflectionProbeDimmer : ICameraEffect {
    readonly List<ReflectionProbe> probes = new List<ReflectionProbe>();
    readonly List<float> exposure = new List<float>();
    readonly List<float> savedIntensity = new List<float>();
    bool isApplied;

    public ReflectionProbeDimmer() {
        var allProbes = Object.FindObjectsByType<ReflectionProbe>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
        foreach (ReflectionProbe probe in allProbes) {
            float probeExposure = SkyExposure.Measure(probe.transform.position);
            if (probeExposure <= 0) {
                continue;
            }
            probes.Add(probe);
            exposure.Add(probeExposure);
            savedIntensity.Add(0);
        }
        Debug.Log($"{DayNightCycleMod.LogPrefix}Reflection probes: {probes.Count} of {allProbes.Length} see the sky");
    }

    public void Apply(in NightFrame frame, Camera camera, float surface) {
        // World darkness, not the camera's: these probes sit on the surface, so a surface probe seen from a cave
        // mouth is still in the night.
        if (frame.Darkness <= 0) {
            return;
        }
        float nightScale = Mathf.Lerp(1, frame.NightAmbientLevel, frame.Darkness);
        for (int index = 0; index < probes.Count; index++) {
            ReflectionProbe probe = probes[index];
            if (probe == null) {
                continue;
            }
            savedIntensity[index] = probe.intensity;
            probe.intensity *= Mathf.Lerp(1, nightScale, exposure[index]);
        }
        isApplied = true;
    }

    public void Restore() {
        if (!isApplied) {
            return;
        }
        for (int index = 0; index < probes.Count; index++) {
            if (probes[index] != null) {
                probes[index].intensity = savedIntensity[index];
            }
        }
        isApplied = false;
    }
}
