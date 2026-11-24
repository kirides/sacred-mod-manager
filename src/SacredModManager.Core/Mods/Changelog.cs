namespace SacredModManager.Core.Mods;

/// <summary>One version's section of a changelog: <c>## v1.2.0 (2026-11-18)</c> and its bullets.</summary>
public sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<string> Lines);

/// <summary>
/// Reads a mod's changelog (<c>history.md</c>: one <c>## vX.Y.Z (date)</c> section per version, newest first, bullets below)
/// and picks the part a player cares about: what changed after the installed version, up to the available one.
/// </summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> Parse(string? text)
    {
        var result = new List<ChangelogEntry>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        string? version = null, date = string.Empty;
        var lines = new List<string>();
        void Flush()
        {
            if (version is not null)
            {
                result.Add(new ChangelogEntry(version, date ?? string.Empty, lines.ToList()));
            }

            lines.Clear();
        }

        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                string head = line[3..].Trim();
                int paren = head.IndexOf('(');
                date = paren >= 0 ? head[(paren + 1)..].TrimEnd(')', ' ') : string.Empty;
                version = (paren >= 0 ? head[..paren] : head).Trim().TrimStart('v', 'V');
            }
            else if (version is not null && line.Length > 0)
            {
                lines.Add(line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ? line[2..].Trim() : line);
            }
        }

        Flush();
        return result;
    }

    /// <summary>
    /// The entries newer than <paramref name="installed"/> and not newer than <paramref name="available"/>, newest first.
    /// Without an installed version (not installed): only the newest entry up to <paramref name="available"/>.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Between(IReadOnlyList<ChangelogEntry> entries, string? installed, string? available)
    {
        IEnumerable<ChangelogEntry> upTo = entries.Where(e => string.IsNullOrEmpty(available) || VersionCompare.Compare(e.Version, available) <= 0)
            .OrderByDescending(e => e.Version, Comparer<string>.Create(VersionCompare.Compare));
        return string.IsNullOrEmpty(installed)
            ? upTo.Take(1).ToList()
            : upTo.Where(e => VersionCompare.IsNewer(e.Version, installed)).ToList();
    }

    /// <summary>Plain text for a list: a heading line per version, then its bullets.</summary>
    public static string Format(IEnumerable<ChangelogEntry> entries, bool withDate = true)
        => string.Join(Environment.NewLine + Environment.NewLine, entries.Select(e =>
            "v" + e.Version + (withDate && e.Date.Length > 0 ? "  (" + e.Date + ")" : string.Empty) + Environment.NewLine
            + string.Join(Environment.NewLine, e.Lines.Select(l => "•  " + l))));
}
