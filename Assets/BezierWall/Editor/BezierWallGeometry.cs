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
        private const float TurnEpsilon = 0.0001f;
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

        internal static bool TryBuild(BezierWallSettings settings, out Mesh mesh, out string error, out Vector2[][] footprints)
        {
            mesh = null;
            error = null;
            footprints = null;
            float height = settings.Height;
            float thickness = settings.Thickness;
            Vector2 tileSize = settings.TileSize;

            if (!IsPositive(height) || !IsPositive(thickness) || !IsPositive(tileSize.x) || !IsPositive(tileSize.y)
                || !IsFinite(height / tileSize.y) || !IsFinite(thickness / tileSize.x)
                || !IsFinite(thickness / tileSize.y))
            {
                error = "墙高、墙厚和贴图平铺尺寸必须是有效的正数。";
                return false;
            }

            if (!TrySamplePath(settings, out List<Vector2> path, out error)
                || !TrySplitOpening(settings, path, out List<Vector2> before, out List<Vector2> after,
                    out float afterDistance, out error))
                return false;

            int runCount = after == null ? 1 : 2;
            int sectionCount = before.Count + (after?.Count ?? 0);
            int vertexCount = sectionCount * VerticesPerSection + runCount * 8;
            var vertices = new List<Vector3>(vertexCount);
            var uvs = new List<Vector2>(vertexCount);
            var colors = new List<Color32>(vertexCount);
            var triangles = new List<int>((sectionCount - runCount) * 24 + runCount * 12);
            var outlines = new Vector2[runCount][];
            if (!TryAppendRun(before, 0f, settings, vertices, uvs, colors, triangles, out outlines[0], out error))
                return false;

            if (after != null
                && !TryAppendRun(after, afterDistance, settings, vertices, uvs, colors, triangles, out outlines[1], out error))
                return false;

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
            footprints = outlines;
            return true;
        }

        private static bool TryAppendRun(List<Vector2> path, float distance, BezierWallSettings settings,
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colors, List<int> triangles,
            out Vector2[] outline, out string error)
        {
            error = null;
            float height = settings.Height;
            float thickness = settings.Thickness;
            Vector2 tileSize = settings.TileSize;
            int firstVertex = vertices.Count;
            outline = new Vector2[path.Count * 2];
            var firstSection = default(WallSection);
            var previousSection = default(WallSection);
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
                    error = "墙线转折过急，请调整描点或转角参数。";
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
                    int current = firstVertex + i * VerticesPerSection;
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
            return true;
        }

        internal static bool TrySplitOpening(BezierWallSettings settings, List<Vector2> path,
            out List<Vector2> before, out List<Vector2> after, out float afterDistance, out string error)
        {
            const string bothSidesRequired = "门洞两侧都要保留墙段。请缩小宽度或把门洞移向墙线内部。";
            before = null;
            after = null;
            afterDistance = 0f;
            error = null;
            if (!settings.OpeningEnabled)
            {
                before = path;
                return true;
            }

            if (path == null || path.Count < 2)
            {
                error = "墙线需要至少两个采样点才能设置门洞。";
                return false;
            }

            if (!IsFinite(settings.OpeningPosition) || settings.OpeningPosition < 0f || settings.OpeningPosition > 1f)
            {
                error = "门洞位置必须在墙线长度的 0% 到 100% 之间。";
                return false;
            }

            if (!IsFinite(settings.OpeningWidth) || settings.OpeningWidth <= PointEpsilon)
            {
                error = "门洞宽度必须是有效的正数，并大于可分辨的最小宽度。";
                return false;
            }

            var distances = new double[path.Count];
            for (int i = 1; i < path.Count; i++)
            {
                float segmentLength = Vector2.Distance(path[i - 1], path[i]);
                if (!IsPositive(segmentLength))
                {
                    error = "墙线上存在长度无效的线段，无法计算门洞位置。";
                    return false;
                }

                distances[i] = distances[i - 1] + segmentLength;
            }

            double length = distances[distances.Length - 1];
            double center = length * settings.OpeningPosition;
            double halfWidth = settings.OpeningWidth * 0.5;
            double start = center - halfWidth;
            double end = center + halfWidth;
            if (start <= PointEpsilon || length - end <= PointEpsilon)
            {
                error = bothSidesRequired;
                return false;
            }

            OpeningCut first = FindOpeningCut(path, distances, start);
            OpeningCut last = FindOpeningCut(path, distances, end);
            if (first.Distance <= PointEpsilon || length - last.Distance <= PointEpsilon)
            {
                error = bothSidesRequired;
                return false;
            }

            if (last.Distance - first.Distance <= PointEpsilon
                || (last.Position - first.Position).sqrMagnitude <= PointEpsilon * PointEpsilon)
            {
                error = "门洞的两个切点过近。请增大宽度或调整位置。";
                return false;
            }

            before = path.GetRange(0, first.LowerIndex + 1);
            if (first.LowerIndex != first.UpperIndex)
                before.Add(first.Position);

            after = new List<Vector2>(path.Count - last.UpperIndex + 1);
            if (last.LowerIndex != last.UpperIndex)
                after.Add(last.Position);
            for (int i = last.UpperIndex; i < path.Count; i++)
                after.Add(path[i]);

            afterDistance = (float)last.Distance;
            return true;
        }

        private static OpeningCut FindOpeningCut(IReadOnlyList<Vector2> path, double[] distances, double distance)
        {
            int index = Array.BinarySearch(distances, distance);
            if (index >= 0)
                return new OpeningCut(path[index], distances[index], index, index);

            int upper = ~index;
            int lower = upper - 1;
            double fromLower = distance - distances[lower];
            double toUpper = distances[upper] - distance;
            Vector2 point = Vector2.LerpUnclamped(path[lower], path[upper], (float)(fromLower / (fromLower + toUpper)));
            float minSquaredDistance = PointEpsilon * PointEpsilon;
            bool nearLower = fromLower <= PointEpsilon || (point - path[lower]).sqrMagnitude <= minSquaredDistance;
            bool nearUpper = toUpper <= PointEpsilon || (point - path[upper]).sqrMagnitude <= minSquaredDistance;
            if (nearLower && (!nearUpper || fromLower <= toUpper))
                return new OpeningCut(path[lower], distances[lower], lower, lower);
            if (nearUpper)
                return new OpeningCut(path[upper], distances[upper], upper, upper);

            return new OpeningCut(point, distance, lower, upper);
        }

        internal static bool TrySamplePath(BezierWallSettings settings, out List<Vector2> path, out string error)
        {
            path = null;
            error = null;
            IReadOnlyList<BezierWallAnchor> anchors = settings.Anchors;
            if (anchors == null || anchors.Count < 2)
            {
                error = "请至少添加两个描点。";
                return false;
            }

            if (settings.Subdivisions < 1 || settings.Subdivisions >= MaxSampleCount || anchors.Count > MaxSampleCount)
            {
                error = "细分数无效或曲线采样点过多。";
                return false;
            }

            foreach (BezierWallAnchor anchor in anchors)
            {
                if (anchor == null || !IsFinite(anchor.Position))
                {
                    error = "描点坐标必须是有效数值。";
                    return false;
                }
            }

            switch (settings.PathMode)
            {
                case WallPathMode.Bezier:
                    return TrySampleBezier(anchors, settings.Subdivisions, out path, out error);
                case WallPathMode.RoundedPolyline:
                    return TrySampleRoundedPolyline(anchors, settings.CornerRadius, settings.Thickness,
                        settings.Subdivisions, out path, out error);
                default:
                    error = "无法识别墙线路径模式。";
                    return false;
            }
        }

        private static bool TrySampleBezier(
            IReadOnlyList<BezierWallAnchor> anchors, int subdivisions, out List<Vector2> path, out string error)
        {
            path = null;
            error = null;
            if ((long)(anchors.Count - 1) * subdivisions >= MaxSampleCount)
            {
                error = "曲线采样点过多，请减少细分或描点。";
                return false;
            }

            UpdateAutomaticTangents(anchors);
            foreach (BezierWallAnchor anchor in anchors)
            {
                if (!IsFinite(anchor.Incoming) || !IsFinite(anchor.Outgoing))
                {
                    error = "贝塞尔控制柄的坐标必须是有效数值。";
                    return false;
                }
            }

            var samples = new List<Vector2>((anchors.Count - 1) * subdivisions + 1) { anchors[0].Position };
            for (int i = 1; i < anchors.Count; i++)
            {
                BezierWallAnchor first = anchors[i - 1];
                BezierWallAnchor second = anchors[i];
                for (int step = 1; step <= subdivisions; step++)
                {
                    Vector2 point = step == subdivisions ? second.Position : Evaluate(first, second, (float)step / subdivisions);
                    if (!TryAppendSample(samples, point, out error))
                        return false;
                }
            }

            if (samples.Count < 2)
            {
                error = "描点需要形成一条有长度的路径。";
                return false;
            }

            path = samples;
            return true;
        }

        private static bool TrySampleRoundedPolyline(IReadOnlyList<BezierWallAnchor> anchors, float radius,
            float thickness, int subdivisions, out List<Vector2> path, out string error)
        {
            path = null;
            error = null;
            if (!IsFinite(radius) || radius < 0f)
            {
                error = "圆角半径必须是有效的非负数。";
                return false;
            }

            var directions = new Vector2[anchors.Count - 1];
            var lengths = new float[directions.Length];
            for (int i = 0; i < directions.Length; i++)
            {
                Vector2 edge = anchors[i + 1].Position - anchors[i].Position;
                float length = edge.magnitude;
                if (!IsFinite(length) || length <= PointEpsilon)
                {
                    error = $"第 {i + 1} 段直线长度无效。请拉开相邻描点。";
                    return false;
                }

                lengths[i] = length;
                directions[i] = edge / length;
            }

            var corners = new (float Turn, float SetbackFactor, int Steps)[anchors.Count];
            int sampleCount = anchors.Count;
            float minimumRadius = 0f;
            for (int i = 1; i < anchors.Count - 1; i++)
            {
                Vector2 incoming = directions[i - 1];
                Vector2 outgoing = directions[i];
                float cross = incoming.x * outgoing.y - incoming.y * outgoing.x;
                float turn = Mathf.Atan2(cross, Vector2.Dot(incoming, outgoing));
                float absTurn = Mathf.Abs(turn);
                if (absTurn >= Mathf.PI - TurnEpsilon)
                {
                    error = $"第 {i + 1} 个描点处的路径掉头。请调整描点位置。";
                    return false;
                }

                if (absTurn <= TurnEpsilon || radius == 0f)
                    continue;

                int steps = Mathf.Max(2, Mathf.CeilToInt(absTurn * subdivisions / (Mathf.PI * 0.5f)));
                sampleCount += steps;
                if (sampleCount > MaxSampleCount)
                {
                    error = "曲线采样点过多，请减少细分或描点。";
                    return false;
                }

                corners[i] = (turn, Mathf.Tan(absTurn * 0.5f), steps);
                // The mesh offsets each chord by half the wall thickness on the inside of the arc.
                float minimumForCorner = (thickness * 0.5f + PointEpsilon) / Mathf.Cos(absTurn / (2f * steps));
                minimumRadius = Mathf.Max(minimumRadius, minimumForCorner);
            }

            float maximumRadius = float.PositiveInfinity;
            for (int i = 0; i < lengths.Length; i++)
            {
                // Trim both corners of a shared straight segment using one radius.
                float factor = corners[i].SetbackFactor + corners[i + 1].SetbackFactor;
                if (factor > 0f)
                    maximumRadius = Mathf.Min(maximumRadius, (lengths[i] - PointEpsilon) / factor);
            }

            if (radius > maximumRadius)
            {
                error = $"当前描点允许的圆角半径上限约为 {maximumRadius:G5}。请减小半径或加长直线段。";
                return false;
            }

            if (minimumRadius > 0f && radius <= minimumRadius)
            {
                error = $"圆角半径需要大于 {minimumRadius:G5} 才能容纳当前墙厚。请放大圆角或减小墙厚。";
                return false;
            }

            var samples = new List<Vector2>(sampleCount) { anchors[0].Position };
            for (int i = 1; i < anchors.Count - 1; i++)
            {
                var corner = corners[i];
                Vector2 position = anchors[i].Position;
                if (corner.Steps == 0)
                {
                    if (!TryAppendSample(samples, position, out error))
                        return false;
                    continue;
                }

                float setback = radius * corner.SetbackFactor;
                Vector2 incoming = directions[i - 1];
                Vector2 entry = position - incoming * setback;
                Vector2 exit = position + directions[i] * setback;
                Vector2 radial = RightOf(incoming) * (Mathf.Sign(corner.Turn) * radius);
                if (!TryAppendArc(samples, entry, exit, radial, corner.Turn, corner.Steps, out error))
                    return false;
            }

            if (!TryAppendSample(samples, anchors[anchors.Count - 1].Position, out error))
                return false;

            path = samples;
            return true;
        }

        private static bool TryAppendArc(List<Vector2> path, Vector2 entry, Vector2 exit, Vector2 radial,
            float turn, int steps, out string error)
        {
            if (!TryAppendSample(path, entry, out error))
                return false;

            for (int i = 1; i < steps; i++)
            {
                float angle = turn * i / steps;
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);
                Vector2 rotated = new Vector2(radial.x * cos - radial.y * sin, radial.x * sin + radial.y * cos);
                if (!TryAppendSample(path, entry + (rotated - radial), out error))
                    return false;
            }

            return TryAppendSample(path, exit, out error);
        }

        private static bool TryAppendSample(List<Vector2> path, Vector2 point, out string error)
        {
            error = null;
            float squaredDistance = (point - path[path.Count - 1]).sqrMagnitude;
            if (!IsFinite(squaredDistance))
            {
                error = "路径坐标超出可生成范围。";
                return false;
            }

            if (squaredDistance > PointEpsilon * PointEpsilon)
                path.Add(point);

            return true;
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

        private readonly struct OpeningCut
        {
            internal readonly Vector2 Position;
            internal readonly double Distance;
            internal readonly int LowerIndex;
            internal readonly int UpperIndex;

            internal OpeningCut(Vector2 position, double distance, int lowerIndex, int upperIndex)
            {
                Position = position;
                Distance = distance;
                LowerIndex = lowerIndex;
                UpperIndex = upperIndex;
            }
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
