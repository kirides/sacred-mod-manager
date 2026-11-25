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

    /// <summary>Requirements whose minimum version is not met by what is installed, and no offered version meets it either. Nothing with an entry here is installed.</summary>
    public List<UnmetRequirement> Unmet { get; } = [];
}

/// <summary>An item a mod to install recommends and that an index offers; the player decides whether it comes along.</summary>
/// <param name="Mod">The recommended item.</param>
/// <param name="RecommendedBy">The ids of the mods of the plan that recommend it.</param>
public sealed record Recommendation(CatalogMod Mod, IReadOnlyList<string> RecommendedBy);

/// <summary>A required item that is there (or offered) in a version older than the minimum a mod asks for.</summary>
/// <param name="Id">The required item.</param>
/// <param name="RequiredBy">The mod that asks for it.</param>
/// <param name="MinVersion">The version it asks for (or newer).</param>
/// <param name="InstalledVersion">What is installed, or null when it is not installed.</param>
/// <param name="OfferedVersion">What the sources offer, or null when nothing offers it.</param>
public sealed record UnmetRequirement(string Id, string RequiredBy, string MinVersion, string? InstalledVersion, string? OfferedVersion)
{
    public string Message => $"{RequiredBy} needs {Id} {MinVersion} or newer"
        + (InstalledVersion is null ? ", which is not installed" : $", you have {InstalledVersion}")
        + (OfferedVersion is null ? (InstalledVersion is null ? string.Empty : ", and no newer one is offered by your sources") : $", and your sources offer {OfferedVersion}");
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
        => Plan(wanted, catalog, installedIds.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => string.Empty, StringComparer.Ordinal));

    /// <summary>
    /// As above, with the version of each installed item, so minimum versions (<c>id&gt;=1.2.0</c>) are checked: an installed item that is too old is updated
    /// when a source offers a version that is new enough, otherwise the requirement is reported in <see cref="InstallPlan.Unmet"/>.
    /// An empty version counts as unknown and meets any minimum.
    /// </summary>
    public static InstallPlan Plan(IEnumerable<CatalogMod> wanted, IReadOnlyCollection<CatalogMod> catalog, IReadOnlyDictionary<string, string> installedVersions)
    {
        var plan = new InstallPlan();
        var byId = catalog.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        static string Offered(CatalogMod m) => m.Version.Length > 0 ? m.Version : m.PackageVersion;

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
                string? min = mod.MinVersions.GetValueOrDefault(req);
                bool have = installedVersions.TryGetValue(req, out string? haveVersion);
                if (have && ModRequirement.IsMet(min, haveVersion))
                {
                    continue;
                }

                if (byId.TryGetValue(req, out CatalogMod? dep))
                {
                    if (!ModRequirement.IsMet(min, Offered(dep)))
                    {
                        plan.Unmet.Add(new UnmetRequirement(req, mod.Id, min!, have ? haveVersion : null, Offered(dep)));
                    }
                    else
                    {
                        Visit(dep, false); // not installed, or too old: this one is installed (updated) first
                    }
                }
                else if (have)
                {
                    plan.Unmet.Add(new UnmetRequirement(req, mod.Id, min!, haveVersion, null));
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

        // a mod whose minimum is not met is not installed (the player is told why); nor is anything that needs only that
        var blocked = new HashSet<string>(plan.Unmet.Select(u => u.RequiredBy), StringComparer.Ordinal);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (CatalogMod m in plan.Install)
            {
                if (!blocked.Contains(m.Id) && m.Requires.Any(blocked.Contains))
                {
                    grew = blocked.Add(m.Id) || grew;
                }
            }
        }

        plan.Install.RemoveAll(m => blocked.Contains(m.Id));
        return plan;
    }

    /// <summary>
    /// The recommendations of the mods <paramref name="plan"/> would newly install: items an index offers that are neither installed (in a version that meets the
    /// minimum) nor already part of the plan. Mods that are only updated do not ask again. Nothing is installed by this; pass the ones the player accepts
    /// to <see cref="Plan(IEnumerable{CatalogMod}, IReadOnlyCollection{CatalogMod}, IReadOnlyDictionary{string, string})"/> as additional wanted mods.
    /// A recommendation that nothing offers (or whose offered version is too old) is left out: there is nothing to ask.
    /// </summary>
    public static List<Recommendation> Recommend(InstallPlan plan, IReadOnlyCollection<CatalogMod> catalog, IReadOnlyDictionary<string, string> installedVersions)
    {
        var byId = catalog.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var inPlan = plan.Install.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (CatalogMod mod in plan.Install)
        {
            if (installedVersions.ContainsKey(mod.Id))
            {
                continue; // an update, not a new install
            }

            foreach (string rec in mod.Recommended)
            {
                string? min = mod.RecommendedMinVersions.GetValueOrDefault(rec);
                if (inPlan.Contains(rec) || (installedVersions.TryGetValue(rec, out string? have) && ModRequirement.IsMet(min, have)))
                {
                    continue;
                }

                if (!byId.TryGetValue(rec, out CatalogMod? offered) || !ModRequirement.IsMet(min, offered.Version.Length > 0 ? offered.Version : offered.PackageVersion))
                {
                    continue;
                }

                if (!found.TryGetValue(rec, out List<string>? by))
                {
                    found[rec] = by = [];
                    order.Add(rec);
                }

                by.Add(mod.Id);
            }
        }

        return order.Select(id => new Recommendation(byId[id], found[id])).ToList();
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
            // The index can be ahead of the package: a "latest release" address keeps serving the previous release for a short while after a new one is published,
            // and a copy kept from then is not replaced by an unchanged-answer. One more download, asked for without the copy's validator, settles it.
            if (await Task.Run(() => PackageIsBehind(zip, mod), ct).ConfigureAwait(false))
            {
                zip = await _packages.GetAsync(mod.DownloadUrl, mod.DownloadSha256, progress, ct, force: true).ConfigureAwait(false);
            }

            if (batchPackages is not null)
            {
                batchPackages[key] = zip;
            }
        }

        return await Task.Run(() =>
        {
            using ArchivePackage outer = ArchivePackage.Open(zip);
            string? innerFile = null;
            ArchivePackage? inner = null;
            try
            {
                if (mod.DownloadInner is not null)
                {
                    byte[] bytes = outer.ReadBytes(mod.DownloadInner, MaxInnerArchiveBytes) ?? throw new PackageException($"the download has no '{mod.DownloadInner}' (or it is larger than {MaxInnerArchiveBytes / (1024 * 1024)} MB)");
                    Directory.CreateDirectory(paths.Packages);
                    innerFile = Path.Combine(paths.Packages, "inner-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllBytes(innerFile, bytes);
                    inner = ArchivePackage.Open(innerFile);
                }

                return InstallFrom(store, mod, inner ?? outer);
            }
            finally
            {
                inner?.Dispose();
                if (innerFile is not null)
                {
                    try
                    {
                        File.Delete(innerFile);
                    }
                    catch (IOException)
                    {
                        // a temporary file; it is only a leftover
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Does the downloaded package hold an older version of the mod than the index lists? False when that cannot be told (not a plain mod, no version listed, unreadable).</summary>
    private static bool PackageIsBehind(string zip, CatalogMod mod)
    {
        if (mod.Kind != ItemKind.Mod || mod.DownloadInner is not null || string.IsNullOrEmpty(mod.Version))
        {
            return false;
        }

        try
        {
            using ArchivePackage package = ArchivePackage.Open(zip);
            string? text = package.ReadText(mod.Path.Length == 0 ? "mod.lua" : mod.Path + "/mod.lua", LuaManifestReader.MaxSourceChars);
            return text is not null && VersionCompare.IsNewer(mod.Version, LuaManifestReader.Read(text).Version);
        }
        catch (Exception ex) when (ex is PackageException or ManifestException or IOException or InvalidDataException)
        {
            return false; // the install reports the real problem
        }
    }

    /// <summary>The most an archive inside the downloaded archive may weigh.</summary>
    public const long MaxInnerArchiveBytes = 64L * 1024 * 1024;

    private static InstallOutcome InstallFrom(ModStore store, CatalogMod mod, ArchivePackage package)
    {
        string index = mod.IndexUrl.AbsoluteUri;
        string source = mod.DownloadUrl.AbsoluteUri;
        string version = mod.Version.Length > 0 ? mod.Version : mod.PackageVersion;
        return mod.Kind switch
        {
            ItemKind.Plugin => store.InstallPlugin(package, mod.Id, version, mod.Path, mod.InstallTo, mod.InstallFiles, index, source),
            ItemKind.Tool => store.InstallTool(package, mod.Id, version, mod.Path, mod.Launch, index, source),
            _ => store.Install(package, mod.Path, mod.Id, index, source),
        };
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

    /// <summary>The parsed changelog of a catalog mod (empty when it has none or it cannot be read), read like a picture: from its https URL or from the mod's zip.</summary>
    public async Task<IReadOnlyList<ChangelogEntry>> GetChangelogAsync(CatalogMod mod, bool allowDownload, CancellationToken ct)
    {
        if (mod.Changelog is not { } source)
        {
            return [];
        }

        byte[]? bytes = await GetImageAsync(mod, source, allowDownload, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return [];
        }

        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Changelog.Parse(Encoding.UTF8.GetString(bytes.AsSpan(skip)));
    }

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
