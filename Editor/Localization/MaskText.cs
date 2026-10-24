using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// Every text of the window in the chosen language, read from the MaskPainter.xx-XX.po files. A key comes from the
    /// chosen language, then another file of the same base language, then English, so a raw key never shows.
    /// </summary>
    public static class MaskText
    {
        public const string FilePrefix = "MaskPainter.";

        static Dictionary<string, PoFile> files;
        static readonly HashSet<string> registered = new HashSet<string>();

        public static readonly string[] Keys =
        {
            "window.title",
            "window.title.unsaved",
            "intro.text",
            "section.mesh",
            "section.paint",
            "section.save",
            "field.mesh",
            "field.mesh.tip",
            "field.material",
            "field.material.tip",
            "field.uv",
            "field.uv.tip",
            "field.start",
            "field.start.tip",
            "start.black",
            "start.white",
            "start.texture",
            "field.texture",
            "field.texture.tip",
            "field.size",
            "field.size.tip",
            "button.start",
            "button.startover",
            "button.start.tip",
            "material.empty",
            "mesh.pick",
            "mesh.none",
            "mesh.nouv",
            "mesh.toolarge",
            "mesh.gone",
            "mesh.describe.one",
            "mesh.describe.many",
            "row.tool",
            "row.tool.tip",
            "tool.off",
            "tool.off.tip",
            "tool.fill",
            "tool.fill.tip",
            "tool.brush",
            "tool.brush.tip",
            "field.brush",
            "field.brush.tip",
            "field.softness",
            "field.softness.tip",
            "field.strength",
            "field.strength.tip",
            "field.mirror",
            "field.mirror.tip",
            "hint.fill",
            "hint.brush",
            "hint.off",
            "hint.clothes",
            "row.whole",
            "row.whole.tip",
            "button.allblack",
            "button.allblack.tip",
            "button.allwhite",
            "button.allwhite.tip",
            "button.invert",
            "button.invert.tip",
            "field.show",
            "field.show.tip",
            "show.overlay",
            "show.mask",
            "show.nothing",
            "saved.none",
            "saved.unsaved",
            "saved.saved",
            "row.file",
            "row.file.tip",
            "button.save",
            "button.save.tip",
            "button.saveas",
            "button.saveas.tip",
            "row.slot",
            "row.slot.tip",
            "button.pickslot",
            "button.pickslot.tip",
            "button.pickslot.picked",
            "note.embedded",
            "note.noslots",
            "note.lockedempty",
            "note.locked",
            "button.put",
            "button.put.tip",
            "status.saved",
            "status.put",
            "dialog.save.title",
            "dialog.save.message",
            "dialog.new.title",
            "dialog.new.message",
            "dialog.new.ok",
            "dialog.cancel",
            "quit.message",
            "quit.mesh",
            "quit.save",
            "quit.discard",
            "picker.title",
            "undo.fillpiece",
            "undo.fillwhite",
            "undo.fillblack",
            "undo.invert",
            "undo.paint",
            "undo.put",
            "field.language",
            "field.language.tip",
        };

        public static string T(string key) => Find(key, MaskLanguage.Current);

        public static string T(string key, params object[] args)
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, T(key), args);
            }
            catch (FormatException)
            {
                return string.Format(CultureInfo.InvariantCulture, Find(key, MaskLanguage.English), args);
            }
        }

        /// <summary>The singular text for exactly 1, the plural text otherwise; both get args.</summary>
        public static string Count(int count, string singularKey, string pluralKey, params object[] args)
            => T(count == 1 ? singularKey : pluralKey, args);

        /// <summary>The languages that have a file, lowercase, English first.</summary>
        public static List<string> Languages()
        {
            var codes = new List<string> { MaskLanguage.English };
            codes.AddRange(Files().Keys.Where(c => c != MaskLanguage.English).OrderBy(c => c, StringComparer.Ordinal));
            foreach (var code in codes)
                if (registered.Add(code))
                    MaskLanguage.Store.Register(code);
            return codes;
        }

        public static PoFile PoOf(string code) => Files().TryGetValue(MaskLanguage.Normalize(code), out var file) ? file : null;

        public static void Invalidate() => files = null;

        static string Find(string key, string language)
        {
            var all = Files();
            language = MaskLanguage.Normalize(language);
            string text = null;
            if (all.TryGetValue(language, out var exact))
                text = exact.Text(key);
            if (text == null)
            {
                string baseCode = MaskLanguage.BaseOf(language);
                foreach (var pair in all)
                {
                    if (pair.Key == language || MaskLanguage.BaseOf(pair.Key) != baseCode) continue;
                    text = pair.Value.Text(key);
                    if (text != null) break;
                }
            }
            if (text == null && all.TryGetValue(MaskLanguage.English, out var english))
                text = english.Text(key);
            return text ?? key;
        }

        static Dictionary<string, PoFile> Files()
        {
            if (files != null) return files;
            var found = new Dictionary<string, PoFile>();
            foreach (var guid in AssetDatabase.FindAssets("MaskPainter"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = Path.GetFileNameWithoutExtension(path);
                if (!path.EndsWith(".po", StringComparison.OrdinalIgnoreCase) || !name.StartsWith(FilePrefix, StringComparison.Ordinal)) continue;
                var text = PoFile.Decode(SafeRead(path), out _);
                if (text == null) continue;
                found[MaskLanguage.Normalize(name.Substring(FilePrefix.Length))] = PoFile.Parse(text);
            }
            return files = found;
        }

        static byte[] SafeRead(string assetPath)
        {
            try { return System.IO.File.ReadAllBytes(Path.GetFullPath(assetPath)); }
            catch (Exception) { return null; }
        }

        sealed class Watcher : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                foreach (var path in imported.Concat(deleted).Concat(moved))
                    if (path.EndsWith(".po", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith(FilePrefix, StringComparison.Ordinal))
                    {
                        Invalidate();
                        return;
                    }
            }
        }
    }
}
