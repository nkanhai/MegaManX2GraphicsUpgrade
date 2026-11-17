using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace X2HD2D.Unity
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class DiagnosticEditMode
    {
        public const string Message = "X2 graphics diagnostics are Edit-Mode-only. Exit Play Mode and wait for the Editor to finish switching, then reimport the capture.";
#if UNITY_EDITOR
        private static bool transitioning = UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode;
        private static bool queued;
        private static readonly HashSet<CaptureScene> scenes = new HashSet<CaptureScene>();
        private static readonly HashSet<Object> objects = new HashSet<Object>();
        static DiagnosticEditMode()
        {
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeState;
            UnityEditor.EditorApplication.delayCall += RecoverAfterReload;
            UnityEditor.EditorApplication.update += DrainWhenReady;
        }
        public static bool IsAvailable => !transitioning && !Application.isPlaying &&
            !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode &&
            !UnityEditor.EditorApplication.isCompiling && !UnityEditor.EditorApplication.isUpdating;
        private static void OnPlayModeState(UnityEditor.PlayModeStateChange state)
        {
            transitioning = true;
            if (state == UnityEditor.PlayModeStateChange.EnteredEditMode)
                UnityEditor.EditorApplication.delayCall += FinishReturningToEditMode;
        }
        private static void FinishReturningToEditMode()
        {
            if (Application.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            transitioning = false;
            // This also covers Enter Play Mode with domain/scene reload disabled.
            foreach (var scene in Resources.FindObjectsOfTypeAll<CaptureScene>())
                if (scene && scene.gameObject.scene.IsValid()) DeferCleanup(scene);
            DrainWhenReady();
        }
        private static void RecoverAfterReload()
        {
            if (!IsAvailable) return;
            foreach (var scene in Resources.FindObjectsOfTypeAll<CaptureScene>())
                if (scene && scene.gameObject.scene.IsValid() && scene.Frame == null) DeferCleanup(scene);
        }
        internal static void DeferCleanup(CaptureScene scene)
        {
            scenes.Add(scene);
            foreach (var item in scene.OwnedResourceSnapshot()) if (item) objects.Add(item);
            queued = true;
        }
        internal static void CancelCleanup(CaptureScene scene)
        {
            scenes.Remove(scene);
            foreach (var item in scene.OwnedResourceSnapshot()) objects.Remove(item);
        }
        public static void DeferDestroy(Object item)
        {
            if (item) { objects.Add(item); queued = true; }
        }
        private static void DrainWhenReady()
        {
            if (!queued || !IsAvailable) return;
            queued = false;
            var pendingScenes = scenes.ToArray(); scenes.Clear();
            foreach (var scene in pendingScenes) if (scene) scene.Clear();
            // A scene/root may have been destroyed after OnDisable. Its copied
            // resource handles still let us release orphan textures/materials.
            var pendingObjects = objects.ToArray(); objects.Clear();
            foreach (var item in pendingObjects) if (item) Object.DestroyImmediate(item);
        }
#else
        public static bool IsAvailable => false;
        internal static void DeferCleanup(CaptureScene scene) { }
        internal static void CancelCleanup(CaptureScene scene) { }
        public static void DeferDestroy(Object item) { }
#endif
        public static void Require(string operation)
        {
            if (!IsAvailable) throw new InvalidOperationException(operation + ": " + Message);
        }
    }
}
