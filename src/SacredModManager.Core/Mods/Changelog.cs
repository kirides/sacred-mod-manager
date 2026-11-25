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

    /// <summary>The entry of exactly this version (none when the changelog does not have one: a version newer than the published ones, a local copy, a skipped entry).</summary>
    public static IReadOnlyList<ChangelogEntry> ForVersion(IReadOnlyList<ChangelogEntry> entries, string? version)
        => string.IsNullOrEmpty(version) ? [] : entries.Where(e => VersionCompare.Compare(e.Version, version) == 0).Take(1).ToList();

    /// <summary>What to tell when an installed version has no changelog entry, or null when it has one (or there is no changelog at all).</summary>
    public static string? MissingEntryNote(IReadOnlyList<ChangelogEntry> entries, string? version)
    {
        if (entries.Count == 0 || string.IsNullOrEmpty(version) || ForVersion(entries, version).Count > 0)
        {
            return null;
        }

        ChangelogEntry newest = entries.OrderByDescending(e => e.Version, Comparer<string>.Create(VersionCompare.Compare)).First();
        return VersionCompare.IsNewer(version, newest.Version)
            ? "Version " + version + " has no published changelog entry yet. It is newer than the newest published version (" + newest.Version + "), for example a copy you are developing."
            : "The changelog has no entry for version " + version + ".";
    }

    /// <summary>Plain text for a list: a heading line per version, then its bullets.</summary>
    public static string Format(IEnumerable<ChangelogEntry> entries, bool withDate = true)
        => string.Join(Environment.NewLine + Environment.NewLine, entries.Select(e =>
            "v" + e.Version + (withDate && e.Date.Length > 0 ? "  (" + e.Date + ")" : string.Empty) + Environment.NewLine
            + string.Join(Environment.NewLine, e.Lines.Select(l => "•  " + l))));

    /// <summary>The entries as markdown (<c>## v1.2.0 (date)</c> and bullets), for <see cref="MarkdownLite"/>.</summary>
    public static string ToMarkdown(IEnumerable<ChangelogEntry> entries, bool withDate = true)
        => string.Join("\n\n", entries.Select(e =>
            "## v" + e.Version + (withDate && e.Date.Length > 0 ? " (" + e.Date + ")" : string.Empty) + "\n"
            + string.Join("\n", e.Lines.Select(l => "- " + l))));
}
