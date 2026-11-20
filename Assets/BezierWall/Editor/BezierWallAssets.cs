using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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

        internal static bool TryBuild(BezierWallSettings settings, out Mesh mesh, out Vector2[] footprint, out string error)
        {
            if (float.IsNaN(settings.Lean) || float.IsInfinity(settings.Lean) || Mathf.Abs(settings.Lean) > 2f)
            {
                mesh = null;
                footprint = null;
                error = "墙顶倾斜比例必须介于 -2 和 2 之间。";
                return false;
            }

            BezierWallGeometry.UpdateAutomaticTangents(settings.Anchors);
            if (!BezierWallGeometry.TryBuild(settings.Anchors, settings.Height, settings.Thickness,
                    settings.Subdivisions, settings.TileSize, out mesh, out error, out footprint))
                return false;

            BezierWallProjection.Apply(mesh, settings.Lean, settings.TopColor, settings.SideColor);
            if (mesh.GetIndexCount(0) > 0)
                return true;

            Object.DestroyImmediate(mesh);
            mesh = null;
            error = "当前参数未生成可见墙面。";
            return false;
        }

        internal static Material CreateCartoonMaterial(BezierWallSettings settings)
        {
            Shader shader = Shader.Find(CartoonShader);
            if (shader == null)
                return null;

            var material = new Material(shader) { name = "Cartoon Wall" };
            if (TryGetImage(settings.SurfaceTexture, out Texture2D texture, out Rect uv))
            {
                material.SetTexture("_MainTex", texture);
                material.SetVector("_TextureRect", new Vector4(uv.x, uv.y, uv.width, uv.height));
            }

            return material;
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

            if (!TryBuild(settings, out Mesh mesh, out Vector2[] footprint, out error))
                return false;

            Material generated = settings.Material == null ? CreateCartoonMaterial(settings) : null;
            if (settings.Material == null && generated == null)
            {
                Object.DestroyImmediate(mesh);
                error = "找不到弧形墙的卡通着色器。";
                return false;
            }

            mesh.name = objectName;
            AssetDatabase.CreateAsset(mesh, path);
            if (generated != null)
                AssetDatabase.AddObjectToAsset(generated, mesh);
            AssetDatabase.SaveAssets();

            wall = new GameObject(objectName, typeof(MeshFilter), typeof(MeshRenderer));
            StageUtility.PlaceGameObjectInCurrentStage(wall);
            wall.transform.position = settings.Origin;
            wall.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = wall.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = settings.Material != null ? settings.Material : generated;
            renderer.sortingLayerID = settings.SortingLayer;
            renderer.sortingOrder = settings.SortingOrder;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            if (settings.AddCollider)
                wall.AddComponent<PolygonCollider2D>().SetPath(0, footprint);

            Undo.RegisterCreatedObjectUndo(wall, "创建 2D 弧形墙");
            EditorSceneManager.MarkSceneDirty(wall.scene);
            return true;
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
