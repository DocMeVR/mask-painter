using System;
using System.Collections.Generic;
using System.Text;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// A gettext .po language file: msgid is a stable key, msgstr the text in the file's language, "#. en:" the English
    /// a translation was made from and "#, fuzzy" an outdated translation, which counts as missing.
    /// </summary>
    public sealed class PoFile
    {
        public sealed class Entry
        {
            public string key;
            public string text = "";
            public string english;
            public string previousEnglish;
            public bool fuzzy;
            public bool obsolete;

            /// <summary>Comment lines that are not read, kept as written.</summary>
            public readonly List<string> comments = new List<string>();
        }

        public const string LanguageField = "Language";
        public const string ReviewedField = "X-DocMe-Reviewed";

        /// <summary>The comment lines above the header, such as the ones Poedit writes; kept as written.</summary>
        public readonly List<string> HeaderComments = new List<string>();

        public readonly List<(string name, string value)> Header = new List<(string, string)>();
        public readonly List<Entry> Entries = new List<Entry>();
        public readonly List<string> Problems = new List<string>();
        readonly Dictionary<string, Entry> byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>False only while the header says X-DocMe-Reviewed: no.</summary>
        public bool Reviewed => !string.Equals(HeaderValue(ReviewedField)?.Trim(), "no", StringComparison.OrdinalIgnoreCase);

        public Entry Find(string key) => key != null && byKey.TryGetValue(key, out var entry) ? entry : null;

        /// <summary>The text of a key that is there, not empty and not outdated; null otherwise.</summary>
        public string Text(string key)
        {
            var entry = Find(key);
            return entry == null || entry.fuzzy || entry.text.Length == 0 ? null : entry.text;
        }

        public string HeaderValue(string name)
        {
            foreach (var (n, v) in Header)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return v;
            return null;
        }

        public void SetHeader(string name, string value)
        {
            for (int i = 0; i < Header.Count; i++)
            {
                if (!string.Equals(Header[i].name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                Header[i] = (Header[i].name, value);
                return;
            }
            Header.Add((name, value));
        }

        public void Add(Entry entry)
        {
            Entries.Add(entry);
            if (!entry.obsolete)
                byKey[entry.key] = entry;
        }

        public void SetEntries(IEnumerable<Entry> entries)
        {
            var list = new List<Entry>(entries);
            Entries.Clear();
            byKey.Clear();
            foreach (var entry in list)
                Add(entry);
        }

        /// <summary>The text of a file's bytes in UTF-8, without its byte order mark; null, with the reason, when the bytes are UTF-16 or not valid UTF-8.</summary>
        public static string Decode(byte[] bytes, out string error)
        {
            error = null;
            if (bytes == null)
            {
                error = "the file could not be opened.";
                return null;
            }
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                error = "the file is saved as UTF-16. Save it again as UTF-8.";
                return null;
            }
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
            }
            catch (DecoderFallbackException)
            {
                error = "the file is not saved as UTF-8. Save it again as UTF-8.";
                return null;
            }
        }

        public static PoFile Parse(string text)
        {
            var file = new PoFile();
            text ??= "";
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var entry = new Entry();
            var id = new StringBuilder();
            var str = new StringBuilder();
            StringBuilder open = null;
            bool hasId = false, hasStr = false;
            int start = 0;

            void Finish()
            {
                if (hasId && hasStr)
                {
                    entry.key = id.ToString();
                    entry.text = str.ToString();
                    if (entry.key.Length == 0)
                    {
                        if (!entry.obsolete)
                        {
                            file.ReadHeader(entry.text);
                            file.HeaderComments.AddRange(entry.comments);
                            if (entry.fuzzy)
                                file.HeaderComments.Add("#, fuzzy");
                        }
                    }
                    else if (!entry.obsolete && file.byKey.ContainsKey(entry.key))
                        file.Problems.Add("line " + start + ": the key \"" + entry.key + "\" is there twice");
                    else
                        file.Add(entry);
                }
                else if (hasId)
                    file.Problems.Add("line " + start + ": msgid without msgstr");
                entry = new Entry();
                id.Clear();
                str.Clear();
                open = null;
                hasId = hasStr = false;
            }

            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0)
                {
                    if (hasStr)
                        Finish();
                    continue;
                }
                bool obsolete = line.StartsWith("#~", StringComparison.Ordinal);
                if (obsolete)
                {
                    line = line.Substring(2).TrimStart();
                    if (line.Length == 0)
                        continue;
                }
                else if (line[0] == '#')
                {
                    if (hasId)
                        Finish();
                    ReadComment(entry, line);
                    continue;
                }

                if (line.StartsWith("msgid ", StringComparison.Ordinal))
                {
                    if (hasId)
                        Finish();
                    entry.obsolete = obsolete;
                    hasId = true;
                    start = n + 1;
                    open = id;
                    open.Append(ReadQuoted(line.Substring(6), file, n + 1));
                }
                else if (line.StartsWith("msgstr ", StringComparison.Ordinal) && hasId && !hasStr)
                {
                    hasStr = true;
                    open = str;
                    open.Append(ReadQuoted(line.Substring(7), file, n + 1));
                }
                else if (line[0] == '"' && open != null)
                    open.Append(ReadQuoted(line, file, n + 1));
                else
                    file.Problems.Add("line " + (n + 1) + ": not understood: " + line);
            }
            Finish();
            return file;
        }

        static void ReadComment(Entry entry, string line)
        {
            if (line.StartsWith("#. en:", StringComparison.Ordinal))
                entry.english = Unescape(line.Substring(6).Trim());
            else if (line.StartsWith("#. was:", StringComparison.Ordinal))
                entry.previousEnglish = Unescape(line.Substring(7).Trim());
            else if (line.StartsWith("#,", StringComparison.Ordinal))
            {
                var kept = new List<string>();
                foreach (var flag in line.Substring(2).Split(','))
                {
                    string f = flag.Trim();
                    if (f == "fuzzy")
                        entry.fuzzy = true;
                    else if (f.Length > 0)
                        kept.Add(f);
                }
                if (kept.Count > 0)
                    entry.comments.Add("#, " + string.Join(", ", kept));
            }
            else
                entry.comments.Add(line);
        }

        static string ReadQuoted(string part, PoFile file, int line)
        {
            part = part.Trim();
            if (part.Length < 2 || part[0] != '"' || part[part.Length - 1] != '"')
            {
                file.Problems.Add("line " + line + ": a text must stand in double quotes");
                return "";
            }
            var inner = part.Substring(1, part.Length - 2);
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '"')
                {
                    file.Problems.Add("line " + line + ": a double quote inside a text must be written \\\", and each line holds one text.");
                    return "";
                }
                if (inner[i] != '\\')
                    continue;
                if (i + 1 >= inner.Length)
                {
                    file.Problems.Add("line " + line + ": the text ends in a lone backslash. Write \\\\ for a backslash.");
                    return "";
                }
                if (Escapes.IndexOf(inner[++i]) < 0)
                {
                    file.Problems.Add("line " + line + ": \\" + inner[i] + " is not an escape Mask Painter reads. Write \\\\ for a backslash, \\\" for a double quote, \\n for a line break or \\t for a tab.");
                    return "";
                }
            }
            return Unescape(inner);
        }

        /// <summary>The characters that may follow a backslash in a text: the escapes Escape writes.</summary>
        const string Escapes = "\\\"ntr";

        void ReadHeader(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    Header.Add((line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()));
            }
        }

        public string Write()
        {
            var sb = new StringBuilder();
            foreach (var comment in HeaderComments)
                sb.Append(comment).Append('\n');
            sb.Append("msgid \"\"\nmsgstr \"\"\n");
            foreach (var (name, value) in Header)
                sb.Append('"').Append(Escape(name + ": " + value + "\n")).Append("\"\n");
            foreach (var entry in Entries)
            {
                sb.Append('\n');
                foreach (var comment in entry.comments)
                    sb.Append(comment).Append('\n');
                if (entry.english != null)
                    sb.Append("#. en: ").Append(Escape(entry.english)).Append('\n');
                if (entry.previousEnglish != null)
                    sb.Append("#. was: ").Append(Escape(entry.previousEnglish)).Append('\n');
                if (entry.fuzzy)
                    sb.Append("#, fuzzy\n");
                string prefix = entry.obsolete ? "#~ " : "";
                sb.Append(prefix).Append("msgid \"").Append(Escape(entry.key)).Append("\"\n");
                AppendText(sb, prefix, entry.text ?? "");
            }
            return sb.ToString();
        }

        static void AppendText(StringBuilder sb, string prefix, string text)
        {
            int newline = text.IndexOf('\n');
            if (newline < 0 || newline == text.Length - 1)
            {
                sb.Append(prefix).Append("msgstr \"").Append(Escape(text)).Append("\"\n");
                return;
            }
            sb.Append(prefix).Append("msgstr \"\"\n");
            int from = 0;
            while (from < text.Length)
            {
                int end = text.IndexOf('\n', from);
                end = end < 0 ? text.Length : end + 1;
                sb.Append(prefix).Append('"').Append(Escape(text.Substring(from, end - from))).Append("\"\n");
                from = end;
            }
        }

        public static string Escape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");

        public static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0)
                return s ?? "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char e = s[++i];
                sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e);
            }
            return sb.ToString();
        }
    }
}
