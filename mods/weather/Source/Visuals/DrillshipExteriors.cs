// Copyright © 2026 Jalapeno Labs

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
/// built and removed as the game goes on, so the exteriors are found again every few seconds; whatever is derived from
/// them watches <see cref="Version"/> to know when to rebuild.
/// </summary>
internal sealed class DrillshipExteriors {
    const float RefreshSeconds = 8;

    readonly List<DrillshipExterior> all = new List<DrillshipExterior>();
    float nextRefresh;

    public IReadOnlyList<DrillshipExterior> All => all;

    /// <summary>Goes up by one each time the exteriors are found again; 0 until they first are.</summary>
    public int Version { get; private set; }

    public void Update() {
        if (Time.time < nextRefresh) {
            return;
        }
        nextRefresh = Time.time + RefreshSeconds;
        all.Clear();
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
            all.Add(new DrillshipExterior(wagons.transform, renderers));
        }
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
