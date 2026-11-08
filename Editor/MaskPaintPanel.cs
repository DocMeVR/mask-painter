using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// The painting controls in the Scene view panel (MaskPainterOverlay): channel, tool, Add or Replace, brush, mirror,
    /// what the Scene view shows, whole-mask buttons. It reads and writes the open Mask Painter window's settings and
    /// refreshes four times a second, so the window and the panel never disagree.
    /// </summary>
    public sealed class MaskPaintPanel : VisualElement
    {
        static string[] ChannelChoices => new[] { MaskText.T("channel.all"), MaskText.T("channel.red"), MaskText.T("channel.green"), MaskText.T("channel.blue"), MaskText.T("channel.alpha") };
        static string[] ViewChoices => new[] { MaskText.T("show.overlay"), MaskText.T("show.every"), MaskText.T("show.mask"), MaskText.T("show.nothing") };
        static readonly MaskShow[] ViewOrder = { MaskShow.Overlay, MaskShow.EveryChannel, MaskShow.MaskOnly, MaskShow.Nothing };
        static readonly string[] SwatchClasses = { "docme-mp-swatch-all", "docme-mp-swatch-red", "docme-mp-swatch-green", "docme-mp-swatch-blue", "docme-mp-swatch-alpha" };

        DropdownField channelField, viewField;
        Label channelHint, hint;
        Button[] toolItems, blendItems, paintItems, penItems;
        Slider brushField, softnessField, strengthField, overlayField;
        Toggle mirrorField, isolateField;
        Button copyButton;
        VisualElement blendRow, paintRow, strengthRow, swatch, penRow;
        string builtLanguage;

        public MaskPaintPanel()
        {
            Build();
            schedule.Execute(Refresh).Every(250);
        }

        static MaskPainterWindow Window => MaskPainterWindow.Active;

        void Build()
        {
            Clear();
            builtLanguage = MaskLanguage.Current;
            var root = MaskUI.Root("docme-mp-panel");
            Add(root);

            channelField = new DropdownField(MaskText.T("field.channel"), ChannelChoices.ToList(), 0) { tooltip = MaskText.T("field.channel.tip") };
            channelField.RegisterValueChangedCallback(_ => Window?.SelectChannel((MaskChannel)Mathf.Max(0, channelField.index)));
            swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            swatch.AddToClassList("docme-mp-swatch");
            channelField.Insert(1, swatch);
            root.Add(channelField);
            channelHint = MaskUI.Note("");
            root.Add(channelHint);

            var tools = MaskUI.Segments(out toolItems,
                (MaskText.T("tool.off"), MaskText.T("tool.off.tip"), () => Window?.SetTool(MaskPainterWindow.PaintTool.Off)),
                (MaskText.T("tool.fill"), MaskText.T("tool.fill.tip"), () => Window?.SetTool(MaskPainterWindow.PaintTool.Fill)),
                (MaskText.T("tool.brush"), MaskText.T("tool.brush.tip"), () => Window?.SetTool(MaskPainterWindow.PaintTool.Brush)),
                (MaskText.T("tool.smooth"), MaskText.T("tool.smooth.tip"), () => Window?.SetTool(MaskPainterWindow.PaintTool.Smooth)));
            tools.AddToClassList("docme-mp-tools");
            root.Add(tools);
            blendRow = MaskUI.Row(MaskText.T("field.blend"), MaskText.T("field.blend.tip"), MaskUI.Segments(out blendItems,
                (MaskText.T("blend.add"), MaskText.T("blend.add.tip"), () => SetBlend(MaskBlend.Add)),
                (MaskText.T("blend.replace"), MaskText.T("blend.replace.tip"), () => SetBlend(MaskBlend.Replace))));
            root.Add(blendRow);
            paintRow = MaskUI.Row(MaskText.T("row.paints"), MaskText.T("row.paints.tip"), MaskUI.Segments(out paintItems,
                (MaskText.T("start.white"), MaskText.T("paint.white.tip"), () => SetPaintsBlack(false)),
                (MaskText.T("start.black"), MaskText.T("paint.black.tip"), () => SetPaintsBlack(true))));
            root.Add(paintRow);

            brushField = new Slider(MaskText.T("field.brush"), MaskPainterWindow.MinBrushCm, MaskPainterWindow.MaxBrushCm) { showInputField = true, tooltip = MaskText.T("field.brush.tip") };
            brushField.RegisterValueChangedCallback(e => { if (Window != null) Window.BrushCm = e.newValue; });
            root.Add(brushField);
            softnessField = new Slider(MaskText.T("field.softness"), 0f, 1f) { showInputField = true, tooltip = MaskText.T("field.softness.tip") };
            softnessField.RegisterValueChangedCallback(e => { if (Window != null) Window.Softness = e.newValue; });
            root.Add(softnessField);
            strengthField = new Slider(MaskText.T("field.strength"), 0f, 1f) { showInputField = true };
            strengthField.RegisterValueChangedCallback(e => { if (Window != null) Window.Strength = e.newValue; });
            strengthRow = strengthField;
            root.Add(strengthField);
            penRow = MaskUI.Row(MaskText.T("row.pen"), MaskText.T("row.pen.tip"), MaskUI.Switches(out penItems,
                (MaskText.T("pen.size"), MaskText.T("pen.size.tip"), () => { if (Window != null) Window.PressureSize = !Window.PressureSize; Refresh(); }),
                (MaskText.T("pen.strength"), MaskText.T("pen.strength.tip"), () => { if (Window != null) Window.PressureStrength = !Window.PressureStrength; Refresh(); })));
            root.Add(penRow);
            mirrorField = new Toggle(MaskText.T("field.mirror")) { tooltip = MaskText.T("field.mirror.tip") };
            mirrorField.RegisterValueChangedCallback(e => { if (Window != null) Window.Mirror = e.newValue; });
            root.Add(mirrorField);
            viewField = new DropdownField(MaskText.T("field.show"), ViewChoices.ToList(), 0) { tooltip = MaskText.T("field.show.tip") };
            viewField.RegisterValueChangedCallback(_ => { if (Window != null) Window.View = ViewOrder[Mathf.Clamp(viewField.index, 0, ViewOrder.Length - 1)]; });
            root.Add(viewField);
            overlayField = new Slider(MaskText.T("field.overlay"), 0f, 1f) { showInputField = true, tooltip = MaskText.T("field.overlay.tip") };
            overlayField.RegisterValueChangedCallback(e => { if (Window != null) Window.OverlayStrength = e.newValue; });
            root.Add(overlayField);
            isolateField = new Toggle(MaskText.T("field.isolate")) { tooltip = MaskText.T("field.isolate.tip") };
            isolateField.RegisterValueChangedCallback(e => { if (Window != null) Window.OnlyThisMesh = e.newValue; });
            root.Add(isolateField);
            root.Add(MaskUI.Row(MaskText.T("row.whole"), MaskText.T("row.whole.tip"), MaskUI.Wrap(
                MaskUI.Button(MaskText.T("button.allblack"), MaskText.T("button.allblack.tip"), () => Window?.FillAll(0f)),
                MaskUI.Button(MaskText.T("button.allwhite"), MaskText.T("button.allwhite.tip"), () => Window?.FillAll(1f)),
                MaskUI.Button(MaskText.T("button.invert"), MaskText.T("button.invert.tip"), () => Window?.Invert()),
                copyButton = MaskUI.Button(MaskText.T("button.copyfrom"), MaskText.T("button.copyfrom.tip"), OpenCopyMenu))));
            root.Add(MaskUI.Row(MaskText.T("row.seams"), MaskText.T("button.seams.tip"), MaskUI.Wrap(
                MaskUI.Button(MaskText.T("button.seams"), MaskText.T("button.seams.tip"), () => Window?.EvenSeams()))));
            hint = MaskUI.Note("");
            root.Add(hint);
            Refresh();
        }

        void OpenCopyMenu()
        {
            var window = Window;
            if (window == null || !window.IsPainting) return;
            var menu = new GenericMenu();
            for (var c = MaskChannel.Red; c <= MaskChannel.Alpha; c++)
            {
                var from = c;
                var name = new GUIContent(ChannelChoices[(int)c]);
                if (c == window.Channel) menu.AddDisabledItem(name);
                else menu.AddItem(name, false, () => Window?.CopyChannel(from));
            }
            menu.DropDown(copyButton.worldBound);
        }

        void SetPaintsBlack(bool black)
        {
            if (Window == null) return;
            Window.PaintsBlack = black;
            Refresh();
        }

        void SetBlend(MaskBlend blend)
        {
            if (Window == null) return;
            Window.Blend = blend;
            Refresh();
        }

        public void Refresh()
        {
            if (builtLanguage != MaskLanguage.Current)
            {
                Build();
                return;
            }
            var window = Window;
            if (window == null || !window.IsPainting) return;
            var channel = window.Channel;
            channelField.SetValueWithoutNotify(ChannelChoices[(int)channel]);
            for (int i = 0; i < SwatchClasses.Length; i++) swatch.EnableInClassList(SwatchClasses[i], i == (int)channel);
            channelHint.text = channel == MaskChannel.All ? "" : MaskText.T("hint.channel", ChannelChoices[(int)channel]);
            MaskUI.Show(channelHint, channel != MaskChannel.All);

            var tool = window.Tool;
            bool painting = tool != MaskPainterWindow.PaintTool.Off;
            bool smooth = tool == MaskPainterWindow.PaintTool.Smooth;
            bool brush = tool == MaskPainterWindow.PaintTool.Brush || smooth;
            MaskUI.Mark(toolItems, (int)tool);
            MaskUI.Mark(blendItems, (int)window.Blend);
            MaskUI.Show(blendRow, painting && !smooth);
            MaskUI.Mark(paintItems, window.PaintsBlack ? 1 : 0);
            MaskUI.Show(paintRow, painting && !smooth);
            brushField.SetValueWithoutNotify(window.BrushCm);
            softnessField.SetValueWithoutNotify(window.Softness);
            MaskUI.Show(brushField, brush);
            MaskUI.Show(softnessField, brush);
            MaskUI.SetSwitch(penItems[0], window.PressureSize);
            MaskUI.SetSwitch(penItems[1], window.PressureStrength);
            MaskUI.Show(penRow, brush);
            bool replace = window.Blend == MaskBlend.Replace && !smooth;
            strengthField.label = MaskText.T(replace ? "field.level" : "field.strength");
            strengthField.tooltip = MaskText.T(replace ? "field.level.tip" : smooth ? "field.smooth.tip" : "field.strength.tip");
            strengthField.SetValueWithoutNotify(window.Strength);
            MaskUI.Show(strengthRow, painting);
            mirrorField.SetValueWithoutNotify(window.Mirror);
            MaskUI.Show(mirrorField, painting);
            int viewIndex = System.Array.IndexOf(ViewOrder, window.View);
            viewField.SetValueWithoutNotify(ViewChoices[Mathf.Max(0, viewIndex)]);
            overlayField.SetValueWithoutNotify(window.OverlayStrength);
            isolateField.SetValueWithoutNotify(window.OnlyThisMesh);
            MaskUI.Show(overlayField, window.View == MaskShow.Overlay || window.View == MaskShow.EveryChannel);
            hint.text = tool == MaskPainterWindow.PaintTool.Fill ? MaskText.T("hint.fill")
                : smooth ? MaskText.T("hint.smooth")
                : brush ? MaskText.T("hint.brush")
                : MaskText.T("hint.off");
        }
    }
}
