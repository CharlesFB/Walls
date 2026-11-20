using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    internal static class BezierWallAssets
    {
        private const string CartoonShader = "MagicTavern/BezierWallCartoon";

        internal static bool IsImage(Object asset)
        {
            return asset == null || asset is Texture2D || asset is Sprite;
        }

        internal static bool TryGetImage(Object asset, out Texture2D texture, out Rect uv)
        {
            uv = new Rect(0f, 0f, 1f, 1f);
            texture = asset as Texture2D;
            if (asset is Sprite sprite)
            {
                // Read the source texture and source rect, independent of how an atlas packs the sprite.
                string path = AssetDatabase.GetAssetPath(sprite);
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture != null)
                {
                    // Sprite.rect uses source pixels. Import max-size can rescale the texture.
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    int width = texture.width;
                    int height = texture.height;
                    if (importer != null)
                        importer.GetSourceTextureWidthAndHeight(out width, out height);

                    Rect rect = sprite.rect;
                    uv = new Rect(rect.x / width, rect.y / height, rect.width / width, rect.height / height);
                }
                else if (!sprite.packed || sprite.packingMode == SpritePackingMode.Rectangle
                    && sprite.packingRotation == SpritePackingRotation.None)
                {
                    texture = sprite.texture;
                    Rect rect = sprite.textureRect;
                    uv = new Rect(rect.x / texture.width, rect.y / texture.height,
                        rect.width / texture.width, rect.height / texture.height);
                }
            }

            return texture != null;
        }

        internal static bool TryBuild(BezierWallSettings settings, out Mesh mesh, out Vector2[][] footprints, out string error)
        {
            if (float.IsNaN(settings.Lean) || float.IsInfinity(settings.Lean) || Mathf.Abs(settings.Lean) > 2f)
            {
                mesh = null;
                footprints = null;
                error = "墙顶倾斜比例必须介于 -2 和 2 之间。";
                return false;
            }

            if (!BezierWallGeometry.TryBuild(settings, out mesh, out error, out footprints))
                return false;

            BezierWallProjection.Apply(mesh, settings.Lean, settings.TopColor, settings.SideColor,
                settings.UseSeparateTopTexture);
            if (settings.Closed && !settings.OpeningEnabled)
                BezierWallGeometry.SmoothClosedSeam(mesh);
            if (mesh.GetIndexCount(0) > 0)
                return true;

            Object.DestroyImmediate(mesh);
            mesh = null;
            footprints = null;
            error = "当前参数未生成可见墙面。";
            return false;
        }

        internal static Material CreateCartoonMaterial(BezierWallSettings settings)
        {
            Shader shader = Shader.Find(CartoonShader);
            if (shader == null)
                return null;

            var material = new Material(shader) { name = "Cartoon Wall" };
            SetMaterialImage(material, settings.SurfaceTexture, "_MainTex", "_TextureRect");
            if (settings.UseSeparateTopTexture)
            {
                material.SetFloat("_UseSeparateTopTex", 1f);
                SetMaterialImage(material, settings.TopTexture, "_TopTex", "_TopTextureRect");
            }

            return material;
        }

        private static void SetMaterialImage(Material material, Object asset, string textureProperty, string rectProperty)
        {
            if (!TryGetImage(asset, out Texture2D texture, out Rect uv))
                return;

            material.SetTexture(textureProperty, texture);
            material.SetVector(rectProperty, new Vector4(uv.x, uv.y, uv.width, uv.height));
        }

        internal static bool TryBake(BezierWallSettings settings, string path, string objectName,
            out GameObject wall, out string error)
        {
            wall = null;
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            {
                error = "该路径已有资产，请使用另一个文件名。";
                return false;
            }

            if (!BezierWallDecorations.TryPlan(settings, out IReadOnlyList<BezierWallDecorationPlacement> placements, out error)
                || !TryBuild(settings, out Mesh mesh, out Vector2[][] footprints, out error))
                return false;

            Material generated = settings.Material == null ? CreateCartoonMaterial(settings) : null;
            if (settings.Material == null && generated == null)
            {
                Object.DestroyImmediate(mesh);
                error = "找不到弧形墙的卡通着色器。";
                return false;
            }

            wall = new GameObject(objectName, typeof(MeshFilter), typeof(MeshRenderer));
            StageUtility.PlaceGameObjectInCurrentStage(wall);
            wall.transform.position = settings.Origin;
            wall.GetComponent<MeshFilter>().sharedMesh = mesh;
            ConfigureWallRenderer(settings, wall.GetComponent<MeshRenderer>(),
                settings.Material != null ? settings.Material : generated);

            if (settings.AddCollider)
            {
                var collider = wall.AddComponent<PolygonCollider2D>();
                collider.pathCount = footprints.Length;
                for (int i = 0; i < footprints.Length; i++)
                    collider.SetPath(i, footprints[i]);
            }

            try
            {
                AddDecorations(settings, placements, wall.transform);
            }
            catch (Exception exception)
            {
                Object.DestroyImmediate(wall);
                Object.DestroyImmediate(mesh);
                if (generated != null)
                    Object.DestroyImmediate(generated);
                wall = null;
                error = $"无法实例化沿墙摆件。{exception.Message}";
                return false;
            }

            mesh.name = objectName;
            AssetDatabase.CreateAsset(mesh, path);
            if (generated != null)
                AssetDatabase.AddObjectToAsset(generated, mesh);
            AssetDatabase.SaveAssets();

            Undo.RegisterCreatedObjectUndo(wall, "创建 2D 弧形墙");
            EditorSceneManager.MarkSceneDirty(wall.scene);
            return true;
        }

        internal static void ConfigureWallRenderer(BezierWallSettings settings, MeshRenderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.sortingLayerID = settings.SortingLayer;
            renderer.sortingOrder = settings.SortingOrder;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        internal static void AddDecorations(BezierWallSettings settings,
            IReadOnlyList<BezierWallDecorationPlacement> placements, Transform wall)
        {
            if (placements.Count == 0)
                return;

            GameObject container = EditorUtility.CreateGameObjectWithHideFlags("沿墙摆件", wall.gameObject.hideFlags);
            container.transform.SetParent(wall, false);

            int sortingOrder = Mathf.Clamp(settings.SortingOrder, short.MinValue, short.MaxValue - 1) + 1;
            foreach (BezierWallDecorationPlacement placement in placements)
            {
                var instance = PrefabUtility.InstantiatePrefab(settings.DecorationPrefab, container.transform) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException("所选 Prefab 无法实例化。");

                instance.transform.localPosition = placement.Position;
                instance.transform.localRotation = Quaternion.Euler(0f, 0f, placement.Angle);
                if (!instance.TryGetComponent(out SortingGroup group))
                    group = instance.AddComponent<SortingGroup>();

                group.sortingLayerID = settings.SortingLayer;
                group.sortingOrder = sortingOrder;
            }
        }

        internal static BezierWallLayout SaveLayout(BezierWallSettings settings, string path)
        {
            var draft = ScriptableObject.CreateInstance<BezierWallLayout>();
            draft.Settings = settings.Copy();
            AssetDatabase.CreateAsset(draft, path);
            AssetDatabase.SaveAssets();
            return draft;
        }
    }
}
