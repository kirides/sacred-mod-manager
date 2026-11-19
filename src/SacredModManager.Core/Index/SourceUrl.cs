using System.Text.RegularExpressions;

namespace SacredModManager.Core.Index;

/// <summary>Turns what the player types or pastes as a source into the URL of a <c>mod-index.json</c>.</summary>
public static partial class SourceUrl
{
    public const string IndexFileName = "mod-index.json";

    /// <summary>
    /// Accepts: an https URL of an index file; a GitHub repository URL (its <c>mod-index.json</c> on the default branch); a GitHub
    /// <c>blob</c> URL of a file; a local file or folder path (a folder means its <c>mod-index.json</c>); a bare host path without scheme.
    /// </summary>
    public static Uri Parse(string text)
    {
        if (!TryParse(text, out Uri? uri, out string? error))
        {
            throw new IndexException(error!);
        }

        return uri!;
    }

    public static bool TryParse(string? text, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        text = text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(text))
        {
            error = "enter the address of a mod index";
            return false;
        }

        if (LocalPath(text) is { } local)
        {
            uri = local;
            return true;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? u))
        {
            error = "this is not a valid address";
            return false;
        }

        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback))
        {
            error = "only https addresses (or a local file) can be used";
            return false;
        }

        uri = GitHubRaw(u) ?? StripFragment(u);
        return true;
    }

    /// <summary>The key two URLs of the same index share: scheme and host in lower case, no fragment.</summary>
    public static string Key(Uri uri) => StripFragment(uri).AbsoluteUri;

    public static bool IsAllowedDownload(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps || uri.IsFile || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static Uri StripFragment(Uri u) => string.IsNullOrEmpty(u.Fragment) ? u : new Uri(u.GetLeftPart(UriPartial.Query));

    private static Uri? LocalPath(string text)
    {
        bool looksLocal = (text.Length > 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] is '\\' or '/'))
                          || text.StartsWith(@"\\", StringComparison.Ordinal)
                          || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!looksLocal)
        {
            return null;
        }

        string path = text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out Uri? f) ? f.LocalPath : text;
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, IndexFileName);
        }

        return new Uri(Path.GetFullPath(path));
    }

    private static Uri? GitHubRaw(Uri u)
    {
        if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !u.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string owner = parts[0];
        string repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!NamePart().IsMatch(owner) || !NamePart().IsMatch(repo))
        {
            return null;
        }

        if (parts.Length == 2)
        {
            return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/HEAD/{IndexFileName}");
        }

        if (parts.Length >= 5 && parts[2] is "blob" or "raw")
        {
            return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{string.Join('/', parts.Skip(3))}");
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex NamePart();
}
