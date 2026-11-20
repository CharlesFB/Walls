using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    public sealed class BezierWallLayout : ScriptableObject
    {
        [SerializeField] internal BezierWallSettings Settings = new BezierWallSettings();
    }

    internal enum WallPathMode
    {
        [InspectorName("贝塞尔曲线")] Bezier = 0,
        [InspectorName("圆角直线")] RoundedPolyline = 1
    }

    [Serializable]
    internal sealed class BezierWallSettings
    {
        public Object Reference = null;
        public Vector2 ReferenceCenter = Vector2.zero;
        public float ReferenceWidth = 10f;
        public float ReferenceOpacity = 0.7f;

        public WallPathMode PathMode = WallPathMode.Bezier;
        public float CornerRadius = 0.3f;
        public bool OpeningEnabled = false;
        public float OpeningPosition = 0.5f;
        public float OpeningWidth = 1f;

        public GameObject DecorationPrefab = null;
        public float DecorationSpacing = 1.25f;
        public float DecorationClearance = 0.3f;

        public float Height = 0.55f;
        public float Thickness = 0.14f;
        public float Lean = 0.12f;
        public int Subdivisions = 18;
        public Color TopColor = new Color32(255, 218, 147, 255);
        public Color SideColor = new Color32(214, 139, 92, 255);
        public Object SurfaceTexture = null;
        public Material Material = null;
        public Vector2 TileSize = Vector2.one;

        public Vector3 Origin = Vector3.zero;
        public int SortingLayer = 0;
        public int SortingOrder = 0;
        public bool AddCollider = false;
        public List<BezierWallAnchor> Anchors = new List<BezierWallAnchor>();

        internal BezierWallSettings Copy()
        {
            var result = (BezierWallSettings)MemberwiseClone();
            result.Anchors = new List<BezierWallAnchor>(Anchors.Count);
            foreach (BezierWallAnchor anchor in Anchors)
            {
                result.Anchors.Add(new BezierWallAnchor(anchor.Position)
                {
                    Incoming = anchor.Incoming,
                    Outgoing = anchor.Outgoing,
                    Automatic = anchor.Automatic
                });
            }

            return result;
        }
    }
}
