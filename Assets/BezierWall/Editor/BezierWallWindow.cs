using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    internal sealed class BezierWallWindow : EditorWindow
    {
        private static readonly Color CanvasColor = new Color32(31, 37, 44, 255);
        private static readonly Color GridColor = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color CurveColor = new Color32(115, 232, 200, 255);
        private static readonly Color HandleColor = new Color32(255, 202, 117, 255);
        private static readonly Color DecorationColor = new Color32(202, 178, 255, 255);
        private static readonly string[] Tabs = { "描点", "成品预览" };
        private static readonly string[] Endpoints = { "自动选最近端点", "起点", "终点" };

        [SerializeField] private BezierWallSettings settings = new BezierWallSettings();
        [SerializeField] private BezierWallLayout currentDraft;
        [SerializeField] private BezierWallStylePreset stylePreset;
        [SerializeField] private BezierWallLayout joinDraft;
        [SerializeField] private int currentJoinEndpoint;
        [SerializeField] private int sourceJoinEndpoint;
        [SerializeField] private float joinSnapDistance = 0.35f;
        [SerializeField] private Vector2 viewportCenter;
        [SerializeField] private float scale = 60f;
        [SerializeField] private int activeTab;

        private SerializedObject serializedWindow;
        private Vector2 scrollPosition;
        private int selected = -1;
        private int dragControl;
        private int dragGroup = -1;
        private DragMode dragMode;
        private bool frameRequested = true;
        private bool traceDirty = true;
        private List<Vector2> tracePath;
        private List<Vector2> traceAfterOpening;
        private IReadOnlyList<BezierWallDecorationPlacement> traceDecorations;
        private string traceError;
        private bool previewDirty = true;
        private string previewError;
        private Mesh previewMesh;
        private Material previewMaterial;
        private GameObject previewRoot;
        private Bounds previewBounds;
        private PreviewRenderUtility previewRenderer;

        private enum DragMode { None, Pan, Anchor, Incoming, Outgoing }

        private bool UsesBezier => settings.PathMode == WallPathMode.Bezier;

        [MenuItem("Tools/2D 弧形墙编辑器")]
        private static void Open()
        {
            GetWindow<BezierWallWindow>("2D 弧形墙");
        }

        [OnOpenAsset(1)]
        private static bool OpenLayout(int instanceId, int line)
        {
            var draft = EditorUtility.InstanceIDToObject(instanceId) as BezierWallLayout;
            if (draft == null)
                return false;

            GetWindow<BezierWallWindow>("2D 弧形墙").LoadDraft(draft);
            return true;
        }

        private void OnEnable()
        {
            minSize = new Vector2(780f, 480f);
            serializedWindow = new SerializedObject(this);
            Undo.undoRedoPerformed += OnUndo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndo;
            FinishDrag();
            ReleasePreview();
            previewRenderer?.Cleanup();
            previewRenderer = null;
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        private void OnUndo()
        {
            selected = selected < settings.Anchors.Count ? selected : -1;
            OnShapeChanged();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                int tab = GUILayout.Toolbar(activeTab, Tabs, EditorStyles.toolbarButton, GUILayout.Width(164f));
                if (activeTab != tab)
                {
                    activeTab = tab;
                    frameRequested = true;
                }

                if (GUILayout.Button("适配视图", EditorStyles.toolbarButton, GUILayout.Width(78f)))
                    frameRequested = true;

                GUILayout.FlexibleSpace();
                GUILayout.Label(activeTab == 0 ? "左键描点或拖动 · 滚轮缩放 · 中键平移" : "正交 2D 预览 · 滚轮缩放 · 中键平移",
                    EditorStyles.miniLabel);
                GUILayout.Space(8f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(306f)))
                {
                    scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                    DrawSettings();
                    EditorGUILayout.EndScrollView();
                    DrawActions();
                }

                Rect canvas = GUILayoutUtility.GetRect(100f, 10000f, 100f, 10000f,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                DrawCanvas(canvas);
            }
        }

        private void DrawSettings()
        {
            EditorGUILayout.Space(8f);
            GUILayout.Label("草稿", EditorStyles.boldLabel);
            var draft = (BezierWallLayout)EditorGUILayout.ObjectField("打开草稿", currentDraft, typeof(BezierWallLayout), false);
            if (draft != currentDraft)
                LoadDraft(draft);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("新草稿"))
                    NewDraft();
                if (GUILayout.Button("另存草稿"))
                    SaveDraft();
                using (new EditorGUI.DisabledScope(currentDraft == null))
                {
                    if (GUILayout.Button("更新草稿"))
                    {
                        Undo.RecordObject(currentDraft, "更新弧形墙草稿");
                        currentDraft.Settings = settings.Copy();
                        EditorUtility.SetDirty(currentDraft);
                        AssetDatabase.SaveAssets();
                    }
                }
            }

            DrawWallBinding();
            DrawStyleSettings();

            serializedWindow.Update();
            SerializedProperty data = serializedWindow.FindProperty(nameof(settings));
            EditorGUILayout.Space(8f);
            GUILayout.Label("描点底图", EditorStyles.boldLabel);
            Object previous = settings.Reference;
            DrawImageField(data, nameof(settings.Reference), "Texture / Sprite");
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.ReferenceCenter)), new GUIContent("底图中心"));
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.ReferenceWidth)), new GUIContent("底图宽度"));
            EditorGUILayout.Slider(data.FindPropertyRelative(nameof(settings.ReferenceOpacity)), 0f, 1f, new GUIContent("不透明度"));

            EditorGUILayout.Space(6f);
            GUILayout.Label("墙线路径", EditorStyles.boldLabel);
            SerializedProperty mode = data.FindPropertyRelative(nameof(settings.PathMode));
            EditorGUILayout.PropertyField(mode, new GUIContent("路径类型"));
            SerializedProperty closed = data.FindPropertyRelative(nameof(settings.Closed));
            EditorGUILayout.PropertyField(closed, new GUIContent("首尾闭合", "沿路径连接最后一个描点和第一个描点。至少需要三个描点。"));
            bool rounded = mode.enumValueIndex == (int)WallPathMode.RoundedPolyline;
            if (rounded)
            {
                EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.CornerRadius)),
                    new GUIContent("圆角半径", "沿墙脚中心线度量，单位为 Unity 单位。0 使用折角。"));
            }

            EditorGUILayout.IntSlider(data.FindPropertyRelative(nameof(settings.Subdivisions)), 4, 48,
                new GUIContent(rounded ? "每 90° 弧段细分" : "每段曲线细分"));
            DrawOpeningSettings(data);

            EditorGUILayout.Space(6f);
            GUILayout.Label("薄墙外观", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.Height)), new GUIContent("墙顶抬高"));
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.Thickness)), new GUIContent("墙厚"));
            EditorGUILayout.Slider(data.FindPropertyRelative(nameof(settings.Lean)), -0.65f, 0.65f, new GUIContent("墙顶偏移比例"));
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.TopColor)), new GUIContent("顶面色"));
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.SideColor)), new GUIContent("侧面色"));
            var material = data.FindPropertyRelative(nameof(settings.Material));
            using (new EditorGUI.DisabledScope(material.objectReferenceValue != null))
            {
                SerializedProperty separate = data.FindPropertyRelative(nameof(settings.UseSeparateTopTexture));
                EditorGUILayout.PropertyField(separate, new GUIContent("独立顶面贴图"));
                DrawImageField(data, nameof(settings.SurfaceTexture), separate.boolValue ? "侧面贴图" : "墙面贴图");
                if (separate.boolValue)
                    DrawImageField(data, nameof(settings.TopTexture), "顶面贴图");

                EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.TileSize)), new GUIContent("贴图平铺尺寸"));
            }

            EditorGUILayout.PropertyField(material, new GUIContent("自定义材质"));

            DrawDecorationSettings(data);

            EditorGUILayout.Space(6f);
            GUILayout.Label("场景输出", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.Origin)), new GUIContent("场景位置"));
            DrawSortingLayer(data.FindPropertyRelative(nameof(settings.SortingLayer)));
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.SortingOrder)), new GUIContent("层内顺序"));
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.AddCollider)), new GUIContent("添加 2D 脚印碰撞"));

            if (selected >= 0 && selected < settings.Anchors.Count)
            {
                EditorGUILayout.Space(6f);
                GUILayout.Label("选中描点", EditorStyles.boldLabel);
                SerializedProperty point = data.FindPropertyRelative(nameof(settings.Anchors)).GetArrayElementAtIndex(selected);
                EditorGUILayout.PropertyField(point.FindPropertyRelative(nameof(BezierWallAnchor.Position)), new GUIContent("位置"));
                if (!rounded)
                {
                    var automatic = point.FindPropertyRelative(nameof(BezierWallAnchor.Automatic));
                    EditorGUILayout.PropertyField(automatic, new GUIContent("自动切线"));
                    if (!automatic.boolValue)
                    {
                        bool loopHandles = closed.boolValue && settings.Anchors.Count >= 3;
                        if (selected > 0 || loopHandles)
                            EditorGUILayout.PropertyField(point.FindPropertyRelative(nameof(BezierWallAnchor.Incoming)), new GUIContent("入控制柄偏移"));
                        if (selected < settings.Anchors.Count - 1 || loopHandles)
                            EditorGUILayout.PropertyField(point.FindPropertyRelative(nameof(BezierWallAnchor.Outgoing)), new GUIContent("出控制柄偏移"));
                    }
                }
            }

            if (serializedWindow.ApplyModifiedProperties())
            {
                OnShapeChanged();
                if (previous != settings.Reference)
                    frameRequested = true;
            }

            if (selected >= 0 && GUILayout.Button("删除选中描点"))
                DeleteSelected();

            if (!settings.Closed)
                DrawJoinSettings();
            EditorGUILayout.Space(7f);
        }

        private void DrawWallBinding()
        {
            GameObject wall = Selection.activeGameObject;
            if (wall == null || !wall.scene.IsValid() || wall.GetComponent<MeshFilter>() == null
                || wall.GetComponent<MeshRenderer>() == null)
                return;

            EditorGUILayout.Space(6f);
            GUILayout.Label("选中墙体", EditorStyles.boldLabel);
            BezierWallLayout linked = BezierWallBakeBindings.GetDraft(wall);
            if (linked != null)
            {
                EditorGUILayout.LabelField("关联草稿", linked.name);
                if (currentDraft != linked && GUILayout.Button("打开关联草稿"))
                    LoadDraft(linked);
                return;
            }

            using (new EditorGUI.DisabledScope(currentDraft == null))
            {
                if (GUILayout.Button("关联选中墙"))
                {
                    if (BezierWallBakeBindings.TryAssociate(wall, currentDraft, currentDraft.Settings, out string error))
                        Notify("已关联草稿。保存场景后可从墙体重新打开。");
                    else
                        Notify(error);
                }
            }
        }

        private void DrawStyleSettings()
        {
            EditorGUILayout.Space(6f);
            GUILayout.Label("墙体样式", EditorStyles.boldLabel);
            stylePreset = (BezierWallStylePreset)EditorGUILayout.ObjectField("样式预设", stylePreset,
                typeof(BezierWallStylePreset), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(stylePreset == null || stylePreset.Style == null))
                {
                    if (GUILayout.Button("套用预设"))
                        ApplyStylePreset();
                }

                if (GUILayout.Button("另存预设"))
                    SaveStylePreset();

                using (new EditorGUI.DisabledScope(stylePreset == null))
                {
                    if (GUILayout.Button("更新预设"))
                    {
                        Undo.RecordObject(stylePreset, "更新墙体样式");
                        stylePreset.Style = BezierWallStyle.Capture(settings);
                        EditorUtility.SetDirty(stylePreset);
                        AssetDatabase.SaveAssetIfDirty(stylePreset);
                    }
                }
            }
        }

        private void ApplyStylePreset()
        {
            BezierWallSettings candidate = settings.Copy();
            stylePreset.Style.ApplyTo(candidate);
            if (candidate.Anchors.Count >= (candidate.Closed ? 3 : 2))
            {
                if (!BezierWallDecorations.TryPlan(candidate, out _, out string error)
                    || !BezierWallAssets.TryBuild(candidate, out Mesh mesh, out _, out error))
                {
                    Notify($"无法套用样式。{error}");
                    return;
                }

                DestroyImmediate(mesh);
            }

            Undo.RecordObject(this, "套用墙体样式");
            stylePreset.Style.ApplyTo(settings);
            InvalidatePreviews();
        }

        private void SaveStylePreset()
        {
            string path = EditorUtility.SaveFilePanelInProject("另存墙体样式预设", "墙体样式", "asset", "选择预设的保存位置。");
            if (string.IsNullOrEmpty(path))
                return;

            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            {
                Notify("该路径已有资产，请选择另一个文件名。");
                return;
            }

            var preset = CreateInstance<BezierWallStylePreset>();
            preset.Style = BezierWallStyle.Capture(settings);
            AssetDatabase.CreateAsset(preset, path);
            AssetDatabase.SaveAssetIfDirty(preset);
            stylePreset = preset;
            EditorGUIUtility.PingObject(preset);
        }

        private void DrawJoinSettings()
        {
            EditorGUILayout.Space(6f);
            GUILayout.Label("接墙", EditorStyles.boldLabel);
            var source = (BezierWallLayout)EditorGUILayout.ObjectField("接入草稿", joinDraft, typeof(BezierWallLayout), false);
            if (source != joinDraft)
            {
                joinDraft = source;
                frameRequested = true;
                Repaint();
            }

            if (joinDraft == null)
                return;

            currentJoinEndpoint = EditorGUILayout.Popup("当前墙端点", currentJoinEndpoint, Endpoints);
            sourceJoinEndpoint = EditorGUILayout.Popup("接入墙端点", sourceJoinEndpoint, Endpoints);
            joinSnapDistance = EditorGUILayout.FloatField(
                new GUIContent("吸附距离", "允许两个端点相距的最大 Unity 单位数。接入段的其他点保持场景位置。"), joinSnapDistance);

            EditorGUILayout.HelpBox("蓝色虚线是接入草稿。接墙后使用当前墙的路径类型和样式。", MessageType.None);
            using (new EditorGUI.DisabledScope(settings.Anchors.Count < 2))
            {
                if (GUILayout.Button("吸附并接墙"))
                    JoinDraft();
            }
        }

        private void JoinDraft()
        {
            if (!BezierWallJoins.TryJoin(settings, joinDraft.Settings, joinSnapDistance,
                    EndpointChoice(currentJoinEndpoint), EndpointChoice(sourceJoinEndpoint), out BezierWallSettings joined, out string error))
            {
                Notify(error);
                return;
            }

            Undo.RecordObject(this, "吸附并接墙");
            settings = joined;
            joinDraft = null;
            selected = -1;
            frameRequested = true;
            OnShapeChanged();
            Notify("已合并两段墙的描点。");
        }

        private static bool? EndpointChoice(int choice)
        {
            if (choice == 0)
                return null;
            return choice == 1;
        }

        private static void DrawOpeningSettings(SerializedProperty data)
        {
            SerializedProperty enabled = data.FindPropertyRelative(nameof(BezierWallSettings.OpeningEnabled));
            EditorGUILayout.PropertyField(enabled, new GUIContent("启用门洞"));
            if (!enabled.boolValue)
                return;

            SerializedProperty position = data.FindPropertyRelative(nameof(BezierWallSettings.OpeningPosition));
            EditorGUI.BeginChangeCheck();
            float percent = EditorGUILayout.Slider(
                new GUIContent("门洞位置 (%)", "门洞中心沿墙脚路径的位置。从第一个描点开始计为 0%。"),
                position.floatValue * 100f, 0f, 100f);
            if (EditorGUI.EndChangeCheck())
                position.floatValue = percent / 100f;

            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(BezierWallSettings.OpeningWidth)),
                new GUIContent("门洞宽度", "门洞沿墙脚路径的长度，单位为 Unity 单位。"));
        }

        private void DrawDecorationSettings(SerializedProperty data)
        {
            EditorGUILayout.Space(6f);
            GUILayout.Label("沿墙摆件", EditorStyles.boldLabel);
            SerializedProperty prefab = data.FindPropertyRelative(nameof(BezierWallSettings.DecorationPrefab));
            var selectedPrefab = (GameObject)EditorGUILayout.ObjectField("柱子 / 灯笼 Prefab",
                prefab.objectReferenceValue, typeof(GameObject), false);
            if (BezierWallDecorations.IsPrefabAsset(selectedPrefab))
                prefab.objectReferenceValue = selectedPrefab;
            else if (selectedPrefab != prefab.objectReferenceValue)
                Notify("请选择项目里的 Prefab 根资产。");

            if (prefab.objectReferenceValue == null)
                return;

            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(BezierWallSettings.DecorationSpacing)),
                new GUIContent("摆件间距", "沿墙脚中心线按 Unity 单位等距摆放，第一个位置距起点半个间距。"));
            EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(BezierWallSettings.DecorationClearance)),
                new GUIContent("摆件占地半径", "从 Prefab 根节点量到图像或碰撞最远边缘的 XY 距离，用于给门洞留空。"));
        }

        private void DrawImageField(SerializedProperty property, string name, string label)
        {
            SerializedProperty image = property.FindPropertyRelative(name);
            var asset = EditorGUILayout.ObjectField(label, image.objectReferenceValue, typeof(Object), false);
            if (BezierWallAssets.IsImage(asset))
                image.objectReferenceValue = asset;
            else if (asset != image.objectReferenceValue)
                Notify("请选择 Texture2D 或 Sprite 资产。");
        }

        private static void DrawSortingLayer(SerializedProperty property)
        {
            SortingLayer[] layers = SortingLayer.layers;
            var names = new string[layers.Length];
            var ids = new int[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                names[i] = layers[i].name;
                ids[i] = layers[i].id;
            }

            property.intValue = EditorGUILayout.IntPopup("排序层", property.intValue, names, ids);
        }

        private void DrawActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(settings.Anchors.Count == 0))
                {
                    if (GUILayout.Button("清空描点", GUILayout.Height(26f), GUILayout.Width(88f)))
                    {
                        Undo.RecordObject(this, "清空弧形墙描点");
                        settings.Anchors.Clear();
                        selected = -1;
                        OnShapeChanged();
                    }
                }

                using (new EditorGUI.DisabledScope(settings.Anchors.Count < 2))
                {
                    GameObject wall = Selection.activeGameObject;
                    if (currentDraft != null && BezierWallBakeBindings.GetDraft(wall) == currentDraft
                        && GUILayout.Button("更新选中墙", GUILayout.Height(26f)))
                    {
                        if (BezierWallBakeUpdates.TryUpdate(wall, currentDraft, settings, out string error))
                            Notify("已更新场景里的原墙。");
                        else
                            Notify(error);
                    }

                    if (GUILayout.Button("烘焙到场景", GUILayout.Height(26f)))
                        Bake();
                }
            }
        }

        private void DrawCanvas(Rect rect)
        {
            int control = GUIUtility.GetControlID(FocusType.Keyboard, rect);
            if (rect.width < 20f || rect.height < 20f)
                return;

            if (frameRequested && Event.current.type == EventType.Repaint)
            {
                Frame(rect.size);
                frameRequested = false;
            }

            GUI.BeginGroup(rect);
            Rect area = new Rect(Vector2.zero, rect.size);
            if (Event.current.type == EventType.Repaint)
            {
                if (activeTab == 0)
                    DrawTrace(area);
                else
                    DrawPreview(area);
            }

            HandleCanvasInput(control, area);
            GUI.EndGroup();
        }

        private void DrawTrace(Rect area)
        {
            EditorGUI.DrawRect(area, CanvasColor);
            if (TryReference(out Texture2D image, out Rect uv, out Vector2 size))
            {
                Vector2 topLeft = ToScreen(settings.ReferenceCenter + new Vector2(-size.x, size.y) * 0.5f, area.size);
                Color previous = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(settings.ReferenceOpacity));
                GUI.DrawTextureWithTexCoords(new Rect(topLeft, size * scale), image, uv);
                GUI.color = previous;
            }

            Handles.BeginGUI();
            DrawGrid(area);
            DrawJoinGuide(area.size);
            DrawTracePath(area.size);

            if (UsesBezier && selected >= 0 && selected < settings.Anchors.Count)
            {
                BezierWallAnchor anchor = settings.Anchors[selected];
                if (HasIncomingHandle(selected))
                    DrawHandle(anchor.Position, anchor.Incoming, area.size);
                if (HasOutgoingHandle(selected))
                    DrawHandle(anchor.Position, anchor.Outgoing, area.size);
            }

            for (int i = 0; i < settings.Anchors.Count; i++)
            {
                Vector2 point = ToScreen(settings.Anchors[i].Position, area.size);
                DrawDot(point, i == selected ? 6.5f : 5f, i == selected ? Color.white : CurveColor);
            }

            Handles.EndGUI();
            if (traceError != null)
                DrawCanvasHint(traceError, area);
            else if (settings.Anchors.Count == 0)
                DrawCanvasHint("沿着墙脚轮廓点击放置描点。放置两个以上描点后即可预览。", area);
        }

        private void DrawJoinGuide(Vector2 areaSize)
        {
            if (settings.Closed || joinDraft == null || joinDraft.Settings.Anchors.Count < 2)
                return;

            List<BezierWallAnchor> anchors = joinDraft.Settings.Anchors;
            Vector2 offset = joinDraft.Settings.Origin - settings.Origin;
            var color = new Color(0.51f, 0.74f, 1f, 0.75f);
            Handles.color = color;
            for (int i = 1; i < anchors.Count; i++)
            {
                Handles.DrawDottedLine(ToScreen(anchors[i - 1].Position + offset, areaSize),
                    ToScreen(anchors[i].Position + offset, areaSize), 4f);
            }

            DrawDot(ToScreen(anchors[0].Position + offset, areaSize), 4.5f, color);
            DrawDot(ToScreen(anchors[anchors.Count - 1].Position + offset, areaSize), 4.5f, color);
        }

        private void DrawTracePath(Vector2 areaSize)
        {
            if (!UsesBezier)
            {
                Handles.color = new Color(0.86f, 0.92f, 0.92f, 0.55f);
                for (int i = 1; i < settings.Anchors.Count; i++)
                {
                    Handles.DrawDottedLine(ToScreen(settings.Anchors[i - 1].Position, areaSize),
                        ToScreen(settings.Anchors[i].Position, areaSize), 4f);
                }

                if (settings.Closed && settings.Anchors.Count >= 3)
                {
                    Handles.DrawDottedLine(ToScreen(settings.Anchors[settings.Anchors.Count - 1].Position, areaSize),
                        ToScreen(settings.Anchors[0].Position, areaSize), 4f);
                }
            }

            if (traceDirty)
            {
                traceDirty = false;
                tracePath = null;
                traceAfterOpening = null;
                traceDecorations = null;
                traceError = null;
                if (settings.Anchors.Count >= 2
                    && BezierWallGeometry.TrySamplePath(settings, out List<Vector2> sampled, out traceError))
                {
                    if (BezierWallGeometry.TrySplitOpening(settings, sampled,
                        out tracePath, out traceAfterOpening, out _, out traceError))
                        BezierWallDecorations.TryPlan(settings, out traceDecorations, out traceError);
                }
            }

            if (tracePath == null)
                return;

            DrawTraceRun(tracePath, areaSize);
            if (traceAfterOpening != null)
            {
                DrawTraceRun(traceAfterOpening, areaSize);
                DrawDot(ToScreen(tracePath[tracePath.Count - 1], areaSize), 4f, HandleColor);
                DrawDot(ToScreen(traceAfterOpening[0], areaSize), 4f, HandleColor);
            }

            if (traceDecorations == null)
                return;

            foreach (BezierWallDecorationPlacement placement in traceDecorations)
            {
                Vector2 position = ToScreen(placement.Position, areaSize);
                float radians = placement.Angle * Mathf.Deg2Rad;
                var direction = new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians));
                DrawDot(position, 4f, DecorationColor);
                Handles.DrawLine(position, position + direction * 11f);
            }
        }

        private void DrawTraceRun(List<Vector2> path, Vector2 areaSize)
        {
            var points = new Vector3[path.Count];
            for (int i = 0; i < points.Length; i++)
                points[i] = ToScreen(path[i], areaSize);

            Handles.color = CanvasColor;
            Handles.DrawAAPolyLine(6f, points);
            Handles.color = CurveColor;
            Handles.DrawAAPolyLine(2.5f, points);
        }

        private void DrawGrid(Rect area)
        {
            float step = Mathf.Pow(2f, Mathf.Ceil(Mathf.Log(38f / scale, 2f)));
            float pixelStep = step * scale;
            Vector2 axis = ToScreen(Vector2.zero, area.size);
            Handles.color = GridColor;
            for (float x = Mathf.Repeat(axis.x, pixelStep); x < area.width; x += pixelStep)
                Handles.DrawLine(new Vector3(x, 0f), new Vector3(x, area.height));
            for (float y = Mathf.Repeat(axis.y, pixelStep); y < area.height; y += pixelStep)
                Handles.DrawLine(new Vector3(0f, y), new Vector3(area.width, y));
        }

        private void DrawHandle(Vector2 anchor, Vector2 offset, Vector2 size)
        {
            Vector2 start = ToScreen(anchor, size);
            Vector2 end = ToScreen(anchor + offset, size);
            Handles.color = HandleColor;
            Handles.DrawDottedLine(start, end, 4f);
            DrawDot(end, 4.5f, HandleColor);
        }

        private static void DrawDot(Vector2 position, float radius, Color color)
        {
            Handles.color = CanvasColor;
            Handles.DrawSolidDisc(position, Vector3.forward, radius + 1.5f);
            Handles.color = color;
            Handles.DrawSolidDisc(position, Vector3.forward, radius);
        }

        private void DrawPreview(Rect area)
        {
            EnsurePreview();
            Material material = settings.Material != null ? settings.Material : previewMaterial;
            if (previewMesh == null || material == null)
            {
                EditorGUI.DrawRect(area, CanvasColor);
                DrawCanvasHint(previewError ?? "未找到可用的预览材质。", area);
                return;
            }

            if (previewRenderer == null)
                previewRenderer = new PreviewRenderUtility();

            Camera camera = previewRenderer.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = CanvasColor;
            camera.orthographic = true;
            camera.orthographicSize = area.height / (scale * 2f);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 20f + previewBounds.size.z;
            camera.transform.SetPositionAndRotation(new Vector3(viewportCenter.x, viewportCenter.y, previewBounds.min.z - 10f),
                Quaternion.identity);

            previewRenderer.BeginPreview(area, GUIStyle.none);
            if (previewRoot == null)
                previewRenderer.DrawMesh(previewMesh, Matrix4x4.identity, material, 0);
            else
                SortingGroup.UpdateAllSortingGroups();
            previewRenderer.Render(allowScriptableRenderPipeline: true, updatefov: false);
            previewRenderer.EndAndDrawPreview(area);
        }

        private static void DrawCanvasHint(string message, Rect area)
        {
            var hint = new Rect(13f, 12f, Mathf.Max(20f, area.width - 26f), 42f);
            GUI.Label(hint, message, EditorStyles.wordWrappedMiniLabel);
        }

        private void HandleCanvasInput(int control, Rect area)
        {
            Event evt = Event.current;
            Vector2 mouse = evt.mousePosition;
            bool inside = area.Contains(mouse);
            if (evt.type == EventType.ScrollWheel && inside)
            {
                Vector2 before = ToWorld(mouse, area.size);
                scale = Mathf.Clamp(scale * Mathf.Exp(-evt.delta.y * 0.07f), 4f, 1600f);
                viewportCenter += before - ToWorld(mouse, area.size);
                evt.Use();
                Repaint();
            }

            if (evt.type == EventType.MouseDown && inside && (evt.button == 2 || activeTab == 0 && evt.button == 0))
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = control;
                GUIUtility.hotControl = control;
                dragControl = control;
                if (evt.button == 2)
                    dragMode = DragMode.Pan;
                else
                    BeginTraceDrag(mouse, area.size);

                evt.Use();
                Repaint();
            }

            if (evt.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            {
                if (dragMode == DragMode.Pan)
                    viewportCenter += new Vector2(-evt.delta.x, evt.delta.y) / scale;
                else if (dragMode != DragMode.None)
                    MoveSelected(ToWorld(mouse, area.size));

                evt.Use();
                Repaint();
            }

            if (evt.type == EventType.MouseUp && GUIUtility.hotControl == control)
            {
                FinishDrag();
                evt.Use();
            }

            if (activeTab == 0 && evt.type == EventType.KeyDown && GUIUtility.keyboardControl == control
                && (evt.keyCode == KeyCode.Delete || evt.keyCode == KeyCode.Backspace))
            {
                DeleteSelected();
                evt.Use();
            }
        }

        private void BeginTraceDrag(Vector2 mouse, Vector2 size)
        {
            if (UsesBezier && selected >= 0)
            {
                BezierWallAnchor anchor = settings.Anchors[selected];
                if (HasIncomingHandle(selected) && NearHandle(mouse, anchor, anchor.Incoming, size))
                {
                    dragMode = DragMode.Incoming;
                    return;
                }
                if (HasOutgoingHandle(selected) && NearHandle(mouse, anchor, anchor.Outgoing, size))
                {
                    dragMode = DragMode.Outgoing;
                    return;
                }
            }

            selected = -1;
            float nearest = 11f * 11f;
            for (int i = 0; i < settings.Anchors.Count; i++)
            {
                float distance = (ToScreen(settings.Anchors[i].Position, size) - mouse).sqrMagnitude;
                if (distance >= nearest)
                    continue;
                nearest = distance;
                selected = i;
            }

            dragMode = DragMode.Anchor;
            if (selected >= 0)
                return;

            RecordDrag();
            settings.Anchors.Add(new BezierWallAnchor(ToWorld(mouse, size)));
            selected = settings.Anchors.Count - 1;
            OnShapeChanged();
        }

        private bool HasIncomingHandle(int index) => index > 0 || settings.Closed && settings.Anchors.Count >= 3;

        private bool HasOutgoingHandle(int index) =>
            index < settings.Anchors.Count - 1 || settings.Closed && settings.Anchors.Count >= 3;

        private bool NearHandle(Vector2 mouse, BezierWallAnchor anchor, Vector2 offset, Vector2 size)
        {
            return offset.sqrMagnitude > 0f
                && (mouse - ToScreen(anchor.Position + offset, size)).sqrMagnitude < 9f * 9f
                && (mouse - ToScreen(anchor.Position, size)).sqrMagnitude > 7f * 7f;
        }

        private void MoveSelected(Vector2 target)
        {
            if (selected < 0)
                return;

            RecordDrag();
            BezierWallAnchor anchor = settings.Anchors[selected];
            switch (dragMode)
            {
                case DragMode.Anchor:
                    anchor.Position = target;
                    break;
                case DragMode.Incoming:
                    anchor.Incoming = target - anchor.Position;
                    anchor.Automatic = false;
                    break;
                case DragMode.Outgoing:
                    anchor.Outgoing = target - anchor.Position;
                    anchor.Automatic = false;
                    break;
            }

            OnShapeChanged();
        }

        private void RecordDrag()
        {
            if (dragGroup < 0)
            {
                Undo.IncrementCurrentGroup();
                dragGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("调整弧形墙描点");
            }

            Undo.RecordObject(this, "调整弧形墙描点");
        }

        private void FinishDrag()
        {
            if (dragControl != 0 && GUIUtility.hotControl == dragControl)
                GUIUtility.hotControl = 0;
            if (dragGroup >= 0)
                Undo.CollapseUndoOperations(dragGroup);

            dragGroup = -1;
            dragControl = 0;
            dragMode = DragMode.None;
        }

        private void DeleteSelected()
        {
            if (selected < 0 || selected >= settings.Anchors.Count)
                return;

            Undo.RecordObject(this, "删除弧形墙描点");
            settings.Anchors.RemoveAt(selected);
            selected = -1;
            OnShapeChanged();
        }

        private void LoadDraft(BezierWallLayout draft)
        {
            Undo.RecordObject(this, "打开弧形墙草稿");
            currentDraft = draft;
            if (draft != null)
            {
                settings = draft.Settings.Copy();
                joinDraft = null;
                selected = -1;
                activeTab = 0;
                frameRequested = true;
                OnShapeChanged();
            }
        }

        private void NewDraft()
        {
            Undo.RecordObject(this, "新建弧形墙草稿");
            currentDraft = null;
            joinDraft = null;
            settings = new BezierWallSettings();
            selected = -1;
            activeTab = 0;
            frameRequested = true;
            OnShapeChanged();
        }

        private void SaveDraft()
        {
            string path = EditorUtility.SaveFilePanelInProject("另存弧形墙草稿", "弧形墙草稿", "asset", "选择草稿的保存位置。");
            if (string.IsNullOrEmpty(path))
                return;
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            {
                Notify("该路径已有资产，请选择另一个文件名。");
                return;
            }

            Undo.RecordObject(this, "另存弧形墙草稿");
            currentDraft = BezierWallAssets.SaveLayout(settings, path);
            EditorGUIUtility.PingObject(currentDraft);
        }

        private void Bake()
        {
            string name = currentDraft != null ? currentDraft.name + " Wall" : "Bezier Wall";
            string path = EditorUtility.SaveFilePanelInProject("保存弧形墙 Mesh", name, "asset", "选择 Mesh 和生成材质的保存位置。");
            if (string.IsNullOrEmpty(path))
                return;

            if (BezierWallAssets.TryBake(settings, path, Path.GetFileNameWithoutExtension(path), out GameObject wall, out string error))
            {
                Selection.activeGameObject = wall;
                if (currentDraft != null && !string.IsNullOrEmpty(wall.scene.path))
                {
                    if (BezierWallBakeBindings.TryAssociate(wall, currentDraft, settings, out string associationError))
                        Notify("弧形墙已烘焙并关联草稿。");
                    else
                        Notify($"墙体已烘焙。{associationError}");
                }
                else
                    Notify("弧形墙已烘焙到当前场景。");
            }
            else
                Notify(error);
        }

        private void OnShapeChanged()
        {
            if (UsesBezier)
                BezierWallGeometry.UpdateAutomaticTangents(settings.Anchors, settings.Closed);
            InvalidatePreviews();
        }

        private void InvalidatePreviews()
        {
            traceDirty = true;
            previewDirty = true;
            Repaint();
        }

        private void EnsurePreview()
        {
            if (!previewDirty)
                return;

            previewDirty = false;
            ReleasePreview();
            if (!BezierWallDecorations.TryPlan(settings, out IReadOnlyList<BezierWallDecorationPlacement> placements, out previewError)
                || !BezierWallAssets.TryBuild(settings, out previewMesh, out _, out previewError))
                return;

            if (settings.Material == null)
                previewMaterial = BezierWallAssets.CreateCartoonMaterial(settings);

            previewBounds = previewMesh.bounds;
            Material material = settings.Material != null ? settings.Material : previewMaterial;
            if (material == null || placements.Count == 0)
                return;

            if (previewRenderer == null)
                previewRenderer = new PreviewRenderUtility();

            previewRoot = EditorUtility.CreateGameObjectWithHideFlags("弧形墙预览", HideFlags.HideAndDontSave,
                typeof(MeshFilter), typeof(MeshRenderer));
            previewRenderer.AddSingleGO(previewRoot);
            previewRoot.GetComponent<MeshFilter>().sharedMesh = previewMesh;
            BezierWallAssets.ConfigureWallRenderer(settings, previewRoot.GetComponent<MeshRenderer>(), material);

            try
            {
                BezierWallAssets.AddDecorations(settings, placements, previewRoot.transform);
            }
            catch (System.Exception exception)
            {
                ReleasePreview();
                previewError = $"无法预览沿墙摆件。{exception.Message}";
                return;
            }

            foreach (Renderer renderer in previewRoot.GetComponentsInChildren<Renderer>())
            {
                if (renderer.enabled)
                    previewBounds.Encapsulate(renderer.bounds);
            }
        }

        private void ReleasePreview()
        {
            if (previewRoot != null)
                DestroyImmediate(previewRoot);
            if (previewMesh != null)
                DestroyImmediate(previewMesh);
            if (previewMaterial != null)
                DestroyImmediate(previewMaterial);

            previewRoot = null;
            previewMesh = null;
            previewMaterial = null;
        }

        private bool TryReference(out Texture2D image, out Rect uv, out Vector2 size)
        {
            size = Vector2.zero;
            if (!BezierWallAssets.TryGetImage(settings.Reference, out image, out uv)
                || settings.ReferenceWidth <= 0f || float.IsInfinity(settings.ReferenceWidth)
                || float.IsNaN(settings.ReferenceWidth))
                return false;

            size.x = settings.ReferenceWidth;
            size.y = size.x * image.height * uv.height / (image.width * uv.width);
            return true;
        }

        private void Frame(Vector2 area)
        {
            var bounds = new Bounds();
            bool found = false;
            if (activeTab == 1)
            {
                EnsurePreview();
                if (previewMesh != null)
                {
                    bounds = previewBounds;
                    found = true;
                }
            }

            if (activeTab == 0 && TryReference(out _, out _, out Vector2 size))
            {
                bounds = new Bounds(settings.ReferenceCenter, size);
                found = true;
            }

            if (activeTab == 0)
            {
                foreach (BezierWallAnchor anchor in settings.Anchors)
                {
                    if (!found)
                    {
                        bounds = new Bounds(anchor.Position, Vector3.zero);
                        found = true;
                    }

                    bounds.Encapsulate(anchor.Position);
                    if (UsesBezier)
                    {
                        bounds.Encapsulate(anchor.Position + anchor.Incoming);
                        bounds.Encapsulate(anchor.Position + anchor.Outgoing);
                    }
                }
            }

            if (activeTab == 0 && !settings.Closed && joinDraft != null)
            {
                Vector2 offset = joinDraft.Settings.Origin - settings.Origin;
                foreach (BezierWallAnchor anchor in joinDraft.Settings.Anchors)
                {
                    Vector2 position = anchor.Position + offset;
                    if (!found)
                    {
                        bounds = new Bounds(position, Vector3.zero);
                        found = true;
                    }

                    bounds.Encapsulate(position);
                }
            }

            viewportCenter = found ? (Vector2)bounds.center : Vector2.zero;
            Vector2 extent = found ? (Vector2)bounds.size : new Vector2(8f, 5f);
            scale = Mathf.Clamp(Mathf.Min((area.x - 44f) / Mathf.Max(0.8f, extent.x),
                (area.y - 44f) / Mathf.Max(0.8f, extent.y)), 4f, 1600f);
        }

        private Vector2 ToScreen(Vector2 point, Vector2 size)
        {
            Vector2 delta = (point - viewportCenter) * scale;
            return size * 0.5f + new Vector2(delta.x, -delta.y);
        }

        private Vector2 ToWorld(Vector2 point, Vector2 size)
        {
            Vector2 delta = (point - size * 0.5f) / scale;
            return viewportCenter + new Vector2(delta.x, -delta.y);
        }

        private void Notify(string message)
        {
            ShowNotification(new GUIContent(message), 4d);
        }
    }
}
