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
public sealed record BatchItem(string Id, InstallOutcome? Outcome, string? Error);

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
    {
        string zip = await _packages.GetAsync(mod.DownloadUrl, mod.DownloadSha256, progress, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using ZipPackage package = ZipPackage.Open(zip);
            return store.Install(package, mod.Path, mod.Id, mod.IndexUrl.AbsoluteUri, mod.DownloadUrl.AbsoluteUri);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Installs the planned mods in order; one failure does not stop the others (a mod that requires it will be reported by the game).</summary>
    public async Task<List<BatchItem>> InstallPlanAsync(ModStore store, InstallPlan plan, IProgress<string>? status, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var results = new List<BatchItem>();
        foreach (CatalogMod mod in plan.Install)
        {
            status?.Report("Installing " + mod.DisplayName);
            try
            {
                results.Add(new BatchItem(mod.Id, await InstallAsync(store, mod, progress, ct).ConfigureAwait(false), null));
            }
            catch (Exception ex) when (ex is StoreException or PackageException or FetchException)
            {
                results.Add(new BatchItem(mod.Id, null, ex.Message));
            }
        }

        return results;
    }

    /// <summary>What a local zip or folder offers to install.</summary>
    public sealed record LocalMod(string PathInZip, ModManifest Manifest);

    /// <summary>Lists the mods in a zip file (any folder with a mod.lua).</summary>
    public static List<LocalMod> InspectZip(string zipPath)
    {
        using ZipPackage package = ZipPackage.Open(zipPath);
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

    public InstallOutcome InstallLocal(ModStore store, string zipPath, string pathInZip)
    {
        using ZipPackage package = ZipPackage.Open(zipPath);
        return store.Install(package, pathInZip, null, null, null);
    }

    /// <summary>
    /// The thumbnail bytes of a catalog mod: its https URL (kept in the thumbnail cache), or the file inside the mod's zip
    /// (the zip is downloaded for this when it is not cached; <paramref name="allowDownload"/> false returns null instead).
    /// </summary>
    public async Task<byte[]?> GetThumbnailAsync(CatalogMod mod, bool allowDownload, CancellationToken ct)
    {
        const long max = 4 * 1024 * 1024;
        try
        {
            if (mod.ThumbnailUrl is { } url)
            {
                string file = Path.Combine(paths.Thumbnails, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceUrl.Key(url))))[..16]);
                if (File.Exists(file))
                {
                    return await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                }

                byte[] bytes = await fetcher.GetBytesAsync(url, max, ct).ConfigureAwait(false);
                Directory.CreateDirectory(paths.Thumbnails);
                await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
                return bytes;
            }

            if (mod.ThumbnailPath is { } inside)
            {
                string? zip = _packages.Cached(mod.DownloadUrl);
                if (zip is null && allowDownload)
                {
                    zip = await _packages.GetAsync(mod.DownloadUrl, mod.DownloadSha256, null, ct).ConfigureAwait(false);
                }

                if (zip is not null)
                {
                    return await Task.Run(() =>
                    {
                        using ZipPackage package = ZipPackage.Open(zip);
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
