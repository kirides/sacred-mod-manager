namespace SacredModManager.Core.Mods;

/// <summary>A run of text with its inline style.</summary>
public readonly record struct MdSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false);

public enum MdBlockKind { Paragraph, Heading, Bullet, Blank }

/// <summary>One line of markdown: what kind it is, the heading level (1-6), and its styled text.</summary>
public sealed record MdBlock(MdBlockKind Kind, int Level, IReadOnlyList<MdSpan> Spans);

/// <summary>
/// The few markdown features a changelog needs, parsed without any markup library: headings (<c>#</c>), bullets (<c>-</c>, <c>*</c>, <c>+</c>),
/// <c>**bold**</c>, <c>*italic*</c> / <c>_italic_</c> and <c>`code`</c>. Everything else is plain text; nothing is ever executed or fetched.
/// </summary>
public static class MarkdownLite
{
    public static IReadOnlyList<MdBlock> ParseBlocks(string? text)
    {
        var result = new List<MdBlock>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            string t = line.TrimStart();
            if (t.Length == 0)
            {
                result.Add(new MdBlock(MdBlockKind.Blank, 0, []));
            }
            else if (t[0] == '#')
            {
                int level = 0;
                while (level < t.Length && t[level] == '#')
                {
                    level++;
                }

                if (level <= 6 && level < t.Length && t[level] == ' ')
                {
                    result.Add(new MdBlock(MdBlockKind.Heading, level, ParseInline(t[(level + 1)..].Trim())));
                    continue;
                }

                result.Add(new MdBlock(MdBlockKind.Paragraph, 0, ParseInline(t)));
            }
            else if (t.Length > 2 && t[0] is '-' or '*' or '+' && t[1] == ' ' && t.Length > 2)
            {
                result.Add(new MdBlock(MdBlockKind.Bullet, 0, ParseInline(t[2..].Trim())));
            }
            else
            {
                result.Add(new MdBlock(MdBlockKind.Paragraph, 0, ParseInline(t)));
            }
        }

        return result;
    }

    public static IReadOnlyList<MdSpan> ParseInline(string text)
    {
        var spans = new List<MdSpan>();
        var sb = new System.Text.StringBuilder();
        bool bold = false, italic = false;

        void Flush()
        {
            if (sb.Length > 0)
            {
                spans.Add(new MdSpan(sb.ToString(), bold, italic));
                sb.Clear();
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && "*_`\\".Contains(text[i + 1]))
            {
                sb.Append(text[++i]);
            }
            else if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    spans.Add(new MdSpan(text[(i + 1)..end], bold, italic, Code: true));
                    i = end;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                if (bold || text.IndexOf("**", i + 2, StringComparison.Ordinal) > 0)
                {
                    Flush();
                    bold = !bold;
                    i++;
                }
                else
                {
                    sb.Append("**");
                    i++;
                }
            }
            else if (c is '*' or '_' && IsEmphasis(text, i, italic))
            {
                Flush();
                italic = !italic;
            }
            else
            {
                sb.Append(c);
            }
        }

        Flush();
        return spans;
    }

    /// <summary>
    /// Does the * or _ at <paramref name="i"/> start emphasis (<paramref name="inside"/> false) or end it (true)? Starting needs a non-space after it and a matching one later,
    /// ending a non-space before it; an underscore inside a word (snake_case) is never emphasis, and "2 * 3" stays as it is.
    /// </summary>
    private static bool IsEmphasis(string s, int i, bool inside)
    {
        char c = s[i];
        char before = i > 0 ? s[i - 1] : ' ';
        char after = i + 1 < s.Length ? s[i + 1] : ' ';
        if (inside)
        {
            return !char.IsWhiteSpace(before) && !(c == '_' && char.IsLetterOrDigit(after));
        }

        return !char.IsWhiteSpace(after) && !(c == '_' && char.IsLetterOrDigit(before)) && s.IndexOf(c, i + 1) > i;
    }
}
