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
        private void OnEnable() => EditorApplication.update += Tick;
        private void OnDisable() { EditorApplication.update -= Tick; if (tilePreview) DestroyImmediate(tilePreview); }
        private void Tick()
        {
            if (!scene) scene = FindAnyObjectByType<CaptureScene>();
            if (!scene || scene.Frame == null || EditorApplication.timeSinceStartup - lastRender < 0.1) return;
            lastRender = EditorApplication.timeSinceStartup;
            try { scene.RenderViews(); if (error != null && error.StartsWith("URP render request:")) error = null; }
            catch (Exception e) { error = "URP render request: " + e.Message; }
            Repaint();
        }
        private void Import()
        {
            string path = EditorUtility.OpenFilePanel("Read private runtime .x2scan (not copied into Assets)", "", "x2scan");
            if (string.IsNullOrEmpty(path)) return;
            try { var capture = ScanoutReader.ReadFile(path); scene.Load(capture); line = 1; sourceName = Path.GetFileName(path); error = null; }
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
            if (!scene) scene = FindAnyObjectByType<CaptureScene>();
            if (!scene) { EditorGUILayout.HelpBox("Open the diagnostic scene to inspect independent BG and OAM components.", MessageType.Info); if (GUILayout.Button("Open diagnostic scene")) ProjectSetup.OpenScene(); return; }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            GUILayout.Label("Offline graphics reconstruction", EditorStyles.boldLabel);
            if (GUILayout.Button("Import private .x2scan (read only)")) Import();
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (scene.Capture == null) { EditorGUILayout.HelpBox("Import an original synthetic fixture or a private gameplay capture outside Assets. Nothing is copied into the project.", MessageType.Info); EditorGUILayout.EndScrollView(); return; }
            GUILayout.Label((sourceName ?? "Capture") + " | session " + scene.Capture.Session + " | sequence " + scene.Capture.Sequence);
            if (!scene.Frame.IsSupported) EditorGUILayout.HelpBox("INCOMPLETE — selected-state component inspection, not full-frame reconstruction.\n" + string.Join("\n", scene.Frame.UnsupportedReasons), MessageType.Warning);
            else EditorGUILayout.HelpBox("Supported static Mode-1 profile. Use Export comparison to measure Unity GPU fidelity; supported does not mean already verified.", MessageType.Info);
            int selected = EditorGUILayout.IntSlider("State before scanline", line, 1, 224);
            if (selected != line) { line = selected; scene.Load(scene.Capture, line); }
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < 3; i++) scene.BackgroundVisible[i] = GUILayout.Toggle(scene.BackgroundVisible[i], "BG" + (i + 1));
            scene.ObjectsVisible = GUILayout.Toggle(scene.ObjectsVisible, "OAM"); scene.ApplyVisibility();
            if (GUILayout.Button("Restore main-screen visibility")) { scene.BackgroundVisible = new[] { true, true, true }; scene.ObjectsVisible = true; scene.ApplyVisibility(); }
            EditorGUILayout.EndHorizontal();
            float width = Mathf.Max(128, (position.width - 40) / 3);
            EditorGUILayout.BeginHorizontal();
            View("Original reference", scene.ReferenceView, width);
            View("Unity components (unlit)", scene.BaselineView, width);
            View("EXPERIMENT — normal + Light2D", scene.ExperimentView, width);
            EditorGUILayout.EndHorizontal();
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
                if (tilePreview) DestroyImmediate(tilePreview); tilePreview = CaptureScene.Texture(pixels, 8, 8);
            }
            if (tilePreview) GUILayout.Label(tilePreview, GUILayout.Width(64), GUILayout.Height(64));
            slot = EditorGUILayout.IntSlider("OAM hardware slot", slot, 0, 127);
            var piece = scene.Frame.Pieces[slot];
            EditorGUILayout.SelectableLabel($"slot {slot}, evaluation rank {piece.EvaluationOrder}, XY {piece.X},{piece.Y}, {piece.Size}×{piece.Size}, tile {piece.TileNumber}, palette {piece.Palette}, priority {piece.Priority}, flip {piece.FlipX}/{piece.FlipY}\nHardware graphics piece only; no gameplay entity identity is inferred.", GUILayout.Height(40));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select piece in Hierarchy")) Selection.activeGameObject = scene.ObjectRoots[slot].gameObject;
            if (GUILayout.Button("Light this piece in experiment")) scene.BuildExperiment(slot);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }
    }
}
