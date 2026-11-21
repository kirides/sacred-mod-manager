using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public sealed class ModManagerServiceTests : IDisposable
{
    private const string PkgUrl = "https://example.com/pkg.zip";
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CatalogMod Cat(string id, string requires = "", string? pkg = PkgUrl, string? thumbUrl = null, string? thumbPath = null, string? sha = null)
        => new()
        {
            Id = id, Path = "mods/" + id, DownloadUrl = new Uri(pkg ?? PkgUrl), IndexUrl = new Uri("https://example.com/mod-index.json"),
            Requires = requires.Length == 0 ? [] : requires.Split(','), ThumbnailUrl = thumbUrl is null ? null : new Uri(thumbUrl), ThumbnailPath = thumbPath, DownloadSha256 = sha,
        };

    private static string[] Ids(InstallPlan p) => p.Install.Select(m => m.Id).ToArray();

    // ---- Plan -------------------------------------------------------------------------------------------------

    [Fact]
    public void PlanPutsRequiredModsFirst()
    {
        CatalogMod a = Cat("a"), b = Cat("b", "a"), c = Cat("c", "b");
        InstallPlan p = ModManagerService.Plan([c], [c, b, a], []);
        Assert.Equal(["a", "b", "c"], Ids(p));
        Assert.Equal(["c"], p.Wanted.ToArray());
        Assert.Empty(p.Missing);
    }

    [Fact]
    public void PlanOrdersMultipleRequirementsInDeclaredOrder()
    {
        CatalogMod a = Cat("a"), b = Cat("b"), c = Cat("c", "b,a");
        InstallPlan p = ModManagerService.Plan([c], [a, b, c], []);
        Assert.Equal(["b", "a", "c"], Ids(p));
    }

    [Fact]
    public void PlanSkipsInstalledRequirements()
    {
        CatalogMod a = Cat("a"), c = Cat("c", "a,b"), b = Cat("b");
        InstallPlan p = ModManagerService.Plan([c], [a, b, c], ["a"]);
        Assert.Equal(["b", "c"], Ids(p));
    }

    [Fact]
    public void PlanDoesNotSkipAWantedModThatIsInstalled()
    {
        CatalogMod a = Cat("a");
        InstallPlan p = ModManagerService.Plan([a], [a], ["a"]);
        Assert.Equal(["a"], Ids(p));
        Assert.Contains("a", p.Wanted);
    }

    [Fact]
    public void PlanReportsMissingRequirementsOnceAndStillPlansTheMod()
    {
        CatalogMod c = Cat("c", "ghost,phantom"), d = Cat("d", "ghost");
        InstallPlan p = ModManagerService.Plan([c, d], [c, d], []);
        Assert.Equal(["c", "d"], Ids(p));
        Assert.Equal(["ghost", "phantom"], p.Missing.ToArray());
    }

    [Fact]
    public void PlanMissingRequirementOfADependencyIsReported()
    {
        CatalogMod a = Cat("a", "ghost"), b = Cat("b", "a");
        InstallPlan p = ModManagerService.Plan([b], [a, b], []);
        Assert.Equal(["a", "b"], Ids(p));
        Assert.Equal(["ghost"], p.Missing.ToArray());
    }

    [Fact]
    public void PlanInstalledRequirementIsNotMissing()
    {
        CatalogMod c = Cat("c", "local-only");
        InstallPlan p = ModManagerService.Plan([c], [c], ["local-only"]);
        Assert.Empty(p.Missing);
    }

    [Fact]
    public void PlanTerminatesOnCycles()
    {
        CatalogMod a = Cat("a", "b"), b = Cat("b", "a");
        InstallPlan p = ModManagerService.Plan([a], [a, b], []);
        Assert.Equal(2, p.Install.Count);
        Assert.Equal(["a", "b"], p.Install.Select(m => m.Id).Order().ToArray());
        Assert.Equal(["a"], p.Wanted.ToArray());

        CatalogMod self = Cat("s", "s");
        Assert.Equal(["s"], Ids(ModManagerService.Plan([self], [self], [])));

        CatalogMod x = Cat("x", "y"), y = Cat("y", "z"), z = Cat("z", "x");
        Assert.Equal(3, ModManagerService.Plan([x, y, z], [x, y, z], []).Install.Count);
    }

    [Fact]
    public void PlanPlansEveryModOnce()
    {
        CatalogMod core = Cat("core"), a = Cat("a", "core"), b = Cat("b", "core");
        InstallPlan p = ModManagerService.Plan([a, b, a], [core, a, b], []);
        Assert.Equal(["core", "a", "b"], Ids(p));
        Assert.Equal(["a", "b"], p.Wanted.Order().ToArray());
    }

    [Fact]
    public void PlanMarksAWantedModAsWantedEvenWhenAnotherWantedModRequiresIt()
    {
        CatalogMod a = Cat("a"), c = Cat("c", "a");
        InstallPlan p = ModManagerService.Plan([c, a], [a, c], []);
        Assert.Equal(["a", "c"], Ids(p));
        Assert.Equal(["a", "c"], p.Wanted.Order().ToArray());
    }

    [Fact]
    public void PlanOfNothingIsEmpty()
    {
        InstallPlan p = ModManagerService.Plan([], [], []);
        Assert.Empty(p.Install);
        Assert.Empty(p.Missing);
    }

    // ---- InspectArchive -------------------------------------------------------------------------------------------

    [Fact]
    public void InspectArchiveListsValidModsInOrderAndSkipsBrokenOnes()
    {
        string zip = Pack.WriteZip(_root.Path, "i.zip", ("mods/b/mod.lua", Pack.ModLua("b", "2.0")), ("mods/a/mod.lua", Pack.ModLua("a", "1.0")), ("mods/broken/mod.lua", "return {"), ("mods/c/main.lua", "x"));
        List<ModManagerService.LocalMod> list = ModManagerService.InspectArchive(zip);
        Assert.Equal(["mods/a", "mods/b"], list.Select(m => m.PathInArchive).ToArray());
        Assert.Equal("2.0", list[1].Manifest.Version);
    }

    [Fact]
    public void InspectArchiveFindsARootLevelMod()
    {
        string zip = Pack.WriteZip(_root.Path, "r.zip", ("mod.lua", Pack.ModLua("rootmod")), ("main.lua", "x"));
        ModManagerService.LocalMod m = Assert.Single(ModManagerService.InspectArchive(zip));
        Assert.Equal(string.Empty, m.PathInArchive);
        Assert.Equal("rootmod", m.Manifest.Id);
    }

    [Fact]
    public void InspectArchiveOfANonZipThrowsPackageException()
    {
        string path = Path.Combine(_root.Path, "x.zip");
        File.WriteAllText(path, "nope");
        Assert.Throws<PackageException>(() => ModManagerService.InspectArchive(path));
    }

    [Fact]
    public void InspectArchiveWithNoModsIsEmpty()
        => Assert.Empty(ModManagerService.InspectArchive(Pack.WriteZip(_root.Path, "e.zip", ("readme.txt", "x"))));

    // ---- thumbnails -------------------------------------------------------------------------------------------

    private (ModManagerService Service, FakeFetcher Fetcher, AppPaths Paths) Create(FakeFetcher? fetcher = null)
    {
        fetcher ??= new FakeFetcher();
        var paths = new AppPaths(Path.Combine(_root.Path, "home"));
        return (new ModManagerService(paths, fetcher), fetcher, paths);
    }

    [Fact]
    public async Task ThumbnailFromHttpsUrlIsFetchedOnceAndCached()
    {
        const string url = "https://img.example.com/a.png";
        byte[] png = [1, 2, 3, 4, 5];
        (ModManagerService s, FakeFetcher f, AppPaths paths) = Create();
        f.Add(url, png);
        CatalogMod mod = Cat("a", thumbUrl: url);

        byte[]? first = await s.GetThumbnailAsync(mod, allowDownload: false, TestContext.Current.CancellationToken);
        byte[]? second = await s.GetThumbnailAsync(mod, allowDownload: false, TestContext.Current.CancellationToken);

        Assert.Equal(png, first);
        Assert.Equal(png, second);
        Assert.Equal(1, f.CountCalls(url));
        Assert.Single(Directory.GetFiles(paths.Thumbnails));
    }

    [Fact]
    public async Task AnOldCachedThumbnailIsFetchedAgainSoUpdatedPicturesShowUp()
    {
        const string url = "https://img.example.com/a.png";
        (ModManagerService s, FakeFetcher f, AppPaths paths) = Create();
        f.Add(url, [9, 9]);
        Directory.CreateDirectory(paths.Thumbnails);
        string file = Path.Combine(paths.Thumbnails, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SacredModManager.Core.Index.SourceUrl.Key(new Uri(url)))))[..16]);
        File.WriteAllBytes(file, [1, 1]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));

        Assert.Equal([9, 9], await s.GetThumbnailAsync(Cat("a", thumbUrl: url), false, TestContext.Current.CancellationToken));
        Assert.Equal([9, 9], File.ReadAllBytes(file));
    }

    [Fact]
    public async Task ThumbnailCacheIsSharedByModsWithTheSameUrlAndSeparatePerUrl()
    {
        (ModManagerService s, FakeFetcher f, AppPaths paths) = Create();
        f.Add("https://img.example.com/a.png", [1]).Add("https://img.example.com/b.png", [2]);
        Assert.Equal([1], await s.GetThumbnailAsync(Cat("a", thumbUrl: "https://img.example.com/a.png"), false, TestContext.Current.CancellationToken));
        Assert.Equal([1], await s.GetThumbnailAsync(Cat("other", thumbUrl: "https://img.example.com/a.png#x"), false, TestContext.Current.CancellationToken));
        Assert.Equal([2], await s.GetThumbnailAsync(Cat("b", thumbUrl: "https://img.example.com/b.png"), false, TestContext.Current.CancellationToken));
        Assert.Equal(2, f.Calls.Count);
        Assert.Equal(2, Directory.GetFiles(paths.Thumbnails).Length);
    }

    [Fact]
    public async Task ThumbnailFetchFailureGivesNullAndIsRetriedLater()
    {
        const string url = "https://img.example.com/missing.png";
        (ModManagerService s, FakeFetcher f, _) = Create();
        CatalogMod mod = Cat("a", thumbUrl: url);
        Assert.Null(await s.GetThumbnailAsync(mod, true, TestContext.Current.CancellationToken));
        f.Add(url, [9]);
        Assert.Equal([9], await s.GetThumbnailAsync(mod, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThumbnailWithoutAnySourceIsNull()
    {
        (ModManagerService s, FakeFetcher f, _) = Create();
        Assert.Null(await s.GetThumbnailAsync(Cat("a"), true, TestContext.Current.CancellationToken));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task ThumbnailFromZipPathNeedsADownloadUnlessAllowed()
    {
        byte[] img = [7, 7, 7];
        (ModManagerService s, FakeFetcher f, _) = Create();
        f.Add(PkgUrl, Pack.Zip(("mods/a/mod.lua", System.Text.Encoding.UTF8.GetBytes(Pack.ModLua("a"))), ("mods/a/shot.png", img)));
        CatalogMod mod = Cat("a", thumbPath: "mods/a/shot.png");

        Assert.Null(await s.GetThumbnailAsync(mod, allowDownload: false, TestContext.Current.CancellationToken));
        Assert.Empty(f.Calls);
        Assert.False(s.IsPackageCached(mod));

        Assert.Equal(img, await s.GetThumbnailAsync(mod, allowDownload: true, TestContext.Current.CancellationToken));
        Assert.Equal(1, f.CountCalls(PkgUrl));
        Assert.True(s.IsPackageCached(mod));

        // now the package is on disk: no download needed, none made
        Assert.Equal(img, await s.GetThumbnailAsync(mod, allowDownload: false, TestContext.Current.CancellationToken));
        Assert.Equal(1, f.CountCalls(PkgUrl));
    }

    [Fact]
    public async Task ThumbnailPathMissingInTheZipIsNull()
    {
        (ModManagerService s, FakeFetcher f, _) = Create();
        f.Add(PkgUrl, Pack.Zip(("mods/a/mod.lua", Pack.ModLua("a"))));
        Assert.Null(await s.GetThumbnailAsync(Cat("a", thumbPath: "mods/a/none.png"), true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThumbnailFromADamagedZipOrFailedDownloadIsNull()
    {
        (ModManagerService s, FakeFetcher f, _) = Create();
        f.Add(PkgUrl, "not a zip");
        Assert.Null(await s.GetThumbnailAsync(Cat("a", thumbPath: "x.png"), true, TestContext.Current.CancellationToken));
        Assert.Null(await s.GetThumbnailAsync(Cat("a", thumbPath: "x.png", pkg: "https://example.com/404.zip"), true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedThumbnailInAZipIsNull()
    {
        (ModManagerService s, FakeFetcher f, _) = Create();
        f.Add(PkgUrl, Pack.Zip(("big.png", new byte[12 * 1024 * 1024 + 1])));
        Assert.Null(await s.GetThumbnailAsync(Cat("a", thumbPath: "big.png"), true, TestContext.Current.CancellationToken));
    }

    // ---- catalog and install ----------------------------------------------------------------------------------

    [Fact]
    public async Task LoadCatalogMixesValidAndInvalidSources()
    {
        (ModManagerService s, FakeFetcher f, _) = Create();
        f.Add("https://example.com/mod-index.json", "{\"download\":{\"url\":\"p.zip\"},\"mods\":[{\"id\":\"a\",\"path\":\"mods/a\"}]}");
        f.Add("https://raw.githubusercontent.com/o/r/HEAD/mod-index.json", "{\"download\":{\"url\":\"p.zip\"},\"mods\":[{\"id\":\"g\",\"path\":\"mods/g\"}]}");
        IndexCatalog c = await s.LoadCatalogAsync(["", "http://insecure.example.com/x.json", "https://example.com/mod-index.json", "github.com/o/r"], TestContext.Current.CancellationToken);
        Assert.Equal(["a", "g"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Equal(2, c.Warnings.Count);
        Assert.Contains("only https", c.Warnings[1], StringComparison.Ordinal);
    }

    private static byte[] BountyZip(string version, string main = "main")
        => Pack.Zip(("mods/bounty/mod.lua", Pack.ModLua("bounty", version)), ("mods/bounty/main.lua", main));

    [Fact]
    public async Task InstallPlanSharesTheCachedZipBetweenModsOfTheSamePackage()
    {
        (ModManagerService s, FakeFetcher f, AppPaths paths) = Create();
        f.Add(PkgUrl, Pack.Zip(("mods/a/mod.lua", Pack.ModLua("a")), ("mods/b/mod.lua", Pack.ModLua("b"))));
        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        var store = new ModStore(game, paths.Backups);
        CatalogMod a = Cat("a"), b = Cat("b");

        var status = new List<string>();
        List<BatchItem> results = await s.InstallPlanAsync(store, ModManagerService.Plan([a, b], [a, b], []), new Progress<string>(status.Add), null, TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal(["a", "b"], results.Select(r => r.Id).ToArray());
        Assert.Equal("a", results[0].Outcome!.Id);
        // the zip is asked for once per batch, however many mods come from it
        Assert.Equal(1, f.CountCalls(PkgUrl));
        Assert.Single(Directory.GetFiles(paths.Packages, "*.pkg"));
        Assert.True(File.Exists(Path.Combine(game, "mods", "a", "mod.lua")));
        Assert.True(File.Exists(Path.Combine(game, "mods", "b", "mod.lua")));
        Assert.Equal("https://example.com/mod-index.json", store.Registry.Load().Mods["a"].IndexUrl);
        Assert.Equal(PkgUrl, store.Registry.Load().Mods["a"].PackageUrl);
    }

    [Fact]
    public async Task InstallWarnsWhenThePackageHoldsAnotherVersionThanTheIndex()
    {
        (ModManagerService s, FakeFetcher f, AppPaths paths) = Create();
        f.Add(PkgUrl, Pack.Zip(("mods/b/mod.lua", Pack.ModLua("b", "1.0.0")), ("mods/c/mod.lua", Pack.ModLua("c", "1.0.0"))));
        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        var store = new ModStore(game, paths.Backups);
        CatalogMod ahead = Cat("b"), same = Cat("c");
        ahead.Version = "1.1.0";
        same.Version = "1.0.0";
        var plan = new InstallPlan();
        plan.Install.AddRange([ahead, same]);

        List<BatchItem> r = await s.InstallPlanAsync(store, plan, null, null, TestContext.Current.CancellationToken);

        Assert.Null(r[0].Error);
        Assert.Contains("1.1.0", r[0].Warning, StringComparison.Ordinal);
        Assert.Contains("not been published", r[0].Warning, StringComparison.Ordinal);
        Assert.Null(r[1].Warning);
    }

    [Fact]
    public async Task InstallPlanContinuesAfterAFailure()
    {
        (ModManagerService s, FakeFetcher f, AppPaths paths) = Create();
        f.Add(PkgUrl, Pack.Zip(("mods/b/mod.lua", Pack.ModLua("b")), ("mods/wrongid/mod.lua", Pack.ModLua("other"))));
        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        var store = new ModStore(game, paths.Backups);
        CatalogMod down = Cat("down", pkg: "https://example.com/404.zip"), wrong = Cat("wrongid"), ok = Cat("b"), sha = Cat("b2", sha: new string('0', 64));
        var plan = new InstallPlan();
        plan.Install.AddRange([down, wrong, sha, ok]);

        List<BatchItem> r = await s.InstallPlanAsync(store, plan, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(4, r.Count);
        Assert.Contains("404", r[0].Error, StringComparison.Ordinal);
        Assert.Contains("promised", r[1].Error, StringComparison.Ordinal);
        Assert.Contains("checksum", r[2].Error, StringComparison.Ordinal);
        Assert.Null(r[3].Error);
        Assert.True(File.Exists(Path.Combine(game, "mods", "b", "mod.lua")));
        Assert.Equal(["b"], store.Registry.Load().Mods.Keys.ToArray());
    }

    [Fact]
    public async Task InstallAsyncUpdatesWhenANewerPackageAppears()
    {
        (ModManagerService s, FakeFetcher f, AppPaths paths) = Create();
        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        var store = new ModStore(game, paths.Backups);
        CatalogMod bounty = Cat("bounty");
        f.Add(PkgUrl, BountyZip("1.0.0", "v1"));
        InstallOutcome first = await s.InstallAsync(store, bounty, null, TestContext.Current.CancellationToken);
        f.Add(PkgUrl, BountyZip("1.1.0", "v2"));
        InstallOutcome second = await s.InstallAsync(store, bounty, null, TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", first.Version);
        Assert.Equal("1.1.0", second.Version);
        Assert.Equal("1.0.0", second.PreviousVersion);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(game, "mods", "bounty", "main.lua")));
    }

    [Fact]
    public void InstallLocalInstallsFromAZipFileWithoutIndexInfo()
    {
        (ModManagerService s, _, AppPaths paths) = Create();
        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        var store = new ModStore(game, paths.Backups);
        string zip = Pack.WriteZip(_root.Path, "local.zip", ("mods/a/mod.lua", Pack.ModLua("a")), ("mods/a/main.lua", "x"));
        InstallOutcome o = s.InstallLocal(store, zip, "mods/a");
        Assert.Equal("a", o.Id);
        TrackedMod t = store.Registry.Load().Mods["a"];
        Assert.Null(t.IndexUrl);
        Assert.Null(t.PackageUrl);
    }
}
