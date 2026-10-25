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
        static string[] ViewChoices => new[] { MaskText.T("show.overlay"), MaskText.T("show.mask"), MaskText.T("show.nothing") };

        DropdownField channelField, viewField;
        Label channelHint, hint, strengthLabel;
        Button[] toolItems, blendItems;
        Slider brushField, softnessField, strengthField;
        Toggle mirrorField;
        VisualElement blendRow, strengthRow;
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
            root.Add(channelField);
            channelHint = MaskUI.Note("");
            root.Add(channelHint);

            root.Add(MaskUI.Row(MaskText.T("row.tool"), MaskText.T("row.tool.tip"), MaskUI.Segments(out toolItems,
                (MaskText.T("tool.off"), MaskText.T("tool.off.tip"), () => Window?.SetTool(MaskPainterWindow.PaintTool.Off)),
                (MaskText.T("tool.fill"), MaskText.T("tool.fill.tip"), () => Window?.SetTool(MaskPainterWindow.PaintTool.Fill)),
                (MaskText.T("tool.brush"), MaskText.T("tool.brush.tip"), () => Window?.SetTool(MaskPainterWindow.PaintTool.Brush)))));
            blendRow = MaskUI.Row(MaskText.T("field.blend"), MaskText.T("field.blend.tip"), MaskUI.Segments(out blendItems,
                (MaskText.T("blend.add"), MaskText.T("blend.add.tip"), () => SetBlend(MaskBlend.Add)),
                (MaskText.T("blend.replace"), MaskText.T("blend.replace.tip"), () => SetBlend(MaskBlend.Replace))));
            root.Add(blendRow);

            brushField = new Slider(MaskText.T("field.brush"), MaskPainterWindow.MinBrushCm, MaskPainterWindow.MaxBrushCm) { showInputField = true, tooltip = MaskText.T("field.brush.tip") };
            brushField.RegisterValueChangedCallback(e => { if (Window != null) Window.BrushCm = e.newValue; });
            root.Add(brushField);
            softnessField = new Slider(MaskText.T("field.softness"), 0f, 1f) { showInputField = true, tooltip = MaskText.T("field.softness.tip") };
            softnessField.RegisterValueChangedCallback(e => { if (Window != null) Window.Softness = e.newValue; });
            root.Add(softnessField);
            strengthField = new Slider("", 0f, 1f) { showInputField = true };
            strengthField.RegisterValueChangedCallback(e => { if (Window != null) Window.Strength = e.newValue; });
            strengthLabel = strengthField.labelElement;
            strengthRow = strengthField;
            root.Add(strengthField);
            mirrorField = new Toggle(MaskText.T("field.mirror")) { tooltip = MaskText.T("field.mirror.tip") };
            mirrorField.RegisterValueChangedCallback(e => { if (Window != null) Window.Mirror = e.newValue; });
            root.Add(mirrorField);
            viewField = new DropdownField(MaskText.T("field.show"), ViewChoices.ToList(), 0) { tooltip = MaskText.T("field.show.tip") };
            viewField.RegisterValueChangedCallback(_ => { if (Window != null) Window.ShowMode = Mathf.Max(0, viewField.index); });
            root.Add(viewField);
            root.Add(MaskUI.Row(MaskText.T("row.whole"), MaskText.T("row.whole.tip"), MaskUI.Wrap(
                MaskUI.Button(MaskText.T("button.allblack"), MaskText.T("button.allblack.tip"), () => Window?.FillAll(0f)),
                MaskUI.Button(MaskText.T("button.allwhite"), MaskText.T("button.allwhite.tip"), () => Window?.FillAll(1f)),
                MaskUI.Button(MaskText.T("button.invert"), MaskText.T("button.invert.tip"), () => Window?.Invert()))));
            hint = MaskUI.Note("");
            root.Add(hint);
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
            channelHint.text = channel == MaskChannel.All ? "" : MaskText.T("hint.channel", ChannelChoices[(int)channel]);
            MaskUI.Show(channelHint, channel != MaskChannel.All);

            var tool = window.Tool;
            bool painting = tool != MaskPainterWindow.PaintTool.Off;
            bool brush = tool == MaskPainterWindow.PaintTool.Brush;
            MaskUI.Mark(toolItems, (int)tool);
            MaskUI.Mark(blendItems, (int)window.Blend);
            MaskUI.Show(blendRow, painting);
            brushField.SetValueWithoutNotify(window.BrushCm);
            softnessField.SetValueWithoutNotify(window.Softness);
            MaskUI.Show(brushField, brush);
            MaskUI.Show(softnessField, brush);
            bool replace = window.Blend == MaskBlend.Replace;
            strengthLabel.text = MaskText.T(replace ? "field.level" : "field.strength");
            strengthField.tooltip = MaskText.T(replace ? "field.level.tip" : "field.strength.tip");
            strengthField.SetValueWithoutNotify(window.Strength);
            MaskUI.Show(strengthRow, painting);
            mirrorField.SetValueWithoutNotify(window.Mirror);
            MaskUI.Show(mirrorField, painting);
            viewField.SetValueWithoutNotify(ViewChoices[Mathf.Clamp(window.ShowMode, 0, ViewChoices.Length - 1)]);
            hint.text = tool == MaskPainterWindow.PaintTool.Fill ? MaskText.T("hint.fill")
                : brush ? MaskText.T("hint.brush")
                : MaskText.T("hint.off");
        }
    }
}
