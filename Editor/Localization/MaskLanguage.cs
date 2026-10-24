using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>Where the chosen language lives. Codes are lowercase ("ja-jp"); empty means none chosen yet.</summary>
    public interface IMaskLanguageStore
    {
        string Language { get; set; }

        /// <summary>Tells the store a language Mask Painter has a file for, so a shared picker can offer it.</summary>
        void Register(string code);
    }

    public sealed class MemoryLanguageStore : IMaskLanguageStore
    {
        public readonly List<string> Registered = new List<string>();

        public MemoryLanguageStore(string language = "") => Language = language;

        public string Language { get; set; }

        public void Register(string code)
        {
            if (!Registered.Contains(code))
                Registered.Add(code);
        }
    }

    /// <summary>
    /// The window language: shared with NDMF (Modular Avatar, Avatar Optimizer and AvatarDoc use it) when NDMF is
    /// installed, otherwise Mask Painter's own per user setting. Until the user picks one it follows the system language.
    /// </summary>
    public static class MaskLanguage
    {
        public const string English = "en-us";
        public const string PrefsKey = "DocMe.MaskPainter.language";
        public const string NdmfTypeName = "nadena.dev.ndmf.localization.LanguagePrefs, nadena.dev.ndmf";

        static IMaskLanguageStore defaultStore;

        /// <summary>Replaces NDMF and EditorPrefs for tests.</summary>
        public static IMaskLanguageStore StoreOverride;

        public static IMaskLanguageStore Store
            => StoreOverride ?? (defaultStore ??= (IMaskLanguageStore)NdmfStore.TryCreate(NdmfTypeName) ?? new PrefsStore());

        public static string Current
        {
            get
            {
                string code = Normalize(Store.Language);
                return code.Length > 0 ? code : FromSystem(Application.systemLanguage);
            }
        }

        public static void Set(string code) => Store.Language = Normalize(code);

        public static string Normalize(string code) => (code ?? "").Trim().Replace('_', '-').ToLowerInvariant();

        public static string BaseOf(string code)
        {
            code = Normalize(code);
            int dash = code.IndexOf('-');
            return dash < 0 ? code : code.Substring(0, dash);
        }

        public static string FromSystem(SystemLanguage language)
        {
            switch (language)
            {
                case SystemLanguage.Japanese: return "ja-jp";
                case SystemLanguage.Korean: return "ko-kr";
                case SystemLanguage.ChineseSimplified: return "zh-hans";
                case SystemLanguage.ChineseTraditional: return "zh-hant";
                case SystemLanguage.German: return "de-de";
                case SystemLanguage.French: return "fr-fr";
                case SystemLanguage.Spanish: return "es-es";
                case SystemLanguage.Portuguese: return "pt-br";
                case SystemLanguage.Russian: return "ru-ru";
                case SystemLanguage.Italian: return "it-it";
                default: return English;
            }
        }

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            ["en-us"] = "English",
            ["ja-jp"] = "日本語",
            ["ko-kr"] = "한국어",
            ["zh-hans"] = "简体中文",
            ["zh-hant"] = "繁體中文",
        };

        /// <summary>A language in its own name ("日本語"), for the Language dropdown.</summary>
        public static string NativeName(string code)
        {
            code = Normalize(code);
            if (Names.TryGetValue(code, out var name))
                return name;
            try
            {
                var culture = CultureInfo.GetCultureInfo(BaseOf(code));
                string native = culture.NativeName;
                if (!string.IsNullOrEmpty(native) && culture.Name.Length > 0)
                    return char.ToUpper(native[0], culture) + native.Substring(1);
            }
            catch (Exception)
            {
            }
            return code;
        }

        sealed class PrefsStore : IMaskLanguageStore
        {
            string cached;

            public string Language
            {
                get => cached ??= EditorPrefs.GetString(PrefsKey, "");
                set
                {
                    cached = value ?? "";
                    EditorPrefs.SetString(PrefsKey, cached);
                }
            }

            public void Register(string code)
            {
            }
        }

        internal sealed class NdmfStore : IMaskLanguageStore
        {
            readonly PropertyInfo language;
            readonly MethodInfo register;

            NdmfStore(PropertyInfo language, MethodInfo register)
            {
                this.language = language;
                this.register = register;
            }

            /// <summary>Null when NDMF is missing or its LanguagePrefs has no readable and writable static string Language.</summary>
            public static NdmfStore TryCreate(string typeName)
            {
                try
                {
                    var type = Type.GetType(typeName, false);
                    var language = type?.GetProperty("Language", BindingFlags.Public | BindingFlags.Static);
                    if (language == null || language.PropertyType != typeof(string) || !language.CanRead || !language.CanWrite)
                        return null;
                    var register = type.GetMethod("RegisterLanguage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    return new NdmfStore(language, register);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public string Language
            {
                get
                {
                    try { return (string)language.GetValue(null) ?? ""; }
                    catch (Exception) { return ""; }
                }
                set
                {
                    try { language.SetValue(null, value); }
                    catch (Exception) { }
                }
            }

            public void Register(string code)
            {
                try { register?.Invoke(null, new object[] { code }); }
                catch (Exception) { }
            }
        }
    }
}
