using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using X2HD2D.Core;
using X2HD2D.Editor;
using X2HD2D.Unity;
using Object = UnityEngine.Object;

namespace X2HD2D.Tests
{
    public sealed class ReconstructionTests
    {
        private string Root => Environment.GetEnvironmentVariable("X2_HD2D_FIXTURES") ??
            Path.GetFullPath(Path.Combine(Application.dataPath, "../../local/unity-fixtures"));
        private ScanoutCapture Fixture(string name)
        {
            string path = Path.Combine(Root, name + ".x2scan");
            Assert.That(File.Exists(path), Is.True, "Generate original synthetic fixtures first: " + path);
            var capture = ScanoutReader.ReadFile(path);
            Assert.That(capture.Source, Is.Zero, "Tests accept only synthetic captures");
            return capture;
        }
        private string[] StaticCases()
        {
            Assert.That(Directory.Exists(Root), Is.True, "Follow README synthetic fixture generation instructions");
            string[] files = Directory.GetFiles(Root, "*.x2scan");
            Assert.That(files.Length, Is.EqualTo(19), "Expected 18 static fixtures and original raster capture");
            return files.Select(Path.GetFileNameWithoutExtension).Where(n => n != "raster-existing").OrderBy(n => n).ToArray();
        }

        [Test, Category("Managed")]
        public void AllStaticComponentsMatchNativeOracle()
        {
            foreach (string name in StaticCases()) {
                var capture = Fixture(name); var decoded = GraphicsDecoder.Decode(capture);
                Assert.That(decoded.IsSupported, Is.True, name);
                CollectionAssert.AreEqual(capture.CopyReferenceRgba(), GraphicsDecoder.Composite(decoded), name);
                Assert.That(decoded.Backgrounds.Length, Is.EqualTo(6));
                Assert.That(decoded.Pieces.Length, Is.EqualTo(128));
            }
        }

        [Test, Category("Managed")]
        public void RasterInspectionIsExplicitlyIncomplete()
        {
            var capture = Fixture("raster-existing");
            var frame = GraphicsDecoder.Decode(capture, 128);
            Assert.That(frame.State.Line, Is.EqualTo(128));
            Assert.That(frame.IsSupported, Is.False);
            Assert.That(frame.UnsupportedReasons.Any(r => r.StartsWith("Raster")), Is.True);
            Assert.Throws<NotSupportedException>(() => GraphicsDecoder.Composite(frame));
        }

        // Run with an actual graphics device (do NOT use -nographics). These are
        // separate from the license-free managed/native CI result.
        [UnityTest, Category("Graphics")]
        public IEnumerator UnityGpuMatchesEveryStaticFixture()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null), "A real graphics device is required");
            ProjectSetup.Ensure();
            var root = new GameObject("Synthetic component test") { hideFlags = HideFlags.DontSave };
            var scene = root.AddComponent<CaptureScene>();
            try {
                foreach (string name in StaticCases()) {
                    var capture = Fixture(name); scene.Load(capture);
                    EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                    scene.RenderViews();
                    Assert.That(scene.HasRendered, Is.True);
                    Assert.That(ComparisonExport.DifferentPixels(capture.CopyReferenceRgba(), CaptureScene.Readback(scene.BaselineView)), Is.Zero, name);
                }
            } finally { Object.DestroyImmediate(root); }
        }

        [UnityTest, Category("Graphics")]
        public IEnumerator PiecesAreAddressableAndEnhancementDoesNotChangeBaseline()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            ProjectSetup.Ensure();
            var root = new GameObject("Synthetic isolation test") { hideFlags = HideFlags.DontSave };
            var scene = root.AddComponent<CaptureScene>();
            try {
                var capture = Fixture("static8"); scene.Load(capture);
                EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                Assert.That(scene.BackgroundRoots.Length, Is.EqualTo(3));
                Assert.That(scene.ObjectRoots.Length, Is.EqualTo(128));
                Assert.That(scene.ObjectRoots[0].GetComponent<GraphicsSource>().sourceIndex, Is.Zero);
                Assert.That(root.GetComponentsInChildren<SpriteRenderer>(true).All(r =>
                    r.sprite.texture != scene.ReferenceView && (r.gameObject.hideFlags & HideFlags.DontSave) != 0), Is.True);
                scene.RenderViews(); byte[] before = CaptureScene.Readback(scene.BaselineView);
                scene.BuildExperiment(1); scene.RenderViews();
                CollectionAssert.AreEqual(before, CaptureScene.Readback(scene.BaselineView), "Experiment must not alter baseline");
                scene.BackgroundVisible[0] = false; scene.ApplyVisibility(); scene.RenderViews();
                Assert.That(scene.BackgroundRoots[0].gameObject.activeSelf, Is.False);
                Assert.That(scene.BackgroundRoots[1].gameObject.activeSelf, Is.True);
                Assert.That(ComparisonExport.DifferentPixels(before, CaptureScene.Readback(scene.BaselineView)), Is.GreaterThan(0), "BG toggle must change rendered components");
                scene.BackgroundVisible[0] = true; scene.ApplyVisibility(); scene.RenderViews();
                CollectionAssert.AreEqual(before, CaptureScene.Readback(scene.BaselineView));
                scene.Load(capture); Assert.That(scene.ObjectRoots.Length, Is.EqualTo(128), "Reload replaces objects");
            } finally { Object.DestroyImmediate(root); }
        }

        [UnityTest, Category("Graphics")]
        public IEnumerator VisibleSyntheticSpriteRendersThroughAllLightingStages()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            ProjectSetup.Ensure();
            var root = new GameObject("Synthetic lighting stages") { hideFlags = HideFlags.DontSave };
            var scene = root.AddComponent<CaptureScene>();
            try {
                var capture = Fixture("static8"); scene.Load(capture);
                // Slot 0 is an original, opaque, nonblack synthetic tile, not an
                // arbitrary first slot whose empty output could make a test pass.
                Assert.That(CaptureScene.CountNonblackPixels(scene.Frame.Pieces[0].RawRgba), Is.GreaterThan(0));
                foreach (ExperimentStage stage in Enum.GetValues(typeof(ExperimentStage))) {
                    scene.BuildExperiment(0, stage);
                    EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                    EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                    Assert.That(scene.ExperimentError, Is.Null, stage.ToString());
                    Assert.That(scene.ExperimentCamera.cullingMask, Is.EqualTo(1 << CaptureScene.ExperimentLayer));
                    Assert.That(scene.ExperimentSprite.gameObject.layer, Is.EqualTo(CaptureScene.ExperimentLayer));
                    Assert.That(scene.ExperimentSprite.sortingLayerID, Is.EqualTo(SortingLayer.NameToID("Default")));
                    if (stage == ExperimentStage.Unlit) Assert.That(scene.ExperimentLight, Is.Null);
                    else {
                        Assert.That(scene.ExperimentLight.isActiveAndEnabled, Is.True);
                        Assert.That(scene.ExperimentLight.gameObject.layer, Is.EqualTo(CaptureScene.ExperimentLayer));
                        var targets = new SerializedObject(scene.ExperimentLight).FindProperty("m_ApplyToSortingLayers");
                        Assert.That(targets.arraySize, Is.EqualTo(1));
                        Assert.That(targets.GetArrayElementAtIndex(0).intValue, Is.EqualTo(scene.ExperimentSprite.sortingLayerID));
                        float depth = scene.ExperimentCamera.WorldToViewportPoint(scene.ExperimentLight.transform.position).z;
                        Assert.That(depth, Is.InRange(scene.ExperimentCamera.nearClipPlane, scene.ExperimentCamera.farClipPlane));
                    }
                    scene.RenderViews();
                    Assert.That(scene.ExperimentHasRendered, Is.True, string.Join("\n", scene.ExperimentDiagnostics()));
                    Assert.That(CaptureScene.CountNonblackPixels(CaptureScene.Readback(scene.ExperimentView)), Is.GreaterThan(0),
                        stage + ": experimental GPU target must contain actual visible sprite pixels");
                    Assert.That(scene.ExperimentDiagnostics(), Is.Empty, stage.ToString());
                    CollectionAssert.AreEqual(capture.CopyReferenceRgba(), CaptureScene.Readback(scene.BaselineView), stage + ": baseline isolation");
                }
            } finally { Object.DestroyImmediate(root); }
        }

        [UnityTest, Category("Graphics")]
        public IEnumerator ExperimentFaultsAreDiagnosedWithoutBreakingBaseline()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            ProjectSetup.Ensure();
            var root = new GameObject("Synthetic lighting diagnostics") { hideFlags = HideFlags.DontSave };
            var scene = root.AddComponent<CaptureScene>();
            try {
                var capture = Fixture("static8"); scene.Load(capture); scene.BuildExperiment(0, ExperimentStage.PointLight);
                EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                scene.ExperimentLight.enabled = false;
                Assert.That(scene.ExperimentDiagnostics().Any(s => s.Contains("No enabled experiment Light2D")), Is.True);
                scene.ExperimentLight.enabled = true;
                var lightData = new SerializedObject(scene.ExperimentLight);
                lightData.FindProperty("m_ApplyToSortingLayers").arraySize = 0; lightData.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(scene.ExperimentDiagnostics().Any(s => s.Contains("Target Sorting Layers excludes")), Is.True);
                scene.ExperimentCamera.cullingMask = 0;
                Assert.That(scene.ExperimentDiagnostics().Any(s => s.Contains("culling mask")), Is.True);
                scene.RenderViews();
                Assert.That(scene.ExperimentNonblackPixels, Is.Zero, "A zero camera mask must produce a black experimental target");
                Assert.That(scene.ExperimentDiagnostics().Any(s => s.Contains("entirely black")), Is.True);
                CollectionAssert.AreEqual(capture.CopyReferenceRgba(), CaptureScene.Readback(scene.BaselineView));
                scene.ExperimentCamera.cullingMask = 1 << CaptureScene.ExperimentLayer;
                scene.ExperimentView.Release();
                Assert.That(scene.ExperimentDiagnostics().Any(s => s.Contains("render target")), Is.True);
                scene.RenderViews();
                Assert.That(scene.HasRendered, Is.True, "An experimental failure cannot block baseline rendering/export");
                Assert.That(scene.ExperimentHasRendered, Is.False);
                CollectionAssert.AreEqual(capture.CopyReferenceRgba(), CaptureScene.Readback(scene.BaselineView));
                // Fault-inject an empty derived sprite, without altering imported
                // capture state, to exercise the transparent-source diagnostic.
                scene.Frame.Pieces[0].RawRgba = new byte[scene.Frame.Pieces[0].RawRgba.Length];
                scene.BuildExperiment(0, ExperimentStage.Unlit);
                Assert.That(scene.ExperimentDiagnostics().Any(s => s.Contains("fully transparent")), Is.True);
            } finally { Object.DestroyImmediate(root); }
        }

        [UnityTest, Category("Graphics")]
        public IEnumerator HierarchyOamChildrenResolveToTheirSourceSlot()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            ProjectSetup.Ensure();
            var root = new GameObject("Synthetic OAM selection") { hideFlags = HideFlags.DontSave };
            var scene = root.AddComponent<CaptureScene>();
            try {
                scene.Load(Fixture("static8"));
                EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                Assert.That(DiagnosticWindow.TryGetOamSelection(scene, scene.ObjectRoots[1].gameObject, out int slot), Is.True);
                Assert.That(slot, Is.EqualTo(1));
                Assert.That(DiagnosticWindow.TryGetOamSelection(scene, scene.ObjectRoots[1].GetChild(0).gameObject, out slot), Is.True);
                Assert.That(slot, Is.EqualTo(1));
                Assert.That(DiagnosticWindow.TryGetOamSelection(scene, scene.BackgroundRoots[0].gameObject, out slot), Is.False);
            } finally { Object.DestroyImmediate(root); }
        }

        [UnityTest, Category("Graphics")]
        public IEnumerator DisablingRootDefersCleanupAndAllowsCleanReimport()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            ProjectSetup.Ensure();
            var root = new GameObject("Synthetic deferred cleanup") { hideFlags = HideFlags.DontSave };
            var scene = root.AddComponent<CaptureScene>();
            try {
                var capture = Fixture("static8"); scene.Load(capture);
                var target = scene.BaselineView;
                root.SetActive(false);
                Assert.That(scene.Capture, Is.SameAs(capture), "OnDisable must only queue cleanup");
                Assert.That(target.IsCreated(), Is.True, "No destructive cleanup inside activation callbacks");
                for (int frame = 0; frame < 20 && scene.Capture != null; frame++) yield return null;
                Assert.That(scene.Capture, Is.Null, "Deferred cleanup must run in stable Edit Mode");
                Assert.That(scene.GetComponentsInChildren<CaptureResourceSet>(true), Is.Empty);
                root.SetActive(true); scene.Load(capture);
                EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                scene.RenderViews();
                Assert.That(scene.GetComponentsInChildren<CaptureResourceSet>(true).Length, Is.EqualTo(1));
                Assert.That(scene.ObjectRoots.Length, Is.EqualTo(128));
                CollectionAssert.AreEqual(capture.CopyReferenceRgba(), CaptureScene.Readback(scene.BaselineView));
            } finally { Object.DestroyImmediate(root); }
        }

        [UnityTest, Category("Graphics")]
        public IEnumerator PlayModeBlocksDiagnosticsAndExitAllowsReimport()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            ProjectSetup.Ensure();
            bool optionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var options = EditorSettings.enterPlayModeOptions;
            var root = new GameObject("Synthetic Edit-Mode-only guard") { hideFlags = HideFlags.DontSave };
            var scene = root.AddComponent<CaptureScene>();
            try {
                // Preserve references across the transition so the test can prove
                // the guard, rather than merely losing everything to a reload.
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
                var capture = Fixture("static8"); scene.Load(capture);
                var target = scene.BaselineView;
                yield return new EnterPlayMode(expectDomainReload: false);
                Assert.That(DiagnosticEditMode.IsAvailable, Is.False);
                Assert.Throws<InvalidOperationException>(() => scene.Load(capture));
                Assert.Throws<InvalidOperationException>(() => scene.Clear());
                Assert.Throws<InvalidOperationException>(() => scene.BuildExperiment(0));
                Assert.Throws<InvalidOperationException>(() => scene.ApplyVisibility());
                Assert.Throws<InvalidOperationException>(() => scene.RenderViews());
                Assert.Throws<InvalidOperationException>(() => CaptureScene.Readback(target));
                Assert.Throws<InvalidOperationException>(() => ComparisonExport.Export(scene));
                int sceneHandle = root.scene.handle;
                LogAssert.Expect(LogType.Warning, DiagnosticEditMode.Message); ProjectSetup.OpenScene();
                LogAssert.Expect(LogType.Warning, DiagnosticEditMode.Message); ProjectSetup.Ensure();
                Assert.That(root.scene.handle, Is.EqualTo(sceneHandle), "No Editor scene switch in Play Mode");
                root.SetActive(false); // Exercise OnDisable while actual Play Mode is active.
                yield return null;
                Assert.That(scene.Capture, Is.SameAs(capture));
                Assert.That(target.IsCreated(), Is.True, "Cleanup must stay pending throughout Play Mode");
                yield return new ExitPlayMode();
                for (int frame = 0; frame < 20 && (!DiagnosticEditMode.IsAvailable || scene.Capture != null); frame++) yield return null;
                Assert.That(DiagnosticEditMode.IsAvailable, Is.True);
                Assert.That(scene.Capture, Is.Null);
                root.SetActive(true); scene.Load(capture);
                EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                scene.RenderViews();
                Assert.That(scene.GetComponentsInChildren<CaptureResourceSet>(true).Length, Is.EqualTo(1));
                Assert.That(scene.ObjectRoots.Length, Is.EqualTo(128));
                CollectionAssert.AreEqual(capture.CopyReferenceRgba(), CaptureScene.Readback(scene.BaselineView));
            } finally {
                EditorSettings.enterPlayModeOptions = options;
                EditorSettings.enterPlayModeOptionsEnabled = optionsEnabled;
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false;
                DiagnosticEditMode.DeferDestroy(root);
            }
        }
    }
}
