namespace SacredModManager.Core.App;

/// <summary>One definition of "the same folder" and "inside this folder": full path, no trailing separator, case-insensitive on Windows.</summary>
public static class PathRules
{
    /// <summary>The comparison for file system paths on this platform.</summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The full path without trailing separators (a root such as <c>C:\</c> or <c>/</c> keeps its one).</summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (root is not null && full.Length <= root.Length)
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool AreSame(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary>True when <paramref name="path"/> lies strictly below <paramref name="root"/> (after resolving <c>..</c>); the root itself is not inside.</summary>
    public static bool IsInside(string root, string path)
    {
        string r = Normalize(root);
        string p = Normalize(path);
        if (!r.EndsWith(Path.DirectorySeparatorChar))
        {
            r += Path.DirectorySeparatorChar;
        }

        return p.Length > r.Length && p.StartsWith(r, Comparison);
    }

    /// <summary>The full path of <paramref name="relative"/> (with <c>/</c> or <c>\</c>) below <paramref name="root"/>, or null when it escapes the root.</summary>
    public static string? ResolveInside(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(Normalize(root), relative.Replace('/', Path.DirectorySeparatorChar)));
        return IsInside(root, full) ? full : null;
    }
}
