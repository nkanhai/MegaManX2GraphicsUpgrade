using UnityEngine;

namespace X2HD2D.Unity
{
    [CreateAssetMenu(menuName = "X2 HD-2D/Capture-local treatment", fileName = "CaptureTreatment")]
    public sealed class CaptureTreatment : ScriptableObject
    {
        [Range(0, 127)] public int oamSlot;
        public Color tint = Color.white;
        [Range(1, 8)] public float scale = 4;
        [Range(0, 1)] public float normalStrength = 0.7f;
        public Material enhancementMaterial;
        [TextArea] public string scope = "OAM slot selection applies only to the currently inspected capture. Not an asset/entity catalogue.";
    }
}
