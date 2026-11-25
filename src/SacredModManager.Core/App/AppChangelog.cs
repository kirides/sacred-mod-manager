using System.Text;
using System.Text.RegularExpressions;

namespace SacredModManager.Core.App;

/// <summary>One build's section of the manager's own changelog: <c>## 2026-11-22 (d4a5144)</c> and its bullets.</summary>
public sealed record AppChangelogEntry(string Sha, string Heading, string Markdown);

/// <summary>
/// The manager's own changelog (<c>changelog/history.md</c> in its repository): one section per build, newest first, each headed by the date and the
/// 7-character commit of the build (<c>## 2026-11-22 (d4a5144)</c>, the same commit that is in the build stamp <c>nightly-20261122-d4a5144</c>).
/// </summary>
public static partial class AppChangelog
{
    public static string Url(string repository) => "https://raw.githubusercontent.com/" + repository + "/main/changelog/history.md";

    public static IReadOnlyList<AppChangelogEntry> Parse(string? text)
    {
        var result = new List<AppChangelogEntry>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        string? sha = null, heading = null;
        var body = new StringBuilder();
        void Flush()
        {
            if (sha is not null)
            {
                result.Add(new AppChangelogEntry(sha, heading!, ("## " + heading + "\n\n" + body.ToString().Trim('\n')).TrimEnd()));
            }

            body.Clear();
        }

        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            Match m = raw.StartsWith("## ", StringComparison.Ordinal) ? HeadingPattern().Match(raw) : Match.Empty;
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                sha = m.Success ? m.Groups["sha"].Value.ToLowerInvariant() : null;
                heading = raw[3..].Trim();
            }
            else if (sha is not null)
            {
                body.Append(raw).Append('\n');
            }
        }

        Flush();
        return result;
    }

    /// <summary>
    /// The entries of the builds after <paramref name="lastSeenSha"/> up to and including <paramref name="currentSha"/> (both may be a longer commit id; only the first 7
    /// characters count). Without a last seen build only the current one; null when the current build is not in the changelog.
    /// </summary>
    public static IReadOnlyList<AppChangelogEntry>? Since(IReadOnlyList<AppChangelogEntry> entries, string currentSha, string? lastSeenSha)
    {
        int current = IndexOf(entries, currentSha);
        if (current < 0)
        {
            return null;
        }

        int seen = string.IsNullOrEmpty(lastSeenSha) ? -1 : IndexOf(entries, lastSeenSha);
        if (seen < 0)
        {
            return [entries[current]];
        }

        return seen <= current ? [] : entries.Skip(current).Take(seen - current).ToList();
    }

    private static int IndexOf(IReadOnlyList<AppChangelogEntry> entries, string sha)
    {
        string s = sha.Length >= 7 ? sha[..7].ToLowerInvariant() : sha.ToLowerInvariant();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Sha == s)
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"\((?<sha>[0-9a-fA-F]{7})\)\s*$")]
    private static partial Regex HeadingPattern();
}
