namespace SacredModManager.Core.Mods;

/// <summary>
/// The wildcard patterns of <c>install.files[].from</c>. Rules:
/// <list type="bullet">
/// <item>A pattern is a relative path of '/'-separated segments. <c>*</c> is any run of characters and <c>?</c> one character inside a single segment (never across '/').</item>
/// <item>A segment that is exactly <c>**</c> stands for any number of folders (also none): <c>**</c> alone is every file, <c>bin/**</c> every file below bin, <c>**/*.dll</c> every dll anywhere.</item>
/// <item><c>**</c> mixed with other characters in one segment (<c>a**b</c>) is not allowed. Matching is case-insensitive.</item>
/// <item>A pattern is only ever matched against the names of the archive's files, which were checked already, so it cannot reach anything else; '..', '.', empty segments, a leading '/', '\', ':' are rejected.</item>
/// <item>The literal folders before the first wildcard segment are the pattern's base. A match goes to <c>to/&lt;path relative to the base&gt;</c>, so <c>*.dll</c> is flat and <c>**</c> keeps the folder layout.</item>
/// </list>
/// </summary>
public static class PackageGlob
{
    public static bool IsPattern(string s) => s.AsSpan().ContainsAny('*', '?');

    /// <summary>Whether <paramref name="pattern"/> is a well-formed (safe) pattern or literal path.</summary>
    public static bool IsValid(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 240 || pattern.Contains('\\', StringComparison.Ordinal)
            || pattern.StartsWith('/') || pattern.EndsWith('/') || pattern.Contains(':', StringComparison.Ordinal) || pattern.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string seg in pattern.Split('/'))
        {
            if (seg is "" or "." or ".." || (seg.Contains("**", StringComparison.Ordinal) && seg != "**"))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The literal folders (no trailing '/') before the first segment with a wildcard; "" when the pattern starts with one.</summary>
    public static string BaseFolder(string pattern)
    {
        string[] segs = pattern.Split('/');
        int i = 0;
        while (i < segs.Length - 1 && !IsPattern(segs[i]))
        {
            i++;
        }

        return string.Join('/', segs.Take(i));
    }

    public static bool IsMatch(string pattern, string path) => Match(pattern.Split('/'), 0, path.Split('/'), 0);

    private static bool Match(string[] pat, int p, string[] path, int s)
    {
        while (p < pat.Length)
        {
            if (pat[p] == "**")
            {
                // a trailing '**' is every file below; in the middle it swallows zero or more folders (never the file name itself)
                if (p == pat.Length - 1)
                {
                    return s < path.Length;
                }

                for (int k = s; k < path.Length; k++)
                {
                    if (Match(pat, p + 1, path, k))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (s >= path.Length || !SegmentMatch(pat[p], path[s]))
            {
                return false;
            }

            p++;
            s++;
        }

        return s == path.Length;
    }

    private static bool SegmentMatch(string pattern, string text)
    {
        int pi = 0, ti = 0, star = -1, mark = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++;
                mark = ti;
            }
            else if (pi < pattern.Length && (pattern[pi] == '?' || char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(text[ti])))
            {
                pi++;
                ti++;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                ti = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*')
        {
            pi++;
        }

        return pi == pattern.Length;
    }
}
