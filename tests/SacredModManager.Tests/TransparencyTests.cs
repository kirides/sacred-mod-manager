using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

/// <summary>
/// Why installers and scripts are out: the player sees exactly what is installed before it happens, and the manager always knows which mod or plugin
/// provided which file of the game folder.
/// </summary>
public sealed class TransparencyTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly AppPaths _paths;
    private readonly ModManagerService _service;
    private static readonly Uri Idx = new("https://example.com/mod-index.json");
    private static readonly Uri Pkg = new("https://example.com/p.zip");

    public TransparencyTests()
    {
        _paths = new AppPaths(Path.Combine(_root.Path, "home"));
        _service = new ModManagerService(_paths, _fetcher);
        Game = _root.Combine("game");
        Directory.CreateDirectory(Game);
        Store = new ModStore(Game, _paths.Backups, _root.Combine("tools"));
        _fetcher.Add(Pkg.AbsoluteUri, Pack.Zip(
            ("mods/m/mod.lua", Pack.ModLua("m")), ("mods/m/main.lua", "x"), ("base/dsound.dll", "D"), ("base/dsoal-aldrv.dll", "A"),
            ("spk/alsoft.ini", "SPK"), ("hp/alsoft.ini", "HP"), ("tool/Ed.exe", "E"), ("tool/cfg.txt", "c")));
    }

    private string Game { get; }
    private ModStore Store { get; }
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    private static CatalogMod C(string id, ItemKind kind, string path, (string, string)[]? files = null, string installTo = "", string? launch = null, string requires = "")
        => new()
        {
            Id = id, Kind = kind, Path = path, DownloadUrl = Pkg, IndexUrl = Idx, InstallFiles = files ?? [], InstallTo = installTo, Launch = launch,
            Requires = requires.Length == 0 ? [] : requires.Split(','),
        };

    // ---- (a) the player sees what will be installed -----------------------------------------------------------------

    [Fact]
    public void ThePreviewNamesTheSourceAndEveryFileWithItsTarget()
    {
        string[] mod = InstallPreview.Describe(C("m", ItemKind.Mod, "mods/m"), "T").ToArray();
        Assert.Contains("from https://example.com/p.zip", mod);
        Assert.Contains("listed by https://example.com/mod-index.json", mod);
        Assert.Contains(mod, l => l.Contains("mods/m", StringComparison.Ordinal) && l.Contains("->", StringComparison.Ordinal));

        string[] plugin = InstallPreview.Describe(C("p", ItemKind.Plugin, "base", [("dsound.dll", "dsound.dll"), ("x/*.dll", "bin")], installTo: "sub"), "T").ToArray();
        Assert.Contains("dsound.dll -> sub/dsound.dll  (Sacred folder)", plugin);
        Assert.Contains(plugin, l => l.StartsWith("x/*.dll (every match) -> sub/bin", StringComparison.Ordinal));

        string[] all = InstallPreview.Describe(C("p", ItemKind.Plugin, "base"), "T").ToArray();
        Assert.Contains(all, l => l.Contains("every file under 'base'", StringComparison.Ordinal) && l.Contains("Sacred folder", StringComparison.Ordinal));

        string[] tool = InstallPreview.Describe(C("ed", ItemKind.Tool, "tool", launch: "Ed.exe"), Path.Combine("T", "tools")).ToArray();
        Assert.Contains(tool, l => l.Contains(Path.Combine("T", "tools", "ed"), StringComparison.Ordinal));
        Assert.Contains(tool, l => l.Contains("only when you click it", StringComparison.Ordinal) && l.EndsWith("Ed.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheConflictQuestionListsTheFilesAndNamesTheOwner()
    {
        CatalogMod b = C("base", ItemKind.Plugin, "base", [("dsound.dll", "dsound.dll")]);
        CatalogMod s = C("spk", ItemKind.Plugin, "spk", [("alsoft.ini", "alsoft.ini")], requires: "base");
        CatalogMod h = C("hp", ItemKind.Plugin, "hp", [("alsoft.ini", "alsoft.ini")], requires: "base");
        CatalogMod[] all = [b, s, h];
        await _service.InstallPlanAsync(Store, ModManagerService.Plan([s], all, []), null, null, Ct);

        PluginConflictException? seen = null;
        await _service.InstallPlanAsync(Store, ModManagerService.Plan([h], all, ["base"]), null, null, Ct, c => { seen = c; return Task.FromResult(false); });
        Assert.NotNull(seen);
        Assert.Equal("spk", seen.ConflictingId);
        Assert.Equal(["alsoft.ini"], seen.Files);
    }

    // ---- (b) the manager always knows who provided which file --------------------------------------------------------

    private static IEnumerable<string> RelFiles(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'));

    [Fact]
    public async Task EveryFileTheManagerWroteHasExactlyOneOwnerWithItsHash()
    {
        File.WriteAllText(Path.Combine(Game, "Sacred.exe"), "GAME");
        File.WriteAllText(Path.Combine(Game, "alsoft.ini"), "PLAYERS");
        string[] before = RelFiles(Game).ToArray();

        CatalogMod b = C("base", ItemKind.Plugin, "base");
        CatalogMod s = C("spk", ItemKind.Plugin, "spk", [("alsoft.ini", "alsoft.ini")], requires: "base");
        CatalogMod h = C("hp", ItemKind.Plugin, "hp", [("alsoft.ini", "alsoft.ini")], requires: "base");
        CatalogMod m = C("m", ItemKind.Mod, "mods/m");
        CatalogMod t = C("ed", ItemKind.Tool, "tool", launch: "Ed.exe");
        CatalogMod[] all = [b, s, h, m, t];
        foreach (CatalogMod x in new[] { s, m, t })
        {
            Assert.All(await _service.InstallPlanAsync(Store, ModManagerService.Plan([x], all, Store.Registry.Load().Mods.Keys), null, null, Ct), r => Assert.Null(r.Error));
        }

        // swap speakers -> headphones
        Assert.All(await _service.InstallPlanAsync(Store, ModManagerService.Plan([h], all, ["base", "m", "ed"]), null, null, Ct, _ => Task.FromResult(true)), r => Assert.Null(r.Error));

        Dictionary<string, List<ModStore.FileOwner>> map = Store.FileOwnership();
        Assert.All(map, kv => Assert.Single(kv.Value));

        // every file that is new in the game folder is in the map, except the manager's and the game's own record files; nothing in the map is missing on disk
        string[] added = RelFiles(Game).Except(before, StringComparer.OrdinalIgnoreCase)
            .Where(f => f != "mods/" + InstallRegistry.FileName && f != "mods/" + GearModState.FileName).Append("alsoft.ini").ToArray(); // the player's own ini was replaced: owned now
        Assert.Equal(added.Order(StringComparer.OrdinalIgnoreCase), map.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach ((string rel, List<ModStore.FileOwner> owners) in map)
        {
            Assert.Equal(Pack.Sha256(File.ReadAllBytes(Path.Combine(Game, rel.Replace('/', Path.DirectorySeparatorChar)))), owners[0].Sha256);
        }

        Assert.Equal("hp", Store.OwnerOf("alsoft.ini")!.Id);
        Assert.Equal(ItemKind.Plugin, Store.OwnerOf("alsoft.ini")!.Kind);
        Assert.Equal("base", Store.OwnerOf("dsound.dll")!.Id);
        Assert.Equal("m", Store.OwnerOf("mods\\m\\main.lua")!.Id);
        Assert.Equal(ItemKind.Mod, Store.OwnerOf("mods/m/main.lua")!.Kind);
        Assert.Null(Store.OwnerOf("Sacred.exe")); // the game's own file: nobody the manager knows
        Assert.False(map.ContainsKey("Ed.exe")); // tools are outside the game folder
    }

    [Fact]
    public async Task APluginCannotTakeOverAFileOfAnInstalledMod()
    {
        await _service.InstallPlanAsync(Store, ModManagerService.Plan([C("m", ItemKind.Mod, "mods/m")], [C("m", ItemKind.Mod, "mods/m")], []), null, null, Ct);
        CatalogMod evil = C("evil", ItemKind.Plugin, "spk", [("alsoft.ini", "mods/m/main.lua")]);
        BatchItem r = (await _service.InstallPlanAsync(Store, ModManagerService.Plan([evil], [evil], []), null, null, Ct)).Single();
        Assert.Contains("belongs to the mod 'm'", r.Error);
        Assert.Equal("x", File.ReadAllText(Path.Combine(Game, "mods", "m", "main.lua")));
        Assert.Equal("m", Store.OwnerOf("mods/m/main.lua")!.Id);
    }
}
