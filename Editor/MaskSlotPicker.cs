using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// A material's texture slots in the DocMe search menu: the slots with Mask in their name first, then every other
    /// slot, each named by its inspector label with the texture it holds beside it.
    /// </summary>
    public static class MaskSlotPicker
    {
        /// <summary>The material's texture slots in the order the menu lists them.</summary>
        public static List<(string property, string label, bool mask)> Items(Material material)
        {
            var slots = MaskFile.TextureSlots(material).Select(s =>
            {
                var label = MaskFile.SlotLabel(material, s);
                bool mask = s.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0;
                return (property: s, label: label == s ? s : label + " (" + s + ")", mask);
            }).ToList();
            return slots.Where(s => s.mask).Concat(slots.Where(s => !s.mask)).ToList();
        }

        /// <param name="current">The slot to tick, or null.</param>
        /// <param name="onlyFilled">Greys out the slots that hold no texture, for opening a texture from one.</param>
        public static List<MaskSearchMenu.Item> MenuItems(Material material, string current, bool onlyFilled, Action<string> picked)
        {
            var list = new List<MaskSearchMenu.Item>();
            bool masks = false, others = false;
            foreach (var slot in Items(material))
            {
                if (slot.mask && !masks)
                {
                    list.Add(MaskSearchMenu.Item.Heading(MaskText.T("picker.masks")));
                    masks = true;
                }
                if (!slot.mask && !others)
                {
                    list.Add(MaskSearchMenu.Item.Heading(MaskText.T("picker.other")));
                    others = true;
                }
                var texture = material.GetTexture(slot.property);
                var property = slot.property;
                list.Add(new MaskSearchMenu.Item
                {
                    Text = MaskFile.SlotLabel(material, property),
                    Note = texture != null ? texture.name : MaskText.T("picker.empty"),
                    SearchText = property + " " + (texture != null ? texture.name : ""),
                    Checked = property == current,
                    Disabled = onlyFilled && texture == null,
                    Action = () => picked(property),
                });
            }
            return list;
        }

        /// <summary>Opens the menu under button.</summary>
        public static void Show(VisualElement button, Material material, string current, bool onlyFilled, Action<string> picked) =>
            MaskSearchMenu.Open(button, MenuItems(material, current, onlyFilled, picked));
    }
}
