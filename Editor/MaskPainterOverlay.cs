using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine.UIElements;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>The Mask Painter panel in the Scene view, shown only while a painting is open in the Mask Painter window.</summary>
    [Overlay(typeof(SceneView), "docme-mask-painter", "Mask Painter", true)]
    public sealed class MaskPainterOverlay : Overlay, ITransientOverlay
    {
        public bool visible => MaskPainterWindow.Active != null && MaskPainterWindow.Active.IsPainting;

        public override VisualElement CreatePanelContent() => new MaskPaintPanel();
    }
}
