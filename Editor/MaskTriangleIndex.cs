using System.Collections.Generic;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// A tree of boxes over one set of a mesh's triangle corners: what a ray meets first, and which triangles lie near a
    /// point, without checking every triangle. It answers exactly as checking every triangle in index order does.
    /// </summary>
    sealed class MaskTriangleIndex
    {
        const int LeafSize = 6;

        /// <summary>The positions it was built for; a new array means a new index.</summary>
        public readonly Vector3[] Source;
        public readonly Vector3[] TriangleMin, TriangleMax;

        readonly int[] triangles;
        readonly int[] order;
        readonly Vector3[] nodeMin, nodeMax;
        /// <summary>A leaf's first place in order, or an inner node's first child (the second follows it).</summary>
        readonly int[] nodeFirst;
        /// <summary>A leaf's triangle count; 0 for an inner node.</summary>
        readonly int[] nodeCount;
        readonly Stack<int> pending = new Stack<int>();

        public MaskTriangleIndex(Vector3[] positions, int[] triangles)
        {
            Source = positions;
            this.triangles = triangles;
            int count = triangles.Length / 3;
            TriangleMin = new Vector3[count];
            TriangleMax = new Vector3[count];
            var centre = new Vector3[count];
            order = new int[count];
            for (int t = 0; t < count; t++)
            {
                var a = positions[triangles[3 * t]];
                var b = positions[triangles[3 * t + 1]];
                var c = positions[triangles[3 * t + 2]];
                TriangleMin[t] = Vector3.Min(a, Vector3.Min(b, c));
                TriangleMax[t] = Vector3.Max(a, Vector3.Max(b, c));
                centre[t] = (TriangleMin[t] + TriangleMax[t]) * 0.5f;
                order[t] = t;
            }

            var mins = new List<Vector3> { Vector3.zero };
            var maxs = new List<Vector3> { Vector3.zero };
            var firsts = new List<int> { 0 };
            var counts = new List<int> { 0 };
            var work = new Stack<(int node, int start, int n)>();
            if (count > 0) work.Push((0, 0, count));
            while (work.Count > 0)
            {
                var (node, start, n) = work.Pop();
                Vector3 min = TriangleMin[order[start]], max = TriangleMax[order[start]];
                Vector3 cmin = centre[order[start]], cmax = cmin;
                for (int k = start + 1; k < start + n; k++)
                {
                    int t = order[k];
                    min = Vector3.Min(min, TriangleMin[t]);
                    max = Vector3.Max(max, TriangleMax[t]);
                    cmin = Vector3.Min(cmin, centre[t]);
                    cmax = Vector3.Max(cmax, centre[t]);
                }
                mins[node] = min;
                maxs[node] = max;
                if (n <= LeafSize)
                {
                    firsts[node] = start;
                    counts[node] = n;
                    continue;
                }
                var extent = cmax - cmin;
                int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
                float middle = (cmin[axis] + cmax[axis]) * 0.5f;
                int i = start, j = start + n - 1;
                while (i <= j)
                {
                    if (centre[order[i]][axis] < middle) i++;
                    else
                    {
                        (order[i], order[j]) = (order[j], order[i]);
                        j--;
                    }
                }
                int left = i - start;
                // Triangles that all share one centre cannot be split by place: halve them.
                if (left == 0 || left == n) left = n / 2;
                int child = mins.Count;
                for (int k = 0; k < 2; k++)
                {
                    mins.Add(Vector3.zero);
                    maxs.Add(Vector3.zero);
                    firsts.Add(0);
                    counts.Add(0);
                }
                firsts[node] = child;
                counts[node] = 0;
                work.Push((child, start, left));
                work.Push((child + 1, start + left, n - left));
            }
            nodeMin = mins.ToArray();
            nodeMax = maxs.ToArray();
            nodeFirst = firsts.ToArray();
            nodeCount = counts.ToArray();
            if (count == 0) nodeCount[0] = 0;
        }

        bool IsLeaf(int node) => nodeCount[node] > 0 || order.Length == 0;

        /// <summary>
        /// The nearest triangle the ray meets from either side (Möller and Trumbore), the lowest index among equally near
        /// ones. Returns -1 when it meets none.
        /// </summary>
        public int Raycast(Vector3[] positions, Vector3 origin, Vector3 direction, out float distance, out float u, out float v)
        {
            distance = float.MaxValue;
            u = v = 0f;
            int best = -1;
            if (order.Length == 0) return best;
            var inverse = new Vector3(1f / NonZero(direction.x), 1f / NonZero(direction.y), 1f / NonZero(direction.z));
            pending.Clear();
            pending.Push(0);
            while (pending.Count > 0)
            {
                int node = pending.Pop();
                if (!RayMeetsBox(nodeMin[node], nodeMax[node], origin, inverse, distance)) continue;
                if (!IsLeaf(node))
                {
                    pending.Push(nodeFirst[node]);
                    pending.Push(nodeFirst[node] + 1);
                    continue;
                }
                for (int k = nodeFirst[node], end = k + nodeCount[node]; k < end; k++)
                {
                    int t = order[k];
                    var a = positions[triangles[3 * t]];
                    var e1 = positions[triangles[3 * t + 1]] - a;
                    var e2 = positions[triangles[3 * t + 2]] - a;
                    var p = Vector3.Cross(direction, e2);
                    float det = Vector3.Dot(e1, p);
                    if (Mathf.Abs(det) < 1e-12f) continue;
                    float inv = 1f / det;
                    var s = origin - a;
                    float hu = Vector3.Dot(s, p) * inv;
                    if (hu < 0f || hu > 1f) continue;
                    var q = Vector3.Cross(s, e1);
                    float hv = Vector3.Dot(direction, q) * inv;
                    if (hv < 0f || hu + hv > 1f) continue;
                    float d = Vector3.Dot(e2, q) * inv;
                    if (d <= 0f || d > distance || (d == distance && t > best)) continue;
                    distance = d;
                    best = t;
                    u = hu;
                    v = hv;
                }
            }
            return best;
        }

        static float NonZero(float x) => Mathf.Abs(x) < 1e-20f ? (x < 0f ? -1e-20f : 1e-20f) : x;

        static bool RayMeetsBox(Vector3 min, Vector3 max, Vector3 origin, Vector3 inverse, float within)
        {
            float t0 = (min.x - origin.x) * inverse.x, t1 = (max.x - origin.x) * inverse.x;
            float near = Mathf.Min(t0, t1), far = Mathf.Max(t0, t1);
            t0 = (min.y - origin.y) * inverse.y;
            t1 = (max.y - origin.y) * inverse.y;
            near = Mathf.Max(near, Mathf.Min(t0, t1));
            far = Mathf.Min(far, Mathf.Max(t0, t1));
            t0 = (min.z - origin.z) * inverse.z;
            t1 = (max.z - origin.z) * inverse.z;
            near = Mathf.Max(near, Mathf.Min(t0, t1));
            far = Mathf.Min(far, Mathf.Max(t0, t1));
            // A little slack, so rounding in the box test never drops a triangle the exact test would take.
            float slack = 1e-5f * Mathf.Max(1f, Mathf.Abs(far));
            return far + slack >= Mathf.Max(near - slack, 0f) && near - slack <= within;
        }

        /// <summary>
        /// Every triangle whose bounding box comes within distance of point along each axis, in index order, the same
        /// test as MaskMesh.TriangleNear.
        /// </summary>
        public void Near(Vector3 point, float distance, List<int> into)
        {
            into.Clear();
            if (order.Length == 0) return;
            pending.Clear();
            pending.Push(0);
            while (pending.Count > 0)
            {
                int node = pending.Pop();
                if (!Within(nodeMin[node], nodeMax[node], point, distance)) continue;
                if (!IsLeaf(node))
                {
                    pending.Push(nodeFirst[node]);
                    pending.Push(nodeFirst[node] + 1);
                    continue;
                }
                for (int k = nodeFirst[node], end = k + nodeCount[node]; k < end; k++)
                    if (Within(TriangleMin[order[k]], TriangleMax[order[k]], point, distance)) into.Add(order[k]);
            }
            into.Sort();
        }

        public static bool Within(Vector3 min, Vector3 max, Vector3 point, float distance) =>
            point.x >= min.x - distance && point.x <= max.x + distance
            && point.y >= min.y - distance && point.y <= max.y + distance
            && point.z >= min.z - distance && point.z <= max.z + distance;
    }
}
