using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>What Unity's Undo records for the painter: only the history's step number (see MaskHistory).</summary>
    public sealed class MaskPainterUndo : ScriptableObject
    {
        public int Step;
    }
}
