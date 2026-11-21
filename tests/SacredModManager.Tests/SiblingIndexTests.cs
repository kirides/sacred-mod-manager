using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

/// <summary>
/// The sibling-file pattern: a main mod-index.json lists <c>{ "url": "sacred-dsoal.json" }</c>; the second file lives beside it and holds the plugins.
/// Everything stays declarative; a <c>requires</c> id is resolved across all loaded indexes.
/// </summary>
public sealed class SiblingIndexTests : IDisposable
{
    private const string Main = "https://example.com/repo/mod-index.json";
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private const string MainJson = """
        {"schema":1,"name":"Main","indexes":[{"url":"sacred-dsoal.json"}],
         "download":{"url":"main.zip"},
         "mods":[{"id":"my-mod","path":"mods/my-mod","requires":["sacred-dsoal-speakers"]}]}
        """;

    private const string SubJson = """
        {"schema":1,"name":"DSOAL","download":{"url":"dsoal.zip"},"mods":[
          {"id":"sacred-dsoal","kind":"plugin","path":"base","install":{"files":["dsound.dll","dsoal-aldrv.dll"]}},
          {"id":"sacred-dsoal-speakers","kind":"plugin","path":"speakers","requires":["sacred-dsoal"],"install":{"files":["alsoft.ini"]}},
          {"id":"sacred-dsoal-headphones","kind":"plugin","path":"headphones","requires":["sacred-dsoal"],"install":{"files":["alsoft.ini"]}}]}
        """;

    private static byte[] DsoalZip() => Pack.Zip(("base/dsound.dll", "D"), ("base/dsoal-aldrv.dll", "A"), ("speakers/alsoft.ini", "SPK"), ("headphones/alsoft.ini", "HP"));

    private static string[] Ids(InstallPlan p) => p.Install.Select(m => m.Id).ToArray();

    [Fact]
    public async Task ARemoteSiblingResolvesAgainstTheListingIndexAndBringsItsOwnRelativeDownload()
    {
        var f = new FakeFetcher().Add(Main, MainJson).Add("https://example.com/repo/sacred-dsoal.json", SubJson);
        IndexCatalog c = await new IndexLoader(f, "win-x64").LoadAsync([new Uri(Main)], TestContext.Current.CancellationToken);

        Assert.Empty(c.Warnings);
        Assert.Equal(["my-mod", "sacred-dsoal", "sacred-dsoal-headphones", "sacred-dsoal-speakers"], c.Mods.Select(m => m.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("https://example.com/repo/main.zip", c.Mods.Single(m => m.Id == "my-mod").DownloadUrl.AbsoluteUri);
        Assert.Equal("https://example.com/repo/dsoal.zip", c.Mods.Single(m => m.Id == "sacred-dsoal").DownloadUrl.AbsoluteUri);

        // a requires id names a plugin of the other file; the chain base -> speakers -> my-mod is planned across both
        CatalogMod mod = c.Mods.Single(m => m.Id == "my-mod");
        Assert.Equal(["sacred-dsoal", "sacred-dsoal-speakers", "my-mod"], Ids(ModManagerService.Plan([mod], c.Mods, [])));
        CatalogMod hp = c.Mods.Single(m => m.Id == "sacred-dsoal-headphones");
        Assert.Equal(["sacred-dsoal", "sacred-dsoal-headphones"], Ids(ModManagerService.Plan([hp], c.Mods, [])));
        Assert.Equal(["sacred-dsoal-headphones"], Ids(ModManagerService.Plan([hp], c.Mods, ["sacred-dsoal"])));
    }

    [Fact]
    public async Task NestedSiblingsResolveStepByStepWithDotDotAndSubfolders()
    {
        var f = new FakeFetcher()
            .Add(Main, """{"indexes":[{"url":"deps/a.json"}]}""")
            .Add("https://example.com/repo/deps/a.json", """{"indexes":[{"url":"b.json"},{"url":"../top.json"}],"mods":[{"id":"a","path":"a","download":{"url":"a.zip"}}]}""")
            .Add("https://example.com/repo/deps/b.json", """{"mods":[{"id":"b","path":"b","download":{"url":"sub/b.zip"}}]}""")
            .Add("https://example.com/repo/top.json", """{"mods":[{"id":"t","path":"t","download":{"url":"t.zip"}}]}""");
        IndexCatalog c = await new IndexLoader(f, "win-x64").LoadAsync([new Uri(Main)], TestContext.Current.CancellationToken);
        Assert.Empty(c.Warnings);
        Assert.Equal("https://example.com/repo/deps/a.zip", c.Mods.Single(m => m.Id == "a").DownloadUrl.AbsoluteUri);
        Assert.Equal("https://example.com/repo/deps/sub/b.zip", c.Mods.Single(m => m.Id == "b").DownloadUrl.AbsoluteUri);
        Assert.Equal("https://example.com/repo/t.zip", c.Mods.Single(m => m.Id == "t").DownloadUrl.AbsoluteUri);
        Assert.Equal([0, 1, 2, 2], c.Indexes.Select(i => i.Depth).ToArray());
    }

    [Fact]
    public async Task SiblingsStillRespectCyclesDuplicatesAndTheDepthLimit()
    {
        var f = new FakeFetcher()
            .Add(Main, """{"indexes":[{"url":"x.json"},{"url":"x.json"},{"url":"./x.json"}]}""")
            .Add("https://example.com/repo/x.json", """{"indexes":[{"url":"mod-index.json"},{"url":"x.json"}],"mods":[{"id":"x","path":"x","download":{"url":"x.zip"}}]}""");
        IndexCatalog c = await new IndexLoader(f, "win-x64").LoadAsync([new Uri(Main)], TestContext.Current.CancellationToken);
        Assert.Equal(1, f.CountCalls(Main));
        Assert.Equal(1, f.CountCalls("https://example.com/repo/x.json"));
        Assert.Single(c.Mods);

        // a chain longer than the limit stops with a warning
        var chain = new FakeFetcher();
        for (int i = 0; i <= IndexLoader.MaxDepth + 2; i++)
        {
            chain.Add(i == 0 ? Main : $"https://example.com/repo/c{i}.json", $$"""{"indexes":[{"url":"c{{i + 1}}.json"}]}""");
        }

        IndexCatalog deep = await new IndexLoader(chain, "win-x64").LoadAsync([new Uri(Main)], TestContext.Current.CancellationToken);
        Assert.Contains(deep.Warnings, w => w.Contains("nested deeper", StringComparison.Ordinal));
        Assert.True(deep.Indexes.Max(i => i.Depth) <= IndexLoader.MaxDepth);
    }

    [Fact]
    public async Task APlainFileOnDiskWithSiblingFilesInstallsEndToEnd()
    {
        string repo = _root.Combine("repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "mod-index.json"), MainJson.Replace("\"mods\":[{\"id\":\"my-mod\",\"path\":\"mods/my-mod\",\"requires\":[\"sacred-dsoal-speakers\"]}]", "\"mods\":[]"));
        File.WriteAllText(Path.Combine(repo, "sacred-dsoal.json"), SubJson);
        File.WriteAllBytes(Path.Combine(repo, "dsoal.zip"), DsoalZip());

        var paths = new AppPaths(_root.Combine("home"));
        using var fetcher = new Fetcher();
        var service = new ModManagerService(paths, fetcher);
        IndexCatalog c = await service.LoadCatalogAsync([Path.Combine(repo, "mod-index.json")], TestContext.Current.CancellationToken);
        Assert.Empty(c.Warnings);
        Assert.Equal(3, c.Mods.Count);

        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        var store = new ModStore(game, paths.Backups);
        CatalogMod speakers = c.Mods.Single(m => m.Id == "sacred-dsoal-speakers");
        InstallPlan plan = ModManagerService.Plan([speakers], c.Mods, []);
        Assert.Equal(["sacred-dsoal", "sacred-dsoal-speakers"], Ids(plan));
        List<BatchItem> results = await service.InstallPlanAsync(store, plan, null, null, TestContext.Current.CancellationToken);
        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal("D", File.ReadAllText(Path.Combine(game, "dsound.dll")));
        Assert.Equal("SPK", File.ReadAllText(Path.Combine(game, "alsoft.ini")));
    }
}
