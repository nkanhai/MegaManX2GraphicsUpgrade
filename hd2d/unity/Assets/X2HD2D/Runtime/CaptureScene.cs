using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using X2HD2D.Core;
using Object = UnityEngine.Object;

namespace X2HD2D.Unity
{
    public enum ExperimentStage { Unlit, GlobalLight, PointLight, NormalMap }

    [ExecuteAlways]
    public sealed class CaptureScene : MonoBehaviour
    {
        public const int BaselineLayer = 8, ExperimentLayer = 9;
        [NonSerialized] public ScanoutCapture Capture;
        [NonSerialized] public Reconstruction Frame;
        [NonSerialized] public RenderTexture BaselineView, ExperimentView;
        [NonSerialized] public Texture2D ReferenceView;
        [NonSerialized] public Camera BaselineCamera, ExperimentCamera;
        [NonSerialized] public Transform[] BackgroundRoots, ObjectRoots;
        [NonSerialized] public bool[] BackgroundVisible = { true, true, true };
        [NonSerialized] public bool ObjectsVisible = true;
        public CaptureTreatment treatment;
        private CaptureResourceSet resources;
        public Light2D ExperimentLight { get; private set; }
        public SpriteRenderer ExperimentSprite { get; private set; }
        public ExperimentStage LightingStage { get; private set; } = ExperimentStage.NormalMap;
        public int ExperimentSlot { get; private set; } = -1;
        public int ExperimentOpaquePixels { get; private set; }
        public int ExperimentNonblackPixels { get; private set; } = -1;
        public bool ExperimentHasRendered { get; private set; }
        public string ExperimentError { get; private set; }
        private int experimentSourceNonblack;
        private string experimentSetupError;
        public bool HasRendered { get; private set; }
        public bool HasFilters => !ObjectsVisible || BackgroundVisible.Any(v => !v);

        private CaptureResourceSet ResourceSet
        {
            get {
                DiagnosticEditMode.Require("Create diagnostic resources");
                if (!resources) {
                    var node = new GameObject("Transient diagnostic resource ownership") { hideFlags = HideFlags.DontSave };
                    node.transform.SetParent(transform, false);
                    resources = node.AddComponent<CaptureResourceSet>(); resources.hideFlags = HideFlags.DontSave;
                }
                return resources;
            }
        }
        private T Own<T>(T value, bool experiment = false) where T : Object
        { value.hideFlags = HideFlags.DontSave; (experiment ? ResourceSet.Experiment : ResourceSet.Baseline).Add(value); return value; }
        private GameObject Node(string title, Transform parent, int layer, bool experiment = false)
        { var node = Own(new GameObject(title), experiment); node.layer = layer; node.transform.SetParent(parent, false); return node; }
        private static void Release(List<Object> items)
        {
            DiagnosticEditMode.Require("Release diagnostic resources");
            // Only called from stable Edit Mode, never OnDisable/OnDestroy.
            // SetActive during Unity's activation callbacks causes assertions.
            foreach (Object item in items) if (item) DestroyImmediate(item);
            items.Clear();
        }
        internal Object[] OwnedResourceSnapshot()
        {
            return GetComponentsInChildren<CaptureResourceSet>(true)
                .SelectMany(set => set.Baseline.Concat(set.Experiment).Append((Object)set.gameObject)).ToArray();
        }
        public void Clear()
        {
            DiagnosticEditMode.Require("Clear diagnostic capture");
            DiagnosticEditMode.CancelCleanup(this);
            foreach (var set in GetComponentsInChildren<CaptureResourceSet>(true)) {
                Release(set.Experiment); Release(set.Baseline); DestroyImmediate(set.gameObject);
            }
            resources = null; Capture = null; Frame = null;
            ExperimentLight = null; ExperimentSprite = null; ExperimentSlot = -1;
            ExperimentError = experimentSetupError = null; ExperimentHasRendered = false; ExperimentNonblackPixels = -1;
            BaselineView = ExperimentView = null; ReferenceView = null; HasRendered = false;
            BaselineCamera = ExperimentCamera = null; BackgroundRoots = ObjectRoots = null;
        }
        private void OnDisable() => DiagnosticEditMode.DeferCleanup(this);
        private void OnDestroy() => DiagnosticEditMode.DeferCleanup(this);
        private void OnEnable()
        {
            if (Frame == null && GetComponentInChildren<CaptureResourceSet>(true)) DiagnosticEditMode.DeferCleanup(this);
        }

        public static Texture2D Texture(byte[] topDown, int width, int height)
        {
            DiagnosticEditMode.Require("Create diagnostic texture");
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true) {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            byte[] bottomUp = new byte[topDown.Length];
            for (int y = 0; y < height; y++) Buffer.BlockCopy(topDown, y * width * 4, bottomUp, (height - 1 - y) * width * 4, width * 4);
            texture.LoadRawTextureData(bottomUp); texture.Apply(false, false); return texture;
        }
        public Texture2D PreviewTexture(byte[] topDown, int width, int height) => Own(Texture(topDown, width, height));
        private SpriteRenderer Quad(string name, Transform parent, int layer, byte[] pixels, int width, int height,
            float x, float y, int order, Material material, bool experiment = false, Texture2D normal = null)
        {
            var texture = Own(Texture(pixels, width, height), experiment);
            var secondary = normal ? new[] { new SecondarySpriteTexture { name = "_NormalMap", texture = normal } } : null;
            var sprite = Own(Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0, 1), 1, 0,
                SpriteMeshType.FullRect, Vector4.zero, false, secondary), experiment);
            var node = Node(name, parent, layer, experiment);
            node.transform.localPosition = new Vector3(x, -y, 0);
            var renderer = node.AddComponent<SpriteRenderer>(); renderer.sprite = sprite;
            renderer.sharedMaterial = material; renderer.sortingOrder = order;
            return renderer;
        }
        private Camera ViewCamera(string name, int layer, out RenderTexture texture)
        {
            texture = Own(new RenderTexture(256, 224, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) {
                filterMode = FilterMode.Point, antiAliasing = 1, useMipMap = false });
            if (!texture.Create()) throw new InvalidOperationException("Cannot create diagnostic render target");
            var camera = Node(name, transform, layer).AddComponent<Camera>();
            camera.transform.localPosition = new Vector3(128, -112, -10); camera.orthographic = true;
            camera.orthographicSize = 112; camera.aspect = 256f / 224f; camera.nearClipPlane = 0.1f; camera.farClipPlane = 100;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.cullingMask = 1 << layer; camera.allowHDR = false; camera.allowMSAA = false;
            camera.targetTexture = texture; camera.enabled = false;
            var data = camera.GetUniversalAdditionalCameraData(); data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None; data.SetRenderer(0);
            return camera;
        }
        private Material Material(string shaderName, bool experiment = false)
        {
            Shader shader = Shader.Find(shaderName);
            if (!shader) throw new InvalidOperationException("Missing shader: " + shaderName + "; initialize the pinned URP project first");
            return Own(new Material(shader), experiment);
        }
        public void Load(ScanoutCapture capture, int line = 1)
        {
            DiagnosticEditMode.Require("Import diagnostic capture");
            Clear(); Capture = capture; Frame = GraphicsDecoder.Decode(capture, line);
            BackgroundVisible = new[] { true, true, true }; ObjectsVisible = true;
            BaselineCamera = ViewCamera("Baseline camera (unlit, no post processing)", BaselineLayer, out BaselineView);
            ExperimentCamera = ViewCamera("Experiment camera (isolated)", ExperimentLayer, out ExperimentView);
            ReferenceView = Own(Texture(capture.CopyReferenceRgba(), 256, 224));
            Material unlit = Material("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            byte[] backdrop = new byte[ScanoutReader.RgbaBytes];
            byte[] backdropColor = Frame.ForcedBlank ? new byte[] {0,0,0,255} : Frame.Backdrop;
            for (int i = 0; i < backdrop.Length; i += 4) Buffer.BlockCopy(backdropColor, 0, backdrop, i, 4);
            Quad("Backdrop", transform, BaselineLayer, backdrop, 256, 224, 0, 0, -1, unlit);
            BackgroundRoots = Enumerable.Range(0, 3).Select(i => Node("BG" + (i + 1), transform, BaselineLayer).transform).ToArray();
            foreach (var bg in Frame.Backgrounds)
            {
                var quad = Quad("Priority " + bg.Priority, BackgroundRoots[bg.Layer], BaselineLayer, bg.Rgba, 256, 224, 0, 0, bg.SortOrder, unlit);
                var source = quad.gameObject.AddComponent<GraphicsSource>(); source.componentKind = "Background tilemap";
                source.sourceIndex = bg.Layer; source.priority = bg.Priority; source.tileSize = bg.TileSize;
                source.scrollX = bg.ScrollX; source.scrollY = bg.ScrollY; source.mapWidth = bg.MapWidth; source.mapHeight = bg.MapHeight;
                source.mapBaseWord = bg.MapBaseWord; source.tileBaseWord = bg.TileBaseWord; source.enabledOnMain = bg.MainEnabled;
                quad.enabled = bg.MainEnabled && !Frame.ForcedBlank;
            }
            var objects = Node("OAM graphics pieces (not entities)", transform, BaselineLayer).transform;
            ObjectRoots = new Transform[128];
            foreach (var piece in Frame.Pieces)
            {
                var root = ObjectRoots[piece.Slot] = Node("OAM " + piece.Slot.ToString("D3"), objects, BaselineLayer).transform;
                var source = root.gameObject.AddComponent<GraphicsSource>(); source.componentKind = "OAM piece";
                source.sourceIndex = piece.Slot; source.evaluationOrder = piece.EvaluationOrder; source.priority = piece.Priority;
                source.tileNumber = piece.TileNumber; source.tileBaseWord = piece.TileBaseWord; source.palette = piece.Palette;
                source.x = piece.X; source.y = piece.Y; source.tileSize = piece.Size; source.flipX = piece.FlipX; source.flipY = piece.FlipY; source.enabledOnMain = piece.MainEnabled;
                var quad = Quad("Decoded piece", root, BaselineLayer, piece.VisibleRgba, piece.Size, piece.Size, piece.X, piece.Y, piece.SortOrder, unlit);
                quad.enabled = piece.MainEnabled && !Frame.ForcedBlank;
                if (piece.Y + piece.Size > 256)
                {
                    var wrap = Quad("Vertical wrap", root, BaselineLayer, piece.VisibleRgba, piece.Size, piece.Size, piece.X, piece.Y - 256, piece.SortOrder, unlit);
                    wrap.enabled = quad.enabled;
                }
            }
            BuildExperiment();
        }
        public void ApplyVisibility()
        {
            DiagnosticEditMode.Require("Change diagnostic visibility");
            if (Frame == null) return;
            for (int i = 0; i < 3; i++) BackgroundRoots[i].gameObject.SetActive(BackgroundVisible[i]);
            foreach (var root in ObjectRoots) root.parent.gameObject.SetActive(ObjectsVisible);
        }
        public void BuildExperiment(int selectedSlot = -1, ExperimentStage stage = ExperimentStage.NormalMap)
        {
            DiagnosticEditMode.Require("Build lighting experiment");
            if (Frame == null) return;
            Release(ResourceSet.Experiment); ExperimentLight = null; ExperimentSprite = null;
            ExperimentError = experimentSetupError = null; ExperimentHasRendered = false; ExperimentNonblackPixels = -1;
            ExperimentOpaquePixels = experimentSourceNonblack = 0; ExperimentSlot = -1; LightingStage = stage;
            if (Frame == null) return;
            int slot = selectedSlot >= 0 ? selectedSlot : treatment ? treatment.oamSlot : Frame.Pieces.FirstOrDefault(p => p.RawRgba.Where((b, i) => i % 4 == 3).Any(b => b != 0))?.Slot ?? 0;
            ExperimentSlot = Mathf.Clamp(slot, 0, 127);
            var piece = Frame.Pieces[ExperimentSlot];
            for (int p = 0; p < piece.RawRgba.Length; p += 4) if (piece.RawRgba[p + 3] != 0) {
                ExperimentOpaquePixels++;
                if (piece.RawRgba[p] != 0 || piece.RawRgba[p + 1] != 0 || piece.RawRgba[p + 2] != 0) experimentSourceNonblack++;
            }
            try { BuildExperimentObjects(piece, stage); }
            catch (Exception e) { ExperimentError = experimentSetupError = "Experiment setup: " + e.Message; }
            // URP updates its dynamic light mesh/bounds in LateUpdate, including Edit mode.
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }
        private void BuildExperimentObjects(OamPiece piece, ExperimentStage stage)
        {
            float strength = treatment ? treatment.normalStrength : 0.7f;
            Texture2D normal = null;
            if (stage == ExperimentStage.NormalMap) {
                byte[] normals = new byte[piece.Size * piece.Size * 4];
                for (int y = 0; y < piece.Size; y++) for (int x = 0; x < piece.Size; x++) {
                    Vector3 n = new Vector3((x / (float)(piece.Size - 1) - 0.5f) * strength, (0.5f - y / (float)(piece.Size - 1)) * strength, 1).normalized;
                    int p = (y * piece.Size + x) * 4;
                    normals[p] = (byte)((n.x * 0.5f + 0.5f) * 255); normals[p + 1] = (byte)((n.y * 0.5f + 0.5f) * 255);
                    normals[p + 2] = (byte)((n.z * 0.5f + 0.5f) * 255); normals[p + 3] = 255;
                }
                normal = Own(Texture(normals, piece.Size, piece.Size), true);
            }
            var material = stage == ExperimentStage.NormalMap && treatment && treatment.enhancementMaterial
                ? Own(new Material(treatment.enhancementMaterial), true)
                : Material(stage == ExperimentStage.Unlit ? "Universal Render Pipeline/2D/Sprite-Unlit-Default" : "Universal Render Pipeline/2D/Sprite-Lit-Default", true);
            if (normal) material.SetTexture("_NormalMap", normal);
            float scale = Mathf.Clamp(treatment ? treatment.scale : 4, 1, 192f / piece.Size);
            var quad = ExperimentSprite = Quad("EXPERIMENT ONLY - OAM " + piece.Slot + " - " + stage, transform, ExperimentLayer,
                piece.RawRgba, piece.Size, piece.Size, 128 - piece.Size * scale / 2, 112 - piece.Size * scale / 2, 0, material, true, normal);
            quad.sortingLayerID = SortingLayer.NameToID("Default");
            quad.transform.localScale = Vector3.one * scale;
            quad.color = treatment ? treatment.tint : Color.white;
            if (stage == ExperimentStage.Unlit) return;
            var node = Node("2D diagnostic light (experiment only)", transform, ExperimentLayer, true);
            node.SetActive(false); // Set targets before OnEnable registers a global/point light.
            ExperimentLight = node.AddComponent<Light2D>();
            ExperimentLight.lightType = stage == ExperimentStage.GlobalLight ? Light2D.LightType.Global : Light2D.LightType.Point;
            ExperimentLight.blendStyleIndex = 0; ExperimentLight.intensity = 1.5f;
            ExperimentLight.pointLightOuterRadius = 200; ExperimentLight.pointLightInnerRadius = 100;
            ExperimentLight.color = Color.white;
            // Keep the light MESH in the sprite plane, inside the camera frustum.
            // normalMapDistance supplies the virtual normal-light height; Z=-30
            // puts the mesh behind our camera at Z=-10 and can clip all lighting.
            node.transform.localPosition = new Vector3(128, -112, 0);
#if UNITY_EDITOR
            // Stable serialized contract used by URP 17's own Light2D inspector.
            // Do not rely on Awake/Reset populating dynamically created targets.
            var serialized = new UnityEditor.SerializedObject(ExperimentLight);
            var targets = serialized.FindProperty("m_ApplyToSortingLayers");
            if (targets == null) throw new InvalidOperationException("URP Light2D Target Sorting Layers property is missing");
            targets.arraySize = 1; targets.GetArrayElementAtIndex(0).intValue = quad.sortingLayerID;
            serialized.FindProperty("m_NormalMapQuality").intValue = (int)(stage == ExperimentStage.NormalMap ? Light2D.NormalMapQuality.Accurate : Light2D.NormalMapQuality.Disabled);
            serialized.FindProperty("m_NormalMapDistance").floatValue = 30;
            serialized.ApplyModifiedPropertiesWithoutUndo();
#else
            throw new NotSupportedException("Lighting experiment requires the qualified Editor setup path");
#endif
            node.SetActive(true);
        }
        public string[] ExperimentDiagnostics()
        {
            DiagnosticEditMode.Require("Inspect lighting experiment");
            var issues = new List<string>();
            if (ExperimentError != null) issues.Add(ExperimentError);
            if (ExperimentOpaquePixels == 0) issues.Add("Source OAM " + ExperimentSlot + " is fully transparent; choose a visible source piece.");
            else if (experimentSourceNonblack == 0) issues.Add("Source pixels are opaque black (palette/brightness); nonblack output is not expected.");
            if (!ExperimentCamera || !ExperimentCamera.gameObject.activeInHierarchy) issues.Add("Experiment camera is missing or inactive.");
            if (!ExperimentView || !ExperimentView.IsCreated() || ExperimentView.width != 256 || ExperimentView.height != 224)
                issues.Add("Experiment render target is missing, released or not 256x224.");
            if (ExperimentCamera && ExperimentCamera.targetTexture != ExperimentView) issues.Add("Experiment camera has an incorrect render target binding.");
            if (ExperimentCamera && ExperimentCamera.cullingMask != (1 << ExperimentLayer)) issues.Add("Experiment culling mask must contain only GameObject layer 9.");
            if (!ExperimentSprite || !ExperimentSprite.enabled || !ExperimentSprite.gameObject.activeInHierarchy) issues.Add("Experiment sprite is missing or disabled.");
            else {
                if (ExperimentSprite.gameObject.layer != ExperimentLayer) issues.Add("Experiment sprite is not on GameObject layer 9.");
                if (ExperimentCamera && !GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(ExperimentCamera), ExperimentSprite.bounds)) issues.Add("Experiment sprite is outside the camera frustum.");
                Color tint = ExperimentSprite.color;
                if (tint.a <= 0 || (tint.r <= 0 && tint.g <= 0 && tint.b <= 0)) issues.Add("Experiment tint is transparent or black.");
                if (!ExperimentSprite.sharedMaterial || !ExperimentSprite.sharedMaterial.shader || !ExperimentSprite.sharedMaterial.shader.isSupported) issues.Add("Experiment material/shader is missing or unsupported.");
            }
            if (LightingStage != ExperimentStage.Unlit) {
                if (!ExperimentLight || !ExperimentLight.isActiveAndEnabled) issues.Add("No enabled experiment Light2D; lit sprites need a light.");
                else {
                    if (ExperimentLight.gameObject.layer != ExperimentLayer) issues.Add("Experiment Light2D is not on GameObject layer 9.");
                    if (ExperimentLight.intensity <= 0) issues.Add("Experiment Light2D intensity is zero.");
#if UNITY_EDITOR
                    var targets = new UnityEditor.SerializedObject(ExperimentLight).FindProperty("m_ApplyToSortingLayers");
                    bool targetsSprite = false;
                    if (targets != null && ExperimentSprite) for (int i = 0; i < targets.arraySize; i++) targetsSprite |= targets.GetArrayElementAtIndex(i).intValue == ExperimentSprite.sortingLayerID;
                    if (!targetsSprite) issues.Add("Light2D Target Sorting Layers excludes the sprite's sorting layer (expected Default).");
#endif
                    if (ExperimentCamera && ExperimentLight.lightType != Light2D.LightType.Global) {
                        float depth = ExperimentCamera.WorldToViewportPoint(ExperimentLight.transform.position).z;
                        if (depth < ExperimentCamera.nearClipPlane || depth > ExperimentCamera.farClipPlane) issues.Add("Point-light mesh lies outside the camera depth range.");
                    }
                }
            }
            if (ExperimentHasRendered && ExperimentNonblackPixels == 0 && experimentSourceNonblack > 0)
                issues.Add("Experiment GPU output is entirely black despite nonblack source pixels. Check the four lighting stages in order.");
            return issues.ToArray();
        }
        public static int CountNonblackPixels(byte[] rgba)
        {
            int count = 0;
            for (int p = 0; p < rgba.Length; p += 4) if (rgba[p + 3] != 0 && (rgba[p] != 0 || rgba[p + 1] != 0 || rgba[p + 2] != 0)) count++;
            return count;
        }
        public void RenderViews()
        {
            DiagnosticEditMode.Require("Render diagnostic views");
            HasRendered = false;
            if (!BaselineCamera || GraphicsSettings.currentRenderPipeline == null)
                throw new InvalidOperationException("No active capture/URP pipeline. Initialize diagnostic URP settings and allow the Editor to repaint.");
            SubmitView(BaselineCamera, BaselineView);
            HasRendered = true;
            // Experiment failures must not invalidate a faithful baseline export.
            ExperimentHasRendered = false; ExperimentNonblackPixels = -1; ExperimentError = experimentSetupError;
            if (experimentSetupError != null) return;
            try {
                if (ExperimentLight && ExperimentLight.lightType == Light2D.LightType.Point)
                    ExperimentLight.transform.localPosition = new Vector3(128 + Mathf.Sin(Time.realtimeSinceStartup) * 32, -112 + Mathf.Cos(Time.realtimeSinceStartup) * 24, 0);
                SubmitView(ExperimentCamera, ExperimentView);
                ExperimentNonblackPixels = CountNonblackPixels(Readback(ExperimentView));
                ExperimentHasRendered = true;
            } catch (Exception e) { ExperimentError = "Experiment rendering: " + e.Message; }
        }
        private static void SubmitView(Camera camera, RenderTexture target)
        {
            if (!camera || !camera.gameObject.activeInHierarchy || !target || !target.IsCreated() || target.width != 256 || target.height != 224 || camera.targetTexture != target)
                throw new InvalidOperationException("Missing/inactive camera or invalid 256x224 render target/binding");
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
                throw new InvalidOperationException("Active render pipeline does not support the URP single-camera request");
            string failure = null;
            Application.LogCallback onLog = (message, trace, type) => {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) failure = message;
            };
            Application.logMessageReceived += onLog;
            try {
                RenderPipeline.SubmitRenderRequest(camera, request);
            } finally { Application.logMessageReceived -= onLog; }
            if (failure != null) throw new InvalidOperationException("Unity rendering reported: " + failure);
        }
        public static byte[] Readback(RenderTexture view)
        {
            DiagnosticEditMode.Require("Read diagnostic render target");
            var previous = RenderTexture.active;
            var texture = new Texture2D(view.width, view.height, TextureFormat.RGBA32, false, true);
            try {
                RenderTexture.active = view; texture.ReadPixels(new Rect(0, 0, view.width, view.height), 0, 0); texture.Apply();
                var colors = texture.GetPixels32(); byte[] result = new byte[colors.Length * 4];
                for (int y = 0; y < view.height; y++) for (int x = 0; x < view.width; x++) {
                    var c = colors[(view.height - 1 - y) * view.width + x]; int p = (y * view.width + x) * 4;
                    result[p] = c.r; result[p + 1] = c.g; result[p + 2] = c.b; result[p + 3] = c.a;
                }
                return result;
            } finally { RenderTexture.active = previous; DestroyImmediate(texture); }
        }
    }
}
