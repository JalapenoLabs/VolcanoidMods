// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.Weather.Visuals;

/// <summary>The outside of one drillship: the renderers of its exterior case and hull.</summary>
internal sealed class DrillshipExterior {
    public readonly Transform Root;
    public readonly List<Renderer> Renderers;

    public DrillshipExterior(Transform root, List<Renderer> renderers) {
        Root = root;
        Renderers = renderers;
    }
}

/// <summary>
/// The outside of every drillship on the island: what the rain wets and drums on.
///
/// A drillship's paint system already separates its exterior from its interior (the paint grid's exterior case, and
/// the paint segment items without the interior decal layer), so that is what decides what is outside. Modules are
/// built and removed as the game goes on, so the exteriors are looked for again every few seconds; whatever is derived
/// from them watches <see cref="Version"/> to know when to rebuild, which only changes when they really did.
/// </summary>
internal sealed class DrillshipExteriors {
    const float RefreshSeconds = 8;

    readonly List<DrillshipExterior> all = new List<DrillshipExterior>();
    float nextRefresh;
    int signature;

    public IReadOnlyList<DrillshipExterior> All => all;

    /// <summary>
    /// Goes up by one each time the exteriors change (a drillship or module comes or goes); 0 until first found.
    /// </summary>
    public int Version { get; private set; }

    public void Update() {
        if (Time.time < nextRefresh) {
            return;
        }
        nextRefresh = Time.time + RefreshSeconds;
        var found = new List<DrillshipExterior>();
        var foundSignature = new HashCode();
        foreach (TrainWagons wagons in TrainWagons.Instances) {
            if (wagons == null) {
                continue;
            }
            var renderers = new List<Renderer>();
            foreach (PaintGridModule module in wagons.GetComponentsInChildren<PaintGridModule>()) {
                AddRenderers(module.ExteriorCase, renderers);
            }
            foreach (PaintSegment segment in wagons.GetComponentsInChildren<PaintSegment>()) {
                foreach (PaintSegment.NamedItem item in segment.Items) {
                    if ((item.DecalLayer & PaintDecalLayer.Interior) == 0) {
                        AddRenderers(item.Element as PaintElement, renderers);
                    }
                }
            }
            found.Add(new DrillshipExterior(wagons.transform, renderers));
            foundSignature.Add(wagons.GetInstanceID());
            foreach (Renderer renderer in renderers) {
                foundSignature.Add(renderer.GetInstanceID());
            }
        }
        // Unchanged exteriors keep their version, so nothing derived from them (such as where run-off falls) jumps.
        int newSignature = foundSignature.ToHashCode();
        if (Version > 0 && newSignature == signature) {
            return;
        }
        signature = newSignature;
        all.Clear();
        all.AddRange(found);
        Version++;
    }

    static void AddRenderers(PaintElement element, List<Renderer> renderers) {
        if (element == null) {
            return;
        }
        foreach (PaintElement.Element part in element.Renderers) {
            if (part.Renderer != null) {
                renderers.Add(part.Renderer);
            }
        }
    }
}
