using UnityEngine;

namespace X2HD2D.Unity
{
    // Metadata only: a hardware slot/map index has no persistent gameplay identity.
    public sealed class GraphicsSource : MonoBehaviour
    {
        public string componentKind;
        public int sourceIndex, priority, evaluationOrder, tileNumber, palette, x, y, tileSize, scrollX, scrollY, mapWidth, mapHeight, mapBaseWord, tileBaseWord;
        public bool flipX, flipY, enabledOnMain;
        [TextArea] public string interpretation = "Capture-local graphics piece; not a semantic entity";
    }
}
