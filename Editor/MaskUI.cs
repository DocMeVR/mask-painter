using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>The small builders the window and the Scene view panel share, on the DocMe theme (MaskPainterWindow.uss).</summary>
    public static class MaskUI
    {
        const string SheetGuid = "2c4940415d61a374aabc141b7ec42ff8";
        static StyleSheet sheet;

        public static StyleSheet Sheet() => sheet != null ? sheet : sheet =
            AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(SheetGuid))
            ?? AssetDatabase.FindAssets("MaskPainterWindow t:StyleSheet").Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetFileNameWithoutExtension(p) == "MaskPainterWindow").Select(AssetDatabase.LoadAssetAtPath<StyleSheet>).FirstOrDefault();

        /// <summary>A view's root: the theme, docme-root, the light skin's class and the view's own class.</summary>
        public static VisualElement Root(string viewClass)
        {
            var root = new VisualElement();
            var s = Sheet();
            if (s != null) root.styleSheets.Add(s);
            root.AddToClassList("docme-root");
            root.EnableInClassList("docme-light", !EditorGUIUtility.isProSkin);
            root.AddToClassList(viewClass);
            return root;
        }

        /// <summary>The first row of every view: the tool's name and the language picker at its right end.</summary>
        public static VisualElement Header(Action languageChanged)
        {
            var header = new VisualElement();
            header.AddToClassList("docme-header");
            var title = new Label(MaskText.T("window.title"));
            title.AddToClassList("docme-title");
            header.Add(title);
            header.Add(new MaskLanguagePicker(languageChanged));
            return header;
        }

        public static Label Section(string text)
        {
            var label = new Label(text);
            label.AddToClassList("docme-section");
            return label;
        }

        public static Label Note(string text)
        {
            var label = new Label(text);
            label.AddToClassList("docme-note");
            return label;
        }

        public static Button Button(string text, string tooltip, Action click)
        {
            var button = new Button(click) { text = text, tooltip = tooltip };
            button.AddToClassList("docme-mp-button");
            return button;
        }

        /// <summary>A labelled row: the label in the 112 px column, then content filling the rest.</summary>
        public static VisualElement Row(string label, string tooltip, VisualElement content)
        {
            var row = new VisualElement();
            row.AddToClassList("docme-row");
            var caption = new Label(label) { tooltip = tooltip };
            caption.AddToClassList("docme-label");
            row.Add(caption);
            row.Add(content);
            return row;
        }

        /// <summary>A row of buttons that wraps at narrow widths.</summary>
        public static VisualElement Wrap(params Button[] buttons)
        {
            var box = new VisualElement();
            box.AddToClassList("docme-wrap");
            foreach (var b in buttons) box.Add(b);
            return box;
        }

        /// <summary>A segmented choice of 2 to 4 items; Mark shows which one is on.</summary>
        public static VisualElement Segments(out Button[] items, params (string text, string tooltip, Action click)[] choices)
        {
            var seg = new VisualElement();
            seg.AddToClassList("docme-seg");
            items = choices.Select(c =>
            {
                var b = new Button(c.click) { text = c.text, tooltip = c.tooltip };
                b.AddToClassList("docme-seg-item");
                seg.Add(b);
                return b;
            }).ToArray();
            return seg;
        }

        public static void Mark(Button[] items, int on)
        {
            for (int i = 0; i < items.Length; i++) items[i].EnableInClassList("docme-seg-on", i == on);
        }

        public static void Show(VisualElement element, bool visible) => element.EnableInClassList("docme-hidden", !visible);
    }
}
