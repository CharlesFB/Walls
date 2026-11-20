using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    public sealed class BezierWallStylePreset : ScriptableObject
    {
        [SerializeField] internal BezierWallStyle Style = BezierWallStyle.Capture(new BezierWallSettings());
    }

    [Serializable]
    internal sealed class BezierWallStyle
    {
        public float Height;
        public float Thickness;
        public float Lean;
        public Color TopColor;
        public Color SideColor;
        public Object SurfaceTexture;
        public bool UseSeparateTopTexture;
        public Object TopTexture;
        public Vector2 TileSize;
        public Material Material;
        public GameObject DecorationPrefab;
        public float DecorationSpacing;
        public float DecorationClearance;

        internal static BezierWallStyle Capture(BezierWallSettings settings)
        {
            return new BezierWallStyle
            {
                Height = settings.Height,
                Thickness = settings.Thickness,
                Lean = settings.Lean,
                TopColor = settings.TopColor,
                SideColor = settings.SideColor,
                SurfaceTexture = settings.SurfaceTexture,
                UseSeparateTopTexture = settings.UseSeparateTopTexture,
                TopTexture = settings.TopTexture,
                TileSize = settings.TileSize,
                Material = settings.Material,
                DecorationPrefab = settings.DecorationPrefab,
                DecorationSpacing = settings.DecorationSpacing,
                DecorationClearance = settings.DecorationClearance
            };
        }

        internal void ApplyTo(BezierWallSettings settings)
        {
            settings.Height = Height;
            settings.Thickness = Thickness;
            settings.Lean = Lean;
            settings.TopColor = TopColor;
            settings.SideColor = SideColor;
            settings.SurfaceTexture = SurfaceTexture;
            settings.UseSeparateTopTexture = UseSeparateTopTexture;
            settings.TopTexture = TopTexture;
            settings.TileSize = TileSize;
            settings.Material = Material;
            settings.DecorationPrefab = DecorationPrefab;
            settings.DecorationSpacing = DecorationSpacing;
            settings.DecorationClearance = DecorationClearance;
        }
    }
}
