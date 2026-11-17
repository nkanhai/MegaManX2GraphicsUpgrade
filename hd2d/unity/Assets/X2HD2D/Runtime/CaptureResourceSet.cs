using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace X2HD2D.Unity
{
    // Attached only to a transient DontSave child, never the saved scene root.
    // Serialized references survive Editor domain reloads without saving private
    // graphics into scene/assets. No cleanup runs from this object's callbacks.
    [AddComponentMenu("")]
    public sealed class CaptureResourceSet : MonoBehaviour
    {
        [SerializeField, HideInInspector] private List<Object> baseline = new List<Object>();
        [SerializeField, HideInInspector] private List<Object> experiment = new List<Object>();
        internal List<Object> Baseline => baseline;
        internal List<Object> Experiment => experiment;
    }
}
