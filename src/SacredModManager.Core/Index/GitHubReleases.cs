using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SacredModManager.Core.Index;

/// <summary>One release asset the resolver picked.</summary>
public sealed record ResolvedAsset(Uri Url, string Tag, string AssetName, string? Sha256);

public sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    /// <summary>"sha256:&lt;hex&gt;" (GitHub adds it to new assets).</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

public sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

/// <summary>
/// Finds the download of "the newest release of owner/repo that has an asset named like <c>SacredGear-git-*.zip</c>" through the public GitHub API
/// (<c>/repos/owner/repo/releases</c>, newest first, drafts skipped, pre-releases only on request). That is what an index entry with
/// <c>download.github</c> needs when the asset name carries a version or a commit, so that no fixed <c>releases/latest/download/NAME</c> exists.
/// One instance caches the answers of one catalog load.
/// </summary>
public sealed partial class GitHubReleaseResolver(IFetcher fetcher)
{
    public const long MaxResponseBytes = 4 * 1024 * 1024;

    private readonly Dictionary<string, Task<ResolvedAsset>> _cache = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public static bool IsValidRepository(string? text)
        => !string.IsNullOrEmpty(text) && RepositoryPattern().IsMatch(text) && !text.Split('/').Any(part => part.All(c => c == '.')); // "../x" would walk out of /repos/

    public Task<ResolvedAsset> ResolveAsync(string repository, string assetPattern, bool includePrereleases, CancellationToken ct)
    {
        string key = repository.ToLowerInvariant() + "|" + assetPattern.ToLowerInvariant() + "|" + includePrereleases;
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out Task<ResolvedAsset>? task))
            {
                task = ResolveCoreAsync(repository, assetPattern, includePrereleases, ct);
                _cache[key] = task;
            }

            return task;
        }
    }

    private async Task<ResolvedAsset> ResolveCoreAsync(string repository, string pattern, bool includePrereleases, CancellationToken ct)
    {
        if (!IsValidRepository(repository))
        {
            throw new IndexException("'" + repository + "' is not a GitHub repository (owner/name)");
        }

        var url = new Uri("https://api.github.com/repos/" + repository + "/releases?per_page=30");
        byte[] bytes = await fetcher.GetBytesAsync(url, MaxResponseBytes, ct).ConfigureAwait(false);
        List<GitHubReleaseDto> releases;
        try
        {
            releases = JsonSerializer.Deserialize(bytes, GitHubJsonContext.Default.ListGitHubReleaseDto) ?? [];
        }
        catch (JsonException ex)
        {
            throw new IndexException("GitHub answered with something that is not a list of releases: " + ex.Message);
        }

        Func<string, bool> match = name => IsAssetMatch(pattern, name);
        foreach (GitHubReleaseDto r in releases)
        {
            if (r.Draft || (r.Prerelease && !includePrereleases))
            {
                continue;
            }

            foreach (GitHubAssetDto a in r.Assets ?? [])
            {
                if (a.Name is null || !match(a.Name) || !Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out Uri? download) || download.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                string? sha = a.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..].ToLowerInvariant() : null;
                return new ResolvedAsset(download, r.TagName ?? string.Empty, a.Name, sha);
            }
        }

        throw new IndexException($"no {(includePrereleases ? string.Empty : "published ")}release of {repository} has an asset named like '{pattern}'");
    }

    /// <summary>
    /// Does an asset name match the <c>asset</c> of a download? The pattern is one or more globs separated by <c>|</c>; a glob starting with <c>!</c> excludes:
    /// <c>SacredBild-git-*.zip|!*-debug.zip</c> is every SacredBild zip except the debug one. With only exclusions every other name matches.
    /// </summary>
    public static bool IsAssetMatch(string pattern, string name)
    {
        bool anyPositive = false;
        bool positive = false;
        foreach (string part in pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part[0] == '!')
            {
                if (part.Length > 1 && GlobToRegex(part[1..]).IsMatch(name))
                {
                    return false;
                }
            }
            else
            {
                anyPositive = true;
                positive |= GlobToRegex(part).IsMatch(name);
            }
        }

        return !anyPositive || positive;
    }

    /// <summary>A file name pattern with <c>*</c> (any text) and <c>?</c> (one character), case-insensitive, matched against the whole name.</summary>
    public static Regex GlobToRegex(string pattern)
        => new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
}
