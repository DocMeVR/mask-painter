using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// The language picker at the right end of the header, as AvatarDoc's: the current language in its own name and an
    /// arrow. Choosing one changes the language NDMF's tools share, when NDMF is installed.
    /// </summary>
    public sealed class MaskLanguagePicker : Button
    {
        public MaskLanguagePicker(Action changed)
        {
            AddToClassList("docme-language");
            tooltip = MaskText.T("field.language.tip");
            var languages = MaskText.Languages();
            var current = CurrentOf(languages);
            Add(new Label(MaskLanguage.NativeName(current)) { pickingMode = PickingMode.Ignore });
            var arrow = new VisualElement { pickingMode = PickingMode.Ignore };
            arrow.AddToClassList("unity-base-popup-field__arrow");
            Add(arrow);
            clicked += () =>
            {
                var menu = new GenericMenu();
                foreach (var code in languages)
                    menu.AddItem(new GUIContent(MaskLanguage.NativeName(code)), code == current, () =>
                    {
                        MaskLanguage.Set(code);
                        changed?.Invoke();
                    });
                menu.DropDown(worldBound);
            };
        }

        /// <summary>The offered language that matches the current one, by its base language when no exact match exists.</summary>
        public static string CurrentOf(System.Collections.Generic.List<string> languages)
        {
            string current = MaskLanguage.Current;
            if (languages.Contains(current)) return current;
            return languages.FirstOrDefault(l => MaskLanguage.BaseOf(l) == MaskLanguage.BaseOf(current)) ?? MaskLanguage.English;
        }
    }
}
