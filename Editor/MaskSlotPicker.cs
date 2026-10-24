using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// Unity's searchable dropdown with a material's texture slots: the slots with Mask in their name first, then a
    /// line, then every other slot, each named by its inspector label and its property name.
    /// </summary>
    public sealed class MaskSlotPicker : AdvancedDropdown
    {
        readonly List<(string property, string label, bool mask)> items;
        readonly Action<string> picked;

        public MaskSlotPicker(Material material, Action<string> picked) : base(new AdvancedDropdownState())
        {
            items = Items(material);
            this.picked = picked;
            minimumSize = new Vector2(300f, 420f);
        }

        /// <summary>The material's texture slots in the order the picker lists them.</summary>
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

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem(MaskText.T("picker.title"));
            bool separated = false;
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].mask && !separated && i > 0)
                {
                    root.AddSeparator();
                    separated = true;
                }
                root.AddChild(new AdvancedDropdownItem(items[i].label) { id = i });
            }
            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < items.Count) picked(items[item.id].property);
        }
    }
}
