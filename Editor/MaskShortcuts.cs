using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// The painting keys, in Unity's Shortcut Manager under Mask Painter so they can be changed there. They belong to the
    /// Scene view and carry a tag that is on only while a painting is open, so they win over Unity's own keys then and
    /// never otherwise.
    /// </summary>
    public static class MaskShortcuts
    {
        public const string Tag = "DocMe.MaskPainter.Painting";
        static bool on;

        public static bool IsOn => on;

        /// <summary>Turns the tag on while the active window has a painting, off otherwise.</summary>
        public static void Sync()
        {
            var window = MaskPainterWindow.Active;
            bool painting = window != null && window.IsPainting;
            if (painting == on) return;
            on = painting;
            if (on) ShortcutManager.RegisterTag(Tag);
            else ShortcutManager.UnregisterTag(Tag);
        }

        static MaskPainterWindow Painting
        {
            get
            {
                var window = MaskPainterWindow.Active;
                return window != null && window.IsPainting ? window : null;
            }
        }

        [Shortcut("Mask Painter/Brush", typeof(SceneView), Tag, KeyCode.B)]
        public static void UseBrush()
        {
            var window = Painting;
            if (window != null) window.SetTool(MaskPainterWindow.PaintTool.Brush);
        }

        [Shortcut("Mask Painter/Fill Pieces", typeof(SceneView), Tag, KeyCode.G)]
        public static void UseFill()
        {
            var window = Painting;
            if (window != null) window.SetTool(MaskPainterWindow.PaintTool.Fill);
        }

        [Shortcut("Mask Painter/Smooth", typeof(SceneView), Tag, KeyCode.S)]
        public static void UseSmooth()
        {
            var window = Painting;
            if (window != null) window.SetTool(MaskPainterWindow.PaintTool.Smooth);
        }

        [Shortcut("Mask Painter/Swap White and Black", typeof(SceneView), Tag, KeyCode.X)]
        public static void Swap()
        {
            var window = Painting;
            if (window != null) window.SwapPaint();
        }
    }
}
