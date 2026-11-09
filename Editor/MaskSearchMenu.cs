using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// A list with a search field on top, drawn in UI Toolkit inside the window that hosts the control instead of as
    /// Unity's grey system list: AvatarDoc's picker menu (AdMenu with search), cut down to one level of groups. Typing
    /// filters every group at once and shows each result's group beside it.
    /// </summary>
    public sealed class MaskSearchMenu : VisualElement
    {
        public sealed class Item
        {
            public string Text;
            /// <summary>Dim text beside the item's own. A search result without one shows its group there.</summary>
            public string Note;
            /// <summary>Text a search also finds the item by, besides its own.</summary>
            public string SearchText;
            public bool Checked;
            public bool Disabled;
            public bool Header;
            public Action Action;

            public static Item Heading(string text) => new Item { Text = text, Header = true };
        }

        /// <summary>One item a search can find, folded once when the menu opens.</summary>
        readonly struct Entry
        {
            public readonly Item Item;
            public readonly string Name;
            public readonly string Extra;
            public readonly string Group;

            public Entry(Item item, string group)
            {
                Item = item;
                Name = Fold(item.Text);
                Extra = string.IsNullOrEmpty(item.SearchText) ? null : Fold(item.SearchText);
                Group = group;
            }
        }

        public const float RowHeight = 22f;
        /// <summary>The card's width unless its control is wider: fixed, so it never moves while the results change.</summary>
        public const float Width = 260f;
        public const float MaxWidth = 320f;
        public const int MaxRows = 12;
        /// <summary>How many times within one second the menu takes the focus back from something in its own window before it closes.</summary>
        public const int MaxRefocusesPerSecond = 3;
        const float SearchHeight = 26f;
        const float CardFrame = 10f;
        const float EdgeGap = 4f;

        /// <summary>Swapped in tests, which cannot show a menu in a window: gets the control and the items instead.</summary>
        public static Action<VisualElement, List<Item>> ShowOverride;

        /// <summary>The text an input method is composing (Japanese, Chinese), not yet in the field. Replaceable by a test.</summary>
        public static Func<string> CompositionText = () => Input.compositionString;

        /// <summary>Whether the editor is in front and the window hosting the menu has the keyboard focus. Replaceable by a test.</summary>
        public static Func<EditorWindow, bool> HostHasFocus = host => host != null && InternalEditorUtility.isApplicationActive && EditorWindow.focusedWindow == host;

        static StyleSheet sheet;

        readonly List<VisualElement> rows = new List<VisualElement>();
        readonly List<double> refocuses = new List<double>();
        readonly Item[] all;
        readonly Entry[] index;
        readonly VisualElement target;
        readonly EditorWindow host;
        readonly VisualElement panelRoot;
        readonly Rect anchor;
        readonly VisualElement frame;
        readonly ScrollView list;
        readonly TextField searchField;
        readonly Image searchIcon;
        readonly float listHeight;
        Item[] items;
        string[] notes;
        Action showPlaceholder;
        int selected = -1;
        /// <summary>Whether the pointer went down inside the card: a release on a row picks it only then, never the release of the press that opened the menu.</summary>
        bool pressedInside;
        bool closed, focusCheckPending;

        public bool IsClosed => closed;
        public int SelectedIndex => selected;
        public IReadOnlyList<Item> Items => items;
        /// <summary>The dim text each shown row has beside its own.</summary>
        public IReadOnlyList<string> Notes => notes;
        public TextField SearchField => searchField;
        public Image SearchIcon => searchIcon;

        /// <summary>
        /// Opens the menu under target, or under the part of it named by under (above it when there is more room there),
        /// inside the window whose panel hosts target, with the keyboard focus in the search field. Target gets the focus
        /// back when the menu closes, and its removal closes the menu. Null when target is not on a panel, or a test took it.
        /// </summary>
        public static MaskSearchMenu Open(VisualElement target, List<Item> items, VisualElement under = null)
        {
            if (ShowOverride != null)
            {
                ShowOverride(target, items);
                return null;
            }
            if (target == null || target.panel == null || items == null) return null;
            var window = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .FirstOrDefault(w => w != null && w.rootVisualElement.panel == target.panel && w.rootVisualElement.Contains(target));
            var root = window != null ? window.rootVisualElement : target.panel.visualTree;
            foreach (var old in root.Query<MaskSearchMenu>().ToList()) old.Close(false);
            var menu = new MaskSearchMenu(items, target, (under ?? target).worldBound, window, root);
            root.Add(menu);
            target.RegisterCallback<DetachFromPanelEvent>(menu.OnTargetDetached);
            root.RegisterCallback<GeometryChangedEvent>(menu.OnPanelGeometry);
            Undo.undoRedoPerformed += menu.OnUndoRedo;
            menu.searchField.Focus();
            return menu;
        }

        /// <summary>A menu on no panel, for tests: Search, Answer and Commit drive it as typing, keys and clicks would.</summary>
        public MaskSearchMenu(IEnumerable<Item> items) : this(items.ToList(), null, Rect.zero, null, null) { }

        MaskSearchMenu(List<Item> source, VisualElement target, Rect anchor, EditorWindow host, VisualElement panelRoot)
        {
            all = source.Where(i => i != null).ToArray();
            index = BuildIndex(all);
            this.target = target;
            this.anchor = anchor;
            this.host = host;
            this.panelRoot = panelRoot;
            AddToClassList("docme-root");
            AddToClassList("docme-menu");
            EnableInClassList("docme-light", !EditorGUIUtility.isProSkin);
            if (sheet == null) sheet = FindSheet();
            if (sheet != null) styleSheets.Add(sheet);
            // The search field holds the focus, so the menu itself never takes it.
            focusable = false;
            tabIndex = -1;

            frame = new VisualElement();
            frame.AddToClassList("docme-menu-card");
            searchField = BuildSearchField();
            frame.Add(searchField);
            // The magnifier lies over the field's box from outside: an image inside Unity's text input is never drawn.
            searchIcon = new Image { image = FirstIcon("Search Icon", "d_Search Icon", "SearchWindow", "d_SearchWindow", "ViewToolZoom", "d_ViewToolZoom"), pickingMode = PickingMode.Ignore };
            searchIcon.AddToClassList("docme-menu-search-icon");
            frame.Add(searchIcon);
            list = new ScrollView { horizontalScrollerVisibility = ScrollerVisibility.Hidden, verticalScrollerVisibility = ScrollerVisibility.Auto, focusable = false };
            list.contentContainer.focusable = false;
            list.AddToClassList("docme-menu-list");
            // Tall enough for every row, so the card keeps one size while typing.
            listHeight = Mathf.Clamp(all.Length, 1, MaxRows) * RowHeight;
            list.style.height = listHeight;
            frame.Add(list);
            float room = panelRoot != null && panelRoot.layout.width > 0f ? panelRoot.layout.width - 2f * EdgeGap : MaxWidth;
            frame.style.width = Mathf.Max(Mathf.Min(Mathf.Clamp(anchor.width, Width, MaxWidth), room), RowHeight);
            Add(frame);

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<WheelEvent>(e => e.StopPropagation());
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<FocusOutEvent>(OnFocusOut);
            frame.RegisterCallback<GeometryChangedEvent>(e => { if (e.oldRect.size != e.newRect.size) Place(); });
            list.RegisterCallback<GeometryChangedEvent>(RevealSelected);

            items = all;
            notes = all.Select(i => i.Note).ToArray();
            Populate();
            Select(Array.FindIndex(items, i => i.Checked && Enabled(i)), false);
        }

        static StyleSheet FindSheet() => MaskUI.Sheet();

        /// <summary>
        /// The form a search compares: full width letters and digits as plain ones, half width katakana as full width
        /// (NFKC), lower case, and hiragana as katakana. Han characters stay as they are.
        /// </summary>
        static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string normal;
            try { normal = text.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { normal = text; }
            var chars = normal.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] >= (char)0x3041 && chars[i] <= (char)0x3096) chars[i] = (char)(chars[i] + 0x60);
            return new string(chars);
        }

        /// <summary>Every item but the headings, each with the heading over it. Greyed items are found too, so a search says why one cannot be picked.</summary>
        static Entry[] BuildIndex(Item[] source)
        {
            var entries = new List<Entry>();
            string heading = null;
            foreach (var item in source)
            {
                if (item.Header) heading = item.Text;
                else entries.Add(new Entry(item, heading));
            }
            return entries.ToArray();
        }

        static bool Enabled(Item item) => !item.Header && !item.Disabled && item.Action != null;

        /// <summary>A plain TextField with the placeholder inside its one input box and room on the left for the magnifier.</summary>
        TextField BuildSearchField()
        {
            var field = new TextField { tooltip = MaskText.T("search.tip") };
            field.AddToClassList("docme-menu-search");
            // Every key goes to the text, so a focus that comes back never selects what was typed.
            field.selectAllOnFocus = false;
            field.selectAllOnMouseUp = false;
            var input = field.Q(className: "unity-text-field__input") ?? field;
            // Room for the magnifier, as AvatarDoc's search fields leave it: outside the stylesheet's spacing scale.
            input.style.paddingLeft = 20;
            var placeholder = new Label(MaskText.T("search.placeholder")) { pickingMode = PickingMode.Ignore };
            placeholder.AddToClassList("docme-menu-placeholder");
            input.Add(placeholder);
            // Hidden while the field holds text or an input method is composing; every key looks again.
            showPlaceholder = () => placeholder.EnableInClassList("docme-hidden", field.value.Length > 0 || !string.IsNullOrEmpty(CompositionText()));
            field.RegisterCallback<KeyDownEvent>(_ => showPlaceholder());
            field.RegisterCallback<KeyUpEvent>(_ => showPlaceholder());
            field.RegisterValueChangedCallback(e =>
            {
                // The text is the menu's own business: nothing in the window it sits in hears it change.
                e.StopPropagation();
                showPlaceholder();
                Filter(e.newValue);
            });
            return field;
        }

        static readonly Dictionary<string, Texture> icons = new Dictionary<string, Texture>();

        // Icon names differ between Unity versions and skins; a missing one is looked up once.
        static Texture FirstIcon(params string[] names)
        {
            foreach (var name in names)
            {
                if (!icons.TryGetValue(name, out var texture))
                {
                    try { texture = EditorGUIUtility.IconContent(name)?.image; }
                    catch { texture = null; }
                    icons[name] = texture;
                }
                if (texture != null) return texture;
            }
            return null;
        }

        /// <summary>Puts text in the search field and filters, as typing does. A field on no panel sends no change, so tests use this.</summary>
        public void Search(string text)
        {
            searchField.SetValueWithoutNotify(text ?? "");
            showPlaceholder();
            Filter(text);
        }

        /// <summary>Every item whose name or search text holds the typed text, whatever the case, in the groups' order. An empty field shows every group again.</summary>
        void Filter(string text)
        {
            if (closed) return;
            var query = (text ?? "").Trim();
            if (query.Length == 0)
            {
                items = all;
                notes = all.Select(i => i.Note).ToArray();
                Populate();
                return;
            }
            var needle = Fold(query);
            var shown = new List<Item>();
            var shownNotes = new List<string>();
            foreach (var entry in index)
            {
                if (entry.Name.IndexOf(needle, StringComparison.Ordinal) < 0 && (entry.Extra == null || entry.Extra.IndexOf(needle, StringComparison.Ordinal) < 0)) continue;
                shown.Add(entry.Item);
                shownNotes.Add(entry.Item.Note ?? entry.Group);
            }
            if (shown.Count == 0)
            {
                shown.Add(new Item { Text = MaskText.T("search.none", query), Disabled = true });
                shownNotes.Add(null);
            }
            items = shown.ToArray();
            notes = shownNotes.ToArray();
            Populate();
        }

        void Populate()
        {
            list.Clear();
            rows.Clear();
            selected = -1;
            for (int i = 0; i < items.Length; i++)
            {
                var row = BuildRow(items[i], i, notes[i]);
                rows.Add(row);
                list.Add(row);
            }
            list.scrollOffset = Vector2.zero;
            Place();
        }

        VisualElement BuildRow(Item item, int at, string note)
        {
            var row = new VisualElement();
            if (item.Header)
            {
                row.AddToClassList("docme-menu-header");
                row.Add(Cell("docme-menu-header-text", (item.Text ?? "").ToUpperInvariant()));
                return row;
            }
            row.AddToClassList("docme-menu-item");
            row.EnableInClassList("docme-menu-disabled", !Enabled(item));
            row.Add(Cell("docme-menu-check", item.Checked ? "✓" : ""));
            row.Add(Cell("docme-menu-label", item.Text));
            if (!string.IsNullOrEmpty(note)) row.Add(Cell("docme-menu-note", note));
            row.RegisterCallback<PointerMoveEvent>(_ => Select(at, false));
            row.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.button != 0 || !Enabled(item) || !TakePress()) return;
                e.StopPropagation();
                Commit(at);
            });
            return row;
        }

        static Label Cell(string cls, string text)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(cls);
            return label;
        }

        bool TakePress()
        {
            bool pressed = pressedInside;
            pressedInside = false;
            return pressed;
        }

        void Place()
        {
            if (closed || panelRoot == null) return;
            float rootWidth = panelRoot.layout.width;
            float rootHeight = panelRoot.layout.height;
            float width = frame.layout.width > 0f ? frame.layout.width : frame.style.width.value.value;
            float height = frame.layout.height > 0f ? frame.layout.height : CardFrame + SearchHeight + listHeight;
            var local = panelRoot.WorldToLocal(anchor.position);
            if (rootWidth > 0f && rootHeight > 0f)
            {
                frame.style.maxHeight = Mathf.Max(RowHeight, rootHeight - 2f * EdgeGap);
                float below = rootHeight - (local.y + anchor.height) - EdgeGap;
                float above = local.y - EdgeGap;
                bool putBelow = below >= height || below >= above;
                float top = putBelow ? local.y + anchor.height : local.y - height;
                frame.style.top = Mathf.Clamp(top, EdgeGap, Mathf.Max(EdgeGap, rootHeight - height - EdgeGap));
                frame.style.left = Mathf.Clamp(local.x, EdgeGap, Mathf.Max(EdgeGap, rootWidth - width - EdgeGap));
            }
            else
            {
                frame.style.top = local.y + anchor.height;
                frame.style.left = local.x;
            }
        }

        void RevealSelected(GeometryChangedEvent e)
        {
            list.UnregisterCallback<GeometryChangedEvent>(RevealSelected);
            if (selected >= 0 && selected < rows.Count) list.ScrollTo(rows[selected]);
        }

        /// <summary>A press beside the card closes the menu. A press on a row leaves the focus in the field, so typing goes on.</summary>
        void OnPointerDown(PointerDownEvent e)
        {
            e.StopPropagation();
            if (e.target == this)
            {
                Close();
                return;
            }
            pressedInside = true;
            if (!(e.target is VisualElement pressed && searchField.Contains(pressed))) e.PreventDefault();
        }

        void OnTargetDetached(DetachFromPanelEvent e)
        {
            if (e.target == target) Close(false);
        }

        void OnPanelGeometry(GeometryChangedEvent e)
        {
            if (e.oldRect.width > 0f && e.oldRect.height > 0f && e.oldRect.size != e.newRect.size) Close(false);
        }

        void OnUndoRedo() => Close(false);

        /// <summary>
        /// A focus move out of the menu is looked at on the next tick: Unity moves the focus for reasons of its own (an
        /// IMGUI editor in the same inspector syncing its keyboard focus), so the menu takes it back a few times, and
        /// closes when another window or application has it.
        /// </summary>
        void OnFocusOut(FocusOutEvent e)
        {
            if (closed || focusCheckPending) return;
            if (e.relatedTarget is VisualElement next && Contains(next)) return;
            focusCheckPending = true;
            schedule.Execute(CheckFocus);
        }

        void CheckFocus()
        {
            focusCheckPending = false;
            if (closed || panel == null) return;
            var focused = focusController?.focusedElement as VisualElement;
            if (focused != null && Contains(focused)) return;
            if (!HostHasFocus(host))
            {
                Close(false);
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            refocuses.RemoveAll(t => now - t >= 1.0);
            if (refocuses.Count >= MaxRefocusesPerSecond)
            {
                Close(false);
                return;
            }
            refocuses.Add(now);
            searchField.Focus();
        }

        void OnKeyDown(KeyDownEvent e)
        {
            if (closed || !string.IsNullOrEmpty(CompositionText())) return;
            // Windows follows Enter, Tab and Escape with their character, and Space (which opened the menu on its
            // field) with a space: the key itself has been answered.
            bool follower = e.keyCode == KeyCode.None && (e.character == '\n' || e.character == '\r' || e.character == '\t' || e.character == (char)27
                || (e.character == ' ' && searchField.value.Length == 0));
            if (!follower && !Answer(e.keyCode)) return;
            e.PreventDefault();
            e.StopPropagation();
        }

        /// <summary>
        /// The keys the menu answers before its field sees them: Down and Up move through the rows, Enter picks the
        /// highlighted row or the first result, Escape clears the field or closes an empty one, Tab closes. Every other
        /// key types. Public so a test can press them on a menu outside a window.
        /// </summary>
        public bool Answer(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.DownArrow: Move(1); return true;
                case KeyCode.UpArrow: Move(-1); return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Commit(selected >= 0 ? selected : Array.FindIndex(items, Enabled));
                    if (closed) DropEnterCharacter();
                    return true;
                case KeyCode.Escape:
                    if (searchField.value.Length == 0) Close();
                    else Search("");
                    return true;
                case KeyCode.Tab:
                    Close(false);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Windows follows Enter with its character as a key press of its own, which would press the control the focus
        /// went back to and open the menu again. The next key press in the window is dropped when it is that character.
        /// </summary>
        void DropEnterCharacter()
        {
            var root = panelRoot;
            if (root == null) return;
            EventCallback<KeyDownEvent> drop = null;
            drop = e =>
            {
                root.UnregisterCallback(drop, TrickleDown.TrickleDown);
                if (e.keyCode != KeyCode.None || (e.character != '\n' && e.character != '\r')) return;
                e.imguiEvent?.Use();
                e.StopImmediatePropagation();
                e.PreventDefault();
            };
            root.RegisterCallback(drop, TrickleDown.TrickleDown);
        }

        void Move(int direction)
        {
            int at = selected;
            if (at < 0 && direction < 0) at = items.Length;
            do at += direction; while (at >= 0 && at < items.Length && !Enabled(items[at]));
            if (at >= 0 && at < items.Length) Select(at, true);
        }

        void Select(int at, bool reveal)
        {
            if (at < 0 || at >= items.Length || !Enabled(items[at]) || selected == at) return;
            if (selected >= 0 && selected < rows.Count) rows[selected].RemoveFromClassList("docme-menu-selected");
            selected = at;
            rows[at].AddToClassList("docme-menu-selected");
            if (reveal && list.panel != null) list.ScrollTo(rows[at]);
        }

        /// <summary>Picks the row at index as a click on it would: closes the menu, then runs the item. Public so a test can pick without a pointer.</summary>
        public void Commit(int at)
        {
            if (closed || at < 0 || at >= items.Length || !Enabled(items[at])) return;
            var item = items[at];
            Close(true);
            item.Action?.Invoke();
        }

        public void Close(bool focusTarget = true)
        {
            if (closed) return;
            closed = true;
            target?.UnregisterCallback<DetachFromPanelEvent>(OnTargetDetached);
            panelRoot?.UnregisterCallback<GeometryChangedEvent>(OnPanelGeometry);
            Undo.undoRedoPerformed -= OnUndoRedo;
            // A window clearing its root lets go of its child list before it detaches the children one by one.
            if (hierarchy.parent != null && hierarchy.parent.hierarchy.IndexOf(this) >= 0) RemoveFromHierarchy();
            if (focusTarget && target?.panel != null) target.Focus();
        }
    }
}
