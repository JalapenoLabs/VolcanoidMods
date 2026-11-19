// Copyright © 2026 Jalapeno Labs

using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.Shared.Showcase;

/// <summary>
/// Developer captures only: the island's trees, for finding open ground and clear views.
///
/// The trees are Unity terrain trees (the game's TerrainBuilder places them as tree instances), drawn by the terrain
/// with no GameObjects and no colliders, so raycasts pass straight through them. Each is modelled here as an upright
/// cylinder from its base to its top, as wide as its crown reaches in any direction.
/// </summary>
internal static class TerrainTrees {
    internal readonly struct Tree {
        readonly Vector3 treeBase;
        readonly float top;
        readonly float radius;

        public Tree(Vector3 treeBase, float height, float radius) {
            this.treeBase = treeBase;
            top = treeBase.y + height;
            this.radius = radius;
        }

        public bool Contains(Vector3 point) {
            var offset = new Vector2(point.x - treeBase.x, point.z - treeBase.z);
            return offset.sqrMagnitude < radius * radius && point.y > treeBase.y && point.y < top;
        }

        /// <summary>
        /// Whether the segment passes through the tree: where its overhead projection comes within the radius of the
        /// trunk, its height (linear along the segment) must overlap the tree's.
        /// </summary>
        public bool Crosses(Vector3 from, Vector3 to) {
            var offset = new Vector2(from.x - treeBase.x, from.z - treeBase.z);
            var direction = new Vector2(to.x - from.x, to.z - from.z);
            float a = Vector2.Dot(direction, direction);
            float b = 2 * Vector2.Dot(offset, direction);
            float c = offset.sqrMagnitude - radius * radius;
            float discriminant = b * b - 4 * a * c;
            if (a < 1e-6f || discriminant < 0) {
                // Vertical segments never occur here (cameras stand away from what they look at), and a negative
                // discriminant means the segment's line misses the tree.
                return false;
            }
            float root = Mathf.Sqrt(discriminant);
            float enter = Mathf.Max(0, (-b - root) / (2 * a));
            float exit = Mathf.Min(1, (-b + root) / (2 * a));
            if (enter > exit) {
                return false;
            }
            float enterHeight = Mathf.Lerp(from.y, to.y, enter);
            float exitHeight = Mathf.Lerp(from.y, to.y, exit);
            return Mathf.Min(enterHeight, exitHeight) < top && Mathf.Max(enterHeight, exitHeight) > treeBase.y;
        }
    }

    /// <summary>A broad tree, for a tree model whose meshes cannot be read.</summary>
    static readonly Bounds FallbackModelShape = new Bounds(new Vector3(0, 8, 0), new Vector3(10, 16, 10));

    /// <summary>Each tree model's measured extent, measured once and logged so the cylinders can be checked.</summary>
    static readonly Dictionary<GameObject, Bounds> modelShapes = new Dictionary<GameObject, Bounds>();

    /// <summary>Every terrain tree with its trunk within <paramref name="reach"/> of a point, overhead.</summary>
    public static List<Tree> Around(Vector3 centre, float reach) {
        var trees = new List<Tree>();
        foreach (Terrain terrain in Terrain.activeTerrains) {
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.GetPosition();
            TreePrototype[] prototypes = data.treePrototypes;
            foreach (TreeInstance instance in data.treeInstances) {
                Vector3 position = origin + Vector3.Scale(instance.position, data.size);
                var horizontal = new Vector2(position.x - centre.x, position.z - centre.z);
                if (horizontal.sqrMagnitude > reach * reach) {
                    continue;
                }
                Bounds shape = MeasureModel(prototypes[instance.prototypeIndex].prefab);
                // The widest reach from the trunk in any direction, because instances are rotated about it.
                float crown = Mathf.Max(
                    Mathf.Abs(shape.min.x),
                    Mathf.Abs(shape.max.x),
                    Mathf.Abs(shape.min.z),
                    Mathf.Abs(shape.max.z)
                );
                trees.Add(new Tree(position, shape.max.y * instance.heightScale, crown * instance.widthScale));
            }
        }
        return trees;
    }

    /// <summary>A tree model's extent around its trunk, from its meshes: a prefab has no renderer bounds.</summary>
    static Bounds MeasureModel(GameObject prefab) {
        if (prefab != null && modelShapes.TryGetValue(prefab, out Bounds known)) {
            return known;
        }

        Bounds? extent = null;
        if (prefab != null) {
            // The terrain draws a tree at its prefab's own scale (the island's are 2), so only the prefab's position
            // and rotation are taken out; its scale stays in.
            Matrix4x4 toModel = Matrix4x4.Scale(prefab.transform.localScale) * prefab.transform.worldToLocalMatrix;
            foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(includeInactive: true)) {
                if (filter.sharedMesh == null) {
                    continue;
                }
                Matrix4x4 meshToModel = toModel * filter.transform.localToWorldMatrix;
                foreach (Vector3 corner in Corners(filter.sharedMesh.bounds)) {
                    Vector3 point = meshToModel.MultiplyPoint3x4(corner);
                    if (extent.HasValue) {
                        Bounds grown = extent.Value;
                        grown.Encapsulate(point);
                        extent = grown;
                    } else {
                        extent = new Bounds(point, Vector3.zero);
                    }
                }
            }
        }

        string name = prefab != null ? prefab.name : "missing";
        if (!extent.HasValue) {
            Debug.LogWarning(
                $"{ModIdentity.LogPrefix}Tree model '{name}' has no readable meshes; treating it as a broad tree"
            );
            extent = FallbackModelShape;
        } else {
            Debug.Log(
                $"{ModIdentity.LogPrefix}Tree model '{name}': extent {extent.Value.min} to {extent.Value.max}"
            );
        }
        if (prefab != null) {
            modelShapes[prefab] = extent.Value;
        }
        return extent.Value;
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
