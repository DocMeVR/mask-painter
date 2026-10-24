using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>Where a ray met the model: the triangle, the barycentric weights of its corners, the point and the distance.</summary>
    public struct MaskHit
    {
        public int Triangle;
        public Vector3 Barycentric;
        public Vector3 Point;
        public float Distance;
    }

    /// <summary>
    /// A mesh as the painter needs it: its corners in world space, one UV channel, its triangles and its UV islands
    /// (pieces of the UV map: triangles joined by corners with the same UV).
    /// </summary>
    public sealed class MaskMesh
    {
        public Vector3[] Positions;
        public Vector2[] Uvs;
        public int[] Triangles;
        /// <summary>The island of each triangle.</summary>
        public int[] Island;
        public int IslandCount;

        public int TriangleCount => Triangles.Length / 3;

        /// <summary>
        /// A renderer's mesh where it is drawn now. A skinned mesh is baked in its current pose; with useScale true the
        /// bake is in the renderer's plain local space (measured in 2022.3), so the full localToWorldMatrix places it.
        /// </summary>
        /// <summary>
        /// The part of a renderer's mesh drawn with one of its materials (all of it for -1): each material has its own
        /// textures, and parts of other materials often lie over the same UV area.
        /// </summary>
        public static MaskMesh From(Renderer renderer, int uvChannel, int materialIndex = -1)
        {
            var source = SourceMesh(renderer);
            if (source == null) return null;
            var uvs = new List<Vector2>();
            source.GetUVs(uvChannel, uvs);
            // A renderer with more materials than parts draws its last part again with each extra material.
            var triangles = materialIndex < 0 ? source.triangles : source.GetTriangles(Mathf.Min(materialIndex, source.subMeshCount - 1));
            var m = new MaskMesh { Positions = WorldPositions(renderer), Triangles = triangles };
            m.Uvs = uvs.Count == m.Positions.Length ? uvs.ToArray() : new Vector2[m.Positions.Length];
            m.FindIslands();
            return m;
        }

        public static Mesh SourceMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null ? filter.sharedMesh : null;
        }

        static Vector3[] WorldPositions(Renderer renderer)
        {
            Vector3[] local;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                var baked = new Mesh();
                try
                {
                    skinned.BakeMesh(baked, true);
                    local = baked.vertices;
                }
                finally { Object.DestroyImmediate(baked); }
            }
            else local = SourceMesh(renderer).vertices;
            var toWorld = renderer.transform.localToWorldMatrix;
            for (int i = 0; i < local.Length; i++) local[i] = toWorld.MultiplyPoint3x4(local[i]);
            return local;
        }

        /// <summary>Reads the renderer's current pose and place again; the UVs and islands stay. False if its mesh changed.</summary>
        public bool Rebake(Renderer renderer)
        {
            var source = SourceMesh(renderer);
            if (source == null || source.vertexCount != Positions.Length) return false;
            Positions = WorldPositions(renderer);
            triangleMin = null;
            triangleMax = null;
            return true;
        }

        Vector3[] triangleMin;
        Vector3[] triangleMax;

        /// <summary>Whether triangle t's bounding box comes within distance of point.</summary>
        public bool TriangleNear(int t, Vector3 point, float distance)
        {
            if (triangleMin == null)
            {
                triangleMin = new Vector3[TriangleCount];
                triangleMax = new Vector3[TriangleCount];
                for (int k = 0; k < TriangleCount; k++)
                {
                    var a = Positions[Triangles[3 * k]];
                    var b = Positions[Triangles[3 * k + 1]];
                    var c = Positions[Triangles[3 * k + 2]];
                    triangleMin[k] = Vector3.Min(a, Vector3.Min(b, c));
                    triangleMax[k] = Vector3.Max(a, Vector3.Max(b, c));
                }
            }
            var min = triangleMin[t];
            var max = triangleMax[t];
            return point.x >= min.x - distance && point.x <= max.x + distance
                && point.y >= min.y - distance && point.y <= max.y + distance
                && point.z >= min.z - distance && point.z <= max.z + distance;
        }

        public Vector3 NormalAt(MaskHit hit) => FaceNormal(hit.Triangle).normalized;

        /// <summary>Triangle t's normal, not normalised, on the side Unity draws as its front.</summary>
        public Vector3 FaceNormal(int t)
        {
            var a = Positions[Triangles[3 * t]];
            return Vector3.Cross(Positions[Triangles[3 * t + 1]] - a, Positions[Triangles[3 * t + 2]] - a);
        }

        public static MaskMesh From(Mesh mesh, Matrix4x4 toWorld, int uvChannel)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            return Build(mesh.vertices, uvs, mesh.triangles, toWorld);
        }

        static MaskMesh Build(Vector3[] local, List<Vector2> uvs, int[] triangles, Matrix4x4 toWorld)
        {
            var m = new MaskMesh { Positions = new Vector3[local.Length], Triangles = triangles };
            for (int i = 0; i < local.Length; i++) m.Positions[i] = toWorld.MultiplyPoint3x4(local[i]);
            m.Uvs = uvs.Count == local.Length ? uvs.ToArray() : new Vector2[local.Length];
            m.FindIslands();
            return m;
        }

        /// <summary>Whether the chosen channel has UVs at all (an empty channel reads as all zero).</summary>
        public bool HasUvs
        {
            get
            {
                foreach (var uv in Uvs) if (uv != Uvs[0]) return true;
                return false;
            }
        }

        void FindIslands()
        {
            // Corners with the same UV (quantised) are one node, so pieces split only in position or normal stay one.
            var nodes = new Dictionary<(int, int), int>();
            int Node(Vector2 uv)
            {
                var key = (Mathf.RoundToInt(uv.x * 65536f), Mathf.RoundToInt(uv.y * 65536f));
                if (!nodes.TryGetValue(key, out var id)) nodes[key] = id = nodes.Count;
                return id;
            }
            var corner = new int[Triangles.Length];
            for (int i = 0; i < Triangles.Length; i++) corner[i] = Node(Uvs[Triangles[i]]);
            var parent = new int[nodes.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            for (int t = 0; t < TriangleCount; t++)
            {
                int a = Find(corner[3 * t]);
                parent[Find(corner[3 * t + 1])] = a;
                parent[Find(corner[3 * t + 2])] = a;
            }
            var ids = new Dictionary<int, int>();
            Island = new int[TriangleCount];
            for (int t = 0; t < TriangleCount; t++)
            {
                int root = Find(corner[3 * t]);
                if (!ids.TryGetValue(root, out var id)) ids[root] = id = ids.Count;
                Island[t] = id;
            }
            IslandCount = ids.Count;
        }

        /// <summary>The nearest triangle the ray meets from either side (Möller and Trumbore).</summary>
        public bool Raycast(Ray ray, out MaskHit hit)
        {
            hit = default;
            hit.Distance = float.MaxValue;
            hit.Triangle = -1;
            for (int t = 0; t < TriangleCount; t++)
            {
                var a = Positions[Triangles[3 * t]];
                var b = Positions[Triangles[3 * t + 1]];
                var c = Positions[Triangles[3 * t + 2]];
                var e1 = b - a;
                var e2 = c - a;
                var p = Vector3.Cross(ray.direction, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var s = ray.origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                var q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(ray.direction, q) * inv;
                if (v < 0f || u + v > 1f) continue;
                float d = Vector3.Dot(e2, q) * inv;
                if (d <= 0f || d >= hit.Distance) continue;
                hit.Distance = d;
                hit.Triangle = t;
                hit.Barycentric = new Vector3(1f - u - v, u, v);
            }
            if (hit.Triangle < 0) return false;
            hit.Point = ray.GetPoint(hit.Distance);
            return true;
        }

        /// <summary>A mesh to draw the mask over the model with: world positions, the UV in uv0 and each corner's island in uv1.x.</summary>
        public Mesh ToPreviewMesh()
        {
            var mesh = new Mesh { name = "Mask Painter Preview", hideFlags = HideFlags.HideAndDontSave };
            if (Positions.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = Positions;
            mesh.uv = Uvs;
            var island = new Vector2[Positions.Length];
            for (int t = 0; t < TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                    island[Triangles[3 * t + k]] = new Vector2(Island[t], 0f);
            mesh.uv2 = island;
            mesh.triangles = Triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// The surface nearest to point that faces along normal (within 60 degrees), found on a line through point along
        /// normal, at most reach away: where a mirrored brush lands, so an arm resting close to the hip never catches it.
        /// </summary>
        public bool FacingSurfaceNear(Vector3 point, Vector3 normal, float reach, out MaskHit hit)
        {
            hit = default;
            hit.Triangle = -1;
            float best = reach;
            var origin = point + normal * reach;
            var direction = -normal;
            for (int t = 0; t < TriangleCount; t++)
            {
                if (!TriangleNear(t, point, reach)) continue;
                if (Vector3.Dot(FaceNormal(t).normalized, normal) < 0.5f) continue;
                var a = Positions[Triangles[3 * t]];
                var e1 = Positions[Triangles[3 * t + 1]] - a;
                var e2 = Positions[Triangles[3 * t + 2]] - a;
                var p = Vector3.Cross(direction, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var s = origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                var q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(direction, q) * inv;
                if (v < 0f || u + v > 1f) continue;
                float along = Vector3.Dot(e2, q) * inv;
                float off = Mathf.Abs(along - reach);
                if (along < 0f || off >= best) continue;
                best = off;
                hit.Triangle = t;
                hit.Distance = along;
                hit.Barycentric = new Vector3(1f - u - v, u, v);
                hit.Point = origin + direction * along;
            }
            return hit.Triangle >= 0;
        }

        public Vector2 UvAt(MaskHit hit)
        {
            int i = 3 * hit.Triangle;
            return Uvs[Triangles[i]] * hit.Barycentric.x + Uvs[Triangles[i + 1]] * hit.Barycentric.y + Uvs[Triangles[i + 2]] * hit.Barycentric.z;
        }
    }
}
