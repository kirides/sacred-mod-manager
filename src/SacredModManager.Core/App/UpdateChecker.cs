using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;

namespace SacredModManager.Core.App;

/// <summary>What kind of build is running.</summary>
public enum BuildKind
{
    /// <summary>Built from source (no version stamp): never asks for an update.</summary>
    Development,

    /// <summary>A nightly: <c>nightly-YYYYMMDD-sha7</c>.</summary>
    Nightly,

    /// <summary>A regular release: a version like <c>1.2.3</c>.</summary>
    Release,
}

/// <summary>The version stamp of the running build, as the nightly workflow (or a release build) sets it.</summary>
public sealed partial record BuildInfo(BuildKind Kind, string Version, string? Sha, DateOnly? Built)
{
    public static BuildInfo Parse(string? informationalVersion)
    {
        string text = (informationalVersion ?? string.Empty).Trim();
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            text = text[..plus];
        }

        Match n = NightlyPattern().Match(text);
        if (n.Success && DateOnly.TryParseExact(n.Groups["d"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly built))
        {
            return new BuildInfo(BuildKind.Nightly, text, n.Groups["sha"].Value.ToLowerInvariant(), built);
        }

        return ReleasePattern().IsMatch(text) ? new BuildInfo(BuildKind.Release, text.TrimStart('v', 'V'), null, null) : new BuildInfo(BuildKind.Development, text, null, null);
    }

    [GeneratedRegex(@"^nightly-(?<d>\d{8})-(?<sha>[0-9a-fA-F]{7,40})$")]
    private static partial Regex NightlyPattern();

    [GeneratedRegex(@"^[vV]?\d+(\.\d+){0,3}$")]
    private static partial Regex ReleasePattern();
}

/// <summary>A newer version of the manager.</summary>
public sealed record UpdateInfo(string Tag, string Title, Uri Url, DateTimeOffset? Published);

/// <summary>
/// Asks GitHub whether the manager has a newer version: a nightly build looks at the newest nightly (<c>nightly-&lt;sha7&gt;</c> pre-release), a release build
/// at the newest published release. It only reads the list of releases; nothing is downloaded, the player opens the release page.
/// </summary>
public sealed class UpdateChecker(IFetcher fetcher)
{
    public const string Repository = "kirides/sacred-mod-manager";

    /// <summary>The newer version, or null (up to date, a development build, or nothing found).</summary>
    public async Task<UpdateInfo?> CheckAsync(BuildInfo current, string repository, CancellationToken ct)
    {
        if (current.Kind == BuildKind.Development || !GitHubReleaseResolver.IsValidRepository(repository))
        {
            return null;
        }

        byte[] bytes = await fetcher.GetBytesAsync(new Uri("https://api.github.com/repos/" + repository + "/releases?per_page=20"), GitHubReleaseResolver.MaxResponseBytes, ct).ConfigureAwait(false);
        List<GitHubReleaseDto> releases;
        try
        {
            releases = JsonSerializer.Deserialize(bytes, GitHubJsonContext.Default.ListGitHubReleaseDto) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }

        // GitHub's list is not reliably newest first (it follows the date of the tagged commit): the newest by publication date decides
        foreach (GitHubReleaseDto r in releases.Where(r => !r.Draft && !string.IsNullOrEmpty(r.TagName)).OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue))
        {
            string tag = r.TagName!;
            bool nightly = tag.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase);
            if (current.Kind == BuildKind.Nightly ? !nightly : r.Prerelease || nightly)
            {
                continue;
            }

            if (!Uri.TryCreate(r.HtmlUrl, UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            bool newer = current.Kind == BuildKind.Nightly ? IsNewerNightly(current, tag, r.PublishedAt) : VersionCompare.IsNewer(tag.TrimStart('v', 'V'), current.Version);
            // the newest release of the right kind decides
            return newer ? new UpdateInfo(tag, string.IsNullOrWhiteSpace(r.Name) ? tag : r.Name!, url, r.PublishedAt) : null;
        }

        return null;
    }

    private static bool IsNewerNightly(BuildInfo current, string tag, DateTimeOffset? published)
    {
        string sha = tag["nightly-".Length..].ToLowerInvariant();
        if (current.Sha!.StartsWith(sha, StringComparison.Ordinal) || sha.StartsWith(current.Sha, StringComparison.Ordinal))
        {
            return false; // this very build
        }

        // a release that is older than this build (this one is newer than the newest nightly: a local or failed build) is not an update
        return published is not { } p || current.Built is not { } built || DateOnly.FromDateTime(p.UtcDateTime) >= built;
    }
}
