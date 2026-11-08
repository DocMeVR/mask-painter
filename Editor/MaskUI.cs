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

        /// <summary>
        /// A short hint shown in an empty object field instead of Unity's "None (Type)", which does not fit at 300 px; the
        /// view toggles docme-empty on the field.
        /// </summary>
        public static void EmptyHint(UnityEditor.UIElements.ObjectField field, string text)
        {
            var hint = new Label(text) { pickingMode = PickingMode.Ignore };
            hint.AddToClassList("docme-empty-hint");
            field.Q(className: "unity-object-field-display")?.Add(hint);
            field.RegisterValueChangedCallback(e => field.EnableInClassList("docme-empty", e.newValue == null));
            field.EnableInClassList("docme-empty", field.value == null);
        }

        /// <summary>A section's header and the card under it; returns the card.</summary>
        public static VisualElement Card(VisualElement parent, string title)
        {
            parent.Add(Section(title));
            var card = new VisualElement();
            card.AddToClassList("docme-panel");
            parent.Add(card);
            return card;
        }

        /// <summary>An empty, hidden message row; SetMessage fills it.</summary>
        public static VisualElement Message()
        {
            var row = new VisualElement();
            row.AddToClassList("docme-message");
            var icon = new Image();
            icon.AddToClassList("docme-message-icon");
            row.Add(icon);
            var body = new VisualElement();
            body.AddToClassList("docme-message-body");
            var text = new Label();
            text.AddToClassList("docme-message-text");
            body.Add(text);
            row.Add(body);
            Show(row, false);
            return row;
        }

        /// <summary>Shows text in a message row with the tint and icon of its kind; no text hides the row.</summary>
        public static void SetMessage(VisualElement row, string text, HelpBoxMessageType kind)
        {
            Show(row, !string.IsNullOrEmpty(text));
            if (string.IsNullOrEmpty(text)) return;
            row.EnableInClassList("docme-message-info", kind != HelpBoxMessageType.Warning && kind != HelpBoxMessageType.Error);
            row.EnableInClassList("docme-message-warn", kind == HelpBoxMessageType.Warning);
            row.EnableInClassList("docme-message-error", kind == HelpBoxMessageType.Error);
            row.Q<Label>(className: "docme-message-text").text = text;
            var icon = kind == HelpBoxMessageType.Error ? "error" : kind == HelpBoxMessageType.Warning ? "warn" : "info";
            row.Q<Image>(className: "docme-message-icon").image = EditorGUIUtility.IconContent(string.Join(".", "console", icon + "icon", "sml")).image;
        }
    }
}
