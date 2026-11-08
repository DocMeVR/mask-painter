using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// The mask as a flat picture in the window, with the UV layout's lines over it and a ring where the mouse is on the
    /// model. It follows Show: the painted channel's greys, or every channel in its colours. Looking only.
    /// </summary>
    public sealed class MaskUvView : VisualElement
    {
        public const int Size = 512;

        readonly Func<MaskPainterWindow> source;
        readonly Image image;
        RenderTexture view, wire;
        Material material, lines;
        MaskMesh wireMesh;
        (int version, MaskShow show, Vector2? hover, MaskChannel channel, MaskMesh mesh) shown;

        public MaskUvView(Func<MaskPainterWindow> source)
        {
            this.source = source;
            AddToClassList("docme-mp-uvview");
            image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.AddToClassList("docme-mp-uvimage");
            Add(image);
            RegisterCallback<GeometryChangedEvent>(e =>
            {
                if (Mathf.Abs(e.newRect.width - e.newRect.height) > 0.5f) style.height = e.newRect.width;
            });
            RegisterCallback<DetachFromPanelEvent>(_ => Release());
            schedule.Execute(Tick).Every(150);
        }

        void Tick()
        {
            if (float.IsNaN(layout.width) || layout.width <= 0f) return;
            Draw(false);
        }

        /// <summary>Draws now, even before the view has a size.</summary>
        public void Redraw() => Draw(true);

        void Draw(bool always)
        {
            var window = source();
            if (window == null || !window.IsPainting) return;
            var session = window.Session;
            _ = session.PreviewTexture;
            var key = (session.PreviewVersion, window.View, window.HoverUv, session.Channel, session.Mesh);
            if (!always && key == shown && view != null) return;
            shown = key;
            image.image = Render(session, window.View, window.HoverUv);
            image.MarkDirtyRepaint();
        }

        /// <summary>Draws the picture for a painting and returns it; null when there is nothing to draw.</summary>
        public RenderTexture Render(MaskSession session, MaskShow show, Vector2? hover)
        {
            if (session == null || !session.IsLive) return null;
            if (material == null)
            {
                var shader = Shader.Find("Hidden/DocMe/MaskPainter/Overlay");
                if (shader == null) return null;
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (view == null) view = Target(RenderTextureReadWrite.sRGB, "Mask Painter UV View");
            if (wire == null || wireMesh != session.Mesh) DrawWire(session.Mesh);
            var channel = session.Channel;
            material.SetTexture("_Wire", wire);
            material.SetFloat("_Every", show == MaskShow.EveryChannel ? 1f : 0f);
            material.SetFloat("_Channel", MaskLook.Offset(channel));
            material.SetColor("_Tint", MaskLook.Tint(channel));
            material.SetVector("_HoverUv", hover.HasValue ? new Vector4(hover.Value.x, hover.Value.y, 1f, 0f) : Vector4.zero);
            material.SetVector("_ViewSize", new Vector4(Size, Size, 0f, 0f));
            Graphics.Blit(session.PreviewTexture, view, material, 2);
            return view;
        }

        static RenderTexture Target(RenderTextureReadWrite space, string name)
        {
            var rt = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32, space) { name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// The outline of every piece of the painted part (UV edges only one triangle uses), drawn once per mesh. Inner
        /// edges are left out, so a dense mesh never buries the mask.
        /// </summary>
        void DrawWire(MaskMesh mesh)
        {
            if (wire == null) wire = Target(RenderTextureReadWrite.Linear, "Mask Painter UV Lines");
            if (lines == null) lines = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            wireMesh = mesh;
            var active = RenderTexture.active;
            try
            {
                RenderTexture.active = wire;
                GL.Clear(true, true, Color.clear);
                GL.PushMatrix();
                GL.LoadOrtho();
                lines.SetPass(0);
                GL.Begin(GL.LINES);
                GL.Color(Color.white);
                foreach (var (a, b) in Outline(mesh))
                {
                    GL.Vertex3(a.x, a.y, 0f);
                    GL.Vertex3(b.x, b.y, 0f);
                }
                GL.End();
                GL.PopMatrix();
            }
            finally { RenderTexture.active = active; }
        }

        /// <summary>The UV edges that only one triangle uses: the outlines of the pieces.</summary>
        static List<(Vector2 a, Vector2 b)> Outline(MaskMesh mesh)
        {
            var uv = mesh.Uvs;
            var tri = mesh.Triangles;
            var edges = new Dictionary<(Vector2Int, Vector2Int), (Vector2 a, Vector2 b, int count)>();
            for (int t = 0; t < tri.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    var a = uv[tri[t + e]];
                    var b = uv[tri[t + (e + 1) % 3]];
                    var ka = Vector2Int.RoundToInt(a * 65536f);
                    var kb = Vector2Int.RoundToInt(b * 65536f);
                    var key = ka.x < kb.x || (ka.x == kb.x && ka.y <= kb.y) ? (ka, kb) : (kb, ka);
                    edges[key] = edges.TryGetValue(key, out var seen) ? (seen.a, seen.b, seen.count + 1) : (a, b, 1);
                }
            var outline = new List<(Vector2, Vector2)>();
            foreach (var edge in edges.Values)
                if (edge.count == 1) outline.Add((edge.a, edge.b));
            return outline;
        }

        public void Release()
        {
            foreach (var o in new Object[] { view, wire, material, lines })
                if (o != null) Object.DestroyImmediate(o);
            view = wire = null;
            material = lines = null;
            wireMesh = null;
            image.image = null;
        }
    }
}
