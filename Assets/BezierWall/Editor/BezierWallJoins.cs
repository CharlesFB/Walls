using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    internal static class BezierWallJoins
    {
        private const double PlaneTolerance = 0.0001;

        internal static bool TryJoin(BezierWallSettings current, BezierWallSettings source, float snapDistance,
            bool? currentAtStart, bool? sourceAtStart, out BezierWallSettings joined, out string error)
        {
            joined = null;
            error = null;
            if (current == null || source == null || ReferenceEquals(current, source))
            {
                error = "请选择另一份开放的墙体草稿。";
                return false;
            }

            if (current.Closed || source.Closed)
            {
                error = "端点拼接需要两条开放墙线。";
                return false;
            }

            if (!HasValidAnchors(current.Anchors) || !HasValidAnchors(source.Anchors))
            {
                error = "两条墙线都需要至少两个有效描点。";
                return false;
            }

            if (!IsFinite(snapDistance) || snapDistance < 0f)
            {
                error = "端点吸附距离必须是有效的非负数。";
                return false;
            }

            Vector3 currentOrigin = current.Origin;
            Vector3 sourceOrigin = source.Origin;
            if (!IsFinite(currentOrigin) || !IsFinite(sourceOrigin)
                || Math.Abs((double)sourceOrigin.z - currentOrigin.z) > PlaneTolerance)
            {
                error = "两条墙线的原点必须有效，并位于同一个 XY 平面。";
                return false;
            }

            double shiftX = (double)sourceOrigin.x - currentOrigin.x;
            double shiftY = (double)sourceOrigin.y - currentOrigin.y;
            double nearestDistanceSquared = double.PositiveInfinity;
            bool attachAtStart = false;
            bool sourceJoinAtStart = false;
            for (int currentEnd = 0; currentEnd < 2; currentEnd++)
            {
                bool currentStarts = currentEnd == 0;
                if (currentAtStart.HasValue && currentAtStart.Value != currentStarts)
                    continue;

                int currentIndex = currentStarts ? 0 : current.Anchors.Count - 1;
                Vector2 fixedPoint = current.Anchors[currentIndex].Position;
                for (int sourceEnd = 0; sourceEnd < 2; sourceEnd++)
                {
                    bool sourceStarts = sourceEnd == 0;
                    if (sourceAtStart.HasValue && sourceAtStart.Value != sourceStarts)
                        continue;

                    int sourceIndex = sourceStarts ? 0 : source.Anchors.Count - 1;
                    Vector2 movingPoint = source.Anchors[sourceIndex].Position;
                    double dx = movingPoint.x + shiftX - fixedPoint.x;
                    double dy = movingPoint.y + shiftY - fixedPoint.y;
                    double distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared >= nearestDistanceSquared)
                        continue;

                    nearestDistanceSquared = distanceSquared;
                    attachAtStart = currentStarts;
                    sourceJoinAtStart = sourceStarts;
                }
            }

            if (nearestDistanceSquared > (double)snapDistance * snapDistance)
            {
                error = $"可拼接端点相距 {Math.Sqrt(nearestDistanceSquared):G5}，超出吸附距离 {snapDistance:G5}。";
                return false;
            }

            BezierWallSettings result = current.Copy();
            int sharedIndex = attachAtStart ? 0 : result.Anchors.Count - 1;
            result.Anchors[sharedIndex].Automatic = true;

            // Keep the current wall's traversal direction. Reverse only the imported wall if needed.
            bool reverseSource = attachAtStart == sourceJoinAtStart;
            int skipIndex = sourceJoinAtStart ? 0 : source.Anchors.Count - 1;
            var imported = new List<BezierWallAnchor>(source.Anchors.Count - 1);
            for (int i = 0; i < source.Anchors.Count; i++)
            {
                int sourceIndex = reverseSource ? source.Anchors.Count - i - 1 : i;
                if (sourceIndex == skipIndex)
                    continue;

                BezierWallAnchor anchor = source.Anchors[sourceIndex];
                imported.Add(new BezierWallAnchor(new Vector2(
                    (float)(anchor.Position.x + shiftX), (float)(anchor.Position.y + shiftY)))
                {
                    Automatic = anchor.Automatic,
                    Incoming = reverseSource ? anchor.Outgoing : anchor.Incoming,
                    Outgoing = reverseSource ? anchor.Incoming : anchor.Outgoing
                });
            }

            if (attachAtStart)
                result.Anchors.InsertRange(0, imported);
            else
                result.Anchors.AddRange(imported);

            if (!BezierWallAssets.TryBuild(result, out Mesh mesh, out _, out error))
            {
                error = $"拼接后的墙线无法生成。{error}";
                return false;
            }

            Object.DestroyImmediate(mesh);
            joined = result;
            return true;
        }

        private static bool HasValidAnchors(IReadOnlyList<BezierWallAnchor> anchors)
        {
            if (anchors == null || anchors.Count < 2)
                return false;

            foreach (BezierWallAnchor anchor in anchors)
            {
                if (anchor == null || !IsFinite(anchor.Position.x) || !IsFinite(anchor.Position.y))
                    return false;
            }

            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
