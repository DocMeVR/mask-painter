using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>Finds the mesh, material and slot that use a texture, so opening a texture needs no other choice.</summary>
    public static class MaskFind
    {
        /// <summary>
        /// A mesh in the open scenes whose material holds texture, inactive ones too. The preferred renderer wins, then a
        /// real object over a stand-in (an NDMF preview copy), then one of the same avatar, then an active one.
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
            if (IsMesh(prefer)) yield return prefer;
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (r == prefer || !IsMesh(r) || EditorUtility.IsPersistent(r)) continue;
                var scene = r.gameObject.scene;
                if (scene.IsValid() && EditorSceneManager.IsPreviewScene(scene)) continue;
                yield return r;
            }
        }

        static bool IsMesh(Renderer r) => r is MeshRenderer || r is SkinnedMeshRenderer;

        static int Score(Renderer candidate, Renderer prefer)
        {
            if (candidate == prefer) return 16;
            int score = 0;
            if (!IsStandIn(candidate.gameObject)) score += 8;
            if (prefer != null && AvatarRoot(candidate) == AvatarRoot(prefer)) score += 4;
            if (candidate.gameObject.activeInHierarchy) score += 1;
            return score;
        }

        /// <summary>An object the scene never saves or shows, such as a preview copy NDMF draws in place of a mesh.</summary>
        public static bool IsStandIn(GameObject go) => (go.hideFlags & (HideFlags.DontSaveInEditor | HideFlags.HideInHierarchy)) != 0;

        /// <summary>
        /// The avatar a renderer belongs to: the object with its VRChat avatar descriptor, else the topmost Animator above
        /// it (a clothing prefab's own Animator sits lower), else its root.
        /// </summary>
        public static Transform AvatarRoot(Renderer renderer)
        {
            Transform topAnimator = null;
            for (var t = renderer.transform; t != null; t = t.parent)
            {
                if (t.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "VRCAvatarDescriptor")) return t;
                if (t.GetComponent<Animator>() != null) topAnimator = t;
            }
            return topAnimator != null ? topAnimator : renderer.transform.root;
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
