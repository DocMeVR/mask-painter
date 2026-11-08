using System;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// The painting keys, in Unity's Shortcut Manager under Mask Painter so they can be changed there. They are global and
    /// carry a tag that is on only while a painting is open: then they win over Unity's global keys (X toggles the pivot
    /// rotation otherwise), and with the tag off Unity's own keys work as always. They act only while the Scene view has
    /// the focus; a key of a Scene view mode (S while flying) still wins.
    /// </summary>
    public static class MaskShortcuts
    {
        public const string Tag = "DocMe.MaskPainter.Painting";

        /// <summary>Whether the Scene view has the keyboard focus; tests swap it.</summary>
        public static Func<bool> SceneViewHasFocus = () => EditorWindow.focusedWindow is SceneView;
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
                return window != null && window.IsPainting && SceneViewHasFocus() ? window : null;
            }
        }

        [Shortcut("Mask Painter/Brush", null, Tag, KeyCode.B)]
        public static void UseBrush()
        {
            var window = Painting;
            if (window != null) window.SetTool(MaskPainterWindow.PaintTool.Brush);
        }

        [Shortcut("Mask Painter/Fill Pieces", null, Tag, KeyCode.G)]
        public static void UseFill()
        {
            var window = Painting;
            if (window != null) window.SetTool(MaskPainterWindow.PaintTool.Fill);
        }

        [Shortcut("Mask Painter/Smooth", null, Tag, KeyCode.S)]
        public static void UseSmooth()
        {
            var window = Painting;
            if (window != null) window.SetTool(MaskPainterWindow.PaintTool.Smooth);
        }

        [Shortcut("Mask Painter/Swap White and Black", null, Tag, KeyCode.X)]
        public static void Swap()
        {
            var window = Painting;
            if (window != null) window.SwapPaint();
        }
    }
}
