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
        private readonly List<Object> owned = new List<Object>();
        private readonly List<Object> experimentOwned = new List<Object>();
        private Light2D movingLight;
        public bool HasRendered { get; private set; }
        public bool HasFilters => !ObjectsVisible || BackgroundVisible.Any(v => !v);

        private T Own<T>(T value, bool experiment = false) where T : Object
        { value.hideFlags = HideFlags.DontSave; (experiment ? experimentOwned : owned).Add(value); return value; }
        private GameObject Node(string title, Transform parent, int layer, bool experiment = false)
        { var node = Own(new GameObject(title), experiment); node.layer = layer; node.transform.SetParent(parent, false); return node; }
        private static void Release(List<Object> items)
        {
            foreach (Object item in items) if (item) {
                if (item is GameObject node) node.SetActive(false);
                if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
            }
            items.Clear();
        }
        public void Clear()
        {
            Release(experimentOwned); Release(owned); Capture = null; Frame = null;
            movingLight = null; BaselineView = ExperimentView = null; ReferenceView = null; HasRendered = false;
        }
        private void OnDisable() => Clear();

        public static Texture2D Texture(byte[] topDown, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true) {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            byte[] bottomUp = new byte[topDown.Length];
            for (int y = 0; y < height; y++) Buffer.BlockCopy(topDown, y * width * 4, bottomUp, (height - 1 - y) * width * 4, width * 4);
            texture.LoadRawTextureData(bottomUp); texture.Apply(false, false); return texture;
        }
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
            if (Frame == null) return;
            for (int i = 0; i < 3; i++) BackgroundRoots[i].gameObject.SetActive(BackgroundVisible[i]);
            foreach (var root in ObjectRoots) root.parent.gameObject.SetActive(ObjectsVisible);
        }
        public void BuildExperiment(int selectedSlot = -1)
        {
            Release(experimentOwned); movingLight = null;
            if (Frame == null) return;
            int slot = selectedSlot >= 0 ? selectedSlot : treatment ? treatment.oamSlot : Frame.Pieces.FirstOrDefault(p => p.RawRgba.Where((b, i) => i % 4 == 3).Any(b => b != 0))?.Slot ?? 0;
            var piece = Frame.Pieces[Mathf.Clamp(slot, 0, 127)];
            float strength = treatment ? treatment.normalStrength : 0.7f;
            byte[] normals = new byte[piece.Size * piece.Size * 4];
            for (int y = 0; y < piece.Size; y++) for (int x = 0; x < piece.Size; x++)
            {
                Vector3 n = new Vector3((x / (float)(piece.Size - 1) - 0.5f) * strength, (0.5f - y / (float)(piece.Size - 1)) * strength, 1).normalized;
                int p = (y * piece.Size + x) * 4;
                normals[p] = (byte)((n.x * 0.5f + 0.5f) * 255); normals[p + 1] = (byte)((n.y * 0.5f + 0.5f) * 255);
                normals[p + 2] = (byte)((n.z * 0.5f + 0.5f) * 255); normals[p + 3] = 255;
            }
            var normal = Own(Texture(normals, piece.Size, piece.Size), true);
            var lit = treatment && treatment.enhancementMaterial ? Own(new Material(treatment.enhancementMaterial), true) : Material("Universal Render Pipeline/2D/Sprite-Lit-Default", true);
            lit.SetTexture("_NormalMap", normal);
            var quad = Quad("EXPERIMENT ONLY - OAM " + slot + " procedural normal", transform, ExperimentLayer, piece.RawRgba, piece.Size, piece.Size,
                96, 80, 0, lit, true, normal);
            quad.transform.localScale = Vector3.one * (treatment ? treatment.scale : 4);
            quad.color = treatment ? treatment.tint : Color.white;
            movingLight = Node("Dynamic 2D normal light (experiment only)", transform, ExperimentLayer, true).AddComponent<Light2D>();
            movingLight.lightType = Light2D.LightType.Point; movingLight.intensity = 1.5f;
            movingLight.pointLightOuterRadius = 150; movingLight.pointLightInnerRadius = 20;
            movingLight.color = new Color(1, 0.85f, 0.65f);
#if UNITY_EDITOR
            // URP 17 exposes normal settings as getters; configure its serialized fields.
            var serialized = new UnityEditor.SerializedObject(movingLight);
            serialized.FindProperty("m_NormalMapQuality").intValue = (int)Light2D.NormalMapQuality.Accurate;
            serialized.FindProperty("m_NormalMapDistance").floatValue = 30;
            serialized.ApplyModifiedPropertiesWithoutUndo();
#endif
        }
        public void RenderViews()
        {
            HasRendered = false;
            if (!BaselineCamera || GraphicsSettings.currentRenderPipeline == null)
                throw new InvalidOperationException("No active capture/URP pipeline. Initialize diagnostic URP settings and allow the Editor to repaint.");
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = BaselineView };
            if (!RenderPipeline.SupportsRenderRequest(BaselineCamera, request))
                throw new InvalidOperationException("Active render pipeline does not support the URP single-camera request");
            if (movingLight) movingLight.transform.localPosition = new Vector3(128 + Mathf.Sin(Time.realtimeSinceStartup) * 60, -100 + Mathf.Cos(Time.realtimeSinceStartup) * 35, -30);
            string failure = null;
            Application.LogCallback onLog = (message, trace, type) => {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) failure = message;
            };
            Application.logMessageReceived += onLog;
            try {
                RenderPipeline.SubmitRenderRequest(BaselineCamera, request);
                RenderPipeline.SubmitRenderRequest(ExperimentCamera, new UniversalRenderPipeline.SingleCameraRequest { destination = ExperimentView });
            } finally { Application.logMessageReceived -= onLog; }
            if (failure != null) throw new InvalidOperationException("Unity rendering reported: " + failure);
            HasRendered = true;
        }
        public static byte[] Readback(RenderTexture view)
        {
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
