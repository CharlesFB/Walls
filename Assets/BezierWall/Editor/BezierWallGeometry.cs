using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MagicTavern.BezierWall.Editor
{
    [Serializable]
    internal sealed class BezierWallAnchor
    {
        public Vector2 Position;
        public Vector2 Incoming;
        public Vector2 Outgoing;
        public bool Automatic = true;

        internal BezierWallAnchor(Vector2 position)
        {
            Position = position;
        }
    }

    internal static class BezierWallGeometry
    {
        private const float PointEpsilon = 0.00001f;
        private const float MinimumJoinAlignment = 0.25f;
        private const int VerticesPerSection = 8;
        private const int MaxSampleCount = 100000;
        private static readonly Color32 TopMarker = new Color32(255, 0, 0, 255);
        private static readonly Color32 SideMarker = new Color32(0, 255, 0, 255);
        private static readonly Color32 CapMarker = new Color32(0, 0, 255, 255);
        private static readonly Color32 BottomMarker = new Color32(0, 0, 0, 255);

        internal static void UpdateAutomaticTangents(IReadOnlyList<BezierWallAnchor> anchors)
        {
            for (int i = 0; i < anchors.Count; i++)
            {
                BezierWallAnchor anchor = anchors[i];
                if (!anchor.Automatic)
                    continue;

                Vector2 previous = i > 0 ? anchor.Position - anchors[i - 1].Position : Vector2.zero;
                Vector2 next = i + 1 < anchors.Count ? anchors[i + 1].Position - anchor.Position : Vector2.zero;
                Vector2 direction = (previous.normalized + next.normalized).normalized;
                anchor.Incoming = direction == Vector2.zero ? -previous / 3f : -direction * (previous.magnitude / 3f);
                anchor.Outgoing = direction == Vector2.zero ? next / 3f : direction * (next.magnitude / 3f);
            }
        }

        internal static Vector2 Evaluate(BezierWallAnchor first, BezierWallAnchor second, float t)
        {
            float remaining = 1f - t;
            return remaining * remaining * remaining * first.Position
                + 3f * remaining * remaining * t * (first.Position + first.Outgoing)
                + 3f * remaining * t * t * (second.Position + second.Incoming)
                + t * t * t * second.Position;
        }

        internal static bool TryBuild(
            IReadOnlyList<BezierWallAnchor> anchors,
            float height,
            float thickness,
            int subdivisions,
            Vector2 tileSize,
            out Mesh mesh,
            out string error)
        {
            return TryBuild(anchors, height, thickness, subdivisions, tileSize, out mesh, out error, out _);
        }

        internal static bool TryBuild(
            IReadOnlyList<BezierWallAnchor> anchors,
            float height,
            float thickness,
            int subdivisions,
            Vector2 tileSize,
            out Mesh mesh,
            out string error,
            out Vector2[] footprint)
        {
            mesh = null;
            error = null;
            footprint = null;
            if (anchors == null || anchors.Count < 2)
            {
                error = "请至少添加两个描点。";
                return false;
            }

            if (!IsPositive(height) || !IsPositive(thickness) || !IsPositive(tileSize.x) || !IsPositive(tileSize.y)
                || !IsFinite(height / tileSize.y) || !IsFinite(thickness / tileSize.x)
                || !IsFinite(thickness / tileSize.y))
            {
                error = "墙高、墙厚和贴图平铺尺寸必须是有效的正数。";
                return false;
            }

            if (subdivisions < 1 || (long)(anchors.Count - 1) * subdivisions >= MaxSampleCount)
            {
                error = "细分数无效或曲线采样点过多。";
                return false;
            }

            if (!TrySamplePath(anchors, subdivisions, out List<Vector2> path, out error))
                return false;

            int vertexCount = path.Count * VerticesPerSection + 8;
            var vertices = new List<Vector3>(vertexCount);
            var uvs = new List<Vector2>(vertexCount);
            var colors = new List<Color32>(vertexCount);
            var triangles = new List<int>((path.Count - 1) * 24 + 12);
            var outline = new Vector2[path.Count * 2];
            var firstSection = default(WallSection);
            var previousSection = default(WallSection);
            float distance = 0f;
            float verticalUV = height / tileSize.y;
            float topUV = thickness / tileSize.y;

            for (int i = 0; i < path.Count; i++)
            {
                Vector2 center = path[i];
                Vector2 incoming = i > 0 ? center - path[i - 1] : path[1] - center;
                Vector2 outgoing = i + 1 < path.Count ? path[i + 1] - center : incoming;
                Vector2 incomingDirection = incoming.normalized;
                Vector2 side = RightOf((incomingDirection + outgoing.normalized).normalized);
                float alignment = Vector2.Dot(side, RightOf(incomingDirection));
                if (alignment < MinimumJoinAlignment)
                {
                    error = "墙线转折过急，请调整描点或贝塞尔控制柄。";
                    return false;
                }

                Vector2 offset = side * (thickness * 0.5f / alignment);
                var section = new WallSection(center + offset, center - offset);
                if (!IsFinite(section.Positive) || !IsFinite(section.Negative) || section.Positive.Equals(section.Negative))
                {
                    error = "墙厚与描点坐标的比例超出可生成范围。";
                    return false;
                }

                if (i == 0)
                    firstSection = section;
                else
                {
                    if (Vector2.Dot(section.Positive - previousSection.Positive, incomingDirection) <= 0f
                        || Vector2.Dot(section.Negative - previousSection.Negative, incomingDirection) <= 0f)
                    {
                        error = "当前墙厚使弯道内侧折叠，请减小墙厚或放宽弧度。";
                        return false;
                    }

                    distance += incoming.magnitude;
                    int current = i * VerticesPerSection;
                    int previous = current - VerticesPerSection;
                    for (int face = 0; face < VerticesPerSection; face += 2)
                        AddQuad(triangles, previous + face, previous + face + 1, current + face, current + face + 1);
                }

                float lengthUV = distance / tileSize.x;
                if (!IsFinite(lengthUV))
                {
                    error = "墙的路径长度或贴图平铺尺寸超出可生成范围。";
                    return false;
                }

                AddSection(vertices, uvs, colors, section, height, lengthUV, verticalUV, topUV);
                outline[i] = section.Positive;
                outline[outline.Length - i - 1] = section.Negative;
                previousSection = section;
            }

            float capUV = thickness / tileSize.x;
            AddCap(vertices, uvs, colors, triangles, firstSection.Positive, firstSection.Negative, height, capUV, verticalUV);
            AddCap(vertices, uvs, colors, triangles, previousSection.Negative, previousSection.Positive, height, capUV, verticalUV);

            mesh = new Mesh
            {
                name = "Bezier Wall",
                indexFormat = vertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0, false);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            footprint = outline;
            return true;
        }

        private static bool TrySamplePath(
            IReadOnlyList<BezierWallAnchor> anchors, int subdivisions, out List<Vector2> path, out string error)
        {
            path = null;
            error = null;
            foreach (BezierWallAnchor anchor in anchors)
            {
                if (anchor == null || !IsFinite(anchor.Position) || !IsFinite(anchor.Incoming) || !IsFinite(anchor.Outgoing))
                {
                    error = "描点和控制柄的坐标必须是有效数值。";
                    return false;
                }
            }

            path = new List<Vector2>((anchors.Count - 1) * subdivisions + 1) { anchors[0].Position };
            for (int i = 1; i < anchors.Count; i++)
            {
                BezierWallAnchor first = anchors[i - 1];
                BezierWallAnchor second = anchors[i];
                for (int step = 1; step <= subdivisions; step++)
                {
                    Vector2 point = step == subdivisions ? second.Position : Evaluate(first, second, (float)step / subdivisions);
                    Vector2 delta = point - path[path.Count - 1];
                    float squaredDistance = delta.sqrMagnitude;
                    if (!IsFinite(squaredDistance))
                    {
                        error = "描点和控制柄的坐标超出可生成范围。";
                        return false;
                    }

                    if (squaredDistance > PointEpsilon * PointEpsilon)
                        path.Add(point);
                }
            }

            if (path.Count >= 2)
                return true;

            error = "描点需要形成一条有长度的路径。";
            return false;
        }

        private static void AddSection(
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colors,
            WallSection section, float height, float lengthUV, float verticalUV, float topUV)
        {
            // Separate face strips keep side normals smooth along the wall and sharp at its edges.
            AddVertex(vertices, uvs, colors, section.Positive, 0f, lengthUV, 0f, SideMarker);
            AddVertex(vertices, uvs, colors, section.Positive, height, lengthUV, verticalUV, SideMarker);
            AddVertex(vertices, uvs, colors, section.Negative, height, lengthUV, verticalUV, SideMarker);
            AddVertex(vertices, uvs, colors, section.Negative, 0f, lengthUV, 0f, SideMarker);
            AddVertex(vertices, uvs, colors, section.Positive, height, lengthUV, 0f, TopMarker);
            AddVertex(vertices, uvs, colors, section.Negative, height, lengthUV, topUV, TopMarker);
            AddVertex(vertices, uvs, colors, section.Negative, 0f, lengthUV, topUV, BottomMarker);
            AddVertex(vertices, uvs, colors, section.Positive, 0f, lengthUV, 0f, BottomMarker);
        }

        private static void AddCap(
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colors, List<int> triangles,
            Vector2 first, Vector2 second, float height, float horizontalUV, float verticalUV)
        {
            int start = vertices.Count;
            AddVertex(vertices, uvs, colors, first, 0f, 0f, 0f, CapMarker);
            AddVertex(vertices, uvs, colors, second, 0f, horizontalUV, 0f, CapMarker);
            AddVertex(vertices, uvs, colors, first, height, 0f, verticalUV, CapMarker);
            AddVertex(vertices, uvs, colors, second, height, horizontalUV, verticalUV, CapMarker);
            AddQuad(triangles, start, start + 1, start + 2, start + 3);
        }

        private static void AddVertex(
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colors,
            Vector2 position, float height, float u, float v, Color32 marker)
        {
            vertices.Add(new Vector3(position.x, height, position.y));
            uvs.Add(new Vector2(u, v));
            colors.Add(marker);
        }

        private static void AddQuad(List<int> triangles, int first, int second, int third, int fourth)
        {
            triangles.Add(first);
            triangles.Add(second);
            triangles.Add(third);
            triangles.Add(second);
            triangles.Add(fourth);
            triangles.Add(third);
        }

        private static Vector2 RightOf(Vector2 direction)
        {
            return new Vector2(direction.y, -direction.x);
        }

        private static bool IsPositive(float value)
        {
            return value > 0f && IsFinite(value);
        }

        private static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private readonly struct WallSection
        {
            internal readonly Vector2 Positive;
            internal readonly Vector2 Negative;

            internal WallSection(Vector2 positive, Vector2 negative)
            {
                Positive = positive;
                Negative = negative;
            }
        }
    }
}
