using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>Writes a mask to a PNG in the project and puts it into a material's texture slot.</summary>
    public static class MaskFile
    {
        /// <summary>
        /// Writes the canvas as a grey PNG at assetPath (inside Assets) and imports it as a linear, clamped texture, since
        /// a mask holds amounts rather than colours. An existing file at that path is replaced.
        /// </summary>
        public static Texture2D Save(MaskCanvas canvas, string assetPath)
        {
            var full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var texture = canvas.ToTexture();
            try { File.WriteAllBytes(full, texture.EncodeToPNG()); }
            finally { Object.DestroyImmediate(texture); }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            int size = SizeFor(Mathf.Max(canvas.Width, canvas.Height));
            // Never shrunk below the mask's own size, and never the default compression: its 5 bit red bands soft edges.
            bool settings = importer.sRGBTexture || importer.wrapMode != TextureWrapMode.Clamp || importer.textureType != TextureImporterType.Default
                || importer.maxTextureSize < size || importer.textureCompression == TextureImporterCompression.Compressed;
            if (settings)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.alphaSource = TextureImporterAlphaSource.None;
                if (importer.maxTextureSize < size) importer.maxTextureSize = size;
                if (importer.textureCompression == TextureImporterCompression.Compressed) importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>The smallest Max Size choice of the texture importer that holds pixels.</summary>
        static int SizeFor(int pixels)
        {
            int size = 32;
            while (size < pixels && size < 16384) size *= 2;
            return size;
        }

        /// <summary>
        /// Reads a texture's red channel into a new canvas, as a shader sees it (an sRGB texture decoded). A PNG or JPG in
        /// the project is read from its file, at full size and without compression; anything else through the graphics
        /// card at its imported size, so no texture needs the Read/Write setting.
        /// </summary>
        public static MaskCanvas Read(Texture source)
        {
            var path = AssetDatabase.GetAssetPath(source);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if ((extension == ".png" || extension == ".jpg" || extension == ".jpeg") && File.Exists(path))
            {
                var file = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    if (file.LoadImage(File.ReadAllBytes(path)))
                    {
                        var canvas = MaskCanvas.From(file);
                        if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.sRGBTexture)
                            for (int i = 0; i < canvas.Values.Length; i++) canvas.Values[i] = Mathf.GammaToLinearSpace(canvas.Values[i]);
                        return canvas;
                    }
                }
                finally { Object.DestroyImmediate(file); }
            }
            return ReadThroughGraphicsCard(source);
        }

        static MaskCanvas ReadThroughGraphicsCard(Texture source)
        {
            var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var active = RenderTexture.active;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                copy.Apply(false);
                return MaskCanvas.From(copy);
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(copy);
            }
        }

        /// <summary>The material's 2D texture slots that show in its inspector, by property name.</summary>
        public static List<string> TextureSlots(Material material)
        {
            var slots = new List<string>();
            if (material == null || material.shader == null) return slots;
            var shader = material.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                var flags = shader.GetPropertyFlags(i);
                if ((flags & (ShaderPropertyFlags.HideInInspector | ShaderPropertyFlags.PerRendererData | ShaderPropertyFlags.NonModifiableTextureData)) != 0) continue;
                slots.Add(shader.GetPropertyName(i));
            }
            return slots;
        }

        /// <summary>A slot's name as its inspector shows it, without Thry's "--{...}" options.</summary>
        public static string SlotLabel(Material material, string property)
        {
            int i = material.shader.FindPropertyIndex(property);
            if (i < 0) return property;
            var label = material.shader.GetPropertyDescription(i);
            int options = label.IndexOf("--{");
            if (options >= 0) label = label.Substring(0, options);
            label = label.Trim();
            return string.IsNullOrEmpty(label) ? property : label;
        }

        public static bool IsLocked(Material material) => material != null && material.shader != null && material.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal);

        public static void Assign(Material material, string property, Texture texture)
        {
            Undo.RecordObject(material, MaskText.T("undo.put", material.name));
            material.SetTexture(property, texture);
            EditorUtility.SetDirty(material);
        }
    }
}
