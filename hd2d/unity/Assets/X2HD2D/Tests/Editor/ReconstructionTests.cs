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
    }
}
