using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// One painting: the mask and everything needed to pick it up again. It lives on this hidden object rather than on
    /// the window, so a script reload, a maximized Scene view (which destroys and rebuilds the other windows) or a
    /// closed window never loses it, and the window layout file never carries it. Never recorded by Undo.
    /// </summary>
    public sealed class MaskPainterData : ScriptableObject
    {
        public MaskCanvas Canvas;
        public Renderer Renderer;
        public int UvChannel;
        /// <summary>Which of the renderer's materials the mask is for; its part of the mesh is the one painted.</summary>
        public int MaterialIndex;
        public MaskPainterUndo UndoMarker;
        public string SavedPath;
        /// <summary>SavedPath is the file the painting started from, not written yet: the window asks before replacing it.</summary>
        public bool ReplacesStart;
        public bool Unsaved;
        /// <summary>When the painting started (UTC ticks), so the newest one is picked up again first.</summary>
        public long Started;
    }
}
