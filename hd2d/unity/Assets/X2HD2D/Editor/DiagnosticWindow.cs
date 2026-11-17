using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using X2HD2D.Core;
using X2HD2D.Unity;

namespace X2HD2D.Editor
{
    public sealed class DiagnosticWindow : EditorWindow
    {
        private CaptureScene scene;
        private string error, sourceName;
        private int line = 1, bg, cellX, cellY, slot;
        private Vector2 scroll;
        private double lastRender;
        private Texture2D tilePreview;
        [MenuItem("X2 HD-2D/Graphics inspector")]
        public static void Open() => GetWindow<DiagnosticWindow>("X2 Graphics");
        private ExperimentStage stage = ExperimentStage.NormalMap;
        private void OnEnable() { EditorApplication.update += Tick; Selection.selectionChanged += SyncHierarchySelection; EditorApplication.playModeStateChanged += OnPlayModeState; }
        private void OnDisable() {
            EditorApplication.update -= Tick; Selection.selectionChanged -= SyncHierarchySelection; EditorApplication.playModeStateChanged -= OnPlayModeState;
            DiagnosticEditMode.DeferDestroy(tilePreview); tilePreview = null;
        }
        private void OnPlayModeState(PlayModeStateChange state)
        {
            // No scene manipulation or resource destruction inside this callback.
            DiagnosticEditMode.DeferDestroy(tilePreview); tilePreview = null;
            scene = null; sourceName = null; error = null; Repaint();
        }
        public static bool TryGetOamSelection(CaptureScene target, GameObject selected, out int selectedSlot)
        {
            selectedSlot = -1;
            if (!DiagnosticEditMode.IsAvailable || !target || target.Frame == null || target.ObjectRoots == null || !selected) return false;
            // Selecting either the OAM root or its decoded/wrapped child works.
            var source = selected.GetComponentInParent<GraphicsSource>(true);
            if (!source || source.componentKind != "OAM piece" || source.sourceIndex < 0 || source.sourceIndex >= 128 ||
                source.transform != target.ObjectRoots[source.sourceIndex]) return false;
            selectedSlot = source.sourceIndex; return true;
        }
        private void SyncHierarchySelection()
        {
            if (!DiagnosticEditMode.IsAvailable) return;
            if (!scene) scene = FindAnyObjectByType<CaptureScene>();
            if (TryGetOamSelection(scene, Selection.activeGameObject, out int selected)) { slot = selected; Repaint(); }
        }
        private void Tick()
        {
            if (!DiagnosticEditMode.IsAvailable) return;
            if (!scene) scene = FindAnyObjectByType<CaptureScene>();
            if (!scene || scene.Frame == null || EditorApplication.timeSinceStartup - lastRender < 0.1) return;
            lastRender = EditorApplication.timeSinceStartup;
            try { scene.RenderViews(); if (error != null && error.StartsWith("URP render request:")) error = null; }
            catch (Exception e) { error = "URP render request: " + e.Message; }
            Repaint();
        }
        private void Import()
        {
            if (!DiagnosticEditMode.IsAvailable) { error = DiagnosticEditMode.Message; return; }
            string path = EditorUtility.OpenFilePanel("Read private runtime .x2scan (not copied into Assets)", "", "x2scan");
            if (string.IsNullOrEmpty(path)) return;
            try { var capture = ScanoutReader.ReadFile(path); scene.Load(capture); line = 1; slot = scene.ExperimentSlot; stage = scene.LightingStage; sourceName = Path.GetFileName(path); error = null; }
            catch (Exception e) { error = e.Message; }
        }
        private static void View(string title, Texture image, float width)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(width)); GUILayout.Label(title, EditorStyles.boldLabel);
            Rect rect = GUILayoutUtility.GetRect(width, width * 224 / 256, GUILayout.Width(width));
            if (image) GUI.DrawTexture(rect, image, ScaleMode.ScaleToFit, false);
            EditorGUILayout.EndVertical();
        }
        private void OnGUI()
        {
            if (!DiagnosticEditMode.IsAvailable) {
                EditorGUILayout.HelpBox(DiagnosticEditMode.Message, MessageType.Warning);
                return; // No scene switching, imports, renders, exports or visibility changes.
            }
            if (!scene) scene = FindAnyObjectByType<CaptureScene>();
            if (!scene) { EditorGUILayout.HelpBox("Open the diagnostic scene to inspect independent BG and OAM components.", MessageType.Info); if (GUILayout.Button("Open diagnostic scene")) ProjectSetup.OpenScene(); return; }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            GUILayout.Label("Offline graphics reconstruction", EditorStyles.boldLabel);
            if (GUILayout.Button("Import private .x2scan (read only)")) Import();
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (scene.Capture == null) { EditorGUILayout.HelpBox("Import an original synthetic fixture or a private gameplay capture outside Assets. Reimport after exiting Play Mode; transient graphics are cleared safely in Edit Mode. Nothing is copied into the project.", MessageType.Info); EditorGUILayout.EndScrollView(); return; }
            GUILayout.Label((sourceName ?? "Capture") + " | session " + scene.Capture.Session + " | sequence " + scene.Capture.Sequence);
            if (!scene.Frame.IsSupported) EditorGUILayout.HelpBox("INCOMPLETE — selected-state component inspection, not full-frame reconstruction.\n" + string.Join("\n", scene.Frame.UnsupportedReasons), MessageType.Warning);
            else EditorGUILayout.HelpBox("Supported static Mode-1 profile. Use Export comparison to measure Unity GPU fidelity; supported does not mean already verified.", MessageType.Info);
            int selected = EditorGUILayout.IntSlider("State before scanline", line, 1, 224);
            if (selected != line) { line = selected; scene.Load(scene.Capture, line); slot = scene.ExperimentSlot; stage = scene.LightingStage; }
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < 3; i++) scene.BackgroundVisible[i] = GUILayout.Toggle(scene.BackgroundVisible[i], "BG" + (i + 1));
            scene.ObjectsVisible = GUILayout.Toggle(scene.ObjectsVisible, "OAM"); scene.ApplyVisibility();
            if (GUILayout.Button("Restore main-screen visibility")) { scene.BackgroundVisible = new[] { true, true, true }; scene.ObjectsVisible = true; scene.ApplyVisibility(); }
            EditorGUILayout.EndHorizontal();
            float width = Mathf.Max(128, (position.width - 40) / 3);
            EditorGUILayout.BeginHorizontal();
            View("Original reference", scene.ReferenceView, width);
            View("Unity components (unlit)", scene.BaselineView, width);
            View("EXPERIMENT — " + scene.LightingStage, scene.ExperimentHasRendered ? scene.ExperimentView : null, width);
            EditorGUILayout.EndHorizontal();
            GUILayout.Label($"Experiment OAM {scene.ExperimentSlot}: {scene.ExperimentOpaquePixels} opaque source pixels; GPU nonblack pixels: {(scene.ExperimentHasRendered ? scene.ExperimentNonblackPixels.ToString() : "not rendered")}");
            foreach (string issue in scene.ExperimentDiagnostics()) EditorGUILayout.HelpBox(issue, MessageType.Warning);
            if (GUILayout.Button("Export private GPU/reference/component comparison"))
                try { string output = ComparisonExport.Export(scene); EditorUtility.RevealInFinder(output); }
                catch (Exception e) { error = e.Message; }
            GUILayout.Space(12); GUILayout.Label("Source graphics", EditorStyles.boldLabel);
            bg = EditorGUILayout.IntSlider("Background", bg, 0, 2);
            var plane = scene.Frame.Backgrounds[bg * 2];
            cellX = EditorGUILayout.IntSlider("Map cell X", cellX, 0, plane.MapWidth - 1);
            cellY = EditorGUILayout.IntSlider("Map cell Y", cellY, 0, plane.MapHeight - 1);
            int entry = plane.Tilemap[cellY * plane.MapWidth + cellX];
            EditorGUILayout.SelectableLabel($"BG{bg + 1}: {plane.BitsPerPixel}bpp, {plane.TileSize}px tiles, {plane.MapWidth}×{plane.MapHeight} map; scroll {plane.ScrollX},{plane.ScrollY}\nMap word base ${plane.MapBaseWord:X4}, tile word base ${plane.TileBaseWord:X4}; cell ${entry:X4}: tile {entry & 1023}, palette {(entry >> 10) & 7}, priority {(entry >> 13) & 1}, H={(entry & 0x4000) != 0}, V={(entry & 0x8000) != 0}", GUILayout.Height(42));
            if (GUILayout.Button("Preview source 8×8 tile (unflipped)"))
            {
                byte[] pixels = new byte[8 * 8 * 4]; var state = scene.Frame.State;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) {
                    int index = GraphicsDecoder.PlanarPixel(state.Vram, plane.TileBaseWord, entry & 1023, plane.BitsPerPixel, x, y);
                    if (index != 0) Buffer.BlockCopy(GraphicsDecoder.PaletteColor(state.Cgram, ((entry >> 10) & 7) * (1 << plane.BitsPerPixel) + index, state.Get("inidisp") & 15), 0, pixels, (y * 8 + x) * 4, 4);
                }
                if (tilePreview) DestroyImmediate(tilePreview); tilePreview = scene.PreviewTexture(pixels, 8, 8);
            }
            if (tilePreview) GUILayout.Label(tilePreview, GUILayout.Width(64), GUILayout.Height(64));
            slot = EditorGUILayout.IntSlider("OAM hardware slot", slot, 0, 127);
            var piece = scene.Frame.Pieces[slot];
            EditorGUILayout.SelectableLabel($"slot {slot}, evaluation rank {piece.EvaluationOrder}, XY {piece.X},{piece.Y}, {piece.Size}×{piece.Size}, tile {piece.TileNumber}, palette {piece.Palette}, priority {piece.Priority}, flip {piece.FlipX}/{piece.FlipY}\nHardware graphics piece only; no gameplay entity identity is inferred.", GUILayout.Height(40));
            GUILayout.Label("Diagnose in order: Unlit → Global Light → Point Light → Normal Map");
            var nextStage = (ExperimentStage)EditorGUILayout.EnumPopup("Experiment stage", stage);
            if (nextStage != stage) { stage = nextStage; scene.BuildExperiment(slot, stage); }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select piece in Hierarchy")) Selection.activeGameObject = scene.ObjectRoots[slot].gameObject;
            if (GUILayout.Button("Light this piece in experiment")) scene.BuildExperiment(slot, stage);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Hierarchy selection updates the OAM inspector; click Light this piece to apply it. The experiment OAM label identifies the piece currently rendered. Unlit/Global/Point stages use built-in materials; a treatment material override is used only in Normal Map.", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }
    }
}
