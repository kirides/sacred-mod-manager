namespace SacredModManager.Core.Mods;

/// <summary>
/// File types that are programs or scripts. A plugin never writes one by accident: loose files may never be one, and a file of an archive only when the index
/// entry names that file explicitly (never through "every file" or a wildcard). Nothing is ever run by the manager either way.
/// </summary>
public static class ExecutableTargets
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".cpl", ".jar", ".sh",
        ".pif", ".gadget", ".inf", ".url", ".appref-ms",
    };

    /// <summary>Is the path (any separators) a program or script by its extension?</summary>
    public static bool IsExecutable(string path)
    {
        string name = path.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        // a trailing dot or space is dropped by Windows, so "run.bat." is still run.bat
        name = name.TrimEnd('.', ' ');
        foreach (string ext in Extensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && name.Length > ext.Length)
            {
                return true;
            }
        }

        return false;
    }
}
