using System.Globalization;

namespace SacredModManager.Core.Index;

/// <summary>A mod as the catalog offers it: the entry of an index with its resolved download.</summary>
public sealed class CatalogMod
{
    public required string Id { get; init; }

    /// <summary>Folder inside the zip, forward slashes, no leading or trailing slash.</summary>
    public required string Path { get; init; }

    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Requires { get; init; } = [];
    public bool DefaultEnabled { get; init; } = true;

    /// <summary>Absolute URL of the thumbnail, or null.</summary>
    public Uri? ThumbnailUrl { get; init; }

    /// <summary>Path of the thumbnail inside the zip, or null.</summary>
    public string? ThumbnailPath { get; init; }

    public required Uri DownloadUrl { get; init; }
    public string? DownloadSha256 { get; init; }
    public string PackageVersion { get; init; } = string.Empty;

    /// <summary>The index that listed the mod.</summary>
    public required Uri IndexUrl { get; init; }

    public string IndexName { get; init; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>The folder name the mod gets in <c>mods\</c>: the last part of its path in the zip.</summary>
    public string FolderName => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>One index that was read (or failed) while loading.</summary>
public sealed class LoadedIndex
{
    public required Uri Url { get; init; }
    public Uri? ParentUrl { get; init; }
    public int Depth { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Homepage { get; init; } = string.Empty;
    public int ModCount { get; set; }

    /// <summary>Set when the index could not be read; its mods are missing then.</summary>
    public string? Error { get; init; }
}

/// <summary>Everything the sources offer after loading.</summary>
public sealed class IndexCatalog
{
    public List<LoadedIndex> Indexes { get; } = [];
    public List<CatalogMod> Mods { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads mod indexes. An index may list mods and reference other indexes; references are followed breadth first, each index
/// (by normalized URL) is read <b>once</b> however often it is referenced, so cycles and duplicates cost nothing, and the traversal
/// is capped (<see cref="MaxDepth"/>, <see cref="MaxIndexes"/>). A failing index becomes a warning, the rest still loads. A mod id
/// that two indexes offer: the first in traversal order wins.
/// </summary>
public sealed class IndexLoader(IFetcher fetcher)
{
    public const int MaxDepth = 8;
    public const int MaxIndexes = 64;
    public const long MaxIndexBytes = 2 * 1024 * 1024;
    public const int MaxModsPerIndex = 2000;

    public async Task<IndexCatalog> LoadAsync(IEnumerable<Uri> roots, CancellationToken ct = default)
    {
        var catalog = new IndexCatalog();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var seenMods = new Dictionary<string, CatalogMod>(StringComparer.OrdinalIgnoreCase);
        var level = new List<(Uri Url, Uri? Parent)>();
        foreach (Uri root in roots)
        {
            if (visited.Add(SourceUrl.Key(root)))
            {
                level.Add((root, null));
            }
        }

        for (int depth = 0; level.Count > 0; depth++)
        {
            // fetch the whole level in parallel, process it in order so that the result does not depend on timing
            var fetched = await Task.WhenAll(level.Select(item => FetchAsync(item.Url, ct))).ConfigureAwait(false);
            var next = new List<(Uri, Uri?)>();
            for (int i = 0; i < level.Count; i++)
            {
                (Uri url, Uri? parent) = level[i];
                (ModIndex? index, string? error) = fetched[i];
                if (index is null)
                {
                    catalog.Indexes.Add(new LoadedIndex { Url = url, ParentUrl = parent, Depth = depth, Error = error });
                    catalog.Warnings.Add($"{Describe(url)}: {error}");
                    continue;
                }

                var loaded = new LoadedIndex
                {
                    Url = url, ParentUrl = parent, Depth = depth, Name = index.Name?.Trim() ?? string.Empty, Author = index.Author?.Trim() ?? string.Empty,
                    Description = index.Description?.Trim() ?? string.Empty, Homepage = index.Homepage?.Trim() ?? string.Empty,
                };
                catalog.Indexes.Add(loaded);
                AddMods(index, url, loaded, catalog, seenMods);
                AddReferences(index, url, depth, visited, next, catalog);
            }

            level = next;
        }

        return catalog;
    }

    private async Task<(ModIndex?, string?)> FetchAsync(Uri url, CancellationToken ct)
    {
        try
        {
            byte[] bytes = await fetcher.GetBytesAsync(url, MaxIndexBytes, ct).ConfigureAwait(false);
            ModIndex index = IndexJson.Parse(bytes);
            if (index.Schema != 1)
            {
                return (null, $"schema {index.Schema} is not supported (this manager reads schema 1)");
            }

            return (index, null);
        }
        catch (Exception ex) when (ex is FetchException or IndexException)
        {
            return (null, ex.Message);
        }
    }

    private static string Describe(Uri url) => url.IsFile ? url.LocalPath : url.Host + url.AbsolutePath;

    private static void AddReferences(ModIndex index, Uri baseUrl, int depth, HashSet<string> visited, List<(Uri, Uri?)> next, IndexCatalog catalog)
    {
        foreach (IndexRef r in index.Indexes ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Url) || !Uri.TryCreate(baseUrl, r.Url.Trim(), out Uri? target) || !SourceUrl.IsAllowedDownload(target))
            {
                catalog.Warnings.Add($"{Describe(baseUrl)}: skipped an index reference with an unusable address");
                continue;
            }

            // a reference may use the same short forms as a source (a GitHub repository address)
            if (SourceUrl.TryParse(target.AbsoluteUri, out Uri? parsed, out _) && parsed is not null && !target.IsFile)
            {
                target = parsed;
            }

            if (depth + 1 > MaxDepth)
            {
                catalog.Warnings.Add($"{Describe(baseUrl)}: indexes nested deeper than {MaxDepth} levels are not followed");
                continue;
            }

            if (!visited.Add(SourceUrl.Key(target)))
            {
                continue; // already read (or queued): single traversal
            }

            if (visited.Count > MaxIndexes)
            {
                catalog.Warnings.Add($"more than {MaxIndexes} indexes: the rest is not followed");
                return;
            }

            next.Add((target, baseUrl));
        }
    }

    private static void AddMods(ModIndex index, Uri indexUrl, LoadedIndex loaded, IndexCatalog catalog, Dictionary<string, CatalogMod> seen)
    {
        string label = string.IsNullOrWhiteSpace(loaded.Name) ? Describe(indexUrl) : loaded.Name;
        foreach (IndexMod m in (index.Mods ?? []).Take(MaxModsPerIndex))
        {
            string? why = Validate(m, out string path);
            if (why is not null)
            {
                catalog.Warnings.Add($"{label}: skipped mod '{m.Id}': {why}");
                continue;
            }

            IndexDownload? dl = m.Download?.Url is { Length: > 0 } ? m.Download : index.Download;
            if (dl?.Url is null || !Uri.TryCreate(indexUrl, dl.Url.Trim(), out Uri? download) || !SourceUrl.IsAllowedDownload(download))
            {
                catalog.Warnings.Add($"{label}: skipped mod '{m.Id}': it has no usable download");
                continue;
            }

            if (seen.TryGetValue(m.Id!, out CatalogMod? first))
            {
                catalog.Warnings.Add($"{label}: mod '{m.Id}' is already offered by {first.IndexName}; the first one is used");
                continue;
            }

            Uri? thumbUrl = null;
            string? thumbPath = null;
            string? t = m.Thumbnail?.Trim();
            if (!string.IsNullOrEmpty(t))
            {
                if (t.Contains("://", StringComparison.Ordinal))
                {
                    if (Uri.TryCreate(t, UriKind.Absolute, out Uri? tu) && tu.Scheme == Uri.UriSchemeHttps)
                    {
                        thumbUrl = tu;
                    }
                }
                else if (CleanPath(t) is { } tp)
                {
                    thumbPath = tp;
                }
            }

            var mod = new CatalogMod
            {
                Id = m.Id!, Path = path, Name = m.Name?.Trim() ?? string.Empty, Version = m.Version?.Trim() ?? string.Empty,
                Author = m.Author?.Trim() ?? string.Empty, Description = m.Description?.Trim() ?? string.Empty,
                Tags = (m.Tags ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLower(CultureInfo.InvariantCulture)).Distinct().Take(12).ToList(),
                Requires = (m.Requires ?? []).Where(Mods.ModIdRules.IsValid).Select(x => x!).Distinct().ToList(),
                DefaultEnabled = m.DefaultEnabled ?? true, ThumbnailUrl = thumbUrl, ThumbnailPath = thumbPath,
                DownloadUrl = download, DownloadSha256 = dl.Sha256?.Trim().ToLowerInvariant(), PackageVersion = dl.Version?.Trim() ?? string.Empty,
                IndexUrl = indexUrl, IndexName = label,
            };
            seen[mod.Id] = mod;
            catalog.Mods.Add(mod);
            loaded.ModCount++;
        }
    }

    private static string? Validate(IndexMod m, out string path)
    {
        path = string.Empty;
        if (!Mods.ModIdRules.IsValid(m.Id))
        {
            return "no valid id";
        }

        string? clean = CleanPath(m.Path);
        if (clean is null)
        {
            return "no valid path inside the zip";
        }

        path = clean;
        return null;
    }

    /// <summary>A zip path: forward slashes, relative, no '..', no drive; null if it is not usable.</summary>
    internal static string? CleanPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p))
        {
            return null;
        }

        string s = p.Replace('\\', '/').Trim().Trim('/');
        if (s.Length == 0 || s.Contains(':', StringComparison.Ordinal) || s.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (string part in s.Split('/'))
        {
            if (part is "" or "." or "..")
            {
                return null;
            }
        }

        return s;
    }
}
