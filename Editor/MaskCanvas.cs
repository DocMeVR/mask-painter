using System;
using System.Collections.Generic;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>Which colour channel of the mask is painted. All paints red, green and blue together: a plain grey mask.</summary>
    public enum MaskChannel { All, Red, Green, Blue, Alpha }

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
        /// <summary>The channel being painted.</summary>
        public float[] Values;
        /// <summary>
        /// Every channel, four bytes per texel, as they were when Channel was last picked. Values holds the painted one;
        /// Commit writes it back. Null in a painting from before channels, which is grey.
        /// </summary>
        public byte[] Pixels;
        public MaskChannel Channel;

        /// <summary>How many texels a piece's edge values spread into the empty space around it.</summary>
        public const int Bleed = 2;

        [NonSerialized] MaskMesh mesh;
        [NonSerialized] int[] owner;
        [NonSerialized] RectInt[] islandRects;
        /// <summary>The side of the square tiles a stroke keeps its start values and weights in.</summary>
        const int Tile = 64;
        [NonSerialized] bool stroking;
        [NonSerialized] int tilesX;
        [NonSerialized] float[][] tileStart, tileWeight;
        [NonSerialized] List<int> savedTiles;
        [NonSerialized] Stack<float[]> tilePool;
        [NonSerialized] bool hasChanged;
        [NonSerialized] RectInt changed;
        [NonSerialized] int[] borderRowStart;
        [NonSerialized] int[] borderX;
        // Reused from one brush step to the next, so painting allocates nothing.
        [NonSerialized] List<int> nearBuffer;
        [NonSerialized] Vector3[] pathBuffer;
        [NonSerialized] int[] visitBuffer;
        [NonSerialized] int visitMark;
        [NonSerialized] int[] hitIndex;
        [NonSerialized] float[] hitWeight;
        [NonSerialized] int hitCount;
        [NonSerialized] int[] bleedMark, bleedDistance, bleedQueue;
        [NonSerialized] int bleedPass;
        [NonSerialized] float[] blurAcross, blurDone;
        [NonSerialized] double[] blurPrefix;

        public MaskCanvas(int width, int height, float fill)
        {
            Width = width;
            Height = height;
            Values = new float[width * height];
            for (int i = 0; i < Values.Length; i++) Values[i] = fill;
            Pixels = Grey(Values);
        }

        public float this[int x, int y]
        {
            get => Values[y * Width + x];
            set => Values[y * Width + x] = value;
        }

        /// <summary>Every channel of a readable texture, as stored, with red shown.</summary>
        public static MaskCanvas From(Texture2D texture)
        {
            var canvas = new MaskCanvas(texture.width, texture.height, 0f);
            var pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                canvas.Pixels[4 * i] = pixels[i].r;
                canvas.Pixels[4 * i + 1] = pixels[i].g;
                canvas.Pixels[4 * i + 2] = pixels[i].b;
                canvas.Pixels[4 * i + 3] = pixels[i].a;
                canvas.Values[i] = pixels[i].r / 255f;
            }
            return canvas;
        }

        /// <summary>Turns the colour channels of an sRGB texture into the amounts a shader reads from it; alpha stays.</summary>
        public void DecodeSrgb()
        {
            Commit();
            var linear = new byte[256];
            for (int v = 0; v < 256; v++) linear[v] = Byte(Mathf.GammaToLinearSpace(v / 255f));
            int offset = Offset(Channel);
            for (int i = 0; i < Values.Length; i++)
            {
                if (offset < 3) Values[i] = Mathf.GammaToLinearSpace(Pixels[4 * i + offset] / 255f);
                for (int c = 0; c < 3; c++) Pixels[4 * i + c] = linear[Pixels[4 * i + c]];
            }
            MarkChanged(All);
        }

        static byte[] Grey(float[] values)
        {
            var pixels = new byte[4 * values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                byte v = Byte(values[i]);
                pixels[4 * i] = pixels[4 * i + 1] = pixels[4 * i + 2] = v;
                pixels[4 * i + 3] = 255;
            }
            return pixels;
        }

        static byte Byte(float value) => (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);

        static int Offset(MaskChannel channel) => channel == MaskChannel.All ? 0 : (int)channel - 1;

        /// <summary>Writes the painted channel into Pixels everywhere.</summary>
        public void Commit() => Commit(All);

        /// <summary>
        /// Writes the painted channel into Pixels inside rect. All channels writes grey only where the value differs from
        /// red, so the texels it never painted keep their own channels.
        /// </summary>
        public void Commit(RectInt rect)
        {
            if (Pixels == null || Pixels.Length != 4 * Values.Length)
            {
                // A painting from before channels: every channel was the values.
                Pixels = Grey(Values);
                return;
            }
            int offset = Offset(Channel);
            for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                {
                    int i = y * Width + x;
                    byte v = Byte(Values[i]);
                    if (Channel != MaskChannel.All) Pixels[4 * i + offset] = v;
                    else if (v != Pixels[4 * i]) Pixels[4 * i] = Pixels[4 * i + 1] = Pixels[4 * i + 2] = v;
                }
        }

        /// <summary>Gives a painting from before channels its channels, the grey it was. Call before painting on it.</summary>
        public void EnsurePixels()
        {
            if (Pixels == null || Pixels.Length != 4 * Values.Length) Pixels = Grey(Values);
        }

        /// <summary>Every channel's bytes inside rect, row by row.</summary>
        public byte[] PixelsIn(RectInt rect)
        {
            EnsurePixels();
            var bytes = new byte[4 * rect.width * rect.height];
            for (int y = 0; y < rect.height; y++)
                Array.Copy(Pixels, 4 * ((rect.y + y) * Width + rect.x), bytes, 4 * y * rect.width, 4 * rect.width);
            return bytes;
        }

        /// <summary>Writes bytes from PixelsIn back into rect, and the painted channel's values with them.</summary>
        public void RestorePixels(RectInt rect, byte[] bytes)
        {
            int offset = Offset(Channel);
            for (int y = 0; y < rect.height; y++)
            {
                Array.Copy(bytes, 4 * y * rect.width, Pixels, 4 * ((rect.y + y) * Width + rect.x), 4 * rect.width);
                for (int x = 0; x < rect.width; x++)
                {
                    int i = (rect.y + y) * Width + rect.x + x;
                    Values[i] = Pixels[4 * i + offset] / 255f;
                }
            }
            MarkChanged(rect);
        }

        /// <summary>Keeps the channel painted so far and shows another one in Values.</summary>
        public void SelectChannel(MaskChannel channel)
        {
            Commit();
            Channel = channel;
            int offset = Offset(channel);
            for (int i = 0; i < Values.Length; i++) Values[i] = Pixels[4 * i + offset] / 255f;
            MarkChanged(All);
        }

        /// <summary>Whether any texel's alpha is below full.</summary>
        public bool UsesAlpha()
        {
            ChannelsUsed(out _, out _, out bool alpha);
            return alpha;
        }

        /// <summary>Which channels hold anything but what a format without them reads: green or blue above 0, alpha below 1.</summary>
        public void ChannelsUsed(out bool green, out bool blue, out bool alpha)
        {
            Commit();
            green = blue = alpha = false;
            for (int i = 0; i < Pixels.Length; i += 4)
            {
                green |= Pixels[i + 1] != 0;
                blue |= Pixels[i + 2] != 0;
                alpha |= Pixels[i + 3] != 255;
            }
        }

        public Texture2D ToTexture()
        {
            Commit();
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            texture.SetPixelData(Pixels, 0);
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
        /// along a drag do not build up the soft edge. The values before the stroke are kept tile by tile, as the stroke
        /// first reaches each tile, so starting one costs nothing however big the canvas is.
        /// </summary>
        public void BeginStroke()
        {
            if (stroking) ReleaseTiles();
            stroking = true;
            tilesX = (Width + Tile - 1) / Tile;
            int count = tilesX * ((Height + Tile - 1) / Tile);
            if (tileStart == null || tileStart.Length != count)
            {
                tileStart = new float[count][];
                tileWeight = new float[count][];
            }
            if (savedTiles == null) savedTiles = new List<int>();
            if (tilePool == null) tilePool = new Stack<float[]>();
        }

        public bool IsStroking => stroking;

        public void EndStroke() => EndStroke(out _);

        /// <summary>
        /// Ends a stroke. parts holds, for each tile it reached, the tile's rectangle and its values from before the
        /// stroke, row by row, for one Undo step; empty when it reached none.
        /// </summary>
        public void EndStroke(out List<(RectInt area, float[] before)> parts)
        {
            parts = new List<(RectInt, float[])>();
            if (!stroking) return;
            stroking = false;
            foreach (int tile in savedTiles)
            {
                var rect = TileRect(tile);
                var start = tileStart[tile];
                float[] before;
                if (rect.width == Tile && rect.height == Tile)
                {
                    // The step keeps this array, so the pool lets go of it.
                    before = start;
                    tileStart[tile] = null;
                }
                else
                {
                    before = new float[rect.width * rect.height];
                    for (int y = 0; y < rect.height; y++) Array.Copy(start, y * Tile, before, y * rect.width, rect.width);
                }
                parts.Add((rect, before));
            }
            ReleaseTiles();
        }

        /// <summary>Drops a stroke being painted: every texel it changed gets its value from before it back.</summary>
        public void CancelStroke()
        {
            if (!stroking) return;
            stroking = false;
            if (savedTiles.Count > 0)
            {
                foreach (int tile in savedTiles)
                {
                    var rect = TileRect(tile);
                    for (int y = 0; y < rect.height; y++)
                        Array.Copy(tileStart[tile], y * Tile, Values, (rect.y + y) * Width + rect.x, rect.width);
                }
                MarkChanged(SavedArea());
            }
            ReleaseTiles();
        }

        RectInt TileRect(int tile)
        {
            int x = tile % tilesX * Tile, y = tile / tilesX * Tile;
            return new RectInt(x, y, Mathf.Min(Tile, Width - x), Mathf.Min(Tile, Height - y));
        }

        RectInt SavedArea()
        {
            int x0 = Width, y0 = Height, x1 = 0, y1 = 0;
            foreach (int tile in savedTiles)
            {
                var rect = TileRect(tile);
                x0 = Mathf.Min(x0, rect.xMin);
                y0 = Mathf.Min(y0, rect.yMin);
                x1 = Mathf.Max(x1, rect.xMax);
                y1 = Mathf.Max(y1, rect.yMax);
            }
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>Keeps the values of every tile in area that the stroke has not reached yet, and starts its weights at 0.</summary>
        void SaveTiles(RectInt area)
        {
            if (!stroking || area.width <= 0 || area.height <= 0) return;
            for (int ty = area.yMin / Tile; ty <= (area.yMax - 1) / Tile; ty++)
                for (int tx = area.xMin / Tile; tx <= (area.xMax - 1) / Tile; tx++)
                {
                    int tile = ty * tilesX + tx;
                    if (tileStart[tile] != null) continue;
                    var start = tilePool.Count > 0 ? tilePool.Pop() : new float[Tile * Tile];
                    var weight = tilePool.Count > 0 ? tilePool.Pop() : new float[Tile * Tile];
                    Array.Clear(weight, 0, weight.Length);
                    var rect = TileRect(tile);
                    for (int y = 0; y < rect.height; y++)
                        Array.Copy(Values, (rect.y + y) * Width + rect.x, start, y * Tile, rect.width);
                    tileStart[tile] = start;
                    tileWeight[tile] = weight;
                    savedTiles.Add(tile);
                }
        }

        void ReleaseTiles()
        {
            foreach (int tile in savedTiles)
            {
                if (tileStart[tile] != null) tilePool.Push(tileStart[tile]);
                tilePool.Push(tileWeight[tile]);
                tileStart[tile] = tileWeight[tile] = null;
            }
            savedTiles.Clear();
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

        /// <summary>Makes the painted channel a copy of another channel.</summary>
        public void CopyFrom(MaskChannel from)
        {
            Commit();
            int offset = MaskLook.Offset(from);
            for (int i = 0; i < Values.Length; i++) Values[i] = Pixels[4 * i + offset] / 255f;
            MarkChanged(All);
        }

        /// <summary>
        /// Along every seam, the texels on both sides take the average of the two sides, so a shader reads the same value
        /// on either side of the seam and shows no line along it. Only a texel or two next to each seam changes.
        /// </summary>
        public void EvenSeams(IReadOnlyList<SeamEdge> seams)
        {
            if (mesh == null || seams.Count == 0) return;
            var size = new Vector2(Width, Height);
            var writes = new List<(int island, Vector2 at, float value)>();
            foreach (var seam in seams)
            {
                int ia = mesh.Island[seam.TriangleA], ib = mesh.Island[seam.TriangleB];
                Vector2 a0 = Vector2.Scale(seam.A0, size), a1 = Vector2.Scale(seam.A1, size);
                Vector2 b0 = Vector2.Scale(seam.B0, size), b1 = Vector2.Scale(seam.B1, size);
                int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max((a1 - a0).magnitude, (b1 - b0).magnitude) * 2f), 1, 8192);
                for (int k = 0; k <= steps; k++)
                {
                    float t = k / (float)steps;
                    var pa = Vector2.Lerp(a0, a1, t);
                    var pb = Vector2.Lerp(b0, b1, t);
                    if (!NearestIn(pa, ia, out float va) || !NearestIn(pb, ib, out float vb)) continue;
                    float mean = (va + vb) * 0.5f;
                    writes.Add((ia, pa, mean));
                    writes.Add((ib, pb, mean));
                }
            }
            if (writes.Count == 0) return;
            int minX = Width, minY = Height, maxX = -1, maxY = -1;
            foreach (var (island, at, value) in writes)
            {
                int cx = Mathf.FloorToInt(at.x), cy = Mathf.FloorToInt(at.y);
                for (int y = Mathf.Max(0, cy - 1); y <= Mathf.Min(Height - 1, cy + 1); y++)
                    for (int x = Mathf.Max(0, cx - 1); x <= Mathf.Min(Width - 1, cx + 1); x++)
                    {
                        int i = y * Width + x;
                        if (owner[i] != island || (new Vector2(x + 0.5f, y + 0.5f) - at).sqrMagnitude > 2.25f) continue;
                        Values[i] = value;
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
            }
            if (maxX < 0) return;
            var changedArea = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            BleedIntoGaps(changedArea);
            MarkChanged(Grow(changedArea, Bleed));
        }

        /// <summary>The value of island's texel nearest to a point on the map, within two texels.</summary>
        bool NearestIn(Vector2 at, int island, out float value)
        {
            value = 0f;
            float best = float.MaxValue;
            int cx = Mathf.FloorToInt(at.x), cy = Mathf.FloorToInt(at.y);
            for (int y = Mathf.Max(0, cy - 2); y <= Mathf.Min(Height - 1, cy + 2); y++)
                for (int x = Mathf.Max(0, cx - 2); x <= Mathf.Min(Width - 1, cx + 2); x++)
                {
                    int i = y * Width + x;
                    if (owner[i] != island) continue;
                    float d = (new Vector2(x + 0.5f, y + 0.5f) - at).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    value = Values[i];
                }
            return best < float.MaxValue;
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
            FindBorders();
        }

        public bool IsBound => mesh != null;

        /// <summary>The bound mesh's island at a texel, or -1 in the gaps between pieces.</summary>
        public int IslandAt(int x, int y) => owner != null ? owner[y * Width + x] : -1;

        /// <summary>The texels a fill of island can change: the island's rectangle and its bleed.</summary>
        public RectInt IslandArea(int island) => Grow(islandRects[island], Bleed);

        /// <summary>Moves every texel of one island toward value by amount (1 sets it exactly, 0 leaves it).</summary>
        public void FillIsland(int island, float value, float amount = 1f)
        {
            var rect = islandRects[island];
            if (rect.width == 0) return;
            amount = Mathf.Clamp01(amount);
            for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                {
                    int i = y * Width + x;
                    if (owner[i] == island) Values[i] = amount >= 1f ? value : Values[i] + (value - Values[i]) * amount;
                }
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

        /// <summary>
        /// Brush along the line through centres: each texel's weight comes from its distance to the nearest piece of the
        /// line, so a stroke is even along its length instead of a row of round dabs.
        /// </summary>
        /// <param name="continues">
        /// In a stroke, a step of two centres whose first is where the last step ended, with the same radius, softness,
        /// value and strength: the texels behind its start cannot get stronger than that step left them, so they are
        /// skipped (about half of a short step).
        /// </param>
        public void BrushPath(IReadOnlyList<Vector3> centres, float radius, float softness, float value, float strength = 1f, Vector3 facing = default, bool continues = false)
        {
            if (!Reach(centres, radius, softness, facing, out var painted, continues && stroking && centres.Count == 2)) return;
            SaveTiles(Grow(painted, Bleed));
            for (int k = 0; k < hitCount; k++)
            {
                int i = hitIndex[k];
                float amount = Mathf.Clamp01(hitWeight[k] * strength);
                if (!stroking)
                {
                    Values[i] += (value - Values[i]) * amount;
                    continue;
                }
                int x = i % Width, y = i / Width;
                int tile = y / Tile * tilesX + x / Tile, local = y % Tile * Tile + x % Tile;
                var weights = tileWeight[tile];
                if (amount <= weights[local]) continue;
                weights[local] = amount;
                float start = tileStart[tile][local];
                Values[i] = start + (value - start) * amount;
            }
            BleedIntoGaps(painted);
            MarkChanged(Grow(painted, Bleed));
        }

        /// <summary>
        /// Softens the mask along the line through centres: each texel moves toward a box blur of kernel texels, by the
        /// brush weight times strength. The blur runs along rows, then columns, and stops wherever the piece changes, so
        /// gaps and other pieces never bleed in. Its cost grows with the brush area only.
        /// </summary>
        public void Smooth(IReadOnlyList<Vector3> centres, float radius, float softness, float strength, Vector3 facing, int kernel)
        {
            if (!Reach(centres, radius, softness, facing, out var painted, false)) return;
            SaveTiles(Grow(painted, Bleed));
            var area = Grow(painted, kernel);
            int w = area.width, h = area.height, x0 = area.xMin, y0 = area.yMin;
            var across = Cleared(ref blurAcross, w * h);
            var blurred = Cleared(ref blurDone, w * h);
            if (blurPrefix == null || blurPrefix.Length < Mathf.Max(w, h) + 1) blurPrefix = new double[Mathf.Max(w, h) + 1];
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * Width + x0;
                BlurLine(w, row, 1, Values, row, 1, across, y * w, 1, kernel, blurPrefix);
            }
            for (int x = 0; x < w; x++)
                BlurLine(h, y0 * Width + x0 + x, Width, across, x, w, blurred, x, w, kernel, blurPrefix);
            for (int k = 0; k < hitCount; k++)
            {
                int i = hitIndex[k];
                float mean = blurred[(i / Width - y0) * w + i % Width - x0];
                Values[i] += (mean - Values[i]) * Mathf.Clamp01(hitWeight[k] * strength);
            }
            BleedIntoGaps(painted);
            MarkChanged(Grow(painted, Bleed));
        }

        static float[] Cleared(ref float[] buffer, int size)
        {
            if (buffer == null || buffer.Length < size) buffer = new float[Mathf.Max(size, 4096)];
            else Array.Clear(buffer, 0, size);
            return buffer;
        }

        /// <summary>
        /// A box blur of kernel along one line of length texels, inside each run of one piece. The line's texels sit at
        /// ownerStart + k x ownerStep in the canvas, are read from source at readStart + k x readStep and written to
        /// target at writeStart + k x writeStep.
        /// </summary>
        void BlurLine(int length, int ownerStart, int ownerStep, float[] source, int readStart, int readStep, float[] target, int writeStart, int writeStep, int kernel, double[] prefix)
        {
            prefix[0] = 0;
            for (int k = 0; k < length; k++) prefix[k + 1] = prefix[k] + source[readStart + k * readStep];
            int start = 0;
            while (start < length)
            {
                int piece = owner[ownerStart + start * ownerStep];
                int end = start;
                while (end < length && owner[ownerStart + end * ownerStep] == piece) end++;
                if (piece >= 0)
                    for (int k = start; k < end; k++)
                    {
                        int a = Mathf.Max(start, k - kernel), b = Mathf.Min(end - 1, k + kernel);
                        target[writeStart + k * writeStep] = (float)((prefix[b + 1] - prefix[a]) / (b - a + 1));
                    }
                start = end;
            }
        }

        /// <summary>
        /// Every texel within radius of the line through centres, once each, with its brush weight (in hitIndex and
        /// hitWeight): full inside radius x (1 - softness), fading out at radius. Triangles facing away from facing are
        /// skipped. False when none. Nothing is allocated once the buffers have grown.
        /// </summary>
        /// <param name="behind">Leave out what lies behind the first centre, seen along the line to the second.</param>
        bool Reach(IReadOnlyList<Vector3> centres, float radius, float softness, Vector3 facing, out RectInt painted, bool behind)
        {
            painted = default;
            hitCount = 0;
            int count = centres.Count;
            if (count == 0) return false;
            if (pathBuffer == null || pathBuffer.Length < count) pathBuffer = new Vector3[Mathf.Max(8, count)];
            var path = pathBuffer;
            for (int k = 0; k < count; k++) path[k] = centres[k];
            var pathMin = path[0];
            var pathMax = path[0];
            for (int k = 0; k < count; k++)
            {
                pathMin = Vector3.Min(pathMin, path[k]);
                pathMax = Vector3.Max(pathMax, path[k]);
            }
            var middle = (pathMin + pathMax) * 0.5f;
            float reach = radius + (pathMax - pathMin).magnitude * 0.5f;
            if (nearBuffer == null) nearBuffer = new List<int>();
            var near = nearBuffer;
            mesh.TrianglesNear(middle, reach, near);
            var p = mesh.Positions;
            var tri = mesh.Triangles;
            var uv = mesh.Uvs;
            float fw = Width, fh = Height;

            // Keep the triangles that face the brush and come within radius of the path somewhere, and the texel
            // rectangle they cover.
            float far = radius * 1.001f + 1e-6f;
            int kept = 0, rx0 = Width, ry0 = Height, rx1 = -1, ry1 = -1;
            for (int n = 0; n < near.Count; n++)
            {
                int t = near[n];
                if (facing != Vector3.zero && Vector3.Dot(mesh.FaceNormal(t), facing) < 0f) continue;
                var a = p[tri[3 * t]];
                var b = p[tri[3 * t + 1]];
                var c = p[tri[3 * t + 2]];
                var centre = (a + b + c) / 3f;
                float spread = Mathf.Sqrt(Mathf.Max((a - centre).sqrMagnitude, Mathf.Max((b - centre).sqrMagnitude, (c - centre).sqrMagnitude)));
                if (DistanceToPath(centre, path, count) - spread > far) continue;
                if (behind)
                {
                    var step = path[1] - path[0];
                    if (Vector3.Dot(a - path[0], step) <= 0f && Vector3.Dot(b - path[0], step) <= 0f && Vector3.Dot(c - path[0], step) <= 0f) continue;
                }
                near[kept++] = t;
                var ua = uv[tri[3 * t]];
                var ub = uv[tri[3 * t + 1]];
                var uc = uv[tri[3 * t + 2]];
                int bx0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ua.x * fw, Mathf.Min(ub.x * fw, uc.x * fw))));
                int bx1 = Mathf.Min(Width - 1, Mathf.CeilToInt(Mathf.Max(ua.x * fw, Mathf.Max(ub.x * fw, uc.x * fw))));
                int by0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ua.y * fh, Mathf.Min(ub.y * fh, uc.y * fh))));
                int by1 = Mathf.Min(Height - 1, Mathf.CeilToInt(Mathf.Max(ua.y * fh, Mathf.Max(ub.y * fh, uc.y * fh))));
                if (bx0 < rx0) rx0 = bx0;
                if (by0 < ry0) ry0 = by0;
                if (bx1 > rx1) rx1 = bx1;
                if (by1 > ry1) ry1 = by1;
            }
            if (kept == 0 || rx1 < rx0 || ry1 < ry0) return false;

            int rw = rx1 - rx0 + 1, rh = ry1 - ry0 + 1;
            if (visitBuffer == null || visitBuffer.Length < rw * rh)
            {
                visitBuffer = new int[Mathf.Max(rw * rh, 4096)];
                visitMark = 0;
            }
            if (++visitMark == int.MaxValue)
            {
                Array.Clear(visitBuffer, 0, visitBuffer.Length);
                visitMark = 1;
            }
            int mark = visitMark;
            var visited = visitBuffer;
            float inner = radius * (1f - Mathf.Clamp01(softness));
            float fade = Mathf.Max(radius - inner, 1e-6f);
            float outSq = radius * radius;
            float innerSq = inner * inner;
            var s0 = path[0];
            var ab = count == 2 ? path[1] - path[0] : Vector3.zero;
            float abLength = ab.sqrMagnitude;
            float abInverse = abLength > 1e-12f ? 1f / abLength : 0f;
            int minX = Width, minY = Height, maxX = -1, maxY = -1;

            // One texel at model point (px, py, pz): its distance to the path, and its weight when within radius and
            // not taken by an earlier triangle.
            void Take(int x, int y, float px, float py, float pz)
            {
                float q;
                if (count <= 2)
                {
                    float rx = px - s0.x, ry = py - s0.y, rz = pz - s0.z;
                    if (count == 2)
                    {
                        float ahead = (rx * ab.x + ry * ab.y + rz * ab.z) * abInverse;
                        if (behind && ahead <= 0f) return;
                        float along = Mathf.Clamp01(ahead);
                        rx -= ab.x * along;
                        ry -= ab.y * along;
                        rz -= ab.z * along;
                    }
                    q = rx * rx + ry * ry + rz * rz;
                }
                else
                {
                    float d = DistanceToPath(new Vector3(px, py, pz), path, count);
                    q = d * d;
                }
                if (q >= outSq) return;
                float weight = q <= innerSq ? 1f : 1f - Mathf.SmoothStep(0f, 1f, ((float)Math.Sqrt(q) - inner) / fade);
                int local = (y - ry0) * rw + (x - rx0);
                if (visited[local] == mark) return;
                visited[local] = mark;
                AddHit(y * Width + x, weight);
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }

            for (int n = 0; n < kept; n++)
            {
                int t = near[n];
                var pa = p[tri[3 * t]];
                var pb = p[tri[3 * t + 1]];
                var pc = p[tri[3 * t + 2]];
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
                    float inverse = 1f / area;
                    float cyay = c.y - a.y, cxax = c.x - a.x, bxax = b.x - a.x, byay = b.y - a.y;
                    // Along a row each weight changes by the same step per texel, and so does the point on the model.
                    float stepB = cyay * inverse, stepC = -byay * inverse, stepA = -(stepB + stepC);
                    float stepX = pa.x * stepA + pb.x * stepB + pc.x * stepC;
                    float stepY = pa.y * stepA + pb.y * stepB + pc.y * stepC;
                    float stepZ = pa.z * stepA + pb.z * stepB + pc.z * stepC;
                    for (int y = y0; y <= y1; y++)
                    {
                        float syay = y + 0.5f - a.y;
                        float sxax = x0 + 0.5f - a.x;
                        float baseB = (sxax * cyay - cxax * syay) * inverse;
                        float baseC = (bxax * syay - sxax * byay) * inverse;
                        float baseA = 1f - baseB - baseC;
                        // The texels of this row inside the triangle: where each weight stays above eps.
                        float from = 0f, to = x1 - x0;
                        if (!Span(baseA, stepA, eps, ref from, ref to) || !Span(baseB, stepB, eps, ref from, ref to) || !Span(baseC, stepC, eps, ref from, ref to)) continue;
                        int kFrom = Mathf.Max(0, Mathf.FloorToInt(from) - 1), kTo = Mathf.Min(x1 - x0, Mathf.CeilToInt(to) + 1);
                        float px0 = pa.x * baseA + pb.x * baseB + pc.x * baseC;
                        float py0 = pa.y * baseA + pb.y * baseB + pc.y * baseC;
                        float pz0 = pa.z * baseA + pb.z * baseB + pc.z * baseC;
                        for (int k = kFrom; k <= kTo; k++)
                        {
                            float wa = baseA + stepA * k, wb = baseB + stepB * k, wc = baseC + stepC * k;
                            if (wa < eps || wb < eps || wc < eps) continue;
                            any = true;
                            Take(x0 + k, y, px0 + stepX * k, py0 + stepY * k, pz0 + stepZ * k);
                        }
                    }
                }
                if (any) continue;
                var centre = (a + b + c) / 3f;
                int mx = Mathf.FloorToInt(centre.x), my = Mathf.FloorToInt(centre.y);
                var mean = (pa + pb + pc) / 3f;
                if (mx >= 0 && my >= 0 && mx < Width && my < Height) Take(mx, my, mean.x, mean.y, mean.z);
            }
            if (maxX < 0) return false;
            painted = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return true;
        }

        /// <summary>Narrows from..to (steps along a row) to where base + step x k stays at least eps; false when nothing is left.</summary>
        static bool Span(float start, float step, float eps, ref float from, ref float to)
        {
            if (Mathf.Abs(step) < 1e-12f) return start >= eps;
            float edge = (eps - start) / step;
            if (step > 0f) from = Mathf.Max(from, edge);
            else to = Mathf.Min(to, edge);
            return from <= to + 1f;
        }

        /// <summary>The rectangle the picked pieces cover, with their bleed; empty when none is picked.</summary>
        public RectInt PiecesArea(bool[] pieces)
        {
            int x0 = Width, y0 = Height, x1 = 0, y1 = 0;
            for (int k = 0; k < pieces.Length && k < islandRects.Length; k++)
            {
                if (!pieces[k] || islandRects[k].width == 0) continue;
                var rect = IslandArea(k);
                x0 = Mathf.Min(x0, rect.xMin);
                y0 = Mathf.Min(y0, rect.yMin);
                x1 = Mathf.Max(x1, rect.xMax);
                y1 = Mathf.Max(y1, rect.yMax);
            }
            return x1 <= x0 || y1 <= y0 ? new RectInt(0, 0, 0, 0) : new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// Moves the texels of the picked pieces toward value: by amount at start and behind it, fading along the line to
        /// end, leaving texels past end alone. Each texel's place comes from its triangle, so the fade follows the model
        /// and runs across UV seams. With mirror, texels on the far side of the mirror plane fade along the mirrored line.
        /// </summary>
        public void Gradient(bool[] pieces, Vector3 start, Vector3 end, float value, float amount, Matrix4x4? mirror = null)
        {
            var area = PiecesArea(pieces);
            if (area.width <= 0 || area.height <= 0) return;
            var line = end - start;
            var sink = new GradientSink
            {
                Canvas = this,
                Start = start,
                Line = line,
                Inverse = line.sqrMagnitude > 1e-12f ? 1f / line.sqrMagnitude : 0f,
                Value = value,
                Amount = Mathf.Clamp01(amount),
                X0 = area.xMin,
                Y0 = area.yMin,
                W = area.width,
            };
            if (mirror.HasValue)
            {
                var m = mirror.Value;
                Vector3 mirroredStart = m.MultiplyPoint3x4(start), mirroredEnd = m.MultiplyPoint3x4(end);
                // The mirror plane lies half way between a point and its mirror image, across the line joining them.
                var (point, image) = (start - mirroredStart).sqrMagnitude > 1e-12f ? (start, mirroredStart) : (end, mirroredEnd);
                if ((point - image).sqrMagnitude > 1e-12f)
                {
                    sink.Mirrored = true;
                    sink.MirrorStart = mirroredStart;
                    sink.MirrorLine = mirroredEnd - mirroredStart;
                    sink.PlanePoint = (point + image) * 0.5f;
                    sink.PlaneNormal = point - image;
                }
            }
            int size = area.width * area.height;
            if (visitBuffer == null || visitBuffer.Length < size)
            {
                visitBuffer = new int[Mathf.Max(size, 4096)];
                visitMark = 0;
            }
            if (++visitMark == int.MaxValue)
            {
                Array.Clear(visitBuffer, 0, visitBuffer.Length);
                visitMark = 1;
            }
            sink.Visited = visitBuffer;
            sink.Mark = visitMark;
            for (int t = 0; t < mesh.TriangleCount; t++)
                if (mesh.Island[t] < pieces.Length && pieces[mesh.Island[t]]) Scan(t, ref sink);
            BleedIntoGaps(area);
            MarkChanged(Grow(area, Bleed));
        }

        interface ITexelSink
        {
            void Take(int x, int y, Vector3 point);
        }

        /// <summary>A gradient's texel: moved once, by the amount its place along the line gives.</summary>
        struct GradientSink : ITexelSink
        {
            public MaskCanvas Canvas;
            public Vector3 Start, Line, MirrorStart, MirrorLine, PlanePoint, PlaneNormal;
            public bool Mirrored;
            public float Inverse, Value, Amount;
            public int[] Visited;
            public int Mark, X0, Y0, W;

            public void Take(int x, int y, Vector3 point)
            {
                int local = (y - Y0) * W + (x - X0);
                if (Visited[local] == Mark) return;
                Visited[local] = Mark;
                bool other = Mirrored && Vector3.Dot(point - PlanePoint, PlaneNormal) < 0f;
                float along = Mathf.Clamp01(Vector3.Dot(point - (other ? MirrorStart : Start), other ? MirrorLine : Line) * Inverse);
                float amount = Amount * (1f - along);
                if (amount <= 0f) return;
                int i = y * Canvas.Width + x;
                Canvas.Values[i] += (Value - Canvas.Values[i]) * amount;
            }
        }

        /// <summary>
        /// Every texel whose centre lies in triangle t on the UV map, with its place on the model, row by row; a triangle
        /// too small to hold a texel centre gives the texel under its middle.
        /// </summary>
        void Scan<T>(int t, ref T sink) where T : struct, ITexelSink
        {
            var p = mesh.Positions;
            var tri = mesh.Triangles;
            var uv = mesh.Uvs;
            var pa = p[tri[3 * t]];
            var pb = p[tri[3 * t + 1]];
            var pc = p[tri[3 * t + 2]];
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
                float inverse = 1f / area;
                float cyay = c.y - a.y, cxax = c.x - a.x, bxax = b.x - a.x, byay = b.y - a.y;
                float stepB = cyay * inverse, stepC = -byay * inverse, stepA = -(stepB + stepC);
                var step = pa * stepA + pb * stepB + pc * stepC;
                for (int y = y0; y <= y1; y++)
                {
                    float syay = y + 0.5f - a.y;
                    float sxax = x0 + 0.5f - a.x;
                    float baseB = (sxax * cyay - cxax * syay) * inverse;
                    float baseC = (bxax * syay - sxax * byay) * inverse;
                    float baseA = 1f - baseB - baseC;
                    float from = 0f, to = x1 - x0;
                    if (!Span(baseA, stepA, eps, ref from, ref to) || !Span(baseB, stepB, eps, ref from, ref to) || !Span(baseC, stepC, eps, ref from, ref to)) continue;
                    int kFrom = Mathf.Max(0, Mathf.FloorToInt(from) - 1), kTo = Mathf.Min(x1 - x0, Mathf.CeilToInt(to) + 1);
                    var first = pa * baseA + pb * baseB + pc * baseC;
                    for (int k = kFrom; k <= kTo; k++)
                    {
                        float wa = baseA + stepA * k, wb = baseB + stepB * k, wc = baseC + stepC * k;
                        if (wa < eps || wb < eps || wc < eps) continue;
                        any = true;
                        sink.Take(x0 + k, y, first + step * k);
                    }
                }
            }
            if (any) return;
            var centre = (a + b + c) / 3f;
            int mx = Mathf.FloorToInt(centre.x), my = Mathf.FloorToInt(centre.y);
            if (mx >= 0 && my >= 0 && mx < Width && my < Height) sink.Take(mx, my, (pa + pb + pc) / 3f);
        }

        void AddHit(int index, float weight)
        {
            if (hitIndex == null || hitCount == hitIndex.Length)
            {
                int size = hitIndex == null ? 16384 : hitIndex.Length * 2;
                Array.Resize(ref hitIndex, size);
                Array.Resize(ref hitWeight, size);
            }
            hitIndex[hitCount] = index;
            hitWeight[hitCount] = weight;
            hitCount++;
        }

        /// <summary>The distance from point to the nearest piece of the line through the first count points of path (to the point itself when it is one).</summary>
        static float DistanceToPath(Vector3 point, Vector3[] path, int count)
        {
            if (count == 1) return Vector3.Distance(point, path[0]);
            float best = float.MaxValue;
            for (int k = 0; k + 1 < count; k++)
            {
                var a = path[k];
                var ab = path[k + 1] - a;
                float length = ab.sqrMagnitude;
                float t = length > 1e-12f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / length) : 0f;
                best = Mathf.Min(best, Vector3.Distance(point, a + ab * t));
            }
            return best;
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
        /// changed area (grown by Bleed), so a stroke or a fill only redoes the texels around it. Only the covered
        /// texels next to a gap can reach one, so the walk starts from those alone, in the same order as from all.
        /// </summary>
        void BleedIntoGaps(RectInt changed)
        {
            if (owner == null) return;
            var area = Grow(changed, Bleed);
            int x0 = area.xMin, y0 = area.yMin, w = area.width, h = area.height;
            if (w <= 0 || h <= 0) return;
            if (borderRowStart == null) FindBorders();
            int size = w * h;
            if (bleedMark == null || bleedMark.Length < size)
            {
                int grown = Mathf.Max(size, 4096);
                bleedMark = new int[grown];
                bleedDistance = new int[grown];
                bleedQueue = new int[grown];
                bleedPass = 0;
            }
            if (++bleedPass == int.MaxValue)
            {
                Array.Clear(bleedMark, 0, bleedMark.Length);
                bleedPass = 1;
            }
            int pass = bleedPass, head = 0, tail = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y0 + y, end = borderRowStart[row + 1];
                for (int k = FirstAtLeast(borderX, borderRowStart[row], end, x0); k < end && borderX[k] < x0 + w; k++)
                    bleedQueue[tail++] = y * w + borderX[k] - x0;
            }
            while (head < tail)
            {
                int local = bleedQueue[head++];
                int distance = bleedMark[local] == pass ? bleedDistance[local] : 0;
                if (distance >= Bleed) continue;
                int lx = local % w, ly = local / w;
                for (int k = 0; k < 8; k++)
                {
                    int nx = lx + (k == 0 || k == 4 || k == 5 ? 1 : k == 1 || k == 6 || k == 7 ? -1 : 0);
                    int ny = ly + (k == 2 || k == 4 || k == 6 ? 1 : k == 3 || k == 5 || k == 7 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int n = ny * w + nx;
                    int known = bleedMark[n] == pass ? bleedDistance[n] : owner[(y0 + ny) * Width + x0 + nx] >= 0 ? 0 : int.MaxValue;
                    if (known <= distance + 1) continue;
                    bleedMark[n] = pass;
                    bleedDistance[n] = distance + 1;
                    Values[(y0 + ny) * Width + x0 + nx] = Values[(y0 + ly) * Width + x0 + lx];
                    bleedQueue[tail++] = n;
                }
            }
        }

        /// <summary>The first place in xs from lo up to hi holding at least x (hi when none does); xs is sorted there.</summary>
        static int FirstAtLeast(int[] xs, int lo, int hi, int x)
        {
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (xs[mid] < x) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>Lists, row by row, the covered texels with a gap among their eight neighbours: where bleeding starts.</summary>
        void FindBorders()
        {
            borderRowStart = new int[Height + 1];
            var xs = new List<int>();
            for (int y = 0; y < Height; y++)
            {
                borderRowStart[y] = xs.Count;
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                    if (owner[row + x] >= 0 && NextToGap(x, y)) xs.Add(x);
            }
            borderRowStart[Height] = xs.Count;
            borderX = xs.ToArray();
        }

        bool NextToGap(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= Width || ny >= Height) continue;
                    if (owner[ny * Width + nx] < 0) return true;
                }
            return false;
        }
    }
}
