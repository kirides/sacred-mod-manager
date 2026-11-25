using System.Globalization;
using SacredModManager.Core.App;
using SacredModManager.Core.Mods;

namespace SacredModManager.Core.Index;

/// <summary>A picture of a mod: an https URL or a path inside the mod's zip.</summary>
public sealed record CatalogImage(Uri? Url, string? Path);

/// <summary>A plugin file that comes straight from an https address, pinned by its SHA-256 (see docs/ModIndex.md, "Loose files"). <see cref="To"/> is below the plugin's install folder.</summary>
public sealed record LooseFile(Uri Url, string Sha256, string To);

/// <summary>A mod as the catalog offers it: the entry of an index with its resolved download.</summary>
public sealed class CatalogMod
{
    public required string Id { get; init; }

    public ItemKind Kind { get; init; } = ItemKind.Mod;

    /// <summary>Folder inside the zip, forward slashes, no leading or trailing slash. Empty for a plugin or tool at the zip's root.</summary>
    public required string Path { get; init; }

    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Plugins: the folder of the game folder the files go to (forward slashes, no leading slash), "" for the game folder itself.</summary>
    public string InstallTo { get; init; } = string.Empty;

    /// <summary>Plugins: (path inside the item's folder, destination below <see cref="InstallTo"/>) of each file; empty means every file.</summary>
    public IReadOnlyList<(string From, string To)> InstallFiles { get; init; } = [];

    /// <summary>Plugins: files that come straight from an https address instead of from an archive (then there is no archive and <see cref="InstallFiles"/> is empty).</summary>
    public IReadOnlyList<LooseFile> LooseFiles { get; init; } = [];

    /// <summary>Tools: the program to start, relative to the tool's folder.</summary>
    public string? Launch { get; init; }

    /// <summary>The GitHub release this entry's download is looked up in (see <see cref="IndexDownload.Github"/>), until it is resolved.</summary>
    internal (string Repository, string Asset, bool Prerelease)? GithubSource { get; init; }

    public string Name { get; init; } = string.Empty;
    public string Version { get; internal set; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Minimum versions of required items (<c>"id&gt;=1.2.0"</c> in <c>requires</c>); an id without an entry has no minimum.</summary>
    public IReadOnlyDictionary<string, string> MinVersions { get; init; } = new Dictionary<string, string>(0);
    public bool DefaultEnabled { get; init; } = true;

    /// <summary>Absolute URL of the thumbnail, or null.</summary>
    public Uri? ThumbnailUrl { get; init; }

    /// <summary>Path of the thumbnail inside the zip, or null.</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>Where the changelog file is (an https URL or a path inside the zip), or null.</summary>
    public CatalogImage? Changelog { get; init; }

    /// <summary>The pictures for the viewer (the thumbnail alone when the entry lists none).</summary>
    public IReadOnlyList<CatalogImage> Images { get; init; } = [];

    /// <summary>The picture the list shows: the thumbnail, else the first image.</summary>
    public CatalogImage? ThumbnailImage => ThumbnailUrl is not null || ThumbnailPath is not null ? new CatalogImage(ThumbnailUrl, ThumbnailPath) : Images.Count > 0 ? Images[0] : null;

    public Uri DownloadUrl { get; internal set; } = null!;
    public string? DownloadSha256 { get; internal set; }
    public string PackageVersion { get; internal set; } = string.Empty;

    /// <summary>The archive inside the downloaded archive that holds the files (see <see cref="IndexDownload.Inner"/>), or null.</summary>
    public string? DownloadInner { get; internal set; }

    /// <summary>The index that listed the mod.</summary>
    public required Uri IndexUrl { get; init; }

    public string IndexName { get; init; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>"Mod", "Plugin" or "Tool".</summary>
    public string KindLabel() => ItemKinds.Singular(Kind);

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
public sealed class IndexLoader(IFetcher fetcher, string? platform = null)
{
    private readonly string _platform = platform ?? PlatformInfo.CurrentRid;
    private readonly GitHubReleaseResolver _github = new(fetcher);
    public const int MaxDepth = 8;
    public const int MaxIndexes = 64;
    public const long MaxIndexBytes = 2 * 1024 * 1024;
    public const int MaxModsPerIndex = 2000;
    public const int MaxImagesPerMod = 24;

    public async Task<IndexCatalog> LoadAsync(IEnumerable<Uri> roots, CancellationToken ct = default)
    {
        var catalog = new IndexCatalog();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var seenMods = new Dictionary<string, CatalogMod>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<CatalogMod>();
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
                AddMods(index, url, loaded, catalog, seenMods, pending);
                AddReferences(index, url, depth, visited, next, catalog);
            }

            level = next;
        }

        await ResolveGithubDownloadsAsync(catalog, pending, ct).ConfigureAwait(false);
        return catalog;
    }

    /// <summary>Looks up the downloads that name a GitHub repository (a few requests, one per repository and asset pattern); an entry whose release cannot be found is dropped with a warning.</summary>
    private async Task ResolveGithubDownloadsAsync(IndexCatalog catalog, List<CatalogMod> pending, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(pending.Select(async mod =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                (string repo, string asset, bool pre) = mod.GithubSource!.Value;
                return (Mod: mod, Asset: await _github.ResolveAsync(repo, asset, pre, ct).ConfigureAwait(false), Error: (string?)null);
            }
            catch (Exception ex) when (ex is IndexException or FetchException)
            {
                return (Mod: mod, Asset: (ResolvedAsset?)null, Error: ex.Message);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        foreach ((CatalogMod mod, ResolvedAsset? asset, string? error) in results)
        {
            if (asset is null)
            {
                catalog.Mods.Remove(mod);
                catalog.Warnings.Add($"{mod.IndexName}: '{mod.Id}' skipped: {error}");
                LoadedIndex? owner = catalog.Indexes.FirstOrDefault(i => i.Url == mod.IndexUrl);
                if (owner is not null)
                {
                    owner.ModCount--;
                }

                continue;
            }

            mod.DownloadUrl = asset.Url;
            mod.DownloadSha256 ??= asset.Sha256;
            mod.PackageVersion = asset.Tag;
            if (mod.Version.Length == 0)
            {
                mod.Version = asset.Tag.TrimStart('v', 'V');
            }
        }
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

    private void AddMods(ModIndex index, Uri indexUrl, LoadedIndex loaded, IndexCatalog catalog, Dictionary<string, CatalogMod> seen, List<CatalogMod> pending)
    {
        string label = string.IsNullOrWhiteSpace(loaded.Name) ? Describe(indexUrl) : loaded.Name;
        foreach (string field in CodeFields.Find(index.Extra))
        {
            catalog.Warnings.Add($"{label}: the field '{field}' is ignored (an index cannot run anything)");
        }

        foreach (IndexMod m in (index.Mods ?? []).Take(MaxModsPerIndex))
        {
            foreach (string field in CodeFields.Find(m.Extra).Concat(CodeFields.Find(m.Install?.Extra)).Concat(CodeFields.Find(m.Download?.Extra)))
            {
                catalog.Warnings.Add($"{label}: '{m.Id}': the field '{field}' is ignored (an index cannot run anything; it only says which files to copy)");
            }

            if (m.Install?.Launch is not null && ItemKinds.Parse(m.Kind) is not ItemKind.Tool)
            {
                catalog.Warnings.Add($"{label}: '{m.Id}': install.launch is ignored (only a tool has a program the player can start)");
            }

            string? why = Validate(m, out ItemKind kind, out string path, out string installTo, out List<(string, string)> files, out string? launch, out List<LooseFile> loose);
            if (why is not null)
            {
                catalog.Warnings.Add($"{label}: skipped '{m.Id}': {why}");
                continue;
            }

            if (!PlatformInfo.Matches(m.Platforms, _platform))
            {
                continue; // an item for another platform: not shown here, not a problem
            }

            IndexDownload? dl = HasSource(m.Download) ? m.Download : index.Download;
            Uri? download = null;
            (string, string, bool)? github = null;
            if (loose.Count > 0)
            {
                dl = new IndexDownload(); // no archive: the files are fetched one by one
                download = loose[0].Url;
            }
            else if (dl is not null && !string.IsNullOrWhiteSpace(dl.Github))
            {
                if (!GitHubReleaseResolver.IsValidRepository(dl.Github.Trim()) || string.IsNullOrWhiteSpace(dl.Asset))
                {
                    catalog.Warnings.Add($"{label}: skipped '{m.Id}': download.github needs owner/repo and an asset name pattern");
                    continue;
                }

                github = (dl.Github.Trim(), dl.Asset.Trim(), dl.Prerelease ?? false);
                download = new Uri("https://api.github.com/repos/" + dl.Github.Trim() + "/releases"); // replaced when the release is looked up
            }
            else if (dl?.Url is null || !Uri.TryCreate(indexUrl, dl.Url.Trim(), out download) || !SourceUrl.IsAllowedDownload(download))
            {
                catalog.Warnings.Add($"{label}: skipped '{m.Id}': it has no usable download");
                continue;
            }

            if (seen.TryGetValue(m.Id!, out CatalogMod? first))
            {
                catalog.Warnings.Add($"{label}: '{m.Id}' is already offered by {first.IndexName}; the first one is used");
                continue;
            }

            CatalogImage? thumb = ParseImage(m.Thumbnail);
            List<CatalogImage> images = (m.Images ?? []).Take(MaxImagesPerMod).Select(ParseImage).OfType<CatalogImage>().ToList();
            if (images.Count == 0 && thumb is not null)
            {
                images.Add(thumb);
            }

            var requires = new List<string>();
            var minVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string? entry in m.Requires ?? [])
            {
                if (!Mods.ModRequirement.TryParse(entry, out Mods.ModRequirement req))
                {
                    if (entry is not null && entry.IndexOfAny(['<', '>', '=', '@', '~', '^']) >= 0) // an invalid plain id has always been dropped quietly; something that looks like a constraint is worth a word
                    {
                        catalog.Warnings.Add($"{label}: '{m.Id}': the requirement '{ClipText(entry)}' is ignored (write an id, or id>=version)");
                    }

                    continue;
                }

                if (!requires.Contains(req.Id))
                {
                    requires.Add(req.Id);
                }

                if (req.MinVersion is not null && (!minVersions.TryGetValue(req.Id, out string? have) || Mods.VersionCompare.Compare(req.MinVersion, have) > 0))
                {
                    minVersions[req.Id] = req.MinVersion; // listed twice: the higher minimum wins
                }
            }

            var mod = new CatalogMod
            {
                Id = m.Id!, Kind = kind, Path = path, Platforms = (m.Platforms ?? []).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList(),
                InstallTo = installTo, InstallFiles = files, LooseFiles = loose, Launch = launch, GithubSource = github,
                Name = m.Name?.Trim() ?? string.Empty, Version = m.Version?.Trim() ?? string.Empty,
                Author = m.Author?.Trim() ?? string.Empty, Description = m.Description?.Trim() ?? string.Empty,
                Tags = (m.Tags ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLower(CultureInfo.InvariantCulture)).Distinct().Take(12).ToList(),
                Requires = requires, MinVersions = minVersions,
                DefaultEnabled = m.DefaultEnabled ?? true, ThumbnailUrl = thumb?.Url, ThumbnailPath = thumb?.Path, Images = images, Changelog = ParseImage(m.Changelog),
                DownloadUrl = download!, DownloadSha256 = dl!.Sha256?.Trim().ToLowerInvariant(), PackageVersion = dl.Version?.Trim() ?? string.Empty,
                IndexUrl = indexUrl, IndexName = label, DownloadInner = CleanPath(dl.Inner),
            };
            seen[mod.Id] = mod;
            catalog.Mods.Add(mod);
            loaded.ModCount++;
            if (github is not null)
            {
                pending.Add(mod);
            }
        }
    }

    private static bool HasSource(IndexDownload? d) => d is not null && (!string.IsNullOrWhiteSpace(d.Url) || !string.IsNullOrWhiteSpace(d.Github));

    /// <summary>An https URL, or a path inside the zip; anything else (http, drive letters, '..') is dropped.</summary>
    internal static CatalogImage? ParseImage(string? text)
    {
        string? t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (t.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(t, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps ? new CatalogImage(u, null) : null;
        }

        return CleanPath(t) is { } p ? new CatalogImage(null, p) : null;
    }

    /// <summary>Checks one entry and reads its kind, its folder in the zip and (for plugins and tools) its install rules; returns why it is unusable, or null.</summary>
    private static string? Validate(IndexMod m, out ItemKind kind, out string path, out string installTo, out List<(string From, string To)> files, out string? launch, out List<LooseFile> loose)
    {
        loose = [];
        path = string.Empty;
        installTo = string.Empty;
        files = [];
        launch = null;
        kind = ItemKind.Mod;
        if (!ModIdRules.IsValid(m.Id))
        {
            return "no valid id";
        }

        ItemKind? parsed = ItemKinds.Parse(m.Kind);
        if (parsed is null)
        {
            return $"unknown kind '{m.Kind}' (mod, plugin or tool)";
        }

        kind = parsed.Value;
        string? rawPath = m.Path?.Trim();
        if (kind != ItemKind.Mod && (string.IsNullOrEmpty(rawPath) || rawPath is "." or "/"))
        {
            path = string.Empty; // the zip's root
        }
        else
        {
            string? clean = CleanPath(rawPath);
            if (clean is null)
            {
                return "no valid path inside the zip";
            }

            path = clean;
        }

        if (kind == ItemKind.Plugin)
        {
            if (!string.IsNullOrWhiteSpace(m.Install?.To))
            {
                string? to = CleanPath(m.Install.To);
                if (to is null)
                {
                    return "install.to is not a folder below the game folder";
                }

                installTo = to;
            }

            List<InstallFileSpec> specs = (m.Install?.Files ?? []).ToList();
            int looseCount = specs.Count(f => f?.Url is not null);
            if (looseCount > 0)
            {
                if (looseCount != specs.Count)
                {
                    return "install.files mixes loose files (url) with files of an archive";
                }

                if (HasSource(m.Download))
                {
                    return "an entry with loose files (url) cannot also have an archive download";
                }

                foreach (InstallFileSpec f in specs)
                {
                    string? bad = CheckLooseFile(f, out LooseFile? file);
                    if (bad is not null)
                    {
                        return bad;
                    }

                    if (loose.Any(l => string.Equals(l.To, file!.To, StringComparison.OrdinalIgnoreCase)))
                    {
                        return $"two loose files go to '{file!.To}'";
                    }

                    loose.Add(file!);
                }

                specs.Clear();
            }

            foreach (InstallFileSpec f in specs)
            {
                string? from = CleanPath(f?.From);
                if (from is not null && PackageGlob.IsPattern(from))
                {
                    // a wildcard: 'to' is a target folder; without one the pattern's literal folder (the game folder itself for "*.dll")
                    string? dir = string.IsNullOrWhiteSpace(f!.To) || f.To == f.From ? PackageGlob.BaseFolder(from) : CleanPath(f.To);
                    if (dir is null || !PackageGlob.IsValid(from) || PackageGlob.IsPattern(dir))
                    {
                        return "install.files has an entry that is not a safe relative path";
                    }

                    files.Add((from, dir));
                    continue;
                }

                string? to = CleanPath(f?.To ?? f?.From);
                if (from is null || to is null || PackageGlob.IsPattern(to))
                {
                    return "install.files has an entry that is not a safe relative path";
                }

                files.Add((from, to));
            }
        }
        else if (kind == ItemKind.Tool && !string.IsNullOrWhiteSpace(m.Install?.Launch))
        {
            launch = CleanPath(m.Install.Launch);
            if (launch is null)
            {
                return "install.launch is not a path inside the tool's folder";
            }
        }

        return null;
    }

    /// <summary>The rules for a loose file: https only, a SHA-256 is mandatory, a target below the plugin's folder, and never a program or script.</summary>
    private static string? CheckLooseFile(InstallFileSpec f, out LooseFile? file)
    {
        file = null;
        if (!Uri.TryCreate(f.Url?.Trim(), UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
        {
            return "a loose file needs an absolute https address";
        }

        string sha = f.Sha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit))
        {
            return $"the loose file {url.AbsoluteUri} has no valid sha256 (64 hex digits are required)";
        }

        string? to = f.To?.TrimStart().StartsWith('/') == true || f.To?.TrimStart().StartsWith('\\') == true ? null : CleanPath(f.To);
        if (to is null || PackageGlob.IsPattern(to))
        {
            return $"the loose file {url.AbsoluteUri} needs a target path below the game folder (no '..', drive or wildcard)";
        }

        if (ExecutableTargets.IsExecutable(to))
        {
            return $"the loose file {url.AbsoluteUri} would write a program or script ('{to}'), which loose files may never do";
        }

        file = new LooseFile(url, sha, to);
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

    private static string ClipText(string? text) => text is null ? string.Empty : text.Length <= 40 ? text : text[..40] + "...";
}
