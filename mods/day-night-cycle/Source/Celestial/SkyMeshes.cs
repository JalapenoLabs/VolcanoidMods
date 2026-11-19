// Copyright © 2026 Jalapeno Labs

using System.Collections.Generic;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Celestial;

/// <summary>
/// Builds the sky's meshes on a unit sphere centred on the camera; the sky root scales them out to the far plane.
/// </summary>
internal static class SkyMeshes {
    /// <summary>Generous bounds: the meshes surround the camera, so they must never be frustum-culled.</summary>
    static readonly Bounds SkyBounds = new Bounds(Vector3.zero, Vector3.one * 2.5f);

    /// <summary>A unit quad in the XY plane, facing along Z.</summary>
    public static Mesh Quad(string name) {
        var mesh = new Mesh { name = name };
        mesh.SetVertices(new[] {
            new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
            new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0),
        });
        mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) });
        mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
        mesh.bounds = SkyBounds;
        return mesh;
    }

    /// <summary>
    /// A band round the horizon, from <paramref name="elevationsDegrees"/>[0] up to the last, with each row of
    /// vertices given the matching alpha. Used for the horizon haze.
    /// </summary>
    public static Mesh HorizonBand(string name, float[] elevationsDegrees, float[] alphas, int segments) {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        for (int segment = 0; segment <= segments; segment++) {
            float azimuth = segment * 2 * Mathf.PI / segments;
            for (int row = 0; row < elevationsDegrees.Length; row++) {
                float elevation = elevationsDegrees[row] * Mathf.Deg2Rad;
                vertices.Add(new Vector3(
                    Mathf.Cos(elevation) * Mathf.Cos(azimuth),
                    Mathf.Sin(elevation),
                    Mathf.Cos(elevation) * Mathf.Sin(azimuth)
                ));
                colors.Add(new Color(1, 1, 1, alphas[row]));
                uvs.Add(new Vector2(0.5f, 0.5f));
            }
        }
        return Strip(name, vertices, colors, uvs, segments, elevationsDegrees.Length);
    }

    /// <summary>
    /// A band round a great circle: <paramref name="pole"/> is the circle's axis, and the band spans
    /// <paramref name="halfWidthDegrees"/> either side of it. u runs once round, v across. Used for the Milky Way.
    /// </summary>
    public static Mesh GreatCircleBand(string name, Vector3 pole, float halfWidthDegrees, int segments, int rows) {
        Vector3 axisA = Vector3.Cross(pole, Mathf.Abs(pole.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 axisB = Vector3.Cross(pole, axisA);
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        for (int segment = 0; segment <= segments; segment++) {
            float angle = segment * 2 * Mathf.PI / segments;
            Vector3 around = axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle);
            for (int row = 0; row < rows; row++) {
                float across = (float)row / (rows - 1);
                float latitude = (across * 2 - 1) * halfWidthDegrees * Mathf.Deg2Rad;
                vertices.Add(around * Mathf.Cos(latitude) + pole * Mathf.Sin(latitude));
                colors.Add(Color.white);
                uvs.Add(new Vector2((float)segment / segments, across));
            }
        }
        return Strip(name, vertices, colors, uvs, segments, rows);
    }

    /// <summary>
    /// A mesh of camera-facing quads, one per point: each quad sits on the unit sphere at its direction, square to it.
    /// Because it is square to the centre, rotating the whole mesh about the centre keeps every quad facing the camera.
    /// </summary>
    public static Mesh Points(
        string name,
        IReadOnlyList<Vector3> directions,
        IReadOnlyList<float> radii,
        IReadOnlyList<Color> colors
    ) {
        int count = directions.Count;
        var vertices = new Vector3[count * 4];
        var vertexColors = new Color[count * 4];
        var uvs = new Vector2[count * 4];
        var triangles = new int[count * 6];
        for (int index = 0; index < count; index++) {
            Vector3 direction = directions[index];
            Vector3 reference = Mathf.Abs(direction.y) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 side = Vector3.Cross(direction, reference).normalized;
            Vector3 up = Vector3.Cross(direction, side);
            side *= radii[index];
            up *= radii[index];

            int vertex = index * 4;
            vertices[vertex] = direction - side - up;
            vertices[vertex + 1] = direction + side - up;
            vertices[vertex + 2] = direction - side + up;
            vertices[vertex + 3] = direction + side + up;
            uvs[vertex] = new Vector2(0, 0);
            uvs[vertex + 1] = new Vector2(1, 0);
            uvs[vertex + 2] = new Vector2(0, 1);
            uvs[vertex + 3] = new Vector2(1, 1);
            for (int corner = 0; corner < 4; corner++) {
                vertexColors[vertex + corner] = colors[index];
            }

            int triangle = index * 6;
            triangles[triangle] = vertex;
            triangles[triangle + 1] = vertex + 2;
            triangles[triangle + 2] = vertex + 1;
            triangles[triangle + 3] = vertex + 2;
            triangles[triangle + 4] = vertex + 3;
            triangles[triangle + 5] = vertex + 1;
        }

        var mesh = new Mesh { name = name };
        mesh.vertices = vertices;
        mesh.colors = vertexColors;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.bounds = SkyBounds;
        return mesh;
    }

    /// <summary>Joins columns of <paramref name="rows"/> vertices, laid out column by column, into a strip.</summary>
    static Mesh Strip(
        string name,
        List<Vector3> vertices,
        List<Color> colors,
        List<Vector2> uvs,
        int segments,
        int rows
    ) {
        var triangles = new List<int>();
        for (int segment = 0; segment < segments; segment++) {
            for (int row = 0; row < rows - 1; row++) {
                int current = segment * rows + row;
                int next = (segment + 1) * rows + row;
                triangles.Add(current);
                triangles.Add(current + 1);
                triangles.Add(next);
                triangles.Add(current + 1);
                triangles.Add(next + 1);
                triangles.Add(next);
            }
        }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.bounds = SkyBounds;
        return mesh;
    }
}
