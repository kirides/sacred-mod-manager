using System.Security.Cryptography;
using System.Text;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;

namespace SacredModManager.Core.App;

/// <summary>What has to be installed so that the wanted mods work.</summary>
public sealed class InstallPlan
{
    /// <summary>In install order: required mods first.</summary>
    public List<CatalogMod> Install { get; } = [];

    /// <summary>The wanted mods themselves (a subset of <see cref="Install"/>).</summary>
    public HashSet<string> Wanted { get; } = new(StringComparer.Ordinal);

    /// <summary>Required ids that neither the catalog offers nor are installed.</summary>
    public List<string> Missing { get; } = [];
}

/// <summary>What happened to one mod of a batch.</summary>
public sealed record BatchItem(string Id, InstallOutcome? Outcome, string? Error)
{
    /// <summary>Set when the install worked but the package holds another version than the index promised (the index is ahead of its release, or behind it).</summary>
    public string? Warning { get; init; }

    /// <summary>True when the player chose to stop because another plugin owns the files: nothing was changed.</summary>
    public bool Aborted { get; init; }
}

/// <summary>Internal: the player answered "no" to a plugin conflict.</summary>
internal sealed class ConflictAbortedException(PluginConflictException conflict) : Exception("install aborted")
{
    public PluginConflictException Conflict { get; } = conflict;
}

/// <summary>The manager's use cases, independent of any window: load sources, plan, install, thumbnails.</summary>
public sealed class ModManagerService(AppPaths paths, IFetcher fetcher)
{
    private readonly PackageCache _packages = new(fetcher, paths.Packages);
    private readonly IndexLoader _loader = new(fetcher);

    public AppPaths Paths => paths;

    public Task<IndexCatalog> LoadCatalogAsync(IEnumerable<string> sources, CancellationToken ct = default)
    {
        var uris = new List<Uri>();
        var errors = new List<string>();
        foreach (string s in sources)
        {
            if (SourceUrl.TryParse(s, out Uri? u, out string? error))
            {
                uris.Add(u!);
            }
            else
            {
                errors.Add($"{s}: {error}");
            }
        }

        return LoadWithErrorsAsync(uris, errors, ct);
    }

    private async Task<IndexCatalog> LoadWithErrorsAsync(List<Uri> uris, List<string> errors, CancellationToken ct)
    {
        IndexCatalog catalog = await _loader.LoadAsync(uris, ct).ConfigureAwait(false);
        catalog.Warnings.InsertRange(0, errors);
        return catalog;
    }

    /// <summary>
    /// Adds what the wanted mods require, from the catalog (or notes it as missing); mods that are installed already are not planned again.
    /// A cycle in <c>requires</c> is harmless: every mod is planned once.
    /// </summary>
    public static InstallPlan Plan(IEnumerable<CatalogMod> wanted, IReadOnlyCollection<CatalogMod> catalog, IEnumerable<string> installedIds)
    {
        var plan = new InstallPlan();
        var byId = catalog.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var installed = new HashSet<string>(installedIds, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        void Visit(CatalogMod mod, bool isWanted)
        {
            if (!done.Add(mod.Id))
            {
                if (isWanted)
                {
                    plan.Wanted.Add(mod.Id); // already planned as somebody's requirement, but wanted in its own right
                }

                return;
            }

            foreach (string req in mod.Requires)
            {
                if (installed.Contains(req) || done.Contains(req))
                {
                    continue;
                }

                if (byId.TryGetValue(req, out CatalogMod? dep))
                {
                    Visit(dep, false);
                }
                else if (!plan.Missing.Contains(req))
                {
                    plan.Missing.Add(req);
                }
            }

            plan.Install.Add(mod);
            if (isWanted)
            {
                plan.Wanted.Add(mod.Id);
            }
        }

        foreach (CatalogMod mod in wanted)
        {
            Visit(mod, true);
        }

        return plan;
    }

    /// <summary>Downloads the package of a mod (once for all mods of the same zip) and installs it into the store.</summary>
    public async Task<InstallOutcome> InstallAsync(ModStore store, CatalogMod mod, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
        => await InstallAsync(store, mod, progress, null, ct).ConfigureAwait(false);

    private async Task<InstallOutcome> InstallAsync(ModStore store, CatalogMod mod, IProgress<(long Done, long Total)>? progress, Dictionary<string, string>? batchPackages, CancellationToken ct)
    {
        if (mod.LooseFiles.Count > 0)
        {
            return await InstallLooseAsync(store, mod, ct).ConfigureAwait(false);
        }

        string key = SourceUrl.Key(mod.DownloadUrl) + "|" + mod.DownloadSha256;
        if (batchPackages is null || !batchPackages.TryGetValue(key, out string? zip) || !File.Exists(zip))
        {
            zip = await _packages.GetAsync(mod.DownloadUrl, mod.DownloadSha256, progress, ct).ConfigureAwait(false);
            if (batchPackages is not null)
            {
                batchPackages[key] = zip;
            }
        }

        return await Task.Run(() =>
        {
            using ArchivePackage package = ArchivePackage.Open(zip);
            string index = mod.IndexUrl.AbsoluteUri;
            string source = mod.DownloadUrl.AbsoluteUri;
            string version = mod.Version.Length > 0 ? mod.Version : mod.PackageVersion;
            return mod.Kind switch
            {
                ItemKind.Plugin => store.InstallPlugin(package, mod.Id, version, mod.Path, mod.InstallTo, mod.InstallFiles, index, source),
                ItemKind.Tool => store.InstallTool(package, mod.Id, version, mod.Path, mod.Launch, index, source),
                _ => store.Install(package, mod.Path, mod.Id, index, source),
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Fetches the loose files of a plugin (small, https, pinned by SHA-256) and installs them; nothing is written unless every file matches its hash.</summary>
    private async Task<InstallOutcome> InstallLooseAsync(ModStore store, CatalogMod mod, CancellationToken ct)
    {
        var data = new List<LooseFileData>();
        foreach (LooseFile f in mod.LooseFiles)
        {
            if (f.Url.Scheme != Uri.UriSchemeHttps)
            {
                throw new StoreException($"refused: {f.Url.AbsoluteUri} is not an https address");
            }

            byte[] bytes = await fetcher.GetBytesAsync(f.Url, ModStore.MaxLooseFileBytes, ct).ConfigureAwait(false);
            data.Add(new LooseFileData(f.To, bytes, f.Sha256, f.Url.AbsoluteUri));
        }

        string version = mod.Version.Length > 0 ? mod.Version : mod.PackageVersion;
        return await Task.Run(() => store.InstallLoosePlugin(mod.Id, version, mod.InstallTo, data, mod.IndexUrl.AbsoluteUri), ct).ConfigureAwait(false);
    }

    /// <summary>Installs the planned mods in order; one failure does not stop the others (a mod that requires it will be reported by the game).</summary>
    public async Task<List<BatchItem>> InstallPlanAsync(ModStore store, InstallPlan plan, IProgress<string>? status, IProgress<(long Done, long Total)>? progress, CancellationToken ct,
        Func<PluginConflictException, Task<bool>>? resolveConflict = null)
    {
        var results = new List<BatchItem>();
        var zips = new Dictionary<string, string>(StringComparer.Ordinal); // each zip is asked for once per batch
        foreach (CatalogMod mod in plan.Install)
        {
            status?.Report("Installing " + mod.DisplayName);
            try
            {
                InstallOutcome outcome;
                var removed = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    try
                    {
                        outcome = await InstallAsync(store, mod, progress, zips, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (PluginConflictException conflict) when (resolveConflict is not null && removed.Add(conflict.ConflictingId))
                    {
                        // nothing was changed so far: the player decides; "no" leaves everything as it was
                        if (!await resolveConflict(conflict).ConfigureAwait(false))
                        {
                            throw new ConflictAbortedException(conflict);
                        }

                        InstalledMod? other = (await store.ScanAsync().ConfigureAwait(false)).FirstOrDefault(m => m.Id == conflict.ConflictingId);
                        if (other is not null)
                        {
                            store.Uninstall(other, removeData: false, forgetSettings: false);
                        }
                    }
                }

                string? warning = null;
                if (mod.Kind == ItemKind.Mod && !string.IsNullOrEmpty(mod.Version) && !string.Equals(outcome.Version, mod.Version, StringComparison.Ordinal))
                {
                    warning = "the package holds version " + outcome.Version + ", but the index lists " + mod.Version
                              + (VersionCompare.IsNewer(mod.Version, outcome.Version) ? ". The index is ahead of the package: the release it points to has not been published yet, so \"update available\" stays." : ".");
                }

                results.Add(new BatchItem(mod.Id, outcome, null) { Warning = warning });
            }
            catch (ConflictAbortedException ex)
            {
                results.Add(new BatchItem(mod.Id, null, "not installed: it needs the files of the plugin '" + ex.Conflict.ConflictingId + "', which you kept") { Aborted = true });
            }
            catch (Exception ex) when (ex is StoreException or PackageException or FetchException)
            {
                results.Add(new BatchItem(mod.Id, null, ex.Message));
            }
        }

        return results;
    }

    /// <summary>What a local zip or folder offers to install.</summary>
    public sealed record LocalMod(string PathInArchive, ModManifest Manifest);

    /// <summary>Lists the mods in a zip file (any folder with a mod.lua).</summary>
    public static List<LocalMod> InspectArchive(string archivePath)
    {
        using ArchivePackage package = ArchivePackage.Open(archivePath);
        var list = new List<LocalMod>();
        foreach (string folder in package.ModFolders().Order(StringComparer.Ordinal))
        {
            string? text = package.ReadText(folder.Length == 0 ? "mod.lua" : folder + "/mod.lua", LuaManifestReader.MaxSourceChars);
            if (text is null)
            {
                continue;
            }

            try
            {
                list.Add(new LocalMod(folder, LuaManifestReader.Read(text)));
            }
            catch (ManifestException)
            {
                // a folder with a broken mod.lua is not offered
            }
        }

        return list;
    }

    /// <summary>The well-known plugin files (<c>ddraw.dll</c> of Sacred-Bild, <c>SacredGear.dll</c>) a zip holds, or none: such a zip is a plugin package, not a mod.</summary>
    public static List<string> FindPluginFiles(string archivePath)
    {
        using ArchivePackage package = ArchivePackage.Open(archivePath);
        return package.EntryNames()
            .Select(n => n[(n.LastIndexOf('/') + 1)..])
            .Where(n => n.Equals("ddraw.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("SacredGear.dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public InstallOutcome InstallLocal(ModStore store, string archivePath, string pathInArchive)
    {
        using ArchivePackage package = ArchivePackage.Open(archivePath);
        return store.Install(package, pathInArchive, null, null, null);
    }

    private static readonly TimeSpan ThumbnailMaxAge = TimeSpan.FromHours(1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _revalidated = new(StringComparer.Ordinal);

    /// <summary>The bytes of the thumbnail of a catalog mod (see <see cref="GetImageAsync"/>).</summary>
    public Task<byte[]?> GetThumbnailAsync(CatalogMod mod, bool allowDownload, CancellationToken ct)
        => mod.ThumbnailImage is { } image ? GetImageAsync(mod, image, allowDownload, ct) : Task.FromResult<byte[]?>(null);

    /// <summary>
    /// The bytes of a picture of a catalog mod: its https URL (kept in the thumbnail cache), or the file inside the mod's zip
    /// (the zip is downloaded for this when it is not cached; <paramref name="allowDownload"/> false returns null instead).
    /// </summary>
    public async Task<byte[]?> GetImageAsync(CatalogMod mod, CatalogImage image, bool allowDownload, CancellationToken ct)
    {
        const long max = 12 * 1024 * 1024;
        try
        {
            if (image.Url is { } url)
            {
                string file = Path.Combine(paths.Thumbnails, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceUrl.Key(url))))[..16]);
                bool have = File.Exists(file);
                // a picture that was updated under the same address must show up: a cached copy is trusted for an hour only
                if (have && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < ThumbnailMaxAge)
                {
                    return await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                }

                try
                {
                    byte[] bytes = await fetcher.GetBytesAsync(url, max, ct).ConfigureAwait(false);
                    Directory.CreateDirectory(paths.Thumbnails);
                    await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
                    return bytes;
                }
                catch (FetchException) when (have)
                {
                    return await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false); // offline: the old picture is better than none
                }
            }

            if (image.Path is { } inside && mod.LooseFiles.Count == 0)
            {
                string? zip = _packages.Cached(mod.DownloadUrl);
                // a cached zip is asked about once per run (an unchanged one costs a short answer), so that updated pictures show up
                if (allowDownload && (zip is null || _revalidated.TryAdd(SourceUrl.Key(mod.DownloadUrl), true)))
                {
                    try
                    {
                        zip = await _packages.GetAsync(mod.DownloadUrl, mod.DownloadSha256, null, ct).ConfigureAwait(false);
                    }
                    catch (FetchException) when (zip is not null)
                    {
                        // offline: keep the cached zip
                    }
                }

                if (zip is not null)
                {
                    return await Task.Run(() =>
                    {
                        using ArchivePackage package = ArchivePackage.Open(zip);
                        return package.ReadBytes(inside, max);
                    }, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is FetchException or PackageException or IOException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Is the mod's package already on disk (so that an install needs no download)?</summary>
    public bool IsPackageCached(CatalogMod mod) => _packages.Cached(mod.DownloadUrl) is not null;
}
