using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using X2HD2D.Unity;

namespace X2HD2D.Editor
{
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/X2HD2D/Scenes/Diagnostic.unity";
        public const string Generated = "Assets/LocalGenerated";
        [InitializeOnLoadMethod] private static void QueueSetup() => EditorApplication.delayCall += () => {
            if (DiagnosticEditMode.IsAvailable) Ensure();
        };
        private static bool CanOperate()
        {
            if (DiagnosticEditMode.IsAvailable) return true;
            Debug.LogWarning(DiagnosticEditMode.Message); return false;
        }
        [MenuItem("X2 HD-2D/Initialize diagnostic URP settings")]
        public static void Ensure()
        {
            if (!CanOperate()) return;
            if (!AssetDatabase.IsValidFolder(Generated)) AssetDatabase.CreateFolder("Assets", "LocalGenerated");
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Generated + "/DiagnosticURP.asset");
            if (!pipeline)
            {
                var renderer = ScriptableObject.CreateInstance<Renderer2DData>();
                AssetDatabase.CreateAsset(renderer, Generated + "/Renderer2D.asset");
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.supportsHDR = false; pipeline.msaaSampleCount = 1; pipeline.renderScale = 1;
                AssetDatabase.CreateAsset(pipeline, Generated + "/DiagnosticURP.asset");
            }
            GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
            QualitySettings.antiAliasing = 0; PlayerSettings.colorSpace = ColorSpace.Gamma;
            // Reference bytes are compared without a new gamma/HDR/postprocessing transform.
            AssetDatabase.SaveAssets();
        }
        [MenuItem("X2 HD-2D/Open diagnostic scene")]
        public static void OpenScene()
        {
            if (!CanOperate()) return; // Guard before even showing the save dialog.
            Ensure();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
            DiagnosticWindow.Open();
        }
        [MenuItem("X2 HD-2D/Initialize diagnostic URP settings", true)]
        [MenuItem("X2 HD-2D/Open diagnostic scene", true)]
        private static bool ValidateEditModeMenu() => DiagnosticEditMode.IsAvailable;
    }
}
