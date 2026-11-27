using System.Text;
using SacredModManager.Core.Index;
using Xunit;

namespace SacredModManager.Tests;

public class IndexLoaderTests
{
    private const string Root = "https://example.com/idx/mod-index.json";

    private static string Mod(string id, string path = "", string extra = "")
        => "{\"id\":\"" + id + "\",\"path\":\"" + (path.Length == 0 ? "mods/" + id : path) + "\"" + (extra.Length == 0 ? "" : "," + extra) + "}";

    private static string Index(string mods = "", string refs = "", string download = "\"download\":{\"url\":\"pkg.zip\"}")
        => "{\"schema\":1," + download + ",\"mods\":[" + mods + "],\"indexes\":[" + refs + "]}";

    private static string Ref(string url) => "{\"url\":\"" + url + "\"}";

    private static Task<IndexCatalog> Load(FakeFetcher f, params string[] roots)
        => new IndexLoader(f).LoadAsync(roots.Select(r => new Uri(r)));

    [Fact]
    public void ParsesAFullIndex()
    {
        ModIndex i = IndexJson.Parse(Encoding.UTF8.GetBytes("""
            {
              "schema": 1, "name": "N", "author": "A", "description": "D", "homepage": "https://h",
              "download": {"url": "pkg.zip", "version": "1.2", "sha256": "ABC"},
              "mods": [{"id": "bounty", "path": "mods/bounty", "name": "Bounty", "version": "0.4.0", "author": "me", "description": "x",
                        "tags": ["A", "b"], "thumbnail": "t.png", "requires": ["core"], "defaultEnabled": false,
                        "download": {"url": "other.zip"}}],
              "indexes": [{"url": "sub/mod-index.json", "name": "Sub"}]
            }
            """));
        Assert.Equal(1, i.Schema);
        Assert.Equal("N", i.Name);
        Assert.Equal("pkg.zip", i.Download!.Url);
        Assert.Equal("1.2", i.Download.Version);
        IndexMod m = Assert.Single(i.Mods!);
        Assert.Equal("bounty", m.Id);
        Assert.Equal(false, m.DefaultEnabled);
        Assert.Equal("other.zip", m.Download!.Url);
        Assert.Equal("Sub", Assert.Single(i.Indexes!).Name);
    }

    [Fact]
    public void ParseToleratesCommentsTrailingCommasAndUnknownFields()
    {
        ModIndex i = IndexJson.Parse(Encoding.UTF8.GetBytes("""
            { // comment
              "schema": 1, "future": {"x": [1,2,3]}, "mods": [{"id": "a", "path": "a", "colour": "red",},],
            }
            """));
        Assert.Equal("a", Assert.Single(i.Mods!).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[1,2]")]
    public void ParseRejectsGarbage(string text)
        => Assert.Throws<IndexException>(() => IndexJson.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void ToJsonRoundTrips()
    {
        var idx = new ModIndex { Name = "x", Mods = [new IndexMod { Id = "a", Path = "a" }] };
        ModIndex back = IndexJson.Parse(Encoding.UTF8.GetBytes(IndexJson.ToJson(idx)));
        Assert.Equal("x", back.Name);
        Assert.Equal("a", back.Mods![0].Id);
        Assert.DoesNotContain("null", IndexJson.ToJson(idx));
    }

    [Fact]
    public async Task LoadsMetadataAndResolvesRelativeDownload()
    {
        var f = new FakeFetcher().Add(Root, """
            {"schema":1,"name":" My index ","author":"Au","download":{"url":"../pkgs/all.zip","version":"2.0","sha256":"ABCDEF"},
             "mods":[{"id":"bounty","path":"\\mods\\bounty/","name":" Bounty ","version":"0.4.0","tags":["Fun","fun"," ",""],"requires":["core","bad id","core"],
                      "thumbnail":"mods/bounty/t.png"}]}
            """);
        IndexCatalog c = await Load(f, Root);
        CatalogMod m = Assert.Single(c.Mods);
        Assert.Equal("https://example.com/pkgs/all.zip", m.DownloadUrl.AbsoluteUri);
        Assert.Equal("abcdef", m.DownloadSha256);
        Assert.Equal("2.0", m.PackageVersion);
        Assert.Equal("mods/bounty", m.Path);
        Assert.Equal("bounty", m.FolderName);
        Assert.Equal("Bounty", m.Name);
        Assert.Equal(["fun"], m.Tags);
        Assert.Equal(["core", "sacred-gear"], m.Requires);
        Assert.Equal("mods/bounty/t.png", m.ThumbnailPath);
        Assert.Null(m.ThumbnailUrl);
        Assert.True(m.DefaultEnabled);
        Assert.Equal("My index", m.IndexName);
        LoadedIndex li = Assert.Single(c.Indexes);
        Assert.Equal(1, li.ModCount);
        Assert.Equal("Au", li.Author);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public async Task SchemaOtherThanOneIsRejectedWithAWarning()
    {
        var f = new FakeFetcher().Add(Root, "{\"schema\":2,\"mods\":[" + Mod("a") + "],\"download\":{\"url\":\"p.zip\"}}");
        IndexCatalog c = await Load(f, Root);
        Assert.Empty(c.Mods);
        Assert.Contains("schema 2", Assert.Single(c.Warnings));
        Assert.NotNull(Assert.Single(c.Indexes).Error);
    }

    [Fact]
    public async Task MissingSchemaDefaultsToOne()
    {
        var f = new FakeFetcher().Add(Root, "{\"download\":{\"url\":\"p.zip\"},\"mods\":[" + Mod("a") + "]}");
        Assert.Single((await Load(f, Root)).Mods);
    }

    [Fact]
    public async Task RelativeIndexUrlsResolveAgainstTheListingIndex()
    {
        var f = new FakeFetcher()
            .Add(Root, Index(refs: Ref("sub/mod-index.json") + "," + Ref("/abs/mod-index.json") + "," + Ref("https://other.org/x/mod-index.json")))
            .Add("https://example.com/idx/sub/mod-index.json", Index(Mod("a"), Ref("../up/mod-index.json")))
            .Add("https://example.com/abs/mod-index.json", Index(Mod("b")))
            .Add("https://other.org/x/mod-index.json", Index(Mod("c")))
            .Add("https://example.com/idx/up/mod-index.json", Index(Mod("d")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal(["a", "b", "c", "d"], c.Mods.Select(m => m.Id).Order().ToArray());
        CatalogMod a = c.Mods.Single(m => m.Id == "a");
        Assert.Equal("https://example.com/idx/sub/pkg.zip", a.DownloadUrl.AbsoluteUri);
        Assert.Equal("https://example.com/idx/sub/mod-index.json", a.IndexUrl.AbsoluteUri);
        Assert.Equal("https://example.com/idx/up/pkg.zip", c.Mods.Single(m => m.Id == "d").DownloadUrl.AbsoluteUri);
        Assert.Empty(c.Warnings);
        LoadedIndex sub = c.Indexes.Single(x => x.Url.AbsoluteUri.EndsWith("/sub/mod-index.json", StringComparison.Ordinal));
        Assert.Equal(Root, sub.ParentUrl!.AbsoluteUri);
        Assert.Equal(1, sub.Depth);
    }

    [Fact]
    public async Task MetaIndexWithOwnModsAndReferences()
    {
        var f = new FakeFetcher()
            .Add(Root, Index(Mod("own"), Ref("child/mod-index.json")))
            .Add("https://example.com/idx/child/mod-index.json", Index(Mod("kid"), download: "\"download\":{\"url\":\"kid.zip\"}"));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal(["own", "kid"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Equal(2, c.Indexes.Count);
        Assert.Equal("https://example.com/idx/pkg.zip", c.Mods[0].DownloadUrl.AbsoluteUri);
        Assert.Equal("https://example.com/idx/child/kid.zip", c.Mods[1].DownloadUrl.AbsoluteUri);
    }

    [Fact]
    public async Task IndexWithOnlyReferencesHasNoMods()
    {
        var f = new FakeFetcher()
            .Add(Root, "{\"schema\":1,\"indexes\":[" + Ref("a/mod-index.json") + "]}")
            .Add("https://example.com/idx/a/mod-index.json", Index(Mod("x")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal("x", Assert.Single(c.Mods).Id);
    }

    [Fact]
    public async Task RecursionFollowsSeveralLevels()
    {
        var f = new FakeFetcher().Add(Root, Index(Mod("m0"), Ref("n1/mod-index.json")))
            .Add("https://example.com/idx/n1/mod-index.json", Index(Mod("m1"), Ref("n2/mod-index.json")))
            .Add("https://example.com/idx/n1/n2/mod-index.json", Index(Mod("m2")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal(["m0", "m1", "m2"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Equal([0, 1, 2], c.Indexes.Select(i => i.Depth).ToArray());
    }

    [Fact]
    public async Task SameIndexReferencedTwiceIsFetchedOnce()
    {
        string shared = "https://example.com/shared/mod-index.json";
        var f = new FakeFetcher()
            .Add(Root, Index(refs: Ref("a/mod-index.json") + "," + Ref("b/mod-index.json") + "," + Ref(shared)))
            .Add("https://example.com/idx/a/mod-index.json", Index(Mod("a"), Ref(shared)))
            .Add("https://example.com/idx/b/mod-index.json", Index(Mod("b"), Ref(shared + "#frag")))
            .Add(shared, Index(Mod("s")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal(1, f.CountCalls(shared));
        Assert.Equal(4, f.Calls.Count);
        Assert.Equal(3, c.Mods.Count(m => m.Id is "a" or "b" or "s"));
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public async Task CyclesTerminateAndFetchEachIndexOnce()
    {
        string a = "https://example.com/a/mod-index.json", b = "https://example.com/b/mod-index.json";
        var f = new FakeFetcher().Add(a, Index(Mod("ma"), Ref(b) + "," + Ref(a))).Add(b, Index(Mod("mb"), Ref(a)));
        IndexCatalog c = await Load(f, a);
        Assert.Equal(1, f.CountCalls(a));
        Assert.Equal(1, f.CountCalls(b));
        Assert.Equal(["ma", "mb"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public async Task SelfReferenceIsFetchedOnce()
    {
        var f = new FakeFetcher().Add(Root, Index(Mod("a"), Ref("mod-index.json")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal(1, f.CountCalls(Root));
        Assert.Single(c.Mods);
    }

    [Fact]
    public async Task DepthCapStopsLongChainsWithAWarning()
    {
        var f = new FakeFetcher();
        int chain = IndexLoader.MaxDepth + 4;
        for (int i = 0; i < chain; i++)
        {
            f.Add($"https://example.com/c/{i}.json", Index(Mod("m" + i), i + 1 < chain ? Ref($"{i + 1}.json") : ""));
        }

        IndexCatalog c = await Load(f, "https://example.com/c/0.json");
        Assert.Equal(IndexLoader.MaxDepth + 1, c.Indexes.Count);
        Assert.Equal(IndexLoader.MaxDepth, c.Indexes.Max(i => i.Depth));
        Assert.Equal(0, f.CountCalls($"https://example.com/c/{IndexLoader.MaxDepth + 1}.json"));
        Assert.Contains(c.Warnings, w => w.Contains("deeper than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IndexCountCapStopsWideFanOut()
    {
        var f = new FakeFetcher();
        int n = IndexLoader.MaxIndexes + 20;
        f.Add(Root, Index(refs: string.Join(",", Enumerable.Range(0, n).Select(i => Ref($"k{i}/mod-index.json")))));
        for (int i = 0; i < n; i++)
        {
            f.Add($"https://example.com/idx/k{i}/mod-index.json", Index(Mod("m" + i)));
        }

        IndexCatalog c = await Load(f, Root);
        Assert.Equal(IndexLoader.MaxIndexes, c.Indexes.Count);
        Assert.Equal(IndexLoader.MaxIndexes, f.Calls.Count);
        Assert.Contains(c.Warnings, w => w.Contains(IndexLoader.MaxIndexes.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailingSubIndexWarnsAndTheRestLoads()
    {
        var f = new FakeFetcher()
            .Add(Root, Index(Mod("a"), Ref("gone/mod-index.json") + "," + Ref("bad/mod-index.json") + "," + Ref("ok/mod-index.json")))
            .Add("https://example.com/idx/bad/mod-index.json", "this is not json")
            .Add("https://example.com/idx/ok/mod-index.json", Index(Mod("b")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal(["a", "b"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Equal(2, c.Warnings.Count);
        Assert.Contains(c.Warnings, w => w.Contains("404", StringComparison.Ordinal));
        Assert.Equal(2, c.Indexes.Count(i => i.Error is not null));
    }

    [Fact]
    public async Task FailingRootStillGivesACatalog()
    {
        IndexCatalog c = await Load(new FakeFetcher(), Root);
        Assert.Empty(c.Mods);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public async Task DuplicateModIdFirstInTraversalOrderWins()
    {
        var f = new FakeFetcher()
            .Add(Root, Index(Mod("dup", extra: "\"version\":\"1\""), Ref("a/mod-index.json") + "," + Ref("b/mod-index.json")))
            .Add("https://example.com/idx/a/mod-index.json", Index(Mod("dup", extra: "\"version\":\"2\"") + "," + Mod("onlya")))
            .Add("https://example.com/idx/b/mod-index.json", Index(Mod("DUP", extra: "\"version\":\"3\"")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal("1", c.Mods.Single(m => m.Id == "dup").Version);
        Assert.Contains(c.Mods, m => m.Id == "onlya");
        Assert.Single(c.Mods, m => m.Id.Equals("dup", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, c.Warnings.Count(w => w.Contains("already offered", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DuplicateIdWithinOneIndexKeepsTheFirst()
    {
        var f = new FakeFetcher().Add(Root, Index(Mod("a", extra: "\"version\":\"1\"") + "," + Mod("a", extra: "\"version\":\"2\"")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal("1", Assert.Single(c.Mods).Version);
        Assert.Single(c.Warnings);
    }

    [Theory]
    [InlineData("{\"id\":\"bad id\",\"path\":\"mods/x\"}")]
    [InlineData("{\"id\":\"\",\"path\":\"mods/x\"}")]
    [InlineData("{\"path\":\"mods/x\"}")]
    [InlineData("{\"id\":\"../x\",\"path\":\"mods/x\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"../x\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"mods/../../x\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"mods/./x\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"mods//x\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"/\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"C:\\\\x\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"C:/x\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"..\\\\x\"}")]
    [InlineData("{\"id\":\"ok\"}")]
    [InlineData("{\"id\":\"ok\",\"path\":\"  \"}")]
    public async Task InvalidEntriesAreSkippedWithAWarning(string entry)
    {
        var f = new FakeFetcher().Add(Root, Index(entry + "," + Mod("good")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal("good", Assert.Single(c.Mods).Id);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public async Task AbsolutePathIsRejectedWhenSlashLeadingIsOnlyStripped()
    {
        // a leading slash is stripped (zip paths are always relative), so "/mods/x" means "mods/x"
        var f = new FakeFetcher().Add(Root, Index("{\"id\":\"x\",\"path\":\"/mods/x\"}"));
        Assert.Equal("mods/x", Assert.Single((await Load(f, Root)).Mods).Path);
    }

    [Fact]
    public async Task EntryWithoutAnyDownloadIsSkipped()
    {
        var f = new FakeFetcher().Add(Root, "{\"schema\":1,\"mods\":[" + Mod("a") + "]}");
        IndexCatalog c = await Load(f, Root);
        Assert.Empty(c.Mods);
        Assert.Contains("no usable download", Assert.Single(c.Warnings));
    }

    [Theory]
    [InlineData("http://example.com/p.zip")]
    [InlineData("ftp://example.com/p.zip")]
    public async Task InsecureDownloadSchemesAreSkipped(string url)
    {
        var f = new FakeFetcher().Add(Root, Index(Mod("a"), download: "\"download\":{\"url\":\"" + url + "\"}"));
        Assert.Empty((await Load(f, Root)).Mods);
    }

    [Fact]
    public async Task ModDownloadOverridesIndexDownload()
    {
        var f = new FakeFetcher().Add(Root, Index(
            Mod("a", extra: "\"download\":{\"url\":\"https://cdn.example.net/a.zip\",\"sha256\":\"AA\",\"version\":\"9\"}") + "," + Mod("b") + "," + Mod("c", extra: "\"download\":{}"),
            download: "\"download\":{\"url\":\"all.zip\",\"sha256\":\"bb\"}"));
        IndexCatalog c = await Load(f, Root);
        CatalogMod a = c.Mods.Single(m => m.Id == "a"), b = c.Mods.Single(m => m.Id == "b"), cc = c.Mods.Single(m => m.Id == "c");
        Assert.Equal("https://cdn.example.net/a.zip", a.DownloadUrl.AbsoluteUri);
        Assert.Equal("aa", a.DownloadSha256);
        Assert.Equal("9", a.PackageVersion);
        Assert.Equal("https://example.com/idx/all.zip", b.DownloadUrl.AbsoluteUri);
        Assert.Equal("bb", b.DownloadSha256);
        Assert.Equal("https://example.com/idx/all.zip", cc.DownloadUrl.AbsoluteUri);
    }

    [Fact]
    public async Task ThumbnailHttpsUrlZipPathAndIgnoredHttp()
    {
        var f = new FakeFetcher().Add(Root, Index(
            Mod("a", extra: "\"thumbnail\":\"https://img.example.com/a.png\"") + ","
            + Mod("b", extra: "\"thumbnail\":\"mods/b/shot.png\"") + ","
            + Mod("c", extra: "\"thumbnail\":\"http://img.example.com/c.png\"") + ","
            + Mod("d", extra: "\"thumbnail\":\"../x.png\"") + ","
            + Mod("e")));
        IndexCatalog c = await Load(f, Root);
        CatalogMod Get(string id) => c.Mods.Single(m => m.Id == id);
        Assert.Equal("https://img.example.com/a.png", Get("a").ThumbnailUrl!.AbsoluteUri);
        Assert.Null(Get("a").ThumbnailPath);
        Assert.Equal("mods/b/shot.png", Get("b").ThumbnailPath);
        Assert.Null(Get("b").ThumbnailUrl);
        Assert.Null(Get("c").ThumbnailUrl);
        Assert.Null(Get("c").ThumbnailPath);
        Assert.Null(Get("d").ThumbnailPath);
        Assert.Null(Get("e").ThumbnailPath);
        Assert.Equal(5, c.Mods.Count);
    }

    [Fact]
    public async Task RootsThatNormalizeToTheSameUrlAreLoadedOnce()
    {
        var f = new FakeFetcher().Add("https://example.com/idx/mod-index.json", Index(Mod("a")));
        IndexCatalog c = await Load(f, "https://EXAMPLE.com/idx/mod-index.json", "https://example.com/idx/mod-index.json#frag", "https://example.com/idx/mod-index.json");
        Assert.Single(f.Calls);
        Assert.Single(c.Mods);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public async Task SeveralRootsAreAllLoadedInOrder()
    {
        var f = new FakeFetcher().Add("https://a.example/mod-index.json", Index(Mod("a"))).Add("https://b.example/mod-index.json", Index(Mod("b")));
        IndexCatalog c = await Load(f, "https://a.example/mod-index.json", "https://b.example/mod-index.json");
        Assert.Equal(["a", "b"], c.Mods.Select(m => m.Id).ToArray());
        Assert.All(c.Indexes, i => Assert.Null(i.ParentUrl));
    }

    [Fact]
    public async Task GitHubRepositoryReferenceIsExpandedToTheRawIndex()
    {
        var f = new FakeFetcher()
            .Add(Root, Index(refs: Ref("https://github.com/owner/repo")))
            .Add("https://raw.githubusercontent.com/owner/repo/HEAD/mod-index.json", Index(Mod("gh")));
        IndexCatalog c = await Load(f, Root);
        Assert.Equal("gh", Assert.Single(c.Mods).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://insecure.example.com/mod-index.json")]
    [InlineData("ftp://x/y")]
    public async Task UnusableReferenceIsSkippedWithAWarning(string url)
    {
        var f = new FakeFetcher().Add(Root, Index(Mod("a"), Ref(url)));
        IndexCatalog c = await Load(f, Root);
        Assert.Single(c.Mods);
        Assert.Contains("unusable address", Assert.Single(c.Warnings));
        Assert.Single(f.Calls);
    }

    [Fact]
    public async Task OversizedIndexIsReportedNotThrown()
    {
        var f = new FakeFetcher().Add(Root, new byte[(int)IndexLoader.MaxIndexBytes + 1]);
        IndexCatalog c = await Load(f, Root);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var f = new FakeFetcher { Delay = TimeSpan.FromSeconds(5) }.Add(Root, Index(Mod("a")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IndexLoader(f).LoadAsync([new Uri(Root)], cts.Token));
    }

    [Fact]
    public async Task LoadsFromLocalFileUrls()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("mod-index.json"), Index(Mod("local")));
        var uri = new Uri(dir.Combine("mod-index.json"));
        IndexCatalog c = await new IndexLoader(new Fetcher()).LoadAsync([uri], TestContext.Current.CancellationToken);
        Assert.Equal("local", Assert.Single(c.Mods).Id);
        Assert.True(Assert.Single(c.Mods).DownloadUrl.IsFile);
    }

    [Theory]
    [InlineData("mods/a", "mods/a")]
    [InlineData("\\mods\\a\\", "mods/a")]
    [InlineData(" a ", "a")]
    [InlineData("a/b/c", "a/b/c")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("a/../b", null)]
    [InlineData("a/./b", null)]
    [InlineData("a//b", null)]
    [InlineData("c:/a", null)]
    [InlineData("a:b", null)]
    public void CleanPath(string? input, string? expected) => Assert.Equal(expected, IndexLoader.CleanPath(input));
}
