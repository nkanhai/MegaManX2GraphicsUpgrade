using System;
using System.IO;
using System.Linq;
using UnityEngine;
using X2HD2D.Core;
using X2HD2D.Unity;

namespace X2HD2D.Editor
{
    public static class ComparisonExport
    {
        [Serializable] private sealed class Report
        {
            public string format = "x2-hd2d.unity-comparison-v1", status, session, sequence, containerSha256, unityVersion;
            public int selectedLine, gpuDifferentPixels, managedDifferentPixels, totalPixels = 57344;
            public bool filtersActive;
            public string[] unsupported;
            public string scope = "Unity GPU and managed component composite versus pinned-PPU reference; no hardware or whole-game claim";
        }
        public static int DifferentPixels(byte[] a, byte[] b) => Enumerable.Range(0, 256 * 224).Count(p => Enumerable.Range(0, 4).Any(c => a[p * 4 + c] != b[p * 4 + c]));
        private static void Png(string path, byte[] rgba)
        {
            var texture = CaptureScene.Texture(rgba, 256, 224);
            try { File.WriteAllBytes(path, texture.EncodeToPNG()); } finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        public static string Export(CaptureScene scene)
        {
            scene.RenderViews(); // Read back current visibility/materials, never a stale or uninitialized target.
            if (scene.Capture == null || !scene.BaselineView || !scene.HasRendered) throw new InvalidOperationException("No rendered capture");
            byte[] reference = scene.Capture.CopyReferenceRgba(), gpu = CaptureScene.Readback(scene.BaselineView), managed = GraphicsDecoder.Composite(scene.Frame, false);
            var report = new Report { session = scene.Capture.Session, sequence = scene.Capture.Sequence.ToString(), containerSha256 = scene.Capture.ContainerHash,
                selectedLine = scene.Frame.State.Line, unityVersion = Application.unityVersion, gpuDifferentPixels = DifferentPixels(reference, gpu),
                managedDifferentPixels = DifferentPixels(reference, managed), filtersActive = scene.HasFilters, unsupported = scene.Frame.UnsupportedReasons };
            report.status = !scene.Frame.IsSupported || scene.HasFilters ? "incomplete-or-filtered-inspection" : report.gpuDifferentPixels == 0 && report.managedDifferentPixels == 0 ? "verified-this-frame" : "mismatch";
            byte[] difference = new byte[reference.Length];
            for (int p = 0; p < difference.Length; p += 4) { bool changed = Enumerable.Range(0, 4).Any(c => reference[p + c] != gpu[p + c]); difference[p] = difference[p + 2] = changed ? (byte)255 : (byte)0; difference[p + 3] = 255; }
            string parent = Path.GetFullPath(Path.Combine(Application.dataPath, "../../local/unity-comparisons"));
            string output = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(output)) throw new IOException("Refusing an existing comparison directory");
            Directory.CreateDirectory(output);
            Png(Path.Combine(output, "reference.png"), reference); Png(Path.Combine(output, "unity-components.png"), gpu);
            Png(Path.Combine(output, "managed-components.png"), managed); Png(Path.Combine(output, "difference.png"), difference);
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
            File.WriteAllText(Path.Combine(output, "report.txt"), $"{report.status}\nUnity GPU: {report.gpuDifferentPixels}/57344 pixels differ\nManaged components: {report.managedDifferentPixels}/57344 pixels differ\n" + string.Join("\n", report.unsupported));
            return output;
        }
    }
}
