using System;
using System.Collections.Generic;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// A mask being painted: width x height values from 0 (black) to 1 (white), laid out like a mesh's UV map. Bound to a
    /// mesh, it knows which texels each UV island covers, so it can fill pieces, paint with a brush measured on the model,
    /// and bleed a few texels into the empty space around the pieces.
    /// </summary>
    [Serializable]
    public sealed class MaskCanvas
    {
        public int Width;
        public int Height;
        public float[] Values;

        /// <summary>How many texels a piece's edge values spread into the empty space around it.</summary>
        public const int Bleed = 2;

        [NonSerialized] MaskMesh mesh;
        [NonSerialized] int[] owner;
        [NonSerialized] RectInt[] islandRects;
        [NonSerialized] float[] strokeStart;
        [NonSerialized] float[] strokeWeight;
        [NonSerialized] bool hasChanged;
        [NonSerialized] RectInt changed;

        public MaskCanvas(int width, int height, float fill)
        {
            Width = width;
            Height = height;
            Values = new float[width * height];
            for (int i = 0; i < Values.Length; i++) Values[i] = fill;
        }

        public float this[int x, int y]
        {
            get => Values[y * Width + x];
            set => Values[y * Width + x] = value;
        }

        /// <summary>The red channel of a readable texture, as stored.</summary>
        public static MaskCanvas From(Texture2D texture)
        {
            var canvas = new MaskCanvas(texture.width, texture.height, 0f);
            var pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++) canvas.Values[i] = pixels[i].r / 255f;
            return canvas;
        }

        public Texture2D ToTexture()
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[Values.Length];
            for (int i = 0; i < Values.Length; i++)
            {
                byte v = (byte)Mathf.RoundToInt(Mathf.Clamp01(Values[i]) * 255f);
                pixels[i] = new Color32(v, v, v, 255);
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        RectInt All => new RectInt(0, 0, Width, Height);

        public float[] Snapshot() => (float[])Values.Clone();

        /// <summary>The values inside area, row by row.</summary>
        public float[] Snapshot(RectInt area)
        {
            var values = new float[area.width * area.height];
            for (int y = 0; y < area.height; y++)
                Array.Copy(Values, (area.y + y) * Width + area.x, values, y * area.width, area.width);
            return values;
        }

        /// <summary>
        /// Starts a stroke: until EndStroke, each texel keeps the strongest brush weight it got, so dabs that overlap
        /// along a drag do not build up the soft edge. Returns the values before the stroke.
        /// </summary>
        public float[] BeginStroke()
        {
            strokeStart = Snapshot();
            strokeWeight = new float[Values.Length];
            return strokeStart;
        }

        public void EndStroke()
        {
            strokeStart = null;
            strokeWeight = null;
        }

        /// <summary>Puts every value back to an earlier snapshot of the whole canvas.</summary>
        public void Load(float[] values)
        {
            Array.Copy(values, Values, Values.Length);
            MarkChanged(All);
        }

        /// <summary>The area changed since the last call, if any, so a preview uploads only that part.</summary>
        public bool TakeChanged(out RectInt rect)
        {
            rect = changed;
            bool any = hasChanged;
            hasChanged = false;
            return any;
        }

        void MarkChanged(RectInt rect)
        {
            if (!hasChanged)
            {
                changed = rect;
                hasChanged = true;
                return;
            }
            int x0 = Mathf.Min(changed.xMin, rect.xMin), y0 = Mathf.Min(changed.yMin, rect.yMin);
            int x1 = Mathf.Max(changed.xMax, rect.xMax), y1 = Mathf.Max(changed.yMax, rect.yMax);
            changed = new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        RectInt Grow(RectInt rect, int by)
        {
            int x0 = Mathf.Max(0, rect.xMin - by), y0 = Mathf.Max(0, rect.yMin - by);
            int x1 = Mathf.Min(Width, rect.xMax + by), y1 = Mathf.Min(Height, rect.yMax + by);
            return new RectInt(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }

        /// <summary>Sets every texel, pieces and gaps alike.</summary>
        public void Fill(float value)
        {
            for (int i = 0; i < Values.Length; i++) Values[i] = value;
            MarkChanged(All);
        }

        public void Invert()
        {
            for (int i = 0; i < Values.Length; i++) Values[i] = 1f - Values[i];
            MarkChanged(All);
        }

        /// <summary>Writes values kept by the history (0 to 65535) back into a rectangle.</summary>
        public void Restore(RectInt rect, ushort[] values)
        {
            int k = 0;
            for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                    Values[y * Width + x] = values[k++] / 65535f;
            MarkChanged(rect);
        }

        /// <summary>Learns which island covers each texel, and the rectangle each island covers. The values stay as they are.</summary>
        public void Bind(MaskMesh mesh)
        {
            this.mesh = mesh;
            owner = new int[Width * Height];
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                int island = mesh.Island[t];
                Rasterise(t, (x, y, b) => owner[y * Width + x] = island);
            }
            var min = new Vector2Int[mesh.IslandCount];
            var max = new Vector2Int[mesh.IslandCount];
            for (int k = 0; k < min.Length; k++)
            {
                min[k] = new Vector2Int(int.MaxValue, int.MaxValue);
                max[k] = new Vector2Int(-1, -1);
            }
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int island = owner[y * Width + x];
                    if (island < 0) continue;
                    min[island] = Vector2Int.Min(min[island], new Vector2Int(x, y));
                    max[island] = Vector2Int.Max(max[island], new Vector2Int(x, y));
                }
            islandRects = new RectInt[min.Length];
            for (int k = 0; k < min.Length; k++)
                islandRects[k] = max[k].x < 0 ? new RectInt(0, 0, 0, 0) : new RectInt(min[k].x, min[k].y, max[k].x - min[k].x + 1, max[k].y - min[k].y + 1);
        }

        public bool IsBound => mesh != null;

        /// <summary>The bound mesh's island at a texel, or -1 in the gaps between pieces.</summary>
        public int IslandAt(int x, int y) => owner != null ? owner[y * Width + x] : -1;

        /// <summary>The texels a fill of island can change: the island's rectangle and its bleed.</summary>
        public RectInt IslandArea(int island) => Grow(islandRects[island], Bleed);

        /// <summary>Sets every texel of one island to value.</summary>
        public void FillIsland(int island, float value)
        {
            var rect = islandRects[island];
            if (rect.width == 0) return;
            for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                    if (owner[y * Width + x] == island) Values[y * Width + x] = value;
            BleedIntoGaps(rect);
            MarkChanged(IslandArea(island));
        }

        /// <summary>
        /// Paints value with a round brush on the model: every texel of a nearby triangle gets its 3D position from the
        /// triangle's corners, so the brush is round on the model and crosses UV seams. Inside radius x (1 - softness)
        /// it paints fully, then fades out at radius. With a facing direction, triangles that face away from it are
        /// left alone, so a stroke on the palm never reaches the back of the hand.
        /// </summary>
        public void Brush(Vector3 centre, float radius, float softness, float value, float strength = 1f, Vector3 facing = default)
        {
            BrushPath(new[] { centre }, radius, softness, value, strength, facing);
        }

        /// <summary>Brush at each centre in turn, finding the triangles near the whole path once.</summary>
        public void BrushPath(IReadOnlyList<Vector3> centres, float radius, float softness, float value, float strength = 1f, Vector3 facing = default)
        {
            if (centres.Count == 0) return;
            var pathMin = centres[0];
            var pathMax = centres[0];
            foreach (var c in centres)
            {
                pathMin = Vector3.Min(pathMin, c);
                pathMax = Vector3.Max(pathMax, c);
            }
            var middle = (pathMin + pathMax) * 0.5f;
            float reach = radius + (pathMax - pathMin).magnitude * 0.5f;
            var near = new List<int>();
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                if (!mesh.TriangleNear(t, middle, reach)) continue;
                if (facing != Vector3.zero && Vector3.Dot(mesh.FaceNormal(t), facing) < 0f) continue;
                near.Add(t);
            }
            if (near.Count == 0) return;

            float inner = radius * (1f - Mathf.Clamp01(softness));
            int minX = Width, minY = Height, maxX = -1, maxY = -1;
            var p = mesh.Positions;
            var tri = mesh.Triangles;
            foreach (var centre in centres)
                foreach (int t in near)
                {
                    if (!mesh.TriangleNear(t, centre, radius)) continue;
                    var a = p[tri[3 * t]];
                    var b = p[tri[3 * t + 1]];
                    var c = p[tri[3 * t + 2]];
                    Rasterise(t, (x, y, w) =>
                    {
                        var position = a * w.x + b * w.y + c * w.z;
                        float d = Vector3.Distance(position, centre);
                        if (d >= radius) return;
                        float weight = d <= inner ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (d - inner) / Mathf.Max(radius - inner, 1e-6f));
                        int i = y * Width + x;
                        float amount = Mathf.Clamp01(weight * strength);
                        if (strokeStart == null) Values[i] += (value - Values[i]) * amount;
                        else if (amount > strokeWeight[i])
                        {
                            strokeWeight[i] = amount;
                            Values[i] = strokeStart[i] + (value - strokeStart[i]) * amount;
                        }
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    });
                }
            if (maxX < 0) return;
            var painted = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            BleedIntoGaps(painted);
            MarkChanged(Grow(painted, Bleed));
        }

        /// <summary>
        /// Calls paint for every texel whose centre lies in triangle t on the UV map, with its barycentric weights. A
        /// triangle too small to hold a texel centre gets the texel under its middle, so tiny pieces can still be painted.
        /// </summary>
        void Rasterise(int t, Action<int, int, Vector3> paint)
        {
            var uv = mesh.Uvs;
            var tri = mesh.Triangles;
            var a = Vector2.Scale(uv[tri[3 * t]], new Vector2(Width, Height));
            var b = Vector2.Scale(uv[tri[3 * t + 1]], new Vector2(Width, Height));
            var c = Vector2.Scale(uv[tri[3 * t + 2]], new Vector2(Width, Height));
            float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            bool any = false;
            if (Mathf.Abs(area) > 1e-9f)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                int x1 = Mathf.Min(Width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                int y1 = Mathf.Min(Height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                const float eps = -1e-5f;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var s = new Vector2(x + 0.5f, y + 0.5f);
                        float wb = ((s.x - a.x) * (c.y - a.y) - (c.x - a.x) * (s.y - a.y)) / area;
                        float wc = ((b.x - a.x) * (s.y - a.y) - (s.x - a.x) * (b.y - a.y)) / area;
                        float wa = 1f - wb - wc;
                        if (wa < eps || wb < eps || wc < eps) continue;
                        paint(x, y, new Vector3(wa, wb, wc));
                        any = true;
                    }
            }
            if (any) return;
            var middle = (a + b + c) / 3f;
            int mx = Mathf.FloorToInt(middle.x), my = Mathf.FloorToInt(middle.y);
            if (mx >= 0 && my >= 0 && mx < Width && my < Height) paint(mx, my, new Vector3(1f / 3f, 1f / 3f, 1f / 3f));
        }

        /// <summary>
        /// Copies the nearest covered texel's value into the empty texels up to Bleed steps away from a piece, within the
        /// changed area (grown by Bleed), so a stroke or a fill only redoes the texels around it.
        /// </summary>
        void BleedIntoGaps(RectInt changed)
        {
            if (owner == null) return;
            var area = Grow(changed, Bleed);
            int x0 = area.xMin, y0 = area.yMin, w = area.width, h = area.height;
            if (w <= 0 || h <= 0) return;
            var distance = new int[w * h];
            var queue = new Queue<int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int local = y * w + x;
                    bool covered = owner[(y0 + y) * Width + x0 + x] >= 0;
                    distance[local] = covered ? 0 : int.MaxValue;
                    if (covered) queue.Enqueue(local);
                }
            while (queue.Count > 0)
            {
                int local = queue.Dequeue();
                if (distance[local] >= Bleed) continue;
                int lx = local % w, ly = local / w;
                for (int k = 0; k < 8; k++)
                {
                    int nx = lx + (k == 0 || k == 4 || k == 5 ? 1 : k == 1 || k == 6 || k == 7 ? -1 : 0);
                    int ny = ly + (k == 2 || k == 4 || k == 6 ? 1 : k == 3 || k == 5 || k == 7 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int n = ny * w + nx;
                    if (distance[n] <= distance[local] + 1) continue;
                    distance[n] = distance[local] + 1;
                    Values[(y0 + ny) * Width + x0 + nx] = Values[(y0 + ly) * Width + x0 + lx];
                    queue.Enqueue(n);
                }
            }
        }
    }
}
