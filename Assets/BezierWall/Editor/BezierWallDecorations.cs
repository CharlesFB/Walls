using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MagicTavern.BezierWall.Editor
{
    internal readonly struct BezierWallDecorationPlacement
    {
        internal readonly Vector2 Position;
        internal readonly float Angle;

        internal BezierWallDecorationPlacement(Vector2 position, float angle)
        {
            Position = position;
            Angle = angle;
        }
    }

    internal static class BezierWallDecorations
    {
        private const int MaxSlots = 500;
        private const double DistanceEpsilon = 0.00001;

        internal static bool IsPrefabAsset(GameObject prefab)
        {
            return prefab == null || (AssetDatabase.IsMainAsset(prefab) && PrefabUtility.IsPartOfPrefabAsset(prefab));
        }

        internal static bool TryPlan(BezierWallSettings settings,
            out IReadOnlyList<BezierWallDecorationPlacement> placements, out string error)
        {
            placements = Array.Empty<BezierWallDecorationPlacement>();
            error = null;
            if (settings.DecorationPrefab == null)
                return true;

            if (!IsPrefabAsset(settings.DecorationPrefab))
            {
                error = "请从 Project 面板选择完整的 Prefab 资产。";
                return false;
            }

            float spacing = settings.DecorationSpacing;
            if (!IsFinite(spacing) || spacing <= 0f)
            {
                error = "摆件间距必须是有效的正数。";
                return false;
            }

            float clearance = settings.DecorationClearance;
            if (!IsFinite(clearance) || clearance < 0f)
            {
                error = "摆件占地半径必须是有效的非负数。";
                return false;
            }

            if (!BezierWallGeometry.TrySamplePath(settings, out List<Vector2> path, out error)
                || !BezierWallGeometry.TrySplitOpening(settings, path, out List<Vector2> before, out List<Vector2> after,
                    out _, out error))
                return false;

            var distances = new double[path.Count];
            for (int i = 1; i < path.Count; i++)
                distances[i] = distances[i - 1] + Vector2.Distance(path[i - 1], path[i]);

            double slotCount = Math.Ceiling(distances[distances.Length - 1] / spacing - 0.5);
            if (slotCount > MaxSlots)
            {
                error = $"墙线的摆件候选位置超过 {MaxSlots} 个。请增大摆件间距。";
                return false;
            }

            if (slotCount <= 0)
                return true;

            List<Vector2> opening = null;
            double openingStart = 0;
            double openingEnd = 0;
            if (after != null)
                opening = GetOpeningPath(path, distances, before, after, out openingStart, out openingEnd);

            int candidateCount = (int)slotCount;
            var result = new List<BezierWallDecorationPlacement>(candidateCount);
            int upper = 1;
            for (int slot = 0; slot < candidateCount; slot++)
            {
                double distance = (slot + 0.5) * spacing;
                if (opening != null && distance >= openingStart - DistanceEpsilon && distance <= openingEnd + DistanceEpsilon)
                    continue;

                while (upper < distances.Length - 1 && distances[upper] < distance)
                    upper++;

                double segmentStart = distances[upper - 1];
                float t = (float)((distance - segmentStart) / (distances[upper] - segmentStart));
                Vector2 first = path[upper - 1];
                Vector2 last = path[upper];
                Vector2 position = Vector2.LerpUnclamped(first, last, t);
                if (opening != null && TouchesOpening(opening, position, clearance))
                    continue;

                Vector2 tangent = last - first;
                float angle = Mathf.Atan2(tangent.y, tangent.x) * Mathf.Rad2Deg;
                result.Add(new BezierWallDecorationPlacement(position, angle));
            }

            placements = result;
            return true;
        }

        private static List<Vector2> GetOpeningPath(IReadOnlyList<Vector2> path, double[] distances,
            List<Vector2> before, List<Vector2> after, out double startDistance, out double endDistance)
        {
            // A solid run contains all of its original vertices and an additional cut point only when interpolated.
            Vector2 start = before[before.Count - 1];
            Vector2 end = after[0];
            int startIndex = before.Count - 1;
            bool startOnSample = path[startIndex].Equals(start);
            int startLower = startOnSample ? startIndex : startIndex - 1;
            startDistance = distances[startLower] + Vector2.Distance(path[startLower], start);

            int endLower = path.Count - after.Count;
            bool endOnSample = path[endLower].Equals(end);
            endDistance = distances[endLower] + Vector2.Distance(path[endLower], end);

            int firstInterior = startOnSample ? startIndex + 1 : startIndex;
            int lastExclusive = endOnSample ? endLower : endLower + 1;
            var opening = new List<Vector2>(Math.Max(0, lastExclusive - firstInterior) + 2) { start };
            for (int i = firstInterior; i < lastExclusive; i++)
                opening.Add(path[i]);
            opening.Add(end);
            return opening;
        }

        private static bool TouchesOpening(List<Vector2> opening, Vector2 position, float radius)
        {
            double clearance = radius + DistanceEpsilon;
            double radiusSquared = clearance * clearance;
            for (int i = 1; i < opening.Count; i++)
            {
                Vector2 first = opening[i - 1];
                Vector2 last = opening[i];
                if (position.x < Math.Min(first.x, last.x) - clearance
                    || position.x > Math.Max(first.x, last.x) + clearance
                    || position.y < Math.Min(first.y, last.y) - clearance
                    || position.y > Math.Max(first.y, last.y) + clearance)
                    continue;

                double dx = (double)last.x - first.x;
                double dy = (double)last.y - first.y;
                double x = (double)position.x - first.x;
                double y = (double)position.y - first.y;
                double t = Math.Min(1, Math.Max(0, (x * dx + y * dy) / (dx * dx + dy * dy)));
                double gapX = x - t * dx;
                double gapY = y - t * dy;
                if (gapX * gapX + gapY * gapY <= radiusSquared)
                    return true;
            }

            return false;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
