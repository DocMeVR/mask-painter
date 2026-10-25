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
            // A painted alpha is an amount like the other channels, so it is read from the file but never see-through.
            var alpha = canvas.UsesAlpha() ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            // Never shrunk below the mask's own size, and never the default compression: its 5 bit red bands soft edges.
            bool settings = importer.sRGBTexture || importer.wrapMode != TextureWrapMode.Clamp || importer.textureType != TextureImporterType.Default
                || importer.maxTextureSize < size || importer.textureCompression == TextureImporterCompression.Compressed
                || importer.alphaSource != alpha || importer.alphaIsTransparency;
            if (settings)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.alphaSource = alpha;
                importer.alphaIsTransparency = false;
                if (importer.maxTextureSize < size) importer.maxTextureSize = size;
                if (importer.textureCompression == TextureImporterCompression.Compressed) importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            if (LiftLossyOverrides(importer, canvas)) importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        static readonly string[] Platforms = { "Standalone", "Android", "iPhone" };

        /// <summary>
        /// Turns off a platform's own format where it keeps fewer channels than the mask uses (a red only format reads
        /// green and blue as 0 and alpha as 1), so a painted channel is never dropped. True when one was turned off.
        /// </summary>
        static bool LiftLossyOverrides(TextureImporter importer, MaskCanvas canvas)
        {
            canvas.ChannelsUsed(out bool green, out bool blue, out bool alpha);
            bool changed = false;
            foreach (var platform in Platforms)
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                if (!settings.overridden) continue;
                int kept = ChannelsKept(settings.format);
                bool loses = (kept < 2 && green) || (kept < 3 && blue) || (kept < 4 && alpha);
                if (!loses) continue;
                settings.overridden = false;
                importer.SetPlatformTextureSettings(settings);
                changed = true;
            }
            return changed;
        }

        /// <summary>How many of red, green, blue and alpha a format keeps; 4 for any colour format.</summary>
        static int ChannelsKept(TextureImporterFormat format)
        {
            switch (format)
            {
                case TextureImporterFormat.BC4:
                case TextureImporterFormat.R8:
                case TextureImporterFormat.R16:
                case TextureImporterFormat.RHalf:
                case TextureImporterFormat.RFloat:
                case TextureImporterFormat.EAC_R:
                case TextureImporterFormat.EAC_R_SIGNED:
                case TextureImporterFormat.Alpha8:
                    return 1;
                case TextureImporterFormat.BC5:
                case TextureImporterFormat.RG16:
                case TextureImporterFormat.RGHalf:
                case TextureImporterFormat.RGFloat:
                case TextureImporterFormat.EAC_RG:
                case TextureImporterFormat.EAC_RG_SIGNED:
                    return 2;
                case TextureImporterFormat.DXT1:
                case TextureImporterFormat.DXT1Crunched:
                case TextureImporterFormat.RGB24:
                case TextureImporterFormat.RGB16:
                case TextureImporterFormat.BC6H:
                case TextureImporterFormat.ETC_RGB4:
                case TextureImporterFormat.ETC_RGB4Crunched:
                case TextureImporterFormat.ETC2_RGB4:
                case TextureImporterFormat.ETC2_RGB4_PUNCHTHROUGH_ALPHA:
                case TextureImporterFormat.PVRTC_RGB2:
                case TextureImporterFormat.PVRTC_RGB4:
                    return 3;
                default:
                    return 4;
            }
        }

        /// <summary>The smallest Max Size choice of the texture importer that holds pixels.</summary>
        static int SizeFor(int pixels)
        {
            int size = 32;
            while (size < pixels && size < 16384) size *= 2;
            return size;
        }

        /// <summary>
        /// Whether Save can write the painting back into the texture's own file: a PNG in Assets imported as a plain
        /// texture (a normal map, sprite or lightmap is never turned into a mask).
        /// </summary>
        public static bool CanWriteBack(Texture texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            return path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType == TextureImporterType.Default && File.Exists(path);
        }

        /// <summary>Whether the file is imported as a colour (sRGB) texture now: saving a mask into it switches that off.</summary>
        public static bool IsColour(string assetPath) => AssetImporter.GetAtPath(assetPath) is TextureImporter importer && importer.sRGBTexture;

        /// <summary>The 2D texture in a material's slot, or null.</summary>
        public static Texture2D TextureIn(Material material, string property) =>
            material != null && material.HasProperty(property) ? material.GetTexture(property) as Texture2D : null;

        /// <summary>
        /// Reads every channel of a texture into a new canvas, as a shader sees it (an sRGB texture decoded). A PNG or JPG in
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
                        if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.sRGBTexture) canvas.DecodeSrgb();
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
