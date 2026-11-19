using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;

namespace SacredModManager.Core.Links;

/// <summary>A request that arrived as a <c>gear-mod-manager://</c> link. The manager never acts on one without asking the player.</summary>
public abstract class ManagerLink
{
    public const string Scheme = "gear-mod-manager";
    public const int MaxLength = 2048;

    /// <summary>Add a mod index as a source: <c>gear-mod-manager://add-index?url=&lt;https address of the index&gt;</c> (also <c>add</c>).</summary>
    public sealed class AddIndex(Uri url) : ManagerLink
    {
        public Uri Url { get; } = url;
    }

    /// <summary>
    /// Install a mod: <c>gear-mod-manager://install?mod=&lt;id&gt;[&amp;index=&lt;https address of the index&gt;]</c>. Without <c>index</c> the mod is
    /// looked up in the sources the player already has.
    /// </summary>
    public sealed class Install(string modId, Uri? index) : ManagerLink
    {
        public string ModId { get; } = modId;
        public Uri? Index { get; } = index;
    }

    /// <summary>Just bring the window to the front (the manager was started again).</summary>
    public sealed class Activate : ManagerLink;

    /// <summary>True when the text looks like one of our links (so that a command line argument can be told from a flag).</summary>
    public static bool LooksLikeLink(string? text)
        => text is not null && text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out ManagerLink? link, out string? error)
    {
        link = null;
        error = null;
        text = text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(text))
        {
            error = "the link is empty";
            return false;
        }

        if (text.Length > MaxLength)
        {
            error = "the link is too long";
            return false;
        }

        if (!LooksLikeLink(text))
        {
            error = "this is not a " + Scheme + " link";
            return false;
        }

        // gear-mod-manager://add-index?url=...  |  gear-mod-manager:add-index?url=...  |  gear-mod-manager:///add-index?url=...
        string rest = text[(Scheme.Length + 1)..];
        int hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            rest = rest[..hash];
        }

        rest = rest.TrimStart('/');
        int q = rest.IndexOf('?', StringComparison.Ordinal);
        string action = (q < 0 ? rest : rest[..q]).Trim('/').ToLowerInvariant();
        string query = q < 0 ? string.Empty : rest[(q + 1)..];
        Dictionary<string, string> args = ParseQuery(query);

        switch (action)
        {
            case "add-index" or "add" or "addindex":
                Uri? indexUri = null;
                string? why = null;
                if (!args.TryGetValue("url", out string? url) || !TryHttps(url, out indexUri, out why))
                {
                    error = args.ContainsKey("url") ? "the index address is not usable: " + why : "the link has no url=... (the address of the mod index)";
                    return false;
                }

                link = new AddIndex(indexUri!);
                return true;

            case "install":
                if (!args.TryGetValue("mod", out string? mod) || !ModIdRules.IsValid(mod))
                {
                    error = "the link has no valid mod=... (the id of the mod)";
                    return false;
                }

                Uri? index = null;
                if (args.TryGetValue("index", out string? idx) && !TryHttps(idx, out index, out string? whyIndex))
                {
                    error = "the index address is not usable: " + whyIndex;
                    return false;
                }

                link = new Install(mod, index);
                return true;

            case "" or "activate" or "open":
                link = new Activate();
                return true;

            default:
                error = "unknown action '" + (action.Length > 40 ? action[..40] : action) + "' (known: add-index, install)";
                return false;
        }
    }

    /// <summary>Builds <c>gear-mod-manager://add-index?url=...</c> for a web page or a README.</summary>
    public static string ForAddIndex(string indexAddress) => Scheme + "://add-index?url=" + Uri.EscapeDataString(indexAddress);

    /// <summary>Builds <c>gear-mod-manager://install?mod=...&amp;index=...</c> (the index is optional).</summary>
    public static string ForInstall(string modId, string? indexAddress = null)
        => Scheme + "://install?mod=" + Uri.EscapeDataString(modId) + (string.IsNullOrEmpty(indexAddress) ? string.Empty : "&index=" + Uri.EscapeDataString(indexAddress));

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Unescape(eq < 0 ? pair : pair[..eq]);
            string value = eq < 0 ? string.Empty : Unescape(pair[(eq + 1)..]);
            result.TryAdd(key.Trim(), value.Trim());
        }

        return result;
    }

    private static string Unescape(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return s;
        }
    }

    /// <summary>A link may only point at an https address (or a GitHub repository address), never at a local file.</summary>
    private static bool TryHttps(string? text, out Uri? uri, out string? error)
    {
        uri = null;
        if (!SourceUrl.TryParse(text, out Uri? parsed, out error) || parsed is null)
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "only https addresses are accepted in links";
            return false;
        }

        uri = parsed;
        return true;
    }
}
