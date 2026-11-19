// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Dev;

/// <summary>
/// Developer captures only: works out where to stand to photograph the showcase drillship
/// (<see cref="ShowcaseDrillship"/>).
///
/// Landing sites sit among rocks, buildings and trees. Rocks and buildings have colliders, so a raycast finds them;
/// trees have none and come from <see cref="TerrainTrees"/>. Each view tries a grid of camera positions around its
/// ideal one, best first, and keeps the first that fits the whole hull in frame with every sightline to it clear (and,
/// for the moon view, the moon in frame and in sight).
/// </summary>
internal sealed class ShowcaseCamera {
    /// <summary>
    /// The showcase lens, vertical. The game's own camera is a very wide 90 degrees, which shrinks a drillship to a
    /// speck; a narrower lens frames it like a product shot.
    /// </summary>
    public const float FieldOfView = 50;

    /// <summary>Where the moon sits in the moon view, as a fraction of the half-height above the centre.</summary>
    const float MoonHeightInFrame = 0.55f;

    /// <summary>How much of the frame, out from its centre, the moon must sit inside to count as in the shot.</summary>
    const float MoonFrameMargin = 0.85f;

    /// <summary>How much of the frame, out from its centre, the whole hull must fit inside.</summary>
    const float HullFrameMargin = 0.95f;

    /// <summary>How far around the drillship trees are read; further out, sightlines clear any canopy.</summary>
    const float TreeReach = 250;

    const float GroundClearance = 2.5f;

    // The candidate grid: degrees off the view's ideal heading, and distances and heights as multiples of the
    // drillship's radius. Each list runs from most to least preferred. Low cameras look up at the hull against the
    // sky, and the ideal heading is the intended composition. The moon view keeps within 32 degrees of its heading so
    // the moon stays in frame.
    static readonly float[] MoonHeadingOffsets = { 0, 12, -12, 24, -24, 32, -32 };
    static readonly float[] SideHeadingOffsets = { 0, 20, -20, 40, -40, 60, -60, 90, -90, 120, -120, 150, -150, 180 };
    static readonly float[] MoonDistances = { 2.1f, 2.6f, 3.2f, 1.7f };
    static readonly float[] SideDistances = { 1.35f, 1.7f, 2.1f, 2.6f };
    static readonly float[] Heights = { 0.15f, 0.4f, 0.7f, 1.0f, 1.3f };

    readonly struct Vantage {
        public readonly Vector3 Position;
        public readonly int Rank;
        public readonly string Description;

        public Vantage(Vector3 position, int rank, string description) {
            Position = position;
            Rank = rank;
            Description = description;
        }
    }

    readonly Train ship;
    readonly Bounds bounds;
    readonly float radius;
    readonly float aspect;
    readonly Vector3[] hullTargets;
    readonly Vector3[] hullCorners;
    readonly List<TerrainTrees.Tree> trees;

    public ShowcaseCamera(Train ship, Bounds bounds, float aspect) {
        this.ship = ship;
        this.bounds = bounds;
        this.aspect = aspect;
        radius = bounds.extents.magnitude;
        hullCorners = Corners(bounds).ToArray();
        trees = TerrainTrees.Around(bounds.center, TreeReach);
        Vector3[] allTargets = HullTargets(bounds);
        hullTargets = allTargets.Where(IsExposed).ToArray();
        Debug.Log(
            $"{DayNightCycleMod.LogPrefix}Showcase framing steers around {trees.Count} tree(s) within "
            + $"{TreeReach:0} m, sighting {hullTargets.Length} of {allTargets.Length} points on the hull"
        );
        if (hullTargets.Length == 0) {
            Debug.LogWarning($"{DayNightCycleMod.LogPrefix}Every hull point is buried; framing cannot check views");
        }
    }

    /// <summary>
    /// Stands on the far side of the drillship from the moon and tilts up just enough to put the moon in the upper
    /// part of the frame, with the whole hull below it.
    /// </summary>
    public Pose FrameUnderMoon(Vector3 moon) {
        Vector3 awayFromMoon = -Vector3.ProjectOnPlane(moon, Vector3.up);
        return Choose(
            "moon view",
            Candidates(awayFromMoon, MoonHeadingOffsets, MoonDistances),
            position => AimUnderMoon(position, moon),
            pose => ShowsWholeHull(pose)
                && IsInFrame(pose, moon, MoonFrameMargin)
                && !IsBlocked(pose.position, pose.position + moon * TreeReach)
        );
    }

    /// <summary>
    /// A close three-quarter view along the drillship's side, where its modules and lights are, with the whole hull in
    /// the frame.
    /// </summary>
    public Pose FrameAlongSide() {
        Transform hull = ship.transform;
        Vector3 threeQuarter = hull.right * 1.3f + hull.forward * 0.7f;
        return Choose(
            "side view",
            Candidates(threeQuarter, SideHeadingOffsets, SideDistances),
            position => new Pose(position, Quaternion.LookRotation(bounds.center - position)),
            ShowsWholeHull
        );
    }

    /// <summary>
    /// Takes the first candidate, in rank order, with every sightline clear and the view's own condition met; failing
    /// that, the one with the fewest problems, with a warning.
    /// </summary>
    Pose Choose(string viewName, IEnumerable<Vantage> candidates, Func<Vector3, Pose> aim, Func<Pose, bool> suits) {
        Pose best = default;
        string bestDescription = null;
        int fewestProblems = int.MaxValue;
        foreach (Vantage vantage in candidates) {
            Pose pose = aim(vantage.Position);
            int problems = hullTargets.Count(target => IsBlocked(vantage.Position, target));
            if (!suits(pose)) {
                problems++;
            }
            if (problems < fewestProblems) {
                best = pose;
                bestDescription = vantage.Description;
                fewestProblems = problems;
            }
            if (problems == 0) {
                break;
            }
        }

        if (fewestProblems > 0) {
            Debug.LogWarning(
                $"{DayNightCycleMod.LogPrefix}Showcase {viewName}: no fully clear view; using {bestDescription} "
                + $"with {fewestProblems} problem(s) (blocked sightlines, or the hull or moon out of frame)"
            );
        } else {
            Debug.Log($"{DayNightCycleMod.LogPrefix}Showcase {viewName}: clear from {bestDescription}");
        }
        return best;
    }

    /// <summary>
    /// Every combination of the grid around the ideal heading, best first. A candidate's rank is the sum of its
    /// positions in the three lists, so a step away from the ideal costs the same whichever way it goes.
    /// </summary>
    IEnumerable<Vantage> Candidates(Vector3 idealHeading, float[] headingOffsets, float[] distances) {
        Vector3 ideal = Vector3.ProjectOnPlane(idealHeading, Vector3.up).normalized;
        var candidates = new List<Vantage>();
        for (int offsetIndex = 0; offsetIndex < headingOffsets.Length; offsetIndex++) {
            Vector3 heading = Quaternion.AngleAxis(headingOffsets[offsetIndex], Vector3.up) * ideal;
            for (int distanceIndex = 0; distanceIndex < distances.Length; distanceIndex++) {
                for (int heightIndex = 0; heightIndex < Heights.Length; heightIndex++) {
                    Vector3 position = bounds.center
                        + heading * radius * distances[distanceIndex]
                        + Vector3.up * radius * Heights[heightIndex];
                    string description = $"{headingOffsets[offsetIndex]:0} degrees off, "
                        + $"{radius * distances[distanceIndex]:0} m out, "
                        + $"{radius * Heights[heightIndex]:0} m above the hull's centre";
                    candidates.Add(new Vantage(
                        KeepAboveGround(position),
                        offsetIndex + distanceIndex + heightIndex,
                        description
                    ));
                }
            }
        }
        return candidates.OrderBy(candidate => candidate.Rank);
    }

    /// <summary>Whether a tree, rock or building stands between two points; the drillship's own parts don't.</summary>
    bool IsBlocked(Vector3 from, Vector3 to) {
        foreach (TerrainTrees.Tree tree in trees) {
            if (tree.Crosses(from, to)) {
                return true;
            }
        }
        Vector3 path = to - from;
        RaycastHit[] hits = Physics.RaycastAll(
            from,
            path.normalized,
            path.magnitude,
            Layers.MaskToolRaycast,
            QueryTriggerInteraction.Ignore
        );
        return hits.Any(hit => !hit.collider.transform.IsChildOf(ship.transform));
    }

    /// <summary>Faces the hull, pitched up to put the moon high in the frame with the hull still below.</summary>
    Pose AimUnderMoon(Vector3 position, Vector3 moon) {
        float halfHeight = FieldOfView * 0.5f;
        float moonElevation = Mathf.Asin(moon.y) * Mathf.Rad2Deg;
        float pitch = moonElevation - MoonHeightInFrame * halfHeight;
        // Never tilt so far up that the hull's lowest corner drops out of the bottom of the frame.
        float lowestCorner = hullCorners.Min(corner => {
            Vector3 toCorner = corner - position;
            return Mathf.Atan2(toCorner.y, new Vector2(toCorner.x, toCorner.z).magnitude) * Mathf.Rad2Deg;
        });
        pitch = Mathf.Min(pitch, lowestCorner + HullFrameMargin * halfHeight);

        Vector3 heading = Vector3.ProjectOnPlane(bounds.center - position, Vector3.up).normalized;
        Vector3 forward = heading * Mathf.Cos(pitch * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(pitch * Mathf.Deg2Rad);
        return new Pose(position, Quaternion.LookRotation(forward));
    }

    /// <summary>
    /// Whether every corner of the drillship's bounds is in frame. The frame is convex, so the hull inside the bounds
    /// is then wholly in frame too.
    /// </summary>
    bool ShowsWholeHull(Pose pose) {
        return hullCorners.All(corner => IsInFrame(pose, corner - pose.position, HullFrameMargin));
    }

    /// <summary>Whether a direction from the camera lands inside <paramref name="margin"/> of the frame.</summary>
    bool IsInFrame(Pose pose, Vector3 direction, float margin) {
        Vector3 local = Quaternion.Inverse(pose.rotation) * direction;
        float halfHeight = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) * margin;
        return local.z > 0
            && Mathf.Abs(local.y / local.z) <= halfHeight
            && Mathf.Abs(local.x / local.z) <= halfHeight * aspect;
    }

    static Vector3 KeepAboveGround(Vector3 position) {
        bool hasGround = Physics.Raycast(
            position + Vector3.up * 500,
            Vector3.down,
            out RaycastHit ground,
            1000,
            Layers.MaskToolRaycast,
            QueryTriggerInteraction.Ignore
        );
        if (!hasGround) {
            // Out over the sea there is no ground to clear, and the camera can stay where it is.
            return position;
        }
        return new Vector3(position.x, Mathf.Max(position.y, ground.point.y + GroundClearance), position.z);
    }

    /// <summary>
    /// Points on the hull a camera must see: the centre and the corners of the drillship's bounds, drawn in towards
    /// the centre so they land on the hull rather than in the empty air around it.
    /// </summary>
    static Vector3[] HullTargets(Bounds bounds) {
        var inner = new Bounds(bounds.center, bounds.size * 0.6f);
        return Corners(inner).Prepend(bounds.center).ToArray();
    }

    /// <summary>
    /// Whether a hull point stands in the open. One inside a tree's crown, the ground or the landing site's structure
    /// cannot be seen from anywhere, and counting it would leave every candidate with a blocked sightline.
    /// </summary>
    bool IsExposed(Vector3 point) {
        if (trees.Any(tree => tree.Contains(point))) {
            return false;
        }
        Collider[] overlaps = Physics.OverlapSphere(
            point,
            0.5f,
            Layers.MaskToolRaycast,
            QueryTriggerInteraction.Ignore
        );
        return overlaps.All(overlap => overlap.transform.IsChildOf(ship.transform));
    }

    static IEnumerable<Vector3> Corners(Bounds bounds) {
        foreach (float x in new[] { -1f, 1f }) {
            foreach (float y in new[] { -1f, 1f }) {
                foreach (float z in new[] { -1f, 1f }) {
                    yield return bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                }
            }
        }
    }
}
