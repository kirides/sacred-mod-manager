using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public sealed class ModManagerServiceKindsTests : IDisposable
{
    private const string PkgUrl = "https://example.com/pkg.zip";
    private readonly TempDir _root = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly AppPaths _paths;
    private readonly ModManagerService _service;

    public ModManagerServiceKindsTests()
    {
        _paths = new AppPaths(Path.Combine(_root.Path, "home"));
        _service = new ModManagerService(_paths, _fetcher);
        Game = _root.Combine("game");
        Directory.CreateDirectory(Game);
        Store = new ModStore(Game, _paths.Backups);
    }

    private string Game { get; }
    private ModStore Store { get; }

    public void Dispose() => _root.Dispose();

    private static CatalogMod Cat(string id, ItemKind kind = ItemKind.Mod, string path = "", string requires = "", string url = PkgUrl, string? launch = null,
        string installTo = "", (string, string)[]? files = null, string version = "")
        => new()
        {
            Id = id, Kind = kind, Path = kind == ItemKind.Mod && path.Length == 0 ? "mods/" + id : path, DownloadUrl = new Uri(url), IndexUrl = new Uri("https://example.com/mod-index.json"),
            Requires = requires.Length == 0 ? [] : requires.Split(','), Launch = launch, InstallTo = installTo, InstallFiles = files ?? [], Version = version,
        };

    private static string[] Ids(InstallPlan p) => p.Install.Select(m => m.Id).ToArray();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private byte[] AllKindsZip(string tag = "1")
        => Pack.Zip(
            ("mods/bounty/mod.lua", Pack.ModLua("bounty", tag)), ("mods/bounty/main.lua", "print(" + tag + ")"),
            ("plug/ddraw.dll", "DLL" + tag), ("plug/readme.txt", "R" + tag),
            ("tool/Ed.exe", "EXE" + tag), ("tool/cfg/default.cfg", "CFG" + tag));

    // ---- InstallAsync dispatches by kind ---------------------------------------------------------------------------

    [Fact]
    public async Task InstallAsyncInstallsAModByDefault()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        InstallOutcome o = await _service.InstallAsync(Store, Cat("bounty"), null, Ct);
        Assert.Equal("bounty", o.Id);
        Assert.True(File.Exists(Path.Combine(Game, "mods", "bounty", "mod.lua")));
        Assert.Null(Store.Registry.Load().Mods["bounty"].Kind);
    }

    [Fact]
    public async Task InstallAsyncInstallsAPluginIntoTheGameFolder()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        CatalogMod plugin = Cat("bild", ItemKind.Plugin, "plug", installTo: "sub", files: [("ddraw.dll", "ddraw.dll")], version: "4.2");
        InstallOutcome o = await _service.InstallAsync(Store, plugin, null, Ct);
        Assert.Equal("4.2", o.Version);
        Assert.Equal("DLL1", File.ReadAllText(Path.Combine(Game, "sub", "ddraw.dll")));
        Assert.False(File.Exists(Path.Combine(Game, "sub", "readme.txt")));
        TrackedMod t = Store.Registry.Load().Mods["bild"];
        Assert.Equal("plugin", t.Kind);
        Assert.Equal("https://example.com/mod-index.json", t.IndexUrl);
        Assert.Equal(PkgUrl, t.PackageUrl);
    }

    [Fact]
    public async Task InstallAsyncInstallsAPluginWithAllFilesWhenTheListIsEmpty()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        await _service.InstallAsync(Store, Cat("bild", ItemKind.Plugin, "plug"), null, Ct);
        Assert.Equal("DLL1", File.ReadAllText(Path.Combine(Game, "ddraw.dll")));
        Assert.Equal("R1", File.ReadAllText(Path.Combine(Game, "readme.txt")));
    }

    [Fact]
    public async Task InstallAsyncInstallsAToolIntoTheToolsFolder()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        InstallOutcome o = await _service.InstallAsync(Store, Cat("ed", ItemKind.Tool, "tool", launch: "Ed.exe"), null, Ct);
        Assert.Equal(2, o.FilesWritten);
        string dir = Path.Combine(_paths.Tools, "ed");
        Assert.Equal(dir, Store.ToolDirectory("ed"));
        Assert.Equal("EXE1", File.ReadAllText(Path.Combine(dir, "Ed.exe")));
        Assert.Equal("CFG1", File.ReadAllText(Path.Combine(dir, "cfg", "default.cfg")));
        Assert.Equal("Ed.exe", Store.Registry.Load().Mods["ed"].Launch);
        Assert.False(File.Exists(Path.Combine(Game, "Ed.exe")));
    }

    [Fact]
    public async Task InstallAsyncUsesThePackageVersionWhenTheEntryHasNone()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        CatalogMod tool = Cat("ed", ItemKind.Tool, "tool");
        tool.PackageVersion = "v3";
        await _service.InstallAsync(Store, tool, null, Ct);
        Assert.Equal("v3", Store.Registry.Load().Mods["ed"].Version);

        CatalogMod plugin = Cat("bild", ItemKind.Plugin, "plug", version: "1.0");
        plugin.PackageVersion = "v3";
        await _service.InstallAsync(Store, plugin, null, Ct);
        Assert.Equal("1.0", Store.Registry.Load().Mods["bild"].Version);
    }

    [Fact]
    public async Task InstallAsyncUpdatesAPluginAndATool()
    {
        _fetcher.Add(PkgUrl, AllKindsZip("1"));
        CatalogMod plugin = Cat("bild", ItemKind.Plugin, "plug", version: "1");
        CatalogMod tool = Cat("ed", ItemKind.Tool, "tool", launch: "Ed.exe", version: "1");
        await _service.InstallAsync(Store, plugin, null, Ct);
        await _service.InstallAsync(Store, tool, null, Ct);

        _fetcher.Add(PkgUrl, AllKindsZip("2"));
        plugin = Cat("bild", ItemKind.Plugin, "plug", version: "2");
        tool = Cat("ed", ItemKind.Tool, "tool", launch: "Ed.exe", version: "2");
        // a cached package of the same URL is revalidated by its ETag, the new content is downloaded
        InstallOutcome p = await _service.InstallAsync(Store, plugin, null, Ct);
        InstallOutcome t = await _service.InstallAsync(Store, tool, null, Ct);
        Assert.Equal("1", p.PreviousVersion);
        Assert.Equal("1", t.PreviousVersion);
        Assert.Equal("DLL2", File.ReadAllText(Path.Combine(Game, "ddraw.dll")));
        Assert.Equal("EXE2", File.ReadAllText(Path.Combine(Store.ToolDirectory("ed"), "Ed.exe")));
    }

    [Fact]
    public async Task InstallAsyncSurfacesTheStoreExceptionOfAPluginWithAMissingFile()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        CatalogMod plugin = Cat("bild", ItemKind.Plugin, "plug", files: [("nope.dll", "nope.dll")]);
        await Assert.ThrowsAsync<StoreException>(() => _service.InstallAsync(Store, plugin, null, Ct));
        CatalogMod tool = Cat("ed", ItemKind.Tool, "tool", launch: "Nope.exe");
        await Assert.ThrowsAsync<StoreException>(() => _service.InstallAsync(Store, tool, null, Ct));
        Assert.Empty(Store.Registry.Load().Mods);
    }

    [Fact]
    public async Task InstallAsyncChecksTheDownloadChecksumForEveryKind()
    {
        byte[] zip = AllKindsZip();
        _fetcher.Add(PkgUrl, zip);
        CatalogMod bad = Cat("ed", ItemKind.Tool, "tool");
        bad.DownloadSha256 = new string('0', 64);
        await Assert.ThrowsAnyAsync<Exception>(() => _service.InstallAsync(Store, bad, null, Ct));
        Assert.False(Directory.Exists(Store.ToolDirectory("ed")));

        CatalogMod good = Cat("ed", ItemKind.Tool, "tool");
        good.DownloadSha256 = Pack.Sha256(zip);
        await _service.InstallAsync(Store, good, null, Ct);
        Assert.True(Directory.Exists(Store.ToolDirectory("ed")));
    }

    [Fact]
    public async Task InstallPlanAsyncInstallsAllKindsFromOneDownloadInOrder()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        CatalogMod bounty = Cat("bounty");
        CatalogMod bild = Cat("bild", ItemKind.Plugin, "plug", requires: "bounty");
        CatalogMod ed = Cat("ed", ItemKind.Tool, "tool", requires: "bild", launch: "Ed.exe");
        InstallPlan plan = ModManagerService.Plan([ed], [ed, bild, bounty], []);
        List<BatchItem> r = await _service.InstallPlanAsync(Store, plan, null, null, Ct);
        Assert.Equal(["bounty", "bild", "ed"], r.Select(x => x.Id).ToArray());
        Assert.All(r, x => Assert.Null(x.Error));
        Assert.Equal(1, _fetcher.CountCalls(PkgUrl));
        Assert.Equal(["bild", "bounty", "ed"], Store.Registry.Load().Mods.Keys.Order().ToArray());
    }

    [Fact]
    public async Task InstallPlanAsyncReportsAFailingPluginAndStillInstallsTheRest()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        CatalogMod bild = Cat("bild", ItemKind.Plugin, "plug", files: [("nope.dll", "n")]);
        CatalogMod ed = Cat("ed", ItemKind.Tool, "tool", requires: "bild", launch: "Ed.exe");
        InstallPlan plan = ModManagerService.Plan([ed], [ed, bild], []);
        List<BatchItem> r = await _service.InstallPlanAsync(Store, plan, null, null, Ct);
        Assert.NotNull(r[0].Error);
        Assert.Null(r[1].Error);
        Assert.Equal(["ed"], Store.Registry.Load().Mods.Keys.ToArray());
    }

    // ---- Plan across kinds ---------------------------------------------------------------------------------------

    [Fact]
    public void PlanOrdersAToolRequiringAPluginRequiringAMod()
    {
        CatalogMod m = Cat("m"), p = Cat("p", ItemKind.Plugin, requires: "m"), t = Cat("t", ItemKind.Tool, requires: "p");
        InstallPlan plan = ModManagerService.Plan([t], [t, p, m], []);
        Assert.Equal(["m", "p", "t"], Ids(plan));
        Assert.Equal(["t"], plan.Wanted.ToArray());
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void PlanDoesNotMindTheKindOfAnInstalledRequirement()
    {
        CatalogMod m = Cat("m"), p = Cat("p", ItemKind.Plugin, requires: "m"), t = Cat("t", ItemKind.Tool, requires: "p,m");
        Assert.Equal(["m", "p", "t"], Ids(ModManagerService.Plan([t], [t, p, m], [])));
        Assert.Equal(["t"], Ids(ModManagerService.Plan([t], [t, p, m], ["p", "m"])));
        Assert.Equal(["p", "t"], Ids(ModManagerService.Plan([t], [t, p, m], ["m"])));
    }

    [Fact]
    public async Task InstalledPluginsAndToolsCountAsInstalledForPlanning()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        CatalogMod bild = Cat("bild", ItemKind.Plugin, "plug");
        CatalogMod ed = Cat("ed", ItemKind.Tool, "tool", requires: "bild");
        CatalogMod top = Cat("top", requires: "ed,bild");
        await _service.InstallAsync(Store, bild, null, Ct);
        await _service.InstallAsync(Store, ed, null, Ct);
        string[] installed = (await Store.ScanAsync(Ct)).Select(m => m.Id).ToArray();
        Assert.Equal(["bild", "ed"], installed);
        InstallPlan plan = ModManagerService.Plan([top], [top, ed, bild], installed);
        Assert.Equal(["top"], Ids(plan));
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void PlanReportsAMissingRequirementOfAPlugin()
    {
        CatalogMod p = Cat("p", ItemKind.Plugin, requires: "ghost");
        InstallPlan plan = ModManagerService.Plan([p], [p], []);
        Assert.Equal(["p"], Ids(plan));
        Assert.Equal(["ghost"], plan.Missing.ToArray());
    }

    [Fact]
    public void PlanHandlesACycleAcrossKinds()
    {
        CatalogMod a = Cat("a", requires: "b"), b = Cat("b", ItemKind.Plugin, requires: "c"), c = Cat("c", ItemKind.Tool, requires: "a");
        InstallPlan plan = ModManagerService.Plan([a], [a, b, c], []);
        Assert.Equal(3, plan.Install.Count);
        Assert.Equal(["a"], plan.Wanted.ToArray());
    }

    // ---- regression: mods next to other kinds --------------------------------------------------------------------

    private async Task InstallAll()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        await _service.InstallAsync(Store, Cat("bounty"), null, Ct);
        await _service.InstallAsync(Store, Cat("bild", ItemKind.Plugin, "plug"), null, Ct);
        await _service.InstallAsync(Store, Cat("ed", ItemKind.Tool, "tool", launch: "Ed.exe"), null, Ct);
    }

    [Fact]
    public async Task OneRegistryHoldsAModAPluginAndAToolAndScanKeepsThemApart()
    {
        await InstallAll();
        InstallRegistryFile reg = Store.Registry.Load();
        Assert.Equal(["bild", "bounty", "ed"], reg.Mods.Keys.Order().ToArray());
        Assert.Equal([ItemKind.Plugin, ItemKind.Mod, ItemKind.Tool], new[] { "bild", "bounty", "ed" }.Select(k => reg.Mods[k].ItemKind).ToArray());

        List<InstalledMod> scan = await Store.ScanAsync(Ct);
        Assert.Equal(3, scan.Count);
        InstalledMod mod = Assert.Single(scan, m => m.Kind == ItemKind.Mod);
        Assert.Equal("bounty", mod.Folder);
        Assert.NotNull(mod.Manifest);
        Assert.Equal(Path.Combine(Game, "mods", "bounty"), mod.Directory);
        Assert.Equal(1, scan.Count(m => m.Kind == ItemKind.Plugin));
        Assert.Equal(1, scan.Count(m => m.Kind == ItemKind.Tool));
    }

    [Fact]
    public async Task ModsKeepWorkingWhileOtherKindsAreInTheRegistry()
    {
        await InstallAll();
        _fetcher.Add(PkgUrl, AllKindsZip("2"));
        InstallOutcome o = await _service.InstallAsync(Store, Cat("bounty"), null, Ct);
        Assert.Equal("1", o.PreviousVersion);
        Assert.Equal("2", o.Version);
        Assert.NotNull(o.Backup);
        Assert.Equal("print(2)", File.ReadAllText(Path.Combine(Game, "mods", "bounty", "main.lua")));
        InstallRegistryFile reg = Store.Registry.Load();
        Assert.Equal("plugin", reg.Mods["bild"].Kind);
        Assert.Equal("tool", reg.Mods["ed"].Kind);
        Assert.Equal("DLL1", File.ReadAllText(Path.Combine(Game, "ddraw.dll")));
    }

    [Fact]
    public async Task UninstallingTheModLeavesPluginAndToolAndTheOtherWayAround()
    {
        await InstallAll();
        List<InstalledMod> scan = await Store.ScanAsync(Ct);
        Assert.NotNull(Store.Uninstall(scan.Single(m => m.Id == "bounty"), false, true));
        Assert.Equal(["bild", "ed"], Store.Registry.Load().Mods.Keys.Order().ToArray());
        Assert.True(File.Exists(Path.Combine(Game, "ddraw.dll")));
        Assert.True(File.Exists(Path.Combine(Store.ToolDirectory("ed"), "Ed.exe")));

        Assert.Null(Store.Uninstall(scan.Single(m => m.Id == "bild"), false, false));
        Assert.Null(Store.Uninstall(scan.Single(m => m.Id == "ed"), false, false));
        Assert.Empty(Store.Registry.Load().Mods);
        Assert.Empty(await Store.ScanAsync(Ct));
    }

    [Fact]
    public async Task ModScanDoesNotTreatPluginOrToolEntriesAsModFolders()
    {
        // a plugin and a tool both have Folder = id; a hand-made mod folder of that name must not pick up their record
        await InstallAll();
        string folder = Path.Combine(Game, "mods", "ed");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.lua"), Pack.ModLua("handmade"));
        string pluginNamed = Path.Combine(Game, "mods", "bild");
        Directory.CreateDirectory(pluginNamed);
        File.WriteAllText(Path.Combine(pluginNamed, "mod.lua"), Pack.ModLua("handmade2"));

        List<InstalledMod> scan = await Store.ScanAsync(Ct);
        Assert.Equal(["handmade", "handmade2"], scan.Where(m => m.Kind == ItemKind.Mod && m.Tracked is null).Select(m => m.Id).Order().ToArray());
        Assert.All(scan.Where(m => m.Kind == ItemKind.Mod && m.Folder is "ed" or "bild"), m => Assert.Null(m.Tracked));
        Assert.Equal(2, scan.Count(m => m.Kind != ItemKind.Mod));
        Assert.Equal(0, scan.Single(m => m.Id == "bounty").ModifiedFiles);
    }

    [Fact]
    public async Task AToolWhoseIdEqualsAModFolderNameDoesNotBreakTheScan()
    {
        Directory.CreateDirectory(Path.Combine(Game, "mods", "same"));
        File.WriteAllText(Path.Combine(Game, "mods", "same", "mod.lua"), Pack.ModLua("same"));
        _fetcher.Add(PkgUrl, AllKindsZip());
        await _service.InstallAsync(Store, Cat("same", ItemKind.Tool, "tool", launch: "Ed.exe"), null, Ct);
        List<InstalledMod> scan = await Store.ScanAsync(Ct);
        Assert.Equal(2, scan.Count);
        InstalledMod mod = Assert.Single(scan, m => m.Kind == ItemKind.Mod);
        Assert.Null(mod.Tracked);
        Assert.Equal("same", mod.Id);
        Assert.Equal(ItemKind.Tool, Assert.Single(scan, m => m.Kind == ItemKind.Tool).Kind);
    }

    [Fact]
    public async Task APluginMayWriteIntoTheModsFolderWithoutBecomingAMod()
    {
        _fetcher.Add(PkgUrl, Pack.Zip(("x/mods/lib/data.txt", "D"), ("x/mods/note.txt", "N")));
        await _service.InstallAsync(Store, Cat("p", ItemKind.Plugin, "x"), null, Ct);
        List<InstalledMod> scan = await Store.ScanAsync(Ct);
        InstalledMod only = Assert.Single(scan); // mods\lib has neither mod.lua nor main.lua
        Assert.Equal(ItemKind.Plugin, only.Kind);
    }

    [Fact]
    public async Task AModAndAPluginWithTheSameIdAreNotConfusedByTheStore()
    {
        // a Gear mod must not take over the record (and thereby the files) of a plugin of the same id
        _fetcher.Add(PkgUrl, AllKindsZip());
        await _service.InstallAsync(Store, Cat("bounty", ItemKind.Plugin, "plug"), null, Ct);
        await Assert.ThrowsAsync<StoreException>(() => _service.InstallAsync(Store, Cat("bounty"), null, Ct));
        TrackedMod t = Store.Registry.Load().Mods["bounty"];
        Assert.Equal("plugin", t.Kind);
        Assert.Contains("ddraw.dll", t.Files.Keys);
    }

    [Fact]
    public async Task AModIdAlreadyUsedByAToolIsRefusedAsWell()
    {
        _fetcher.Add(PkgUrl, AllKindsZip());
        await _service.InstallAsync(Store, Cat("bounty", ItemKind.Tool, "tool"), null, Ct);
        await Assert.ThrowsAsync<StoreException>(() => _service.InstallAsync(Store, Cat("bounty"), null, Ct));
        Assert.Equal("tool", Store.Registry.Load().Mods["bounty"].Kind);
        Assert.False(Directory.Exists(Path.Combine(Game, "mods", "bounty")));
    }

    [Fact]
    public async Task AModWhoseFolderNameEqualsAPluginIdIsRefusedAndTheRecordIsKept()
    {
        // mods\bild would clash with the plugin "bild" (its record uses Folder = id)
        _fetcher.Add(PkgUrl, Pack.Zip(("mods/bild/mod.lua", Pack.ModLua("other")), ("plug/a.dll", "A")));
        await _service.InstallAsync(Store, Cat("bild", ItemKind.Plugin, "plug"), null, Ct);
        await Assert.ThrowsAsync<StoreException>(() => _service.InstallAsync(Store, Cat("other", path: "mods/bild"), null, Ct));
        Assert.Equal("plugin", Store.Registry.Load().Mods["bild"].Kind);
    }
}
