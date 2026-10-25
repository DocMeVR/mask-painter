using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DocMe.MaskPainter.Editor
{
    public enum MaskStart { Black, White, Texture }

    /// <summary>
    /// One mask being painted on one material of one renderer: the painting (kept on a hidden MaskPainterData), the mesh
    /// it is laid out on, the history, and the preview the Scene view draws. The window keeps it and calls Restore after
    /// a script reload, or picks a painting up again with FindParked. Nothing here changes the renderer or its materials.
    /// </summary>
    [Serializable]
    public sealed class MaskSession
    {
        [SerializeField] MaskPainterData data;

        [NonSerialized] MaskMesh mesh;
        [NonSerialized] MaskHistory history;
        [NonSerialized] Mesh preview;
        [NonSerialized] Texture2D texture;
        [NonSerialized] Matrix4x4 bakedAt;
        [NonSerialized] float[] strokeBefore;
        [NonSerialized] Vector3 lastDab;
        [NonSerialized] Vector3 strokeFacing;
        [NonSerialized] float strokeRadius, strokeSoftness, strokeValue, strokeStrength;
        [NonSerialized] bool mirrorStroking;
        [NonSerialized] Vector3 lastMirrorDab;

        /// <summary>
        /// When set, every fill and stroke is also made at the mirrored point of the model (see MirrorAcross). Not kept
        /// over a reload: the window sets it before painting.
        /// </summary>
        [NonSerialized] public Matrix4x4? Mirror;

        /// <summary>The mirror across root's own left and right (its local x = 0), as an avatar is built.</summary>
        public static Matrix4x4 MirrorAcross(Transform root) =>
            root.localToWorldMatrix * Matrix4x4.Scale(new Vector3(-1f, 1f, 1f)) * root.worldToLocalMatrix;

        public Renderer Renderer => data != null ? data.Renderer : null;
        public int UvChannel => data != null ? data.UvChannel : 0;
        public int MaterialIndex => data != null ? data.MaterialIndex : 0;
        public MaskCanvas Canvas => data != null ? data.Canvas : null;
        public MaskMesh Mesh => mesh;
        public string SavedPath => data != null && !string.IsNullOrEmpty(data.SavedPath) ? data.SavedPath : null;
        public bool Unsaved => data != null && data.Unsaved;
        /// <summary>SavedPath is the PNG the painting started from and has not been written yet.</summary>
        public bool ReplacesStart => data != null && data.ReplacesStart && SavedPath != null;
        public MaskChannel Channel => Canvas != null ? Canvas.Channel : MaskChannel.All;
        /// <summary>Whether there is a painting at all, even while its mesh is gone.</summary>
        public bool HasPainting => Canvas != null && Canvas.Values != null && Canvas.Values.Length > 0;
        public bool IsLive => mesh != null && HasPainting && Renderer != null;
        public bool IsStroking => strokeBefore != null;

        /// <summary>The material the mask is for, or null.</summary>
        public Material Material
        {
            get
            {
                var materials = Renderer != null ? Renderer.sharedMaterials : null;
                return materials != null && MaterialIndex < materials.Length ? materials[MaterialIndex] : null;
            }
        }

        /// <summary>
        /// A new mask for one material of renderer, laid out on its UV channel. Null when the renderer has no mesh. A
        /// start texture that is a PNG in Assets becomes the file Save writes, once the window has asked; any other is
        /// only read, and the first save asks where to.
        /// </summary>
        public static MaskSession Start(Renderer renderer, int uvChannel, int materialIndex, int size, MaskStart start, Texture from)
        {
            var mesh = MaskMesh.From(renderer, uvChannel, materialIndex);
            if (mesh == null) return null;
            var canvas = start == MaskStart.Texture && from != null ? MaskFile.Read(from) : new MaskCanvas(size, size, start == MaskStart.White ? 1f : 0f);
            var data = ScriptableObject.CreateInstance<MaskPainterData>();
            data.hideFlags = HideFlags.HideAndDontSave;
            data.Canvas = canvas;
            data.Renderer = renderer;
            data.UvChannel = uvChannel;
            data.MaterialIndex = materialIndex;
            data.UndoMarker = ScriptableObject.CreateInstance<MaskPainterUndo>();
            data.UndoMarker.hideFlags = HideFlags.HideAndDontSave;
            data.Started = DateTime.UtcNow.Ticks;
            if (start == MaskStart.Texture && from != null && MaskFile.CanWriteBack(from))
            {
                data.SavedPath = AssetDatabase.GetAssetPath(from);
                data.ReplacesStart = true;
            }
            var session = new MaskSession { data = data };
            session.Attach(mesh);
            return session;
        }

        /// <summary>The newest painting a closed or rebuilt window left behind, or null.</summary>
        public static MaskSession FindParked()
        {
            var data = Resources.FindObjectsOfTypeAll<MaskPainterData>()
                .Where(d => d.Canvas != null && d.Canvas.Values != null && d.Canvas.Values.Length > 0 && d.UndoMarker != null)
                .OrderByDescending(d => d.Started)
                .FirstOrDefault();
            return data != null ? new MaskSession { data = data } : null;
        }

        /// <summary>Every painting with changes not saved, closed windows included.</summary>
        public static List<MaskSession> AllUnsaved() =>
            Resources.FindObjectsOfTypeAll<MaskPainterData>().Where(d => d.Unsaved && d.Canvas != null).Select(d => new MaskSession { data = d }).ToList();

        /// <summary>Rebuilds what a script reload or a closed window dropped. False when the renderer or the painting is gone.</summary>
        public bool Restore()
        {
            if (IsLive) return true;
            if (Renderer == null || !HasPainting || data.UndoMarker == null) return false;
            var fresh = MaskMesh.From(Renderer, UvChannel, MaterialIndex);
            if (fresh == null) return false;
            Attach(fresh);
            return true;
        }

        /// <summary>
        /// Each painting's history by its data object, so a closed or rebuilt window keeps its Undo steps. Only a script
        /// reload clears it; the history then starts again at the step Unity's Undo holds.
        /// </summary>
        static readonly Dictionary<int, MaskHistory> Histories = new Dictionary<int, MaskHistory>();

        void Attach(MaskMesh fresh)
        {
            mesh = fresh;
            bakedAt = Renderer.transform.localToWorldMatrix;
            Canvas.EnsurePixels();
            Canvas.Bind(mesh);
            int id = data.GetInstanceID();
            if (!Histories.TryGetValue(id, out history))
            {
                history = new MaskHistory(data.UndoMarker.Step);
                Histories[id] = history;
            }
            // Ctrl+Z pressed while no window followed the painting.
            if (history.Position != data.UndoMarker.Step) history.MoveTo(data.UndoMarker.Step, Canvas);
            ReplacePreview();
            UploadAll();
        }

        /// <summary>
        /// Lets go of the preview before a script reload or when the window closes; Restore builds it again. The painting
        /// stays, and a stroke in progress is kept as an Undo step.
        /// </summary>
        public void Suspend()
        {
            if (IsStroking) EndStroke();
            if (preview != null) Object.DestroyImmediate(preview);
            if (texture != null) Object.DestroyImmediate(texture);
            preview = null;
            texture = null;
            mesh = null;
        }

        /// <summary>Throws the painting away.</summary>
        public void Dispose()
        {
            Suspend();
            if (data == null) return;
            Histories.Remove(data.GetInstanceID());
            if (data.UndoMarker != null)
            {
                Undo.ClearUndo(data.UndoMarker);
                Object.DestroyImmediate(data.UndoMarker);
            }
            Object.DestroyImmediate(data);
            data = null;
        }

        /// <summary>Reads the pose again when the renderer moved since the last read, or always when forced (bones move without it).</summary>
        public void Rebake(bool force)
        {
            if (!IsLive) return;
            var now = Renderer.transform.localToWorldMatrix;
            if (!force && now == bakedAt) return;
            bakedAt = now;
            if (!mesh.Rebake(Renderer))
            {
                // The mesh itself changed (a different mesh was put in): start over on it, keeping the values.
                var fresh = MaskMesh.From(Renderer, UvChannel, MaterialIndex);
                if (fresh != null) Attach(fresh);
                return;
            }
            // Only the positions moved: the pieces and the texels they cover stay.
            preview.vertices = mesh.Positions;
            preview.RecalculateBounds();
        }

        public bool Raycast(Ray ray, out MaskHit hit)
        {
            hit = default;
            if (!IsLive) return false;
            Rebake(false);
            return mesh.Raycast(ray, out hit);
        }

        /// <summary>Whether hit belongs to the mesh as it is now (a hit taken before the mesh changed does not).</summary>
        public bool IsValid(MaskHit hit) => IsLive && hit.Triangle >= 0 && hit.Triangle < mesh.TriangleCount;

        public int IslandAt(MaskHit hit) => mesh.Island[hit.Triangle];

        /// <summary>
        /// The point of the model mirrored from hit: the surface facing the mirrored way that lies closest to the
        /// mirrored point, within 2 cm at the avatar's size.
        /// </summary>
        public bool MirroredHit(MaskHit hit, out MaskHit mirrored)
        {
            mirrored = default;
            if (Mirror == null || !IsValid(hit)) return false;
            var m = Mirror.Value;
            var point = m.MultiplyPoint3x4(hit.Point);
            var normal = m.MultiplyVector(mesh.NormalAt(hit)).normalized;
            float reach = 0.02f * Mathf.Max(1e-4f, ((Vector3)m.GetColumn(0)).magnitude);
            return mesh.FacingSurfaceNear(point, normal, reach, out mirrored);
        }

        /// <param name="amount">How far the piece moves toward value: 1 sets it, below 1 builds up as a brush with that strength does.</param>
        public void FillPiece(MaskHit hit, float value, float amount = 1f)
        {
            if (!IsValid(hit)) return;
            int island = IslandAt(hit);
            int other = MirroredHit(hit, out var mirrored) ? IslandAt(mirrored) : island;
            var area = Canvas.IslandArea(island);
            if (other != island) area = Union(area, Canvas.IslandArea(other));
            var before = Canvas.Snapshot(area);
            Canvas.FillIsland(island, value, amount);
            if (other != island) Canvas.FillIsland(other, value, amount);
            Record(MaskText.T("undo.fillpiece"), area, before);
        }

        static RectInt Union(RectInt a, RectInt b)
        {
            if (a.width == 0 || a.height == 0) return b;
            if (b.width == 0 || b.height == 0) return a;
            int x0 = Mathf.Min(a.xMin, b.xMin), y0 = Mathf.Min(a.yMin, b.yMin);
            return new RectInt(x0, y0, Mathf.Max(a.xMax, b.xMax) - x0, Mathf.Max(a.yMax, b.yMax) - y0);
        }

        public void FillAll(float value)
        {
            var before = Canvas.Snapshot();
            Canvas.Fill(value);
            Record(MaskText.T(value > 0.5f ? "undo.fillwhite" : "undo.fillblack"), before);
        }

        public void Invert()
        {
            var before = Canvas.Snapshot();
            Canvas.Invert();
            Record(MaskText.T("undo.invert"), before);
        }

        /// <param name="strength">The most this stroke moves a texel toward value (0 to 1); strokes add up.</param>
        public void BeginStroke(MaskHit hit, float radius, float softness, float value, float strength = 1f)
        {
            if (IsStroking) EndStroke();
            if (!IsValid(hit)) return;
            strokeBefore = Canvas.BeginStroke();
            strokeRadius = radius;
            strokeSoftness = softness;
            strokeValue = value;
            strokeStrength = Mathf.Clamp01(strength);
            strokeFacing = mesh.NormalAt(hit);
            lastDab = hit.Point;
            Canvas.Brush(hit.Point, radius, softness, value, strokeStrength, strokeFacing);
            mirrorStroking = false;
            MirrorStroke(hit, true);
        }

        /// <summary>
        /// Paints from the last dab to the new point, a dab every fifth of the brush size, so a fast drag leaves no gaps.
        /// After the drag left the model (jump), it starts again at the new point instead of painting across the gap.
        /// </summary>
        public void ContinueStroke(MaskHit hit, bool jump = false)
        {
            if (!IsStroking || !IsValid(hit)) return;
            strokeFacing = mesh.NormalAt(hit);
            if (jump)
            {
                lastDab = hit.Point;
                Canvas.Brush(hit.Point, strokeRadius, strokeSoftness, strokeValue, strokeStrength, strokeFacing);
            }
            else if (DabsTo(ref lastDab, hit.Point, out var dabs))
                Canvas.BrushPath(dabs, strokeRadius, strokeSoftness, strokeValue, strokeStrength, strokeFacing);
            MirrorStroke(hit, jump);
        }

        /// <summary>The same stroke at the mirrored point; it starts again wherever the mirrored side was not found before.</summary>
        void MirrorStroke(MaskHit hit, bool jump)
        {
            if (!MirroredHit(hit, out var mirrored))
            {
                mirrorStroking = false;
                return;
            }
            var facing = mesh.NormalAt(mirrored);
            if (jump || !mirrorStroking)
            {
                lastMirrorDab = mirrored.Point;
                Canvas.Brush(mirrored.Point, strokeRadius, strokeSoftness, strokeValue, strokeStrength, facing);
            }
            else if (DabsTo(ref lastMirrorDab, mirrored.Point, out var dabs))
                Canvas.BrushPath(dabs, strokeRadius, strokeSoftness, strokeValue, strokeStrength, facing);
            mirrorStroking = true;
        }

        /// <summary>The dabs from last to to, one every fifth of the brush size; false while to is still closer than that.</summary>
        bool DabsTo(ref Vector3 last, Vector3 to, out Vector3[] dabs)
        {
            dabs = null;
            float spacing = Mathf.Max(strokeRadius * 0.2f, 1e-4f);
            float distance = Vector3.Distance(last, to);
            if (distance < spacing) return false;
            int steps = Mathf.Min(Mathf.CeilToInt(distance / spacing), 64);
            dabs = new Vector3[steps];
            for (int i = 1; i <= steps; i++) dabs[i - 1] = Vector3.Lerp(last, to, i / (float)steps);
            last = to;
            return true;
        }

        public void EndStroke()
        {
            if (!IsStroking) return;
            Canvas.EndStroke();
            var before = strokeBefore;
            strokeBefore = null;
            Record(MaskText.T("undo.paint"), before);
        }

        void Record(string name, float[] before) => Record(name, new RectInt(0, 0, Canvas.Width, Canvas.Height), before);

        /// <summary>Shows and paints another channel from now on; a stroke being painted is kept as a step first.</summary>
        public void SelectChannel(MaskChannel channel)
        {
            if (!HasPainting || Canvas.Channel == channel) return;
            if (IsStroking) EndStroke();
            Canvas.SelectChannel(channel);
        }

        void Record(string name, RectInt area, float[] before)
        {
            Undo.RecordObject(data.UndoMarker, name);
            if (!history.Commit(area, before, Canvas)) return;
            data.UndoMarker.Step = history.Position;
            data.Unsaved = true;
        }

        /// <summary>
        /// Follows Unity's Undo and Redo: called from Undo.undoRedoPerformed. A stroke still being painted is dropped
        /// first, since it is not a step yet.
        /// </summary>
        public void FollowUndo()
        {
            if (!IsLive) return;
            if (IsStroking)
            {
                Canvas.EndStroke();
                Canvas.Load(strokeBefore);
                strokeBefore = null;
            }
            if (history.Position == data.UndoMarker.Step) return;
            history.MoveTo(data.UndoMarker.Step, Canvas);
            data.Unsaved = true;
        }

        /// <summary>The mask as the Scene view preview shows it, with the latest changes uploaded.</summary>
        public Texture2D PreviewTexture
        {
            get
            {
                if (!IsLive) return null;
                if (texture == null || texture.width != Canvas.Width || texture.height != Canvas.Height) UploadAll();
                else if (Canvas.TakeChanged(out var rect)) Upload(rect);
                return texture;
            }
        }

        public Mesh PreviewMesh => preview;

        void ReplacePreview()
        {
            if (preview != null) Object.DestroyImmediate(preview);
            preview = mesh.ToPreviewMesh();
        }

        void UploadAll()
        {
            if (texture != null && (texture.width != Canvas.Width || texture.height != Canvas.Height))
            {
                Object.DestroyImmediate(texture);
                texture = null;
            }
            if (texture == null)
                texture = new Texture2D(Canvas.Width, Canvas.Height, TextureFormat.R8, false, true)
                {
                    name = "Mask Painter Preview",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };
            Canvas.TakeChanged(out _);
            Upload(new RectInt(0, 0, Canvas.Width, Canvas.Height));
        }

        /// <summary>Converts the changed rectangle; Apply then sends the whole texture.</summary>
        void Upload(RectInt rect)
        {
            var pixels = texture.GetPixelData<byte>(0);
            var values = Canvas.Values;
            int width = Canvas.Width;
            for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                {
                    int i = y * width + x;
                    pixels[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(values[i]) * 255f);
                }
            texture.Apply(false);
        }

        public Texture2D Save(string assetPath)
        {
            var saved = MaskFile.Save(Canvas, assetPath);
            data.SavedPath = assetPath;
            data.ReplacesStart = false;
            data.Unsaved = false;
            return saved;
        }
    }
}
