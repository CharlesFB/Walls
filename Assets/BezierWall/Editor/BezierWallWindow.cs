using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MagicTavern.BezierWall.Editor
{
    internal sealed class BezierWallWindow : EditorWindow
    {
        private static readonly Color CanvasColor = new Color32(31, 37, 44, 255);
        private static readonly Color GridColor = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color CurveColor = new Color32(115, 232, 200, 255);
        private static readonly Color HandleColor = new Color32(255, 202, 117, 255);
        private static readonly string[] Tabs = { "描点", "成品预览" };

        [SerializeField] private BezierWallSettings settings = new BezierWallSettings();
        [SerializeField] private BezierWallLayout currentDraft;
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
        private string traceError;
        private bool previewDirty = true;
        private string previewError;
        private Mesh previewMesh;
        private Material previewMaterial;
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
                DrawImageField(data, nameof(settings.SurfaceTexture), "墙面贴图");
                EditorGUILayout.PropertyField(data.FindPropertyRelative(nameof(settings.TileSize)), new GUIContent("贴图平铺尺寸"));
            }

            EditorGUILayout.PropertyField(material, new GUIContent("自定义材质"));

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
                        if (selected > 0)
                            EditorGUILayout.PropertyField(point.FindPropertyRelative(nameof(BezierWallAnchor.Incoming)), new GUIContent("入控制柄偏移"));
                        if (selected < settings.Anchors.Count - 1)
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
            EditorGUILayout.Space(7f);
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
            DrawTracePath(area.size);

            if (UsesBezier && selected >= 0 && selected < settings.Anchors.Count)
            {
                BezierWallAnchor anchor = settings.Anchors[selected];
                if (selected > 0)
                    DrawHandle(anchor.Position, anchor.Incoming, area.size);
                if (selected < settings.Anchors.Count - 1)
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
            }

            if (traceDirty)
            {
                traceDirty = false;
                tracePath = null;
                traceAfterOpening = null;
                traceError = null;
                if (settings.Anchors.Count >= 2
                    && BezierWallGeometry.TrySamplePath(settings, out List<Vector2> sampled, out traceError))
                {
                    BezierWallGeometry.TrySplitOpening(settings, sampled,
                        out tracePath, out traceAfterOpening, out _, out traceError);
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
            camera.farClipPlane = 20f + previewMesh.bounds.size.z;
            camera.transform.SetPositionAndRotation(new Vector3(viewportCenter.x, viewportCenter.y, previewMesh.bounds.min.z - 10f),
                Quaternion.identity);

            previewRenderer.BeginPreview(area, GUIStyle.none);
            previewRenderer.DrawMesh(previewMesh, Matrix4x4.identity, material, 0);
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
                if (selected > 0 && NearHandle(mouse, anchor, anchor.Incoming, size))
                {
                    dragMode = DragMode.Incoming;
                    return;
                }
                if (selected < settings.Anchors.Count - 1 && NearHandle(mouse, anchor, anchor.Outgoing, size))
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
                Notify("弧形墙已烘焙到当前场景。");
            }
            else
                Notify(error);
        }

        private void OnShapeChanged()
        {
            if (UsesBezier)
                BezierWallGeometry.UpdateAutomaticTangents(settings.Anchors);
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
            if (BezierWallAssets.TryBuild(settings, out previewMesh, out _, out previewError) && settings.Material == null)
                previewMaterial = BezierWallAssets.CreateCartoonMaterial(settings);
        }

        private void ReleasePreview()
        {
            if (previewMesh != null)
                DestroyImmediate(previewMesh);
            if (previewMaterial != null)
                DestroyImmediate(previewMaterial);

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
                    bounds = previewMesh.bounds;
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
