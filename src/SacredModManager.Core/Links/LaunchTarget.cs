using SacredModManager.Core.Mods;

namespace SacredModManager.Core.Links;

/// <summary>What a command line argument (or a message from a second start) asks for: a <c>gear-mod-manager://</c> link or a mod archive file to install.</summary>
public enum LaunchKind
{
    None,
    Link,
    ArchiveFile,
}

public static class LaunchTarget
{
    /// <summary>A link is recognised by its scheme, an archive by the file's first bytes (the same check as drag and drop and the file picker use).</summary>
    public static LaunchKind Classify(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return LaunchKind.None;
        }

        if (ManagerLink.LooksLikeLink(argument))
        {
            return LaunchKind.Link;
        }

        return ArchiveFormats.IsArchiveFile(argument.Trim('"')) ? LaunchKind.ArchiveFile : LaunchKind.None;
    }

    /// <summary>The first argument that is a link or an archive file, or null.</summary>
    public static string? FirstTarget(IEnumerable<string> args)
        => args.Select(a => a.Trim('"')).FirstOrDefault(a => Classify(a) != LaunchKind.None);
}
