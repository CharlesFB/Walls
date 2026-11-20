using System.Collections.Generic;
using UnityEngine;

namespace MagicTavern.BezierWall.Editor
{
    internal static class BezierWallProjection
    {
        private readonly struct ProjectedTriangle
        {
            public readonly int IndexOffset;
            public readonly float Depth;

            public ProjectedTriangle(int indexOffset, float depth)
            {
                IndexOffset = indexOffset;
                Depth = depth;
            }
        }

        public static void Apply(Mesh mesh, float lean, Color topColor, Color sideColor)
        {
            Vector3[] vertices = mesh.vertices;
            Color32[] faceMarkers = mesh.colors32;
            Color32[] colors = new Color32[vertices.Length];
            Color32 top = topColor;
            Color32 side = sideColor;
            Color32 cap = Color.Lerp(sideColor, new Color(0f, 0f, 0f, sideColor.a), 0.23f);
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 point = vertices[i];
                vertices[i] = new Vector3(point.x + lean * point.y, point.z + point.y,
                    0.05f * (point.z - 0.25f * point.y));

                Color32 marker = faceMarkers[i];
                colors[i] = marker.r > 0 ? top : marker.b > 0 ? cap : side;
            }

            int[] sourceIndices = mesh.triangles;
            var visibleTriangles = new List<ProjectedTriangle>(sourceIndices.Length / 3);
            for (int i = 0; i < sourceIndices.Length; i += 3)
            {
                Vector3 first = vertices[sourceIndices[i]];
                Vector3 second = vertices[sourceIndices[i + 1]];
                Vector3 third = vertices[sourceIndices[i + 2]];
                // Viewed from negative Z, the outward normal must point toward negative Z.
                if (Vector3.Cross(second - first, third - first).z < 0f)
                {
                    visibleTriangles.Add(new ProjectedTriangle(i, (first.z + second.z + third.z) / 3f));
                }
            }

            visibleTriangles.Sort(CompareBackToFront);
            var visibleIndices = new int[visibleTriangles.Count * 3];
            for (int i = 0; i < visibleTriangles.Count; i++)
            {
                int sourceOffset = visibleTriangles[i].IndexOffset;
                int targetOffset = i * 3;
                visibleIndices[targetOffset] = sourceIndices[sourceOffset];
                visibleIndices[targetOffset + 1] = sourceIndices[sourceOffset + 1];
                visibleIndices[targetOffset + 2] = sourceIndices[sourceOffset + 2];
            }

            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.triangles = visibleIndices;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
        }

        private static int CompareBackToFront(ProjectedTriangle first, ProjectedTriangle second)
        {
            int byDepth = second.Depth.CompareTo(first.Depth);
            return byDepth != 0 ? byDepth : first.IndexOffset.CompareTo(second.IndexOffset);
        }
    }
}
