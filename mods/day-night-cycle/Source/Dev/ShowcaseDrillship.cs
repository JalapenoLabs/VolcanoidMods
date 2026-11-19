// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace JalapenoLabs.DayNightCycle.Dev;

/// <summary>
/// Developer captures only (-daynight-showcase): puts a fully built drillship on the island for marketing shots.
///
/// It spawns a drillship design file (such as the game's own StreamingAssets/Drillships/CreativeDrillship.save) the
/// way the game's developer menu does: instantiate the matching drillship prefab, load the design onto it, and
/// teleport it to a landing site. It arrives deployed, with every module out, and a light is built into every empty
/// light slot so the whole hull glows at night.
///
/// Most landing sites are forest clearings, where trees crowd every view of a drillship, so unless a site is named it
/// goes to the most open one: under open sky, with the fewest trees where the cameras will stand.
/// </summary>
internal static class ShowcaseDrillship {
    /// <summary>The Light item (Assets/Prefabs/Subparts/Lights/Light.asset), as designs reference it.</summary>
    static readonly GUID LightItemId = GUID.Parse("2b016a075df938c4a8e20efbdcda209c");

    /// <summary>How far around a landing site trees count against it: the showcase cameras stand within this.</summary>
    const float ClearingReach = 90;

    /// <summary>The sky exposure of a site out in the open, rather than under a cliff or an arch.</summary>
    const float OpenSky = 0.9f;

    readonly struct SiteSurvey {
        public readonly LandingSite Site;
        public readonly float Distance;
        public readonly float Sky;
        public readonly int Trees;

        public SiteSurvey(LandingSite site, Vector3 near) {
            Site = site;
            Vector3 position = site.transform.position;
            Distance = Vector3.Distance(position, near);
            Sky = Lighting.SkyExposure.Measure(position + Vector3.up * 2);
            Trees = TerrainTrees.Around(position, ClearingReach).Count;
        }
    }

    /// <summary>
    /// Spawns the design at the named surface landing site, or with no name at the most open free one. A drillship
    /// already parked at the named site is moved to the free site nearest it first.
    /// </summary>
    public static Train Spawn(string designPath, string siteName, Vector3 near) {
        if (!(PersistenceJsonExtensions.ReadFromString(File.ReadAllText(designPath)) is DataGameObject design)) {
            throw new InvalidDataException($"{designPath} is not a drillship design");
        }
        string prefabName = design.ObjectId.Replace("(Clone)", "");
        ExposedPrefab prefab = RuntimeAssetDatabase.Prefabs.FirstOrDefault(
            candidate => candidate.name == prefabName && candidate.TryGetComponent<Train>(out _)
        );
        if (prefab == null) {
            throw new InvalidOperationException($"No drillship prefab named {prefabName}");
        }

        List<SiteSurvey> surveys = SurveySites(near);
        LandingSite site = siteName != null
            ? FindSite(siteName)
            : MostOpenFreeSite(surveys);
        if (site.Train != null) {
            LandingSite elsewhere = NearestFreeSite(site.transform.position);
            Debug.Log($"{DayNightCycleMod.LogPrefix}Moving the drillship at '{site.Name}' to '{elsewhere.Name}'");
            site.Train.Teleport(elsewhere, TrainState.Deploy, hasFinished: true);
        }

        Train train = Object.Instantiate(prefab.GetComponent<Train>());
        train.gameObject.LoadStandalone(design);
        train.Teleport(site, TrainState.Deploy, hasFinished: true);
        NetworkServer.Spawn(train.gameObject);
        int added = FillLightSlots(train);
        Debug.Log(
            $"{DayNightCycleMod.LogPrefix}Showcase drillship {prefabName} deployed at '{site.Name}' "
            + $"({Vector3.Distance(site.transform.position, near):0} m away), {added} light(s) added"
        );
        return train;
    }

    static LandingSite FindSite(string siteName) {
        LandingSite site = TrackingBehaviour<LandingSite>.Instances.FirstOrDefault(
            candidate => candidate.IslandLevel == 0
                && string.Equals(candidate.Name, siteName, StringComparison.OrdinalIgnoreCase)
        );
        return site ?? throw new InvalidOperationException($"No surface landing site named '{siteName}'");
    }

    static LandingSite NearestFreeSite(Vector3 near) {
        LandingSite site = TrackingBehaviour<LandingSite>.Instances
            .Where(candidate => candidate.IslandLevel == 0 && candidate.Train == null)
            .OrderBy(candidate => (candidate.transform.position - near).sqrMagnitude)
            .FirstOrDefault();
        return site ?? throw new InvalidOperationException("Every surface landing site is taken");
    }

    /// <summary>Surveys and logs the surface landing sites, nearest first, so a capture can name another.</summary>
    static List<SiteSurvey> SurveySites(Vector3 near) {
        List<SiteSurvey> surveys = TrackingBehaviour<LandingSite>.Instances
            .Where(candidate => candidate.IslandLevel == 0)
            .Select(candidate => new SiteSurvey(candidate, near))
            .OrderBy(survey => survey.Distance)
            .ToList();

        var report = new System.Text.StringBuilder(
            $"{DayNightCycleMod.LogPrefix}Surface landing sites (trees within {ClearingReach:0} m):\n"
        );
        foreach (SiteSurvey survey in surveys) {
            report.AppendLine(
                $"  '{survey.Site.Name}' at {survey.Site.transform.position}, {survey.Distance:0} m away, "
                + $"sky {survey.Sky:0.0}, {survey.Trees} trees"
                + (survey.Site.Train != null ? ", occupied" : "")
            );
        }
        Debug.Log(report.ToString().TrimEnd());
        return surveys;
    }

    static LandingSite MostOpenFreeSite(List<SiteSurvey> surveys) {
        List<SiteSurvey> open = surveys
            .Where(survey => survey.Site.Train == null && survey.Sky >= OpenSky)
            .OrderBy(survey => survey.Trees)
            .ThenBy(survey => survey.Distance)
            .ToList();
        if (open.Count == 0) {
            throw new InvalidOperationException("No free surface landing site is under open sky");
        }
        return open[0].Site;
    }

    /// <summary>
    /// Builds a light into every empty slot that takes one: the slots named by the Light item's variants, each filled
    /// with the variant that fits it, as the building tool does. Slots already holding something (lights, Tesla mini
    /// turrets) are left alone. Logs every slot name with what happened to its slots.
    /// </summary>
    static int FillLightSlots(Train train) {
        if (!RuntimeAssetDatabase.TryGetAsset(LightItemId, out Object asset) || !(asset is ItemDefinition lightItem)) {
            Debug.LogWarning(
                $"{DayNightCycleMod.LogPrefix}Light item not found; the showcase keeps the design's lights"
            );
            return 0;
        }
        Subpart[] lightVariants = lightItem.Prefabs.OfType<Subpart>().ToArray();
        var lightSlotNames = new HashSet<string>(lightVariants.SelectMany(variant => variant.SlotNames));

        // Per slot name: how many slots there are, how many already held something, and how many got a light.
        var tally = new SortedDictionary<string, (int Slots, int Filled, int Added)>();
        var skipped = new List<string>();
        foreach (SubpartSlot slot in train.GetComponentsInChildren<SubpartSlot>(includeInactive: true)) {
            if (!lightSlotNames.Contains(slot.SlotName)) {
                continue;
            }
            tally.TryGetValue(slot.SlotName, out (int Slots, int Filled, int Added) counts);
            counts.Slots++;
            if (slot.Subpart != null) {
                counts.Filled++;
                tally[slot.SlotName] = counts;
                continue;
            }

            Subpart variant = null;
            string refusal = "no Light variant fits";
            foreach (Subpart candidate in lightVariants.Where(each => each.SlotNames.Contains(slot.SlotName))) {
                if (slot.CanSwitchSubpart(candidate, out string notificationId)) {
                    variant = candidate;
                    break;
                }
                refusal = notificationId ?? "refused";
            }
            if (variant == null) {
                skipped.Add($"{slot.SlotName} ({refusal})");
            } else {
                slot.SetSubpart(variant);
                counts.Added++;
            }
            tally[slot.SlotName] = counts;
        }

        var report = new System.Text.StringBuilder($"{DayNightCycleMod.LogPrefix}Showcase light slots:");
        foreach (KeyValuePair<string, (int Slots, int Filled, int Added)> entry in tally) {
            report.Append(
                $"\n  {entry.Key}: {entry.Value.Slots} slots, {entry.Value.Filled} already filled, "
                + $"{entry.Value.Added} lights added"
            );
        }
        if (skipped.Count > 0) {
            report.Append($"\n  Skipped: {string.Join(", ", skipped)}");
        }
        Debug.Log(report.ToString());
        return tally.Values.Sum(counts => counts.Added);
    }

    /// <summary>How far deployed modules reach out from the drillship's track.</summary>
    const float DeployedReach = 25;

    /// <summary>
    /// The drillship's visible extent: its mesh renderers, without particles or lines. The pivot can sit at one end,
    /// so a renderer counts as hull within the drillship's length (plus its deployed modules' reach) of the pivot;
    /// anything further out, such as a drone off working, is left out.
    /// </summary>
    public static Bounds MeasureBounds(Train train) {
        Vector3 pivot = train.transform.position;
        float bodyReach = train.Length + DeployedReach;
        Bounds bounds = new Bounds(pivot, Vector3.zero);
        foreach (Renderer renderer in train.GetComponentsInChildren<Renderer>()) {
            bool isBody = (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                && Vector3.Distance(renderer.bounds.center, pivot) <= bodyReach;
            if (isBody) {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        Debug.Log(
            $"{DayNightCycleMod.LogPrefix}Showcase drillship bounds: centre {bounds.center}, size {bounds.size}, "
            + $"length {train.Length:0} m, pivot {pivot}, facing {train.transform.forward}"
        );
        return bounds;
    }
}
