using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>Finds the mesh, material and slot that use a texture, so opening a texture needs no other choice.</summary>
    public static class MaskFind
    {
        /// <summary>
        /// A renderer in the open scenes whose material holds texture, inactive ones too. The preferred renderer wins,
        /// then one under the same root, then one shown in the Hierarchy, then an active one.
        /// </summary>
        public static bool User(Texture texture, Renderer prefer, out Renderer renderer, out int materialIndex, out string slot)
        {
            renderer = null;
            materialIndex = 0;
            slot = null;
            if (texture == null) return false;
            int best = int.MinValue;
            foreach (var candidate in Candidates(prefer))
            {
                var materials = candidate.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    string first = null;
                    foreach (var s in SlotsHolding(materials[i], texture))
                    {
                        first = s;
                        break;
                    }
                    if (first == null) continue;
                    int score = Score(candidate, prefer);
                    if (score <= best) continue;
                    best = score;
                    renderer = candidate;
                    materialIndex = i;
                    slot = first;
                }
            }
            return renderer != null;
        }

        static IEnumerable<Renderer> Candidates(Renderer prefer)
        {
            if (prefer != null) yield return prefer;
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (r == prefer || EditorUtility.IsPersistent(r)) continue;
                var scene = r.gameObject.scene;
                if (scene.IsValid() && EditorSceneManager.IsPreviewScene(scene)) continue;
                yield return r;
            }
        }

        static int Score(Renderer candidate, Renderer prefer)
        {
            if (candidate == prefer) return 8;
            int score = 0;
            if (prefer != null && candidate.transform.root == prefer.transform.root) score += 4;
            if ((candidate.gameObject.hideFlags & HideFlags.HideInHierarchy) == 0) score += 2;
            if (candidate.gameObject.activeInHierarchy) score += 1;
            return score;
        }

        /// <summary>The texture properties of material that hold texture, in the shader's order.</summary>
        public static IEnumerable<string> SlotsHolding(Material material, Texture texture)
        {
            if (material == null || texture == null) yield break;
            foreach (var name in material.GetTexturePropertyNames())
                if (material.GetTexture(name) == texture) yield return name;
        }
    }
}
