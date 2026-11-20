using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    [InitializeOnLoad]
    internal static class BezierWallBakeUpdates
    {
        private const string UndoName = "更新原墙";
        private static readonly Dictionary<int, (GlobalObjectId Mesh, GlobalObjectId Material)> UpdatedAssets
            = new Dictionary<int, (GlobalObjectId Mesh, GlobalObjectId Material)>();

        static BezierWallBakeUpdates()
        {
            Undo.undoRedoEvent += RefreshRestoredOutputs;
        }

        private static void RefreshRestoredOutputs(in UndoRedoInfo info)
        {
            if (info.undoName != UndoName || !UpdatedAssets.TryGetValue(info.undoGroup, out var assets))
                return;

            var mesh = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(assets.Mesh) as Mesh;
            var material = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(assets.Material) as Material;
            if (mesh != null && mesh.isReadable)
            {
                // Native Undo may restore the serialized channels without rebuilding the GPU vertex layout.
                RefreshMeshData(mesh);
                EditorUtility.SetDirty(mesh);
            }

            // Persist restored owned assets after Unity has finished applying the Undo operation.
            EditorApplication.delayCall += () =>
            {
                if (mesh != null)
                    AssetDatabase.SaveAssetIfDirty(mesh);
                if (material != null)
                    AssetDatabase.SaveAssetIfDirty(material);
            };
            Physics2D.SyncTransforms();
            SortingGroup.UpdateAllSortingGroups();
            SceneView.RepaintAll();
        }

        internal static bool TryUpdate(GameObject wall, BezierWallLayout draft, BezierWallSettings settings, out string error)
        {
            error = null;
            if (!BezierWallBakeBindings.IsSavedSceneObject(wall) || draft == null || settings == null
                || !wall.TryGetComponent(out BezierWallBakeLink link) || BezierWallBakeBindings.GetDraft(wall) != draft)
            {
                error = "请在已保存的场景中选中与当前草稿关联的墙体。";
                return false;
            }

            if (!wall.TryGetComponent(out MeshFilter filter) || !wall.TryGetComponent(out MeshRenderer renderer)
                || link.GeneratedMesh == null || !link.GeneratedMesh.isReadable || filter.sharedMesh != link.GeneratedMesh
                || !AssetDatabase.IsMainAsset(link.GeneratedMesh))
            {
                error = "墙体原有的 Mesh 或渲染组件已更换。请恢复关联时的输出。";
                return false;
            }

            Mesh previousMesh = link.GeneratedMesh;
            Mesh preparedMesh = null;
            Material previousMaterial = link.GeneratedMaterial;
            Material preparedMaterial = null;
            Scene previewScene = default;
            Transform stagedDecorations = null;
            string newMeshPath = null;
            string newMaterialPath = null;
            int undoGroup = -1;
            bool updatedMesh = false;
            bool updatedMaterial = false;
            try
            {
                var work = settings.Copy();
                work.SortingLayer = renderer.sortingLayerID;
                work.SortingOrder = renderer.sortingOrder;
                if (!BezierWallDecorations.TryPlan(work, out IReadOnlyList<BezierWallDecorationPlacement> placements, out error)
                    || !BezierWallAssets.TryBuild(work, out preparedMesh, out Vector2[][] footprints, out error))
                    return false;

                string meshPath = AssetDatabase.GetAssetPath(previousMesh);
                bool usedInOtherAsset = IsUsedInOtherSceneOrPrefab(meshPath, wall.scene.path);
                bool replaceMesh = usedInOtherAsset || !AssetDatabase.IsOpenForEdit(previousMesh)
                    || HasOtherMeshUsers(previousMesh, filter);
                bool applyMaterial = renderer.sharedMaterial == link.LastAppliedMaterial;
                bool reuseMaterial = false;
                if (applyMaterial && work.Material == null)
                {
                    preparedMaterial = BezierWallAssets.CreateCartoonMaterial(work);
                    if (preparedMaterial == null)
                    {
                        error = "找不到弧形墙的卡通着色器。";
                        return false;
                    }

                    string materialPath = AssetDatabase.GetAssetPath(previousMaterial);
                    reuseMaterial = previousMaterial != null && EditorUtility.IsPersistent(previousMaterial)
                        && AssetDatabase.IsOpenForEdit(previousMaterial)
                        && !HasOtherMaterialUsers(previousMaterial, renderer)
                        && !(materialPath == meshPath
                            ? usedInOtherAsset || replaceMesh
                            : IsUsedInOtherSceneOrPrefab(materialPath, wall.scene.path));
                }

                Transform container = link.DecorationsRoot != null && link.DecorationsRoot.parent == wall.transform
                    ? link.DecorationsRoot : null;
                List<GameObject> existing = GetManagedPrefabs(link, container);
                GameObject[] result = FindReusablePrefabs(work.DecorationPrefab, placements, existing);
                if (Array.IndexOf(result, null) >= 0)
                {
                    previewScene = EditorSceneManager.NewPreviewScene();
                    var stagingRoot = EditorUtility.CreateGameObjectWithHideFlags("墙体更新预检", HideFlags.HideAndDontSave);
                    SceneManager.MoveGameObjectToScene(stagingRoot, previewScene);
                    stagedDecorations = BezierWallAssets.AddDecorations(work, placements, stagingRoot.transform);
                }

                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(UndoName);

                Mesh generatedMesh = previousMesh;
                Material generatedMaterial = previousMaterial;
                Material appliedMaterial = link.LastAppliedMaterial;
                if (replaceMesh)
                {
                    newMeshPath = GetNewAssetPath(meshPath, " 更新", ".asset");
                    preparedMesh.name = previousMesh.name;
                    CreateOutputAsset(preparedMesh, newMeshPath);
                    Undo.RegisterCompleteObjectUndo(filter, UndoName);
                    filter.sharedMesh = preparedMesh;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                    generatedMesh = preparedMesh;
                }
                else
                {
                    Undo.RegisterCompleteObjectUndo(previousMesh, UndoName);
                    updatedMesh = true;
                    CopyMeshData(preparedMesh, previousMesh);
                    EditorUtility.SetDirty(previousMesh);
                }

                if (applyMaterial)
                {
                    Material nextMaterial = work.Material;
                    if (reuseMaterial)
                    {
                        Undo.RegisterCompleteObjectUndo(previousMaterial, UndoName);
                        updatedMaterial = true;
                        previousMaterial.shader = preparedMaterial.shader;
                        previousMaterial.CopyPropertiesFromMaterial(preparedMaterial);
                        EditorUtility.SetDirty(previousMaterial);
                        nextMaterial = previousMaterial;
                    }
                    else if (preparedMaterial != null)
                    {
                        newMaterialPath = GetNewAssetPath(meshPath, " 墙体材质", ".mat");
                        CreateOutputAsset(preparedMaterial, newMaterialPath);
                        generatedMaterial = preparedMaterial;
                        nextMaterial = preparedMaterial;
                    }

                    if (renderer.sharedMaterial != nextMaterial)
                    {
                        Undo.RegisterCompleteObjectUndo(renderer, UndoName);
                        renderer.sharedMaterial = nextMaterial;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }

                    appliedMaterial = nextMaterial;
                }

                PolygonCollider2D collider = UpdateCollider(wall, link.GeneratedCollider, work.AddCollider, footprints);
                container = UpdateDecorations(wall, work, placements, container, existing, result, stagedDecorations);

                // Creation/destruction of scene objects can flush Undo. Record the final binding after those operations.
                Undo.RegisterCompleteObjectUndo(link, UndoName);
                link.GeneratedMesh = generatedMesh;
                link.GeneratedMaterial = generatedMaterial;
                link.LastAppliedMaterial = appliedMaterial;
                link.GeneratedCollider = collider;
                link.DecorationsRoot = container;
                link.GeneratedDecorations = result;
                EditorUtility.SetDirty(link);
                PrefabUtility.RecordPrefabInstancePropertyModifications(link);
                Undo.FlushUndoRecordObjects();
                AssetDatabase.SaveAssetIfDirty(generatedMesh);
                if (applyMaterial && preparedMaterial != null)
                    AssetDatabase.SaveAssetIfDirty(generatedMaterial);
                EditorSceneManager.MarkSceneDirty(wall.scene);
                Physics2D.SyncTransforms();
                SortingGroup.UpdateAllSortingGroups();
                SceneView.RepaintAll();
                Undo.CollapseUndoOperations(undoGroup);
                UpdatedAssets[undoGroup] = (
                    updatedMesh ? GlobalObjectId.GetGlobalObjectIdSlow(previousMesh) : default,
                    updatedMaterial ? GlobalObjectId.GetGlobalObjectIdSlow(previousMaterial) : default);
                return true;
            }
            catch (Exception exception)
            {
                if (undoGroup >= 0)
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.RevertAllDownToGroup(undoGroup);
                }

                DeleteNewOutput(preparedMaterial, newMaterialPath);
                DeleteNewOutput(preparedMesh, newMeshPath);

                // A failed save may already have flushed another output. Persist the reverted versions.
                if (updatedMesh)
                {
                    RefreshMeshData(previousMesh);
                    EditorUtility.SetDirty(previousMesh);
                    AssetDatabase.SaveAssetIfDirty(previousMesh);
                }
                if (updatedMaterial)
                    AssetDatabase.SaveAssetIfDirty(previousMaterial);

                error = $"更新墙体失败，已恢复原结果。{exception.Message}";
                return false;
            }
            finally
            {
                if (previewScene.IsValid())
                    EditorSceneManager.ClosePreviewScene(previewScene);
                if (preparedMesh != null && !EditorUtility.IsPersistent(preparedMesh))
                    Object.DestroyImmediate(preparedMesh);
                if (preparedMaterial != null && !EditorUtility.IsPersistent(preparedMaterial))
                    Object.DestroyImmediate(preparedMaterial);
            }
        }

        private static void CreateOutputAsset(Object asset, string path)
        {
            AssetDatabase.CreateAsset(asset, path);
            if (AssetDatabase.GetAssetPath(asset) != path)
                throw new InvalidOperationException($"无法保存墙体资产 {path}。");
        }

        private static void DeleteNewOutput(Object asset, string path)
        {
            if (asset != null && path != null && AssetDatabase.GetAssetPath(asset) == path)
                AssetDatabase.DeleteAsset(path);
        }

        private static string GetNewAssetPath(string meshPath, string suffix, string extension)
        {
            string folder = Path.GetDirectoryName(meshPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(meshPath);
            return AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}{suffix}{extension}");
        }

        private static bool IsUsedInOtherSceneOrPrefab(string assetPath, string wallScenePath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            foreach (string candidate in AssetDatabase.GetAllAssetPaths())
            {
                if (candidate == wallScenePath || !(candidate.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    || candidate.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (Array.IndexOf(AssetDatabase.GetDependencies(candidate, recursive: false), assetPath) >= 0)
                    return true;
            }

            return false;
        }

        private static bool HasOtherMeshUsers(Mesh mesh, MeshFilter filter)
        {
            foreach (MeshFilter other in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (other != filter && other.sharedMesh == mesh)
                    return true;
            }

            foreach (SkinnedMeshRenderer other in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
            {
                if (other.sharedMesh == mesh)
                    return true;
            }

            foreach (MeshCollider other in Resources.FindObjectsOfTypeAll<MeshCollider>())
            {
                if (other.sharedMesh == mesh)
                    return true;
            }

            return false;
        }

        private static bool HasOtherMaterialUsers(Material material, MeshRenderer renderer)
        {
            foreach (Renderer other in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (other != renderer && Array.IndexOf(other.sharedMaterials, material) >= 0)
                    return true;
            }

            return false;
        }

        private static void RefreshMeshData(Mesh mesh)
        {
            Mesh snapshot = Object.Instantiate(mesh);
            try
            {
                CopyMeshData(snapshot, mesh);
            }
            finally
            {
                Object.DestroyImmediate(snapshot);
            }
        }

        private static void CopyMeshData(Mesh source, Mesh target)
        {
            target.Clear(keepVertexLayout: false);
            target.indexFormat = source.indexFormat;
            target.vertices = source.vertices;
            target.normals = source.normals;
            target.tangents = source.tangents;
            target.colors32 = source.colors32;

            target.uv = source.uv;
            if (source.HasVertexAttribute(VertexAttribute.TexCoord2))
                target.uv3 = source.uv3;

            target.SetTriangles(source.triangles, 0, calculateBounds: false);
            target.bounds = source.bounds;
            target.UploadMeshData(markNoLongerReadable: false);
        }

        private static PolygonCollider2D UpdateCollider(GameObject wall, PolygonCollider2D collider,
            bool enabled, Vector2[][] footprints)
        {
            if (collider != null && collider.gameObject != wall)
                collider = null;

            if (!enabled)
            {
                if (collider != null)
                    Undo.DestroyObjectImmediate(collider);
                return null;
            }

            if (collider == null)
                collider = Undo.AddComponent<PolygonCollider2D>(wall);
            Undo.RegisterCompleteObjectUndo(collider, UndoName);
            collider.pathCount = footprints.Length;
            for (int i = 0; i < footprints.Length; i++)
                collider.SetPath(i, footprints[i]);
            PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            return collider;
        }

        private static List<GameObject> GetManagedPrefabs(BezierWallBakeLink link, Transform container)
        {
            var result = new List<GameObject>();
            if (container == null || link.GeneratedDecorations == null)
                return result;

            foreach (GameObject instance in link.GeneratedDecorations)
            {
                if (instance != null && instance.transform.parent == container
                    && PrefabUtility.IsAnyPrefabInstanceRoot(instance)
                    && PrefabUtility.GetCorrespondingObjectFromSource(instance) != null && !result.Contains(instance))
                    result.Add(instance);
            }

            return result;
        }

        private static GameObject[] FindReusablePrefabs(GameObject prefab,
            IReadOnlyList<BezierWallDecorationPlacement> placements, List<GameObject> existing)
        {
            var result = new GameObject[placements.Count];
            var matches = new List<GameObject>();
            foreach (GameObject instance in existing)
            {
                if (PrefabUtility.GetCorrespondingObjectFromSource(instance) == prefab)
                    matches.Add(instance);
            }

            for (int i = 0; i < result.Length && matches.Count > 0; i++)
            {
                int nearest = 0;
                float nearestDistance = float.PositiveInfinity;
                for (int index = 0; index < matches.Count; index++)
                {
                    float distance = ((Vector2)matches[index].transform.localPosition - placements[i].Position).sqrMagnitude;
                    if (distance < nearestDistance)
                    {
                        nearest = index;
                        nearestDistance = distance;
                    }
                }

                result[i] = matches[nearest];
                matches.RemoveAt(nearest);
            }

            return result;
        }

        private static Transform UpdateDecorations(GameObject wall, BezierWallSettings settings,
            IReadOnlyList<BezierWallDecorationPlacement> placements, Transform container, List<GameObject> existing,
            GameObject[] result, Transform staged)
        {
            if (container == null && result.Length > 0)
            {
                var root = new GameObject("沿墙摆件");
                SceneManager.MoveGameObjectToScene(root, wall.scene);
                container = root.transform;
                container.SetParent(wall.transform, worldPositionStays: false);
                Undo.RegisterCreatedObjectUndo(root, UndoName);
            }

            // Stage children are removed from the end so their placement indices remain valid.
            for (int i = result.Length - 1; i >= 0; i--)
            {
                if (result[i] == null)
                {
                    Transform instance = staged.GetChild(i);
                    instance.SetParent(null, worldPositionStays: false);
                    SceneManager.MoveGameObjectToScene(instance.gameObject, wall.scene);
                    instance.SetParent(container, worldPositionStays: false);
                    Undo.RegisterCreatedObjectUndo(instance.gameObject, UndoName);
                    result[i] = instance.gameObject;
                }
                else
                {
                    Transform instance = result[i].transform;
                    Undo.RegisterCompleteObjectUndo(instance, UndoName);
                    instance.localPosition = placements[i].Position;
                    instance.localRotation = Quaternion.Euler(0f, 0f, placements[i].Angle);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(instance);
                    if (!instance.TryGetComponent(out SortingGroup group))
                        group = Undo.AddComponent<SortingGroup>(instance.gameObject);
                    Undo.RegisterCompleteObjectUndo(group, UndoName);
                    group.sortingLayerID = settings.SortingLayer;
                    group.sortingOrder = Mathf.Clamp(settings.SortingOrder, short.MinValue, short.MaxValue - 1) + 1;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(group);
                }
            }

            foreach (GameObject instance in existing)
            {
                if (Array.IndexOf(result, instance) >= 0)
                    continue;

                PreserveAddedChildren(instance, container);
                Undo.DestroyObjectImmediate(instance);
            }

            return container;
        }

        private static void PreserveAddedChildren(GameObject instance, Transform container)
        {
            var added = new HashSet<Transform>();
            foreach (AddedGameObject child in PrefabUtility.GetAddedGameObjects(instance))
            {
                if (child.instanceGameObject != null && child.instanceGameObject != instance
                    && child.instanceGameObject.transform.IsChildOf(instance.transform))
                    added.Add(child.instanceGameObject.transform);
            }

            foreach (Transform child in added)
            {
                bool hasAddedAncestor = false;
                for (Transform parent = child.parent; parent != instance.transform && parent != null; parent = parent.parent)
                {
                    if (!added.Contains(parent))
                        continue;
                    hasAddedAncestor = true;
                    break;
                }

                if (!hasAddedAncestor)
                    Undo.SetTransformParent(child, container, UndoName);
            }
        }
    }
}
