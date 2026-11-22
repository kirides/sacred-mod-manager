using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;

namespace SacredModManager.Core.App;

/// <summary>
/// What the player is told before anything is installed: where the package comes from and exactly which files go where. It is built from the index entry alone
/// (nothing is downloaded yet), so wildcards are shown as written. Nothing else happens at install time: indexes cannot add steps.
/// </summary>
public static class InstallPreview
{
    public static List<string> Describe(CatalogMod m, string toolsRoot)
    {
        var lines = new List<string>();
        if (m.LooseFiles.Count == 0)
        {
            lines.Add("from " + m.DownloadUrl.AbsoluteUri + (m.DownloadInner is null ? string.Empty : " (the archive '" + m.DownloadInner + "' inside it)"));
            if (!string.IsNullOrEmpty(m.DownloadSha256))
            {
                lines.Add("sha256 of the download " + m.DownloadSha256);
            }
        }

        lines.Add("listed by " + m.IndexUrl.AbsoluteUri);
        foreach (LooseFile f in m.LooseFiles)
        {
            lines.Add("loose file " + f.Url.AbsoluteUri + "  sha256 " + f.Sha256 + " (checked after the download; nothing is installed if it differs) -> " + Join(m.InstallTo, f.To) + "  (Sacred folder)");
        }

        string source = m.Path.Length == 0 ? "the package root" : "'" + m.Path + "'";
        switch (m.Kind)
        {
            case ItemKind.Plugin when m.LooseFiles.Count > 0:
                break;
            case ItemKind.Plugin when m.InstallFiles.Count > 0:
                foreach ((string from, string to) in m.InstallFiles)
                {
                    bool pattern = PackageGlob.IsPattern(from);
                    lines.Add(from + (pattern ? " (every match)" : string.Empty) + " -> " + Join(m.InstallTo, to) + (pattern ? "/..." : string.Empty) + "  (Sacred folder)");
                }

                break;
            case ItemKind.Plugin:
                lines.Add("every file under " + source + " -> " + (m.InstallTo.Length == 0 ? string.Empty : m.InstallTo + "/") + "...  (Sacred folder)");
                break;
            case ItemKind.Tool:
                lines.Add("every file under " + source + " -> " + System.IO.Path.Combine(toolsRoot, m.Id) + System.IO.Path.DirectorySeparatorChar + "...");
                if (!string.IsNullOrEmpty(m.Launch))
                {
                    lines.Add("the Start button (only when you click it) starts " + m.Launch);
                }

                break;
            default:
                lines.Add("the folder " + source + " -> mods/" + m.FolderName + "/...  (Sacred folder)");
                break;
        }

        return lines;
    }

    private static string Join(string folder, string file) => folder.Length == 0 ? file : folder.TrimEnd('/') + "/" + file;
}
