using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    internal static class BezierWallBakeBindings
    {
        private const string UndoName = "关联墙体草稿";
        private const string DecorationContainerName = "沿墙摆件";
        private const string CartoonShaderName = "MagicTavern/BezierWallCartoon";

        internal static bool IsSavedSceneObject(GameObject wall)
        {
            return wall != null
                && !EditorUtility.IsPersistent(wall)
                && wall.scene.IsValid()
                && wall.scene.isLoaded
                && !string.IsNullOrEmpty(wall.scene.path)
                && !EditorSceneManager.IsPreviewScene(wall.scene);
        }

        internal static BezierWallLayout GetDraft(GameObject wall)
        {
            if (wall == null || !wall.TryGetComponent(out BezierWallBakeLink link)
                || string.IsNullOrEmpty(link.DraftGuid))
                return null;

            string path = AssetDatabase.GUIDToAssetPath(link.DraftGuid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<BezierWallLayout>(path);
        }

        internal static bool TryAssociate(GameObject wall, BezierWallLayout draft, BezierWallSettings bakedSettings, out string error)
        {
            if (!IsSavedSceneObject(wall))
            {
                error = "请先保存场景，再选择其中的墙体进行关联。";
                return false;
            }

            if (draft == null || !AssetDatabase.IsMainAsset(draft) || bakedSettings == null)
            {
                error = "请先将墙体草稿保存为项目资产。";
                return false;
            }

            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(draft));
            if (string.IsNullOrEmpty(guid))
            {
                error = "无法读取草稿资产标识。";
                return false;
            }

            if (wall.TryGetComponent(out BezierWallBakeLink existing))
            {
                if (existing.DraftGuid == guid)
                {
                    error = null;
                    return true;
                }

                error = "该墙已关联其他草稿，请选择原草稿或另一个墙体。";
                return false;
            }

            if (!wall.TryGetComponent(out MeshFilter filter) || !wall.TryGetComponent(out MeshRenderer renderer))
            {
                error = "请选择带有 MeshFilter 和 MeshRenderer 的烘焙墙体。";
                return false;
            }

            Mesh mesh = filter.sharedMesh;
            string meshPath = AssetDatabase.GetAssetPath(mesh);
            if (mesh == null || !mesh.isReadable || !AssetDatabase.IsMainAsset(mesh)
                || !meshPath.StartsWith("Assets/", StringComparison.Ordinal)
                || !meshPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                error = "墙体必须使用项目 Assets 中可读的烘焙 Mesh 主资产。";
                return false;
            }

            BezierWallSettings snapshot = bakedSettings.Copy();
            if (!BezierWallDecorations.TryPlan(snapshot, out IReadOnlyList<BezierWallDecorationPlacement> placements, out error)
                || !BezierWallAssets.TryBuild(snapshot, out Mesh expected, out Vector2[][] footprints, out error))
                return false;

            bool matches;
            try
            {
                matches = HasMatchingMesh(mesh, expected);
            }
            finally
            {
                Object.DestroyImmediate(expected);
            }

            if (!matches)
            {
                error = "场景墙体的网格与当前草稿不一致。请使用烘焙时保存的草稿进行关联。";
                return false;
            }

            Material generatedMaterial = snapshot.Material == null
                ? FindGeneratedMaterial(meshPath, renderer.sharedMaterial)
                : null;
            PolygonCollider2D[] colliders = wall.GetComponents<PolygonCollider2D>();
            PolygonCollider2D collider = snapshot.AddCollider ? FindGeneratedCollider(colliders, footprints) : null;
            if (snapshot.AddCollider && collider == null && colliders.Length != 0)
            {
                error = "无法唯一识别原墙的烘焙碰撞体。请核对墙体上的 PolygonCollider2D 后再关联。";
                return false;
            }

            FindGeneratedDecorations(wall, snapshot.DecorationPrefab, placements,
                out Transform decorationsRoot, out GameObject[] generatedDecorations);

            BezierWallBakeLink link = Undo.AddComponent<BezierWallBakeLink>(wall);
            Undo.RecordObject(link, UndoName);
            link.DraftGuid = guid;
            link.GeneratedMesh = mesh;
            link.GeneratedMaterial = generatedMaterial;
            link.LastAppliedMaterial = snapshot.Material != null ? snapshot.Material : generatedMaterial;
            link.GeneratedCollider = collider;
            link.DecorationsRoot = decorationsRoot;
            link.GeneratedDecorations = generatedDecorations;

            Undo.SetCurrentGroupName(UndoName);
            EditorUtility.SetDirty(link);
            if (PrefabUtility.IsPartOfPrefabInstance(link))
                PrefabUtility.RecordPrefabInstancePropertyModifications(link);
            EditorSceneManager.MarkSceneDirty(wall.scene);
            error = null;
            return true;
        }

        private static bool HasMatchingMesh(Mesh actual, Mesh expected)
        {
            return actual.vertexCount == expected.vertexCount
                && actual.subMeshCount == expected.subMeshCount
                && actual.vertices.SequenceEqual(expected.vertices)
                && actual.triangles.SequenceEqual(expected.triangles)
                && actual.colors32.SequenceEqual(expected.colors32)
                && actual.uv.SequenceEqual(expected.uv)
                && actual.uv3.SequenceEqual(expected.uv3);
        }

        private static Material FindGeneratedMaterial(string meshPath, Material current)
        {
            Material result = null;
            bool ambiguous = false;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(meshPath))
            {
                if (!(asset is Material material) || !AssetDatabase.IsSubAsset(material)
                    || material.shader == null || material.shader.name != CartoonShaderName)
                    continue;

                if (material == current)
                    return material;

                ambiguous |= result != null;
                result = material;
            }

            return ambiguous ? null : result;
        }

        private static PolygonCollider2D FindGeneratedCollider(PolygonCollider2D[] colliders, Vector2[][] footprints)
        {
            PolygonCollider2D result = null;
            foreach (PolygonCollider2D candidate in colliders)
            {
                if (candidate.pathCount != footprints.Length)
                    continue;

                bool matches = true;
                for (int i = 0; i < footprints.Length && matches; i++)
                    matches = candidate.GetPath(i).SequenceEqual(footprints[i]);

                if (!matches)
                    continue;
                if (result != null)
                    return null;

                result = candidate;
            }

            return result;
        }

        private static void FindGeneratedDecorations(GameObject wall, GameObject prefab,
            IReadOnlyList<BezierWallDecorationPlacement> placements, out Transform root, out GameObject[] decorations)
        {
            root = null;
            decorations = Array.Empty<GameObject>();
            if (prefab == null || placements.Count == 0)
                return;

            foreach (Transform candidate in wall.transform)
            {
                if (candidate.name != DecorationContainerName)
                    continue;

                GameObject[] matches = MatchDecorations(candidate, prefab, placements);
                if (matches.Length == 0)
                    continue;

                if (root != null)
                {
                    root = null;
                    decorations = Array.Empty<GameObject>();
                    return;
                }

                root = candidate;
                decorations = matches;
            }
        }

        private static GameObject[] MatchDecorations(Transform root, GameObject prefab,
            IReadOnlyList<BezierWallDecorationPlacement> placements)
        {
            var matches = new GameObject[placements.Count];
            var duplicateSlots = new bool[placements.Count];
            foreach (Transform child in root)
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject)
                    || PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject) != prefab)
                    continue;

                for (int i = 0; i < placements.Count; i++)
                {
                    BezierWallDecorationPlacement placement = placements[i];
                    if ((child.localPosition - (Vector3)placement.Position).sqrMagnitude > 0.00000001f
                        || Mathf.Abs(Quaternion.Dot(child.localRotation, Quaternion.Euler(0f, 0f, placement.Angle))) < 0.99999f)
                        continue;

                    duplicateSlots[i] |= matches[i] != null;
                    matches[i] = child.gameObject;
                    break;
                }
            }

            var result = new List<GameObject>();
            for (int i = 0; i < matches.Length; i++)
            {
                if (matches[i] != null && !duplicateSlots[i])
                    result.Add(matches[i]);
            }

            return result.ToArray();
        }
    }
}
