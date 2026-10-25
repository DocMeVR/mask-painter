using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// Tools > DocMe > Mask Painter: paints a black and white mask on a mesh in the Scene view, laid out on its UV map,
    /// saves it as a PNG and puts it into a texture slot of the material it is for.
    /// </summary>
    public sealed class MaskPainterWindow : EditorWindow
    {
        public enum PaintTool { Off, Fill, Brush }

        static readonly int ControlHint = "DocMeMaskPainter".GetHashCode();
        static readonly string[] UvChoices = { "UV0", "UV1", "UV2", "UV3" };
        static readonly int[] Sizes = { 512, 1024, 2048, 4096 };
        static string[] StartChoices => new[] { MaskText.T("start.black"), MaskText.T("start.white"), MaskText.T("start.texture") };
        static string[] ViewChoices => new[] { MaskText.T("show.overlay"), MaskText.T("show.mask"), MaskText.T("show.nothing") };
        static string[] ChannelChoices => new[] { MaskText.T("channel.all"), MaskText.T("channel.red"), MaskText.T("channel.green"), MaskText.T("channel.blue"), MaskText.T("channel.alpha") };
        const float MinBrushCm = 0.5f, MaxBrushCm = 20f;
        const int MaxStartSize = 4096;

        [SerializeField] MaskSession session;
        [SerializeField] Renderer target;
        [SerializeField] int materialIndex;
        [SerializeField] int uvChannel;
        [SerializeField] int size = 1024;
        [SerializeField] MaskStart start;
        [SerializeField] Texture2D startTexture;
        [SerializeField] PaintTool tool = PaintTool.Fill;
        [SerializeField] float brushCm = 3f;
        [SerializeField] float softness = 0.5f;
        [SerializeField] float strength = 1f;
        [SerializeField] bool mirror;
        [SerializeField] int showMode;
        [SerializeField] string slot;
        [SerializeField] string status;

        [NonSerialized] Material overlay;
        [NonSerialized] MaskHit hover;
        [NonSerialized] bool hoverValid;
        [NonSerialized] bool dragMissed;
        [NonSerialized] int heldControl;
        [NonSerialized] string builtLanguage;

        ObjectField meshField, textureField;
        DropdownField materialField, uvField, startField, sizeField, viewField, languageField, channelField;
        Button startButton, saveButton, saveAsButton, putButton, slotButton, fromSlotButton;
        Button[] toolButtons;
        Slider brushField, softnessField, strengthField;
        Toggle mirrorField;
        Label meshInfo, hint, clothesHint, channelHint, savedLabel, statusLabel;
        HelpBox meshWarning, materialWarning;
        VisualElement paintSection, saveSection, slotPart, fromSlotRow;

        [MenuItem("Tools/DocMe/Mask Painter")]
        public static void Open()
        {
            var window = GetWindow<MaskPainterWindow>();
            window.Show();
        }

        void OnEnable()
        {
            UpdateTitle();
            minSize = new Vector2(300, 360);
            if (target == null && Selection.activeGameObject != null) target = Selection.activeGameObject.GetComponent<Renderer>();
            // A maximized Scene view rebuilds this window from a file that cannot hold the hidden painting, and a
            // closed window leaves it behind: either way it is picked up again here.
            if (session == null || !session.HasPainting) session = MaskSession.FindParked();
            session?.Restore();
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            ReleaseMouse();
            // The painting stays on its hidden object; only the preview goes.
            session?.Suspend();
            if (overlay != null) DestroyImmediate(overlay);
            overlay = null;
            SceneView.RepaintAll();
        }

        /// <summary>Saves a painting for the quit prompt: into its PNG, or asking where. False when the user cancels.</summary>
        internal static bool SaveForQuit(MaskSession painting)
        {
            if (painting.SavedPath != null && painting.ReplacesStart)
            {
                int answer = AskReplace(painting.SavedPath);
                if (answer == 1) return false;
                if (answer == 0) return SaveOrSay(painting, painting.SavedPath);
            }
            else if (painting.SavedPath != null) return SaveOrSay(painting, painting.SavedPath);
            var path = EditorUtility.SaveFilePanelInProject(MaskText.T("dialog.save.title"), DefaultName(painting), "png", MaskText.T("dialog.save.message"), DefaultFolder(painting));
            if (string.IsNullOrEmpty(path)) return false;
            return SaveOrSay(painting, path);
        }

        /// <summary>Asks before the first save writes over the file a painting started from: 0 replace, 1 cancel, 2 save as a new file.</summary>
        static int AskReplace(string path)
        {
            var message = MaskText.T("dialog.replace.message", path);
            if (MaskFile.IsColour(path)) message += "\n\n" + MaskText.T("dialog.replace.colour");
            return EditorUtility.DisplayDialogComplex(MaskText.T("dialog.replace.title"), message,
                MaskText.T("dialog.replace.ok"), MaskText.T("dialog.cancel"), MaskText.T("dialog.replace.new"));
        }

        /// <summary>Saves, or says why the file could not be written (read only, locked by another program). False then.</summary>
        static bool TrySave(MaskSession painting, string path, out string error)
        {
            error = null;
            try
            {
                painting.Save(path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                error = MaskText.T("status.savefailed", path, e.Message);
                return false;
            }
        }

        /// <summary>The quit prompt's save: a file that cannot be written keeps Unity open and says why.</summary>
        static bool SaveOrSay(MaskSession painting, string path)
        {
            if (TrySave(painting, path, out var error)) return true;
            EditorUtility.DisplayDialog(MaskText.T("window.title"), error, MaskText.T("dialog.ok"));
            return false;
        }

        void UpdateTitle()
        {
            bool unsaved = session != null && session.HasPainting && session.Unsaved;
            titleContent = new GUIContent(MaskText.T(unsaved ? "window.title.unsaved" : "window.title"));
        }

        void CreateGUI() => rootVisualElement.Add(BuildView());

        void Rebuild()
        {
            rootVisualElement.Clear();
            rootVisualElement.Add(BuildView());
        }

        /// <summary>The whole window's contents, with its stylesheet on it, so a screenshot can host it on its own.</summary>
        public VisualElement BuildView()
        {
            builtLanguage = MaskLanguage.Current;
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            var sheet = LoadStyleSheet();
            if (sheet != null) scroll.styleSheets.Add(sheet);
            var root = new VisualElement();
            root.AddToClassList("docme-mp");
            scroll.Add(root);

            root.Add(Intro(MaskText.T("intro.text")));

            root.Add(Section(MaskText.T("section.mesh")));
            meshField = new ObjectField(MaskText.T("field.mesh")) { objectType = typeof(Renderer), allowSceneObjects = true, tooltip = MaskText.T("field.mesh.tip") };
            meshField.RegisterValueChangedCallback(e =>
            {
                target = e.newValue as Renderer;
                materialIndex = 0;
                slot = null;
                Refresh();
            });
            root.Add(meshField);
            materialField = new DropdownField(MaskText.T("field.material")) { tooltip = MaskText.T("field.material.tip") };
            materialField.RegisterValueChangedCallback(_ =>
            {
                materialIndex = Mathf.Max(0, materialField.index);
                slot = null;
                Refresh();
            });
            root.Add(materialField);
            uvField = new DropdownField(MaskText.T("field.uv"), UvChoices.ToList(), 0) { tooltip = MaskText.T("field.uv.tip") };
            uvField.RegisterValueChangedCallback(_ =>
            {
                uvChannel = Mathf.Max(0, uvField.index);
                Refresh();
            });
            root.Add(uvField);
            startField = new DropdownField(MaskText.T("field.start"), StartChoices.ToList(), 0) { tooltip = MaskText.T("field.start.tip") };
            startField.RegisterValueChangedCallback(_ =>
            {
                start = (MaskStart)Mathf.Max(0, startField.index);
                Refresh();
            });
            root.Add(startField);
            textureField = new ObjectField(MaskText.T("field.texture")) { objectType = typeof(Texture2D), allowSceneObjects = false, tooltip = MaskText.T("field.texture.tip") };
            textureField.RegisterValueChangedCallback(e =>
            {
                startTexture = e.newValue as Texture2D;
                Refresh();
            });
            root.Add(textureField);
            fromSlotRow = Row("", "", out var fromSlotBox);
            fromSlotButton = Button(MaskText.T("button.fromslot"), MaskText.T("button.fromslot.tip"), PickStartSlot);
            fromSlotBox.Add(fromSlotButton);
            root.Add(fromSlotRow);
            sizeField = new DropdownField(MaskText.T("field.size"), Sizes.Select(s => s + " x " + s).ToList(), 1) { tooltip = MaskText.T("field.size.tip") };
            sizeField.RegisterValueChangedCallback(_ =>
            {
                size = Sizes[Mathf.Max(0, sizeField.index)];
                Refresh();
            });
            root.Add(sizeField);
            meshWarning = new HelpBox("", HelpBoxMessageType.Warning);
            root.Add(meshWarning);
            startButton = Button(MaskText.T("button.start"), MaskText.T("button.start.tip"), StartPainting);
            startButton.AddToClassList("docme-mp__action");
            root.Add(startButton);
            meshInfo = Hint("");
            root.Add(meshInfo);

            paintSection = new VisualElement();
            root.Add(paintSection);
            paintSection.Add(Section(MaskText.T("section.paint")));
            channelField = new DropdownField(MaskText.T("field.channel"), ChannelChoices.ToList(), 0) { tooltip = MaskText.T("field.channel.tip") };
            channelField.RegisterValueChangedCallback(_ =>
            {
                if (session == null || !session.IsLive) return;
                ReleaseMouse();
                session.SelectChannel((MaskChannel)Mathf.Max(0, channelField.index));
                Refresh();
                SceneView.RepaintAll();
            });
            paintSection.Add(channelField);
            channelHint = Hint("");
            paintSection.Add(channelHint);
            var toolRow = Row(MaskText.T("row.tool"), MaskText.T("row.tool.tip"), out var toolBox);
            toolButtons = new[]
            {
                Choice(MaskText.T("tool.off"), MaskText.T("tool.off.tip"), () => SetTool(PaintTool.Off)),
                Choice(MaskText.T("tool.fill"), MaskText.T("tool.fill.tip"), () => SetTool(PaintTool.Fill)),
                Choice(MaskText.T("tool.brush"), MaskText.T("tool.brush.tip"), () => SetTool(PaintTool.Brush)),
            };
            foreach (var b in toolButtons) toolBox.Add(b);
            paintSection.Add(toolRow);
            brushField = new Slider(MaskText.T("field.brush"), MinBrushCm, MaxBrushCm) { showInputField = true, tooltip = MaskText.T("field.brush.tip") };
            brushField.RegisterValueChangedCallback(e => brushCm = Mathf.Clamp(e.newValue, MinBrushCm, MaxBrushCm));
            paintSection.Add(brushField);
            softnessField = new Slider(MaskText.T("field.softness"), 0f, 1f) { showInputField = true, tooltip = MaskText.T("field.softness.tip") };
            softnessField.RegisterValueChangedCallback(e => softness = Mathf.Clamp01(e.newValue));
            paintSection.Add(softnessField);
            strengthField = new Slider(MaskText.T("field.strength"), 0.05f, 1f) { showInputField = true, tooltip = MaskText.T("field.strength.tip") };
            strengthField.RegisterValueChangedCallback(e => strength = Mathf.Clamp(e.newValue, 0.05f, 1f));
            paintSection.Add(strengthField);
            mirrorField = new Toggle(MaskText.T("field.mirror")) { tooltip = MaskText.T("field.mirror.tip") };
            mirrorField.RegisterValueChangedCallback(e => mirror = e.newValue);
            paintSection.Add(mirrorField);
            hint = Hint("");
            paintSection.Add(hint);
            clothesHint = Hint(MaskText.T("hint.clothes"));
            paintSection.Add(clothesHint);
            var wholeRow = Row(MaskText.T("row.whole"), MaskText.T("row.whole.tip"), out var wholeBox);
            wholeBox.Add(Button(MaskText.T("button.allblack"), MaskText.T("button.allblack.tip"), () => Whole(m => m.FillAll(0f))));
            wholeBox.Add(Button(MaskText.T("button.allwhite"), MaskText.T("button.allwhite.tip"), () => Whole(m => m.FillAll(1f))));
            wholeBox.Add(Button(MaskText.T("button.invert"), MaskText.T("button.invert.tip"), () => Whole(m => m.Invert())));
            paintSection.Add(wholeRow);
            viewField = new DropdownField(MaskText.T("field.show"), ViewChoices.ToList(), 0) { tooltip = MaskText.T("field.show.tip") };
            viewField.RegisterValueChangedCallback(_ =>
            {
                showMode = Mathf.Max(0, viewField.index);
                SceneView.RepaintAll();
            });
            paintSection.Add(viewField);

            saveSection = new VisualElement();
            root.Add(saveSection);
            saveSection.Add(Section(MaskText.T("section.save")));
            savedLabel = Hint("");
            saveSection.Add(savedLabel);
            var saveRow = Row(MaskText.T("row.file"), MaskText.T("row.file.tip"), out var saveBox);
            saveButton = Button(MaskText.T("button.save"), MaskText.T("button.save.tip"), () => Save());
            saveAsButton = Button(MaskText.T("button.saveas"), MaskText.T("button.saveas.tip"), () => SaveAs());
            saveBox.Add(saveButton);
            saveBox.Add(saveAsButton);
            saveSection.Add(saveRow);
            slotPart = new VisualElement();
            saveSection.Add(slotPart);
            var slotRow = Row(MaskText.T("row.slot"), MaskText.T("row.slot.tip"), out var slotBox);
            slotButton = Button(MaskText.T("button.pickslot"), MaskText.T("button.pickslot.tip"), PickSlot);
            slotButton.AddToClassList("docme-mp__picker");
            slotBox.Add(slotButton);
            slotPart.Add(slotRow);
            materialWarning = new HelpBox("", HelpBoxMessageType.Info);
            slotPart.Add(materialWarning);
            putButton = Button(MaskText.T("button.put"), MaskText.T("button.put.tip"), PutIntoMaterial);
            putButton.AddToClassList("docme-mp__action");
            slotPart.Add(putButton);

            statusLabel = Hint("");
            statusLabel.AddToClassList("docme-mp__status");
            root.Add(statusLabel);

            languageField = new DropdownField(MaskText.T("field.language"), new List<string>(), 0) { tooltip = MaskText.T("field.language.tip") };
            var languages = MaskText.Languages();
            languageField.choices = languages.Select(MaskLanguage.NativeName).ToList();
            languageField.SetValueWithoutNotify(MaskLanguage.NativeName(CurrentLanguage(languages)));
            languageField.RegisterValueChangedCallback(_ =>
            {
                if (languageField.index < 0 || languageField.index >= languages.Count) return;
                MaskLanguage.Set(languages[languageField.index]);
                Rebuild();
            });
            languageField.style.marginTop = 8;
            root.Add(languageField);

            Refresh();
            root.schedule.Execute(() =>
            {
                if (builtLanguage != MaskLanguage.Current) Rebuild();
                else Refresh();
            }).Every(500);
            return scroll;
        }

        static string CurrentLanguage(List<string> languages)
        {
            string current = MaskLanguage.Current;
            if (languages.Contains(current)) return current;
            return languages.FirstOrDefault(l => MaskLanguage.BaseOf(l) == MaskLanguage.BaseOf(current)) ?? MaskLanguage.English;
        }

        void Refresh()
        {
            if (meshField == null) return;
            // A mesh that was deleted and brought back with Ctrl+Z picks up where it was.
            if (session != null && !session.IsLive && session.Renderer != null) session.Restore();
            bool painting = session != null && session.HasPainting;
            bool live = painting && session.IsLive;
            bool gone = painting && session.Renderer == null;
            UpdateTitle();

            meshField.SetValueWithoutNotify(target);
            var materialNames = target != null ? MaterialChoices(target.sharedMaterials) : new List<string>();
            materialField.choices = materialNames;
            materialIndex = Mathf.Clamp(materialIndex, 0, Mathf.Max(0, materialNames.Count - 1));
            materialField.SetValueWithoutNotify(materialNames.Count > 0 ? materialNames[materialIndex] : "");
            Show(materialField, materialNames.Count > 0);
            uvField.SetValueWithoutNotify(UvChoices[Mathf.Clamp(uvChannel, 0, 3)]);
            startField.SetValueWithoutNotify(StartChoices[(int)start]);
            textureField.SetValueWithoutNotify(startTexture);
            int sizeIndex = Array.IndexOf(Sizes, size);
            sizeField.SetValueWithoutNotify(sizeField.choices[sizeIndex < 0 ? 1 : sizeIndex]);
            Show(textureField, start == MaskStart.Texture);
            Show(fromSlotRow, start == MaskStart.Texture && StartMaterial() != null);
            Show(sizeField, start != MaskStart.Texture);

            var problem = MeshProblem();
            meshWarning.text = problem ?? "";
            Show(meshWarning, problem != null);
            startButton.SetEnabled(problem == null && (start != MaskStart.Texture || startTexture != null));
            startButton.text = MaskText.T(painting ? "button.startover" : "button.start");
            meshInfo.text = live ? Describe(session)
                : gone ? MaskText.T("mesh.gone")
                : "";
            Show(meshInfo, live || gone);

            Show(paintSection, live);
            if (live)
            {
                var channel = session.Channel;
                channelField.SetValueWithoutNotify(ChannelChoices[(int)channel]);
                channelHint.text = channel == MaskChannel.All ? "" : MaskText.T("hint.channel", ChannelChoices[(int)channel]);
                Show(channelHint, channel != MaskChannel.All);
                for (int i = 0; i < toolButtons.Length; i++) toolButtons[i].EnableInClassList("docme-mp__choice--on", (int)tool == i);
                brushField.SetValueWithoutNotify(brushCm);
                softnessField.SetValueWithoutNotify(softness);
                Show(brushField, tool == PaintTool.Brush);
                Show(softnessField, tool == PaintTool.Brush);
                strengthField.SetValueWithoutNotify(strength);
                Show(strengthField, tool == PaintTool.Brush);
                mirrorField.SetValueWithoutNotify(mirror);
                hint.text = tool == PaintTool.Fill ? MaskText.T("hint.fill")
                    : tool == PaintTool.Brush ? MaskText.T("hint.brush")
                    : MaskText.T("hint.off");
                Show(clothesHint, tool != PaintTool.Off);
                viewField.SetValueWithoutNotify(ViewChoices[Mathf.Clamp(showMode, 0, ViewChoices.Length - 1)]);
            }
            Show(saveSection, painting);
            if (painting) RefreshSave(live);
            statusLabel.text = status ?? "";
            Show(statusLabel, !string.IsNullOrEmpty(status));
        }

        void RefreshSave(bool live)
        {
            var path = session.SavedPath;
            savedLabel.text = path == null ? MaskText.T("saved.none")
                : session.ReplacesStart ? MaskText.T("saved.opened", path)
                : MaskText.T(session.Unsaved ? "saved.unsaved" : "saved.saved", path);
            Show(saveButton, path != null);
            saveButton.SetEnabled(session.Unsaved);

            var material = CurrentMaterial();
            Show(slotPart, live && material != null);
            if (!live || material == null) return;
            var slots = MaskFile.TextureSlots(material);
            // No slot is picked for the user: putting a mask into the wrong one would replace a texture they need.
            int slotIndex = !string.IsNullOrEmpty(slot) ? slots.IndexOf(slot) : -1;
            slotButton.text = slotIndex >= 0 ? MaskFile.SlotLabel(material, slot) : MaskText.T("button.pickslot");
            slotButton.tooltip = slotIndex >= 0 ? MaskText.T("button.pickslot.picked", slot) : MaskText.T("button.pickslot.tip");
            slotButton.SetEnabled(slots.Count > 0);

            bool ownFile = AssetDatabase.GetAssetPath(material).EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
            string note = null;
            if (!ownFile) note = MaskText.T("note.embedded");
            else if (slots.Count == 0) note = MaskText.T("note.noslots");
            else if (MaskFile.IsLocked(material) && slotIndex >= 0 && material.GetTexture(slot) == null)
                note = MaskText.T("note.lockedempty");
            else if (MaskFile.IsLocked(material))
                note = MaskText.T("note.locked");
            materialWarning.text = note ?? "";
            Show(materialWarning, note != null);
            putButton.SetEnabled(ownFile && slotIndex >= 0);
        }

        string MeshProblem()
        {
            if (target == null) return MaskText.T("mesh.pick");
            var mesh = MaskMesh.SourceMesh(target);
            if (mesh == null) return MaskText.T("mesh.none");
            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs.Count == 0) return MaskText.T("mesh.nouv", UvChoices[Mathf.Clamp(uvChannel, 0, 3)]);
            if (start == MaskStart.Texture && startTexture != null && Mathf.Max(startTexture.width, startTexture.height) > MaxStartSize)
                return MaskText.T("mesh.toolarge", MaxStartSize);
            return null;
        }

        static string Describe(MaskSession s)
        {
            int pieces = s.Mesh.IslandCount;
            var material = s.Material != null ? ", " + s.Material.name : "";
            return MaskText.Count(pieces, "mesh.describe.one", "mesh.describe.many", s.Renderer.name + material, pieces,
                UvChoices[Mathf.Clamp(s.UvChannel, 0, 3)], s.Canvas.Width, s.Canvas.Height);
        }

        static List<string> MaterialChoices(Material[] materials)
        {
            var names = materials.Select(m => m != null ? m.name : MaskText.T("material.empty")).ToList();
            var counts = names.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
            for (int i = 0; i < names.Count; i++)
                if (counts[names[i]] > 1) names[i] = names[i] + " (" + (i + 1) + ")";
            return names;
        }

        Material CurrentMaterial() => session != null ? session.Material : null;

        /// <summary>The material picked above, before a painting starts.</summary>
        Material StartMaterial()
        {
            var materials = target != null ? target.sharedMaterials : null;
            return materials != null && materialIndex < materials.Length ? materials[materialIndex] : null;
        }

        /// <summary>Starts from the texture in one of the material's slots, and keeps that slot for Put into material.</summary>
        void PickStartSlot()
        {
            var material = StartMaterial();
            if (material == null) return;
            new MaskSlotPicker(material, picked =>
            {
                if (this == null) return;
                var texture = MaskFile.TextureIn(material, picked);
                status = texture == null ? MaskText.T("status.slotempty", MaskFile.SlotLabel(material, picked)) : null;
                if (texture != null)
                {
                    startTexture = texture;
                    slot = picked;
                }
                Refresh();
            }).Show(fromSlotButton.worldBound);
        }

        void PickSlot()
        {
            var material = CurrentMaterial();
            if (material == null) return;
            new MaskSlotPicker(material, picked =>
            {
                if (this == null) return;
                slot = picked;
                Refresh();
            }).Show(slotButton.worldBound);
        }

        /// <summary>
        /// The avatar the mesh belongs to, whose left and right the mirror runs across: its VRChat avatar descriptor,
        /// else the topmost Animator above the mesh (a clothing prefab's own Animator sits lower), else its root.
        /// </summary>
        static Transform MirrorRoot(Renderer renderer)
        {
            Transform topAnimator = null;
            for (var t = renderer.transform; t != null; t = t.parent)
            {
                if (t.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "VRCAvatarDescriptor")) return t;
                if (t.GetComponent<Animator>() != null) topAnimator = t;
            }
            return topAnimator != null ? topAnimator : renderer.transform.root;
        }

        void StartPainting()
        {
            if (MeshProblem() != null) return;
            if (session != null && session.HasPainting && session.Unsaved
                && !EditorUtility.DisplayDialog(MaskText.T("dialog.new.title"), MaskText.T("dialog.new.message"), MaskText.T("dialog.new.ok"), MaskText.T("dialog.cancel")))
                return;
            ReleaseMouse();
            session?.Dispose();
            hoverValid = false;
            session = MaskSession.Start(target, uvChannel, materialIndex, size, start, start == MaskStart.Texture ? startTexture : null);
            status = session == null ? MaskText.T("mesh.none") : null;
            if (tool == PaintTool.Off) tool = PaintTool.Fill;
            Refresh();
            SceneView.RepaintAll();
        }

        void SetTool(PaintTool value)
        {
            ReleaseMouse();
            tool = value;
            hoverValid = false;
            Refresh();
            SceneView.RepaintAll();
        }

        void Whole(Action<MaskSession> change)
        {
            if (session == null || !session.IsLive) return;
            change(session);
            AfterPaint();
        }

        bool Save()
        {
            if (session == null || !session.HasPainting) return false;
            if (session.SavedPath == null) return SaveAs();
            if (session.ReplacesStart)
            {
                int answer = AskReplace(session.SavedPath);
                if (answer == 1) return false;
                if (answer == 2) return SaveAs();
            }
            return SaveTo(session.SavedPath);
        }

        bool SaveTo(string path)
        {
            bool saved = TrySave(session, path, out var error);
            status = saved ? MaskText.T("status.saved", path) : error;
            Refresh();
            return saved;
        }

        bool SaveAs()
        {
            if (session == null || !session.HasPainting) return false;
            var current = session.SavedPath;
            var folder = current != null ? Path.GetDirectoryName(current).Replace('\\', '/') : DefaultFolder(session);
            // The file it started from is offered by its folder only: a new file was asked for.
            var name = current != null && !session.ReplacesStart ? Path.GetFileNameWithoutExtension(current) : DefaultName(session);
            var path = EditorUtility.SaveFilePanelInProject(MaskText.T("dialog.save.title"), name, "png", MaskText.T("dialog.save.message"), folder);
            if (string.IsNullOrEmpty(path)) return false;
            return SaveTo(path);
        }

        static string DefaultName(MaskSession painting) => painting.Renderer != null ? painting.Renderer.name + " Mask" : "Mask";

        /// <summary>The folder of the material the mask is for when it is its own file, so a mask lands beside it.</summary>
        static string DefaultFolder(MaskSession painting)
        {
            var path = painting.Material != null ? AssetDatabase.GetAssetPath(painting.Material) : null;
            return !string.IsNullOrEmpty(path) && path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path).Replace('\\', '/') : "Assets";
        }

        void PutIntoMaterial()
        {
            var material = CurrentMaterial();
            if (material == null || string.IsNullOrEmpty(slot) || !MaskFile.TextureSlots(material).Contains(slot)) return;
            if ((session.Unsaved || session.SavedPath == null) && !Save()) return;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(session.SavedPath);
            if (texture == null) return;
            MaskFile.Assign(material, slot, texture);
            status = MaskText.T("status.put", material.name, MaskFile.SlotLabel(material, slot));
            Refresh();
        }

        void AfterPaint()
        {
            UpdateTitle();
            if (savedLabel != null && session != null && session.HasPainting) RefreshSave(session.IsLive);
            SceneView.RepaintAll();
        }

        void OnUndoRedo()
        {
            if (session == null || !session.IsLive) return;
            session.FollowUndo();
            AfterPaint();
            Refresh();
        }

        void ReleaseMouse()
        {
            if (heldControl != 0 && GUIUtility.hotControl == heldControl) GUIUtility.hotControl = 0;
            heldControl = 0;
        }

        void OnSceneGUI(SceneView view)
        {
            if (session == null || !session.IsLive)
            {
                ReleaseMouse();
                return;
            }
            var e = Event.current;
            if (showMode != 2 && e.type == EventType.Repaint) DrawOverlay();
            if (tool == PaintTool.Off || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (session.IsStroking) session.EndStroke();
                ReleaseMouse();
                return;
            }
            if (hoverValid && !session.IsValid(hover)) hoverValid = false;

            int id = GUIUtility.GetControlID(ControlHint, FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    // Clicks paint instead of selecting while a paint tool is on; Alt keeps the view turning.
                    if (!e.alt) HandleUtility.AddDefaultControl(id);
                    break;

                case EventType.MouseMove:
                    UpdateHover(e);
                    view.Repaint();
                    break;

                case EventType.MouseLeaveWindow:
                    hoverValid = false;
                    view.Repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button != 0 || e.alt) break;
                    // Bones may have moved since the last look (an animation preview): read the pose before aiming.
                    session.Rebake(true);
                    session.Mirror = mirror ? MaskSession.MirrorAcross(MirrorRoot(session.Renderer)) : (Matrix4x4?)null;
                    UpdateHover(e);
                    e.Use();
                    if (!hoverValid) break;
                    float value = e.shift ? 0f : 1f;
                    if (tool == PaintTool.Fill) session.FillPiece(hover, value);
                    else
                    {
                        session.BeginStroke(hover, brushCm / 100f, softness, value, strength);
                        GUIUtility.hotControl = heldControl = id;
                        dragMissed = false;
                    }
                    AfterPaint();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    UpdateHover(e);
                    if (hoverValid)
                    {
                        session.ContinueStroke(hover, dragMissed);
                        dragMissed = false;
                    }
                    else dragMissed = true;
                    e.Use();
                    view.Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id || e.button != 0) break;
                    ReleaseMouse();
                    session.EndStroke();
                    e.Use();
                    AfterPaint();
                    break;

                case EventType.KeyDown:
                    if (tool == PaintTool.Brush && (e.keyCode == KeyCode.LeftBracket || e.keyCode == KeyCode.RightBracket))
                    {
                        brushCm = Mathf.Clamp(e.keyCode == KeyCode.LeftBracket ? brushCm / 1.25f : brushCm * 1.25f, MinBrushCm, MaxBrushCm);
                        brushField?.SetValueWithoutNotify(brushCm);
                        e.Use();
                    }
                    view.Repaint();
                    break;

                case EventType.KeyUp:
                    view.Repaint();
                    break;

                case EventType.Repaint:
                    DrawCursor(e);
                    break;
            }
        }

        void UpdateHover(Event e)
        {
            hoverValid = session.Raycast(HandleUtility.GUIPointToWorldRay(e.mousePosition), out hover);
        }

        Material Overlay
        {
            get
            {
                if (overlay != null) return overlay;
                var shader = Shader.Find("Hidden/DocMe/MaskPainter/Overlay");
                if (shader == null) return null;
                overlay = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return overlay;
            }
        }

        void DrawOverlay()
        {
            var material = Overlay;
            if (material == null) return;
            session.Rebake(false);
            if (session.PreviewMesh == null) return;
            material.SetTexture("_Mask", session.PreviewTexture);
            material.SetFloat("_Mode", showMode == 1 ? 1f : 0f);
            material.SetFloat("_Hover", hoverValid && tool == PaintTool.Fill && session.IsValid(hover) ? session.IslandAt(hover) : -1f);
            for (int pass = 0; pass < 2; pass++)
            {
                material.SetPass(pass);
                Graphics.DrawMeshNow(session.PreviewMesh, Matrix4x4.identity);
            }
        }

        void DrawCursor(Event e)
        {
            if (!hoverValid || tool != PaintTool.Brush || !session.IsValid(hover)) return;
            var normal = session.Mesh.NormalAt(hover);
            float radius = brushCm / 100f;
            using (new Handles.DrawingScope(e.shift ? new Color(0.1f, 0.1f, 0.1f, 1f) : new Color(0.25f, 0.79f, 0.66f, 1f)))
            {
                Handles.DrawWireDisc(hover.Point, normal, radius, 2f);
                if (softness > 0.01f) Handles.DrawWireDisc(hover.Point, normal, radius * (1f - softness));
            }
        }

        StyleSheet LoadStyleSheet()
        {
            var folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this)));
            return string.IsNullOrEmpty(folder) ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(Path.Combine(folder, "MaskPainterWindow.uss").Replace('\\', '/'));
        }

        static Label Intro(string text)
        {
            var label = new Label(text);
            label.AddToClassList("docme-mp__intro");
            return label;
        }

        static Label Section(string text)
        {
            var label = new Label(text);
            label.AddToClassList("docme-mp__section");
            return label;
        }

        static Label Hint(string text)
        {
            var label = new Label(text);
            label.AddToClassList("docme-mp__hint");
            return label;
        }

        static Button Button(string text, string tooltip, Action click)
        {
            var button = new Button(click) { text = text, tooltip = tooltip };
            button.AddToClassList("docme-mp__button");
            return button;
        }

        static Button Choice(string text, string tooltip, Action click)
        {
            var button = Button(text, tooltip, click);
            button.AddToClassList("docme-mp__choice");
            return button;
        }

        /// <summary>A row laid out like a labelled field: the label in the 112 px column, then a box that wraps its buttons.</summary>
        static VisualElement Row(string label, string tooltip, out VisualElement box)
        {
            var row = new VisualElement();
            row.AddToClassList(BaseField<bool>.ussClassName);
            row.AddToClassList("docme-mp__row");
            var text = new Label(label) { tooltip = tooltip };
            text.AddToClassList(BaseField<bool>.labelUssClassName);
            row.Add(text);
            box = new VisualElement();
            box.AddToClassList("docme-mp__row-box");
            row.Add(box);
            return row;
        }

        static void Show(VisualElement element, bool visible) => element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
