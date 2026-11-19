using System.Text;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class IndexLoaderKindsTests
{
    private const string Root = "https://example.com/idx/mod-index.json";
    private const string Pkg = "\"download\":{\"url\":\"pkg.zip\"}";

    private static string Index(string mods, string download = Pkg)
        => "{\"schema\":1," + (download.Length == 0 ? "" : download + ",") + "\"mods\":[" + mods + "]}";

    private static async Task<IndexCatalog> Load(string json, string platform = "win-x64", FakeFetcher? f = null)
    {
        f ??= new FakeFetcher();
        f.Add(Root, json);
        return await new IndexLoader(f, platform).LoadAsync([new Uri(Root)], TestContext.Current.CancellationToken);
    }

    private static string Warn(IndexCatalog c) => string.Join("\n", c.Warnings);

    // ---- kinds -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ModsWithoutKindStayMods()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"a\",\"path\":\"mods/a\"}"));
        CatalogMod m = Assert.Single(c.Mods);
        Assert.Equal(ItemKind.Mod, m.Kind);
        Assert.Equal("mods/a", m.Path);
        Assert.Equal("a", m.FolderName);
        Assert.Equal("", m.InstallTo);
        Assert.Empty(m.InstallFiles);
        Assert.Null(m.Launch);
        Assert.Equal("Mod", m.KindLabel());
    }

    [Fact]
    public async Task PluginReadsInstallFilesAndTo()
    {
        IndexCatalog c = await Load(Index("""
            {"id":"bild","kind":"Plugin","path":"bild\\sub/","install":{"to":"\\dir/","files":["a.dll",{"from":"bin\\b.dll","to":"c.dll"},{"from":"d"}]}}
            """));
        CatalogMod m = Assert.Single(c.Mods);
        Assert.Equal(ItemKind.Plugin, m.Kind);
        Assert.Equal("bild/sub", m.Path);
        Assert.Equal("dir", m.InstallTo);
        Assert.Equal([("a.dll", "a.dll"), ("bin/b.dll", "c.dll"), ("d", "d")], m.InstallFiles.ToArray());
        Assert.Equal("Plugin", m.KindLabel());
    }

    [Theory]
    [InlineData("\"path\":\"\"")]
    [InlineData("\"path\":\".\"")]
    [InlineData("\"path\":\"/\"")]
    [InlineData("\"path\":\"  \"")]
    [InlineData("\"x\":1")]
    public async Task PluginAndToolWithoutAPathUseTheZipRoot(string pathPart)
    {
        IndexCatalog c = await Load(Index("{\"id\":\"p\",\"kind\":\"plugin\"," + pathPart + "},{\"id\":\"t\",\"kind\":\"tool\"," + pathPart + "}"));
        Assert.Equal(2, c.Mods.Count);
        Assert.All(c.Mods, m => Assert.Equal("", m.Path));
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public async Task ToolReadsLaunch()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"ed\",\"kind\":\"tool\",\"install\":{\"launch\":\"bin\\\\Ed.exe\"}}"));
        CatalogMod m = Assert.Single(c.Mods);
        Assert.Equal(ItemKind.Tool, m.Kind);
        Assert.Equal("bin/Ed.exe", m.Launch);
        Assert.Equal("", m.Path);
    }

    [Fact]
    public async Task ToolWithoutLaunchIsFine()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"ed\",\"kind\":\"tool\"}"));
        Assert.Null(Assert.Single(c.Mods).Launch);
    }

    [Fact]
    public async Task LaunchAndFilesOfTheWrongKindAreIgnored()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"m\",\"path\":\"m\",\"install\":{\"launch\":\"x.exe\",\"files\":[\"a\"],\"to\":\"t\"}},{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"launch\":\"x.exe\"}}"));
        Assert.All(c.Mods, m => Assert.Null(m.Launch));
        Assert.Empty(c.Mods[0].InstallFiles);
        Assert.Equal("", c.Mods[0].InstallTo);
    }

    [Fact]
    public async Task UnknownKindIsSkippedWithAWarning()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"a\",\"kind\":\"addon\",\"path\":\"a\"},{\"id\":\"b\",\"path\":\"b\"}"));
        Assert.Equal(["b"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Contains("unknown kind 'addon'", Warn(c));
        Assert.Contains("'a'", Warn(c));
        Assert.Equal(1, c.Indexes[0].ModCount);
    }

    [Theory]
    [InlineData("{\"id\":\"a\"}")]
    [InlineData("{\"id\":\"a\",\"path\":\"\"}")]
    [InlineData("{\"id\":\"a\",\"path\":\"../x\"}")]
    [InlineData("{\"id\":\"a\",\"kind\":\"mod\"}")]
    public async Task ModWithoutAValidPathIsStillRejected(string entry)
    {
        IndexCatalog c = await Load(Index(entry));
        Assert.Empty(c.Mods);
        Assert.Contains("no valid path", Warn(c));
    }

    [Theory]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"path\":\"../x\"}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"path\":\"a/../../x\"}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"path\":\"C:/x\"}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"to\":\"..\"}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"to\":\"a/../../b\"}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"to\":\"C:\\\\Windows\"}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[\"../evil.dll\"]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[\"..\\\\evil.dll\"]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[\"C:/evil.dll\"]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[{\"from\":\"a.dll\",\"to\":\"../evil.dll\"}]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[{\"from\":\"a.dll\",\"to\":\"d:evil.dll\"}]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[{\"from\":\"a.dll\",\"to\":\"\"}]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[\"a/./b.dll\"]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[5]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[null]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[{}]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[\"\"]}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"tool\",\"install\":{\"launch\":\"../x.exe\"}}")]
    [InlineData("{\"id\":\"p\",\"kind\":\"tool\",\"install\":{\"launch\":\"C:/x.exe\"}}")]
    public async Task UnsafeInstallPathsAreRejectedWithAWarning(string entry)
    {
        IndexCatalog c = await Load(Index(entry + ",{\"id\":\"ok\",\"path\":\"ok\"}"));
        Assert.Equal(["ok"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Contains("skipped 'p'", Warn(c));
    }

    [Fact]
    public async Task LeadingSlashOfAnInstallPathIsTrimmedNotRejected()
    {
        // documented behaviour of CleanPath: "/dir" is read as "dir" below the game folder (an absolute path cannot be expressed)
        IndexCatalog c = await Load(Index("{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"to\":\"/etc\",\"files\":[\"/passwd\"]}}"));
        CatalogMod m = Assert.Single(c.Mods);
        Assert.Equal("etc", m.InstallTo);
        Assert.Equal([("passwd", "passwd")], m.InstallFiles.ToArray());
    }

    [Fact]
    public async Task InvalidIdIsRejectedForEveryKind()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"a b\",\"kind\":\"plugin\"},{\"id\":\"../x\",\"kind\":\"tool\"},{\"kind\":\"tool\"}"));
        Assert.Empty(c.Mods);
        Assert.Equal(3, c.Warnings.Count(w => w.Contains("no valid id")));
    }

    [Fact]
    public async Task RequiresAreKeptAcrossKinds()
    {
        IndexCatalog c = await Load(Index("""
            {"id":"m","path":"m"},
            {"id":"p","kind":"plugin","requires":["m","bad id","m"]},
            {"id":"t","kind":"tool","requires":["p","m"]}
            """));
        Assert.Empty(c.Mods.Single(m => m.Id == "m").Requires);
        Assert.Equal(["m"], c.Mods.Single(m => m.Id == "p").Requires.ToArray());
        Assert.Equal(["p", "m"], c.Mods.Single(m => m.Id == "t").Requires.ToArray());
    }

    [Fact]
    public async Task SameIdOfDifferentKindsIsOneEntry()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"x\",\"path\":\"x\"},{\"id\":\"x\",\"kind\":\"plugin\"}"));
        Assert.Equal(ItemKind.Mod, Assert.Single(c.Mods).Kind);
        Assert.Contains("already offered", Warn(c));
    }

    // ---- platforms ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EntriesForAnotherPlatformAreHiddenSilently()
    {
        string json = Index("""
            {"id":"w","kind":"tool","platforms":["win"]},
            {"id":"l","kind":"tool","platforms":["linux-x64"]},
            {"id":"any","kind":"plugin"},
            {"id":"both","path":"both","platforms":["osx","linux"]}
            """);
        IndexCatalog lin = await Load(json, "linux-x64");
        Assert.Equal(["l", "any", "both"], lin.Mods.Select(m => m.Id).ToArray());
        Assert.Empty(lin.Warnings);
        Assert.Equal(3, lin.Indexes[0].ModCount);

        IndexCatalog win = await Load(json, "win-x64");
        Assert.Equal(["w", "any"], win.Mods.Select(m => m.Id).ToArray());
        Assert.Empty(win.Warnings);
        Assert.Equal(2, win.Indexes[0].ModCount);

        IndexCatalog arm = await Load(json, "linux-arm64");
        Assert.Equal(["any", "both"], arm.Mods.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task PlatformsAreLowerCasedOnTheCatalogEntry()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"w\",\"kind\":\"tool\",\"platforms\":[\" WIN-x64 \",\"\"]}"), "win-x64");
        Assert.Equal(["win-x64"], Assert.Single(c.Mods).Platforms.ToArray());
    }

    [Fact]
    public async Task OneIdCanBeOfferedPerPlatform()
    {
        string json = Index("""
            {"id":"ed","kind":"tool","platforms":["win"],"name":"Win","install":{"launch":"Ed.exe"}},
            {"id":"ed","kind":"tool","platforms":["linux"],"name":"Lin","install":{"launch":"ed"}}
            """);
        CatalogMod lin = Assert.Single((await Load(json, "linux-x64")).Mods);
        Assert.Equal("Lin", lin.Name);
        Assert.Equal("ed", lin.Launch);
        CatalogMod win = Assert.Single((await Load(json, "win-x64")).Mods);
        Assert.Equal("Win", win.Name);
    }

    [Fact]
    public async Task AnInvalidEntryForAnotherPlatformStillWarns()
    {
        IndexCatalog c = await Load(Index("{\"id\":\"w\",\"kind\":\"plugin\",\"platforms\":[\"win\"],\"install\":{\"to\":\"..\"}}"), "linux-x64");
        Assert.Empty(c.Mods);
        Assert.Contains("skipped 'w'", Warn(c));
    }

    [Fact]
    public async Task DefaultPlatformIsTheCurrentOne()
    {
        var f = new FakeFetcher();
        string other = Core.App.PlatformInfo.CurrentRid.StartsWith("win", StringComparison.Ordinal) ? "linux" : "win";
        f.Add(Root, Index("{\"id\":\"here\",\"kind\":\"tool\",\"platforms\":[\"" + Core.App.PlatformInfo.CurrentRid + "\"]},{\"id\":\"there\",\"kind\":\"tool\",\"platforms\":[\"" + other + "\"]}"));
        IndexCatalog c = await new IndexLoader(f).LoadAsync([new Uri(Root)], TestContext.Current.CancellationToken);
        Assert.Equal(["here"], c.Mods.Select(m => m.Id).ToArray());
    }

    // ---- GitHub downloads --------------------------------------------------------------------------------------

    private const string Api = "https://api.github.com/repos/o/r/releases?per_page=30";

    private static string Asset(string name, string tag, string? digest = null)
        => "{\"name\":\"" + name + "\",\"browser_download_url\":\"https://github.com/o/r/releases/download/" + tag + "/" + name + "\"" + (digest is null ? "" : ",\"digest\":\"" + digest + "\"") + "}";

    private static string Release(string tag, string[] assets, bool draft = false, bool pre = false, string? digest = null)
        => "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") + ",\"assets\":[" + string.Join(",", assets.Select(a => Asset(a, tag, digest))) + "]}";

    private static FakeFetcher Releases(params string[] releases) => new FakeFetcher().Add(Api, "[" + string.Join(",", releases) + "]");

    private static string GhDownload(string asset = "Gear-*.zip", string repo = "o/r", string extra = "")
        => "\"download\":{\"github\":\"" + repo + "\",\"asset\":\"" + asset + "\"" + extra + "}";

    private static Task<IndexCatalog> LoadGh(FakeFetcher f, string mods, string download = Pkg)
        => Load(Index(mods, download), "win-x64", f);

    private const string GhMod = "{\"id\":\"g\",\"path\":\"g\"," + "\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\"}}";

    [Fact]
    public async Task GithubUsesTheNewestReleaseFirst()
    {
        FakeFetcher f = Releases(Release("v2.0", ["Gear-2.zip"]), Release("v1.0", ["Gear-1.zip"]));
        CatalogMod m = Assert.Single((await LoadGh(f, GhMod)).Mods);
        Assert.Equal("https://github.com/o/r/releases/download/v2.0/Gear-2.zip", m.DownloadUrl.AbsoluteUri);
        Assert.Equal("v2.0", m.PackageVersion);
        Assert.Equal("2.0", m.Version);
    }

    [Fact]
    public async Task GithubSkipsDraftsAndPrereleasesUnlessAsked()
    {
        FakeFetcher f = Releases(Release("v4", ["Gear-4.zip"], draft: true), Release("v3-rc", ["Gear-3.zip"], pre: true), Release("v2", ["Gear-2.zip"]));
        Assert.Equal("v2", Assert.Single((await LoadGh(f, GhMod)).Mods).PackageVersion);

        string withPre = "{\"id\":\"g\",\"path\":\"g\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\",\"prerelease\":true}}";
        Assert.Equal("v3-rc", Assert.Single((await LoadGh(f, withPre)).Mods).PackageVersion);

        string explicitFalse = "{\"id\":\"g\",\"path\":\"g\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\",\"prerelease\":false}}";
        Assert.Equal("v2", Assert.Single((await LoadGh(f, explicitFalse)).Mods).PackageVersion);
    }

    [Fact]
    public async Task GithubDraftIsSkippedEvenWhenPrereleasesAreWanted()
    {
        FakeFetcher f = Releases(Release("v4", ["Gear-4.zip"], draft: true, pre: true), Release("v1", ["Gear-1.zip"]));
        string m = "{\"id\":\"g\",\"path\":\"g\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\",\"prerelease\":true}}";
        Assert.Equal("v1", Assert.Single((await LoadGh(f, m)).Mods).PackageVersion);
    }

    [Fact]
    public async Task GithubOlderReleaseIsUsedWhenTheNewestHasNoMatchingAsset()
    {
        FakeFetcher f = Releases(Release("v3", ["notes.txt", "Other-3.zip"]), Release("v2", []), Release("v1", ["Gear-1.zip"]));
        CatalogMod m = Assert.Single((await LoadGh(f, GhMod)).Mods);
        Assert.Equal("v1", m.PackageVersion);
        Assert.EndsWith("/v1/Gear-1.zip", m.DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GithubTakesTheFirstMatchingAssetOfTheRelease()
    {
        FakeFetcher f = Releases(Release("v1", ["readme.md", "Gear-a.zip", "Gear-b.zip"]));
        Assert.EndsWith("/Gear-a.zip", Assert.Single((await LoadGh(f, GhMod)).Mods).DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Gear-*.zip", "gear-1.2.zip", true)]
    [InlineData("Gear-*.zip", "GEAR-.ZIP", true)]
    [InlineData("Gear-*.zip", "Gear-1.zip.sig", false)]
    [InlineData("Gear-*.zip", "xGear-1.zip", false)]
    [InlineData("Gear-*.zip", "Gear-1zip", false)]
    [InlineData("a?.zip", "ab.zip", true)]
    [InlineData("a?.zip", "a.zip", false)]
    [InlineData("a?.zip", "abc.zip", false)]
    [InlineData("a.zip", "abzip", false)]
    [InlineData("a.zip", "a.zip", true)]
    [InlineData("*", "anything.bin", true)]
    [InlineData("*-win-*.zip", "tool-win-x64.zip", true)]
    [InlineData("*-win-*.zip", "tool-linux-x64.zip", false)]
    [InlineData("a+b(1).zip", "a+b(1).zip", true)]
    [InlineData("a+b(1).zip", "aab1.zip", false)]
    [InlineData("[x].zip", "x.zip", false)]
    [InlineData("[x].zip", "[x].zip", true)]
    public void GlobMatching(string pattern, string name, bool expected)
        => Assert.Equal(expected, GitHubReleaseResolver.GlobToRegex(pattern).IsMatch(name));

    [Fact]
    public async Task GithubAssetPatternPicksTheRightAssetByWholeName()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip.sig", "xGear-1.zip", "GEAR-1.ZIP"]));
        Assert.EndsWith("/GEAR-1.ZIP", Assert.Single((await LoadGh(f, GhMod)).Mods).DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2", "1.2")]
    [InlineData("1.2", "1.2")]
    [InlineData("vv3", "3")]
    [InlineData("nightly", "nightly")]
    public async Task GithubTagBecomesPackageVersionAndTrimmedEntryVersion(string tag, string version)
    {
        CatalogMod m = Assert.Single((await LoadGh(Releases(Release(tag, ["Gear-1.zip"])), GhMod)).Mods);
        Assert.Equal(tag, m.PackageVersion);
        Assert.Equal(version, m.Version);
    }

    [Fact]
    public async Task GithubEntryVersionWinsOverTheTag()
    {
        string m = "{\"id\":\"g\",\"path\":\"g\",\"version\":\"9.9\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\"}}";
        CatalogMod mod = Assert.Single((await LoadGh(Releases(Release("v1.0", ["Gear-1.zip"])), m)).Mods);
        Assert.Equal("9.9", mod.Version);
        Assert.Equal("v1.0", mod.PackageVersion);
    }

    [Fact]
    public async Task GithubDigestBecomesTheChecksum()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"], digest: "sha256:ABCDEF0123"));
        Assert.Equal("abcdef0123", Assert.Single((await LoadGh(f, GhMod)).Mods).DownloadSha256);
    }

    [Theory]
    [InlineData("md5:abc")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task GithubOtherDigestsAreIgnored(string digest)
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"], digest: digest));
        Assert.Null(Assert.Single((await LoadGh(f, GhMod)).Mods).DownloadSha256);
    }

    [Fact]
    public async Task GithubWithoutDigestHasNoChecksum()
    {
        Assert.Null(Assert.Single((await LoadGh(Releases(Release("v1", ["Gear-1.zip"])), GhMod)).Mods).DownloadSha256);
    }

    [Fact]
    public async Task GithubIndexSha256WinsOverTheDigest()
    {
        string m = "{\"id\":\"g\",\"path\":\"g\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\",\"sha256\":\"AABB\"}}";
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"], digest: "sha256:ccdd"));
        Assert.Equal("aabb", Assert.Single((await LoadGh(f, m)).Mods).DownloadSha256);
    }

    [Fact]
    public async Task GithubDownloadVersionOfTheEntryIsOverwrittenByTheTag()
    {
        // download.version is documented for url downloads; with github the tag is the package version
        string m = "{\"id\":\"g\",\"path\":\"g\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\",\"version\":\"0.1\"}}";
        Assert.Equal("v5", Assert.Single((await LoadGh(Releases(Release("v5", ["Gear-1.zip"])), m)).Mods).PackageVersion);
    }

    [Fact]
    public async Task PerModGithubOverridesAnIndexLevelUrl()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"]));
        IndexCatalog c = await LoadGh(f, GhMod + ",{\"id\":\"plain\",\"path\":\"plain\"}");
        Assert.EndsWith("/Gear-1.zip", c.Mods[0].DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("https://example.com/idx/pkg.zip", c.Mods[1].DownloadUrl.AbsoluteUri);
        Assert.Equal("", c.Mods[1].PackageVersion);
    }

    [Fact]
    public async Task PerModUrlOverridesAnIndexLevelGithub()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"]));
        IndexCatalog c = await LoadGh(f, "{\"id\":\"own\",\"path\":\"own\",\"download\":{\"url\":\"own.zip\"}},{\"id\":\"shared\",\"path\":\"shared\"}", GhDownload());
        Assert.Equal("https://example.com/idx/own.zip", c.Mods[0].DownloadUrl.AbsoluteUri);
        Assert.EndsWith("/Gear-1.zip", c.Mods[1].DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexLevelGithubAppliesToAllModsWithoutOwnDownload()
    {
        FakeFetcher f = Releases(Release("v7", ["Gear-7.zip"]));
        IndexCatalog c = await LoadGh(f, "{\"id\":\"a\",\"path\":\"a\"},{\"id\":\"b\",\"kind\":\"plugin\"},{\"id\":\"c\",\"kind\":\"tool\",\"version\":\"3\"}", GhDownload());
        Assert.Equal(3, c.Mods.Count);
        Assert.All(c.Mods, m => Assert.EndsWith("/Gear-7.zip", m.DownloadUrl.AbsoluteUri, StringComparison.Ordinal));
        Assert.Equal(["7", "7", "3"], c.Mods.Select(m => m.Version).ToArray());
        Assert.Equal(1, f.CountCalls(Api));
    }

    [Fact]
    public async Task SameRepositoryAndPatternIsAskedOnce()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"]));
        f.Delay = TimeSpan.FromMilliseconds(30);
        string mods = string.Join(",", Enumerable.Range(0, 8).Select(i => "{\"id\":\"m" + i + "\",\"path\":\"m" + i + "\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\"}}"));
        IndexCatalog c = await LoadGh(f, mods);
        Assert.Equal(8, c.Mods.Count);
        Assert.Equal(1, f.CountCalls(Api));
    }

    [Fact]
    public async Task SameRepositoryWithDifferentPatternsOrPrereleaseFlagsAsksPerCombination()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip", "Tool-1.zip"]));
        string mods = "{\"id\":\"a\",\"path\":\"a\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\"}},"
            + "{\"id\":\"b\",\"path\":\"b\",\"download\":{\"github\":\"O/R\",\"asset\":\"gear-*.zip\"}},"
            + "{\"id\":\"c\",\"path\":\"c\",\"download\":{\"github\":\"o/r\",\"asset\":\"Tool-*.zip\"}}";
        IndexCatalog c = await LoadGh(f, mods);
        Assert.Equal(3, c.Mods.Count);
        Assert.EndsWith("/Tool-1.zip", c.Mods[2].DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolverCacheIsSharedAcrossIndexes()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"]));
        f.Add(Root, "{\"schema\":1,\"mods\":[" + GhMod + "],\"indexes\":[{\"url\":\"sub.json\"}]}");
        f.Add("https://example.com/idx/sub.json", "{\"schema\":1,\"mods\":[{\"id\":\"h\",\"path\":\"h\",\"download\":{\"github\":\"o/r\",\"asset\":\"Gear-*.zip\"}}]}");
        IndexCatalog c = await new IndexLoader(f, "win-x64").LoadAsync([new Uri(Root)], TestContext.Current.CancellationToken);
        Assert.Equal(2, c.Mods.Count);
        Assert.Equal(1, f.CountCalls(Api));
    }

    [Theory]
    [InlineData("download\":{\"github\":\"not a repo\",\"asset\":\"x*\"}")]
    [InlineData("download\":{\"github\":\"onlyowner\",\"asset\":\"x*\"}")]
    [InlineData("download\":{\"github\":\"a/b/c\",\"asset\":\"x*\"}")]
    [InlineData("download\":{\"github\":\"../x\",\"asset\":\"x*\"}")]
    [InlineData("download\":{\"github\":\"o/r\"}")]
    [InlineData("download\":{\"github\":\"o/r\",\"asset\":\"\"}")]
    [InlineData("download\":{\"github\":\"o/r\",\"asset\":\"   \"}")]
    public async Task InvalidGithubDownloadIsSkippedWithAWarning(string download)
    {
        var f = new FakeFetcher();
        IndexCatalog c = await LoadGh(f, "{\"id\":\"bad\",\"path\":\"bad\",\"" + download + "},{\"id\":\"ok\",\"path\":\"ok\"}");
        Assert.Equal(["ok"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Contains("skipped 'bad'", Warn(c));
        Assert.Equal(1, c.Indexes[0].ModCount);
        Assert.DoesNotContain(f.Calls, x => x.Contains("api.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GithubWithoutAnyMatchingReleaseRemovesTheEntryAndCorrectsTheCount()
    {
        FakeFetcher f = Releases(Release("v1", ["other.zip"]), Release("v0", ["Gear-0.zip"], draft: true), Release("v0b", ["Gear-0.zip"], pre: true));
        IndexCatalog c = await LoadGh(f, GhMod + ",{\"id\":\"ok\",\"path\":\"ok\"}");
        Assert.Equal(["ok"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Equal(1, c.Indexes[0].ModCount);
        Assert.Contains("'g' skipped", Warn(c));
        Assert.Contains("Gear-*.zip", Warn(c));
    }

    [Fact]
    public async Task GithubPrereleaseOnlyMatchGivesAWarningThatSaysPublished()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"], pre: true));
        IndexCatalog c = await LoadGh(f, GhMod);
        Assert.Empty(c.Mods);
        Assert.Contains("published", Warn(c));
    }

    [Fact]
    public async Task GithubEmptyReleaseListRemovesTheEntry()
    {
        IndexCatalog c = await LoadGh(Releases(), GhMod);
        Assert.Empty(c.Mods);
        Assert.Contains("skipped", Warn(c));
    }

    [Fact]
    public async Task GithubApiFailureIsAWarningAndOtherEntriesStillLoad()
    {
        var f = new FakeFetcher(); // no answer for the API: 404
        IndexCatalog c = await LoadGh(f, GhMod + ",{\"id\":\"ok\",\"path\":\"ok\"},{\"id\":\"pl\",\"kind\":\"plugin\"}");
        Assert.Equal(["ok", "pl"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Contains("404", Warn(c));
        Assert.Equal(2, c.Indexes[0].ModCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[{\"tag_name\":")]
    public async Task GithubInvalidJsonIsAWarningAndOtherEntriesStillLoad(string body)
    {
        var f = new FakeFetcher().Add(Api, body);
        IndexCatalog c = await LoadGh(f, GhMod + ",{\"id\":\"ok\",\"path\":\"ok\"}");
        Assert.Equal(["ok"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Contains("'g' skipped", Warn(c));
        Assert.Equal(1, c.Indexes[0].ModCount);
    }

    [Fact]
    public async Task GithubNullJsonIsTreatedAsNoReleases()
    {
        IndexCatalog c = await LoadGh(new FakeFetcher().Add(Api, "null"), GhMod);
        Assert.Empty(c.Mods);
        Assert.Contains("'g' skipped", Warn(c));
    }

    [Fact]
    public async Task GithubOneFailingRepositoryDoesNotAffectAnother()
    {
        FakeFetcher f = new FakeFetcher().Add("https://api.github.com/repos/x/y/releases?per_page=30", "[" + Release("v1", ["Gear-1.zip"]).Replace("o/r", "x/y", StringComparison.Ordinal) + "]");
        string mods = GhMod + ",{\"id\":\"h\",\"path\":\"h\",\"download\":{\"github\":\"x/y\",\"asset\":\"Gear-*.zip\"}}";
        IndexCatalog c = await LoadGh(f, mods);
        Assert.Equal(["h"], c.Mods.Select(m => m.Id).ToArray());
        Assert.Single(c.Warnings);
    }

    [Fact]
    public async Task GithubSkippedEntryFreesItsIdForNothingButCountsCorrectlyPerIndex()
    {
        var f = new FakeFetcher();
        f.Add(Root, "{\"schema\":1,\"mods\":[" + GhMod + "],\"indexes\":[{\"url\":\"sub.json\"}]}");
        f.Add("https://example.com/idx/sub.json", Index("{\"id\":\"s\",\"path\":\"s\"}"));
        IndexCatalog c = await new IndexLoader(f, "win-x64").LoadAsync([new Uri(Root)], TestContext.Current.CancellationToken);
        Assert.Equal(0, c.Indexes[0].ModCount);
        Assert.Equal(1, c.Indexes[1].ModCount);
        Assert.Equal(["s"], c.Mods.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task GithubPluginAndToolEntriesAreResolvedToo()
    {
        FakeFetcher f = Releases(Release("v3", ["Bild-3.zip"], digest: "sha256:aa"));
        IndexCatalog c = await LoadGh(f, "{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":[\"ddraw.dll\"]}},{\"id\":\"t\",\"kind\":\"tool\",\"install\":{\"launch\":\"t.exe\"}}", GhDownload("Bild-*.zip"));
        Assert.Equal(2, c.Mods.Count);
        Assert.Equal([ItemKind.Plugin, ItemKind.Tool], c.Mods.Select(m => m.Kind).ToArray());
        Assert.All(c.Mods, m => Assert.Equal("aa", m.DownloadSha256));
    }

    // ---- resolver directly ---------------------------------------------------------------------------------------

    private static Task<ResolvedAsset> Resolve(FakeFetcher f, string repo = "o/r", string pattern = "Gear-*.zip", bool pre = false)
        => new GitHubReleaseResolver(f).ResolveAsync(repo, pattern, pre, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ResolverReturnsUrlTagNameAndSha()
    {
        ResolvedAsset a = await Resolve(Releases(Release("v1", ["Gear-1.zip"], digest: "sha256:AB")));
        Assert.Equal("https://github.com/o/r/releases/download/v1/Gear-1.zip", a.Url.AbsoluteUri);
        Assert.Equal("v1", a.Tag);
        Assert.Equal("Gear-1.zip", a.AssetName);
        Assert.Equal("ab", a.Sha256);
    }

    [Fact]
    public async Task ResolverSkipsAssetsWithoutNameOrWithAnUnusableUrl()
    {
        string json = "[{\"tag_name\":\"v1\",\"assets\":["
            + "{\"browser_download_url\":\"https://h/x/Gear-1.zip\"},"
            + "{\"name\":\"Gear-2.zip\"},"
            + "{\"name\":\"Gear-3.zip\",\"browser_download_url\":\"http://h/x/Gear-3.zip\"},"
            + "{\"name\":\"Gear-4.zip\",\"browser_download_url\":\"relative/Gear-4.zip\"},"
            + "{\"name\":\"Gear-5.zip\",\"browser_download_url\":\"https://h/x/Gear-5.zip\"}]}]";
        ResolvedAsset a = await Resolve(new FakeFetcher().Add(Api, json));
        Assert.Equal("Gear-5.zip", a.AssetName);
    }

    [Fact]
    public async Task ResolverToleratesMissingFieldsAndNullAssets()
    {
        string json = "[{\"tag_name\":null,\"assets\":null},{\"assets\":[{\"name\":\"Gear-1.zip\",\"browser_download_url\":\"https://h/Gear-1.zip\"}]}]";
        ResolvedAsset a = await Resolve(new FakeFetcher().Add(Api, json));
        Assert.Equal("", a.Tag);
        Assert.Equal("Gear-1.zip", a.AssetName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("a b/c")]
    [InlineData("a/b?x=1")]
    [InlineData("/b")]
    [InlineData("a/")]
    [InlineData("../x")]
    [InlineData("o/..")]
    public async Task ResolverRejectsInvalidRepositories(string repo)
    {
        var f = new FakeFetcher();
        await Assert.ThrowsAsync<IndexException>(() => Resolve(f, repo));
        Assert.Empty(f.Calls);
    }

    [Theory]
    [InlineData("o/r", true)]
    [InlineData("Some-Owner/repo.name_1", true)]
    [InlineData("o/r ", false)]
    [InlineData(null, false)]
    [InlineData("../x", false)]
    [InlineData("o/..", false)]
    [InlineData("./r", false)]
    [InlineData("o/.github", true)]
    [InlineData("o\\r", false)]
    public void RepositoryValidation(string? repo, bool valid) => Assert.Equal(valid, GitHubReleaseResolver.IsValidRepository(repo));

    [Fact]
    public async Task ResolverNoMatchThrowsIndexExceptionNamingTheRepository()
    {
        IndexException ex = await Assert.ThrowsAsync<IndexException>(() => Resolve(Releases(Release("v1", ["x.zip"]))));
        Assert.Contains("o/r", ex.Message);
        Assert.Contains("Gear-*.zip", ex.Message);
    }

    [Fact]
    public async Task ResolverInvalidJsonThrowsIndexException()
        => await Assert.ThrowsAsync<IndexException>(() => Resolve(new FakeFetcher().Add(Api, "<html>")));

    [Fact]
    public async Task ResolverApiFailureThrowsFetchException()
        => await Assert.ThrowsAsync<FetchException>(() => Resolve(new FakeFetcher()));

    [Fact]
    public async Task ResolverCachesPerRepositoryPatternAndPrereleaseFlag()
    {
        FakeFetcher f = Releases(Release("v2", ["Gear-2.zip"], pre: true), Release("v1", ["Gear-1.zip"]));
        var r = new GitHubReleaseResolver(f);
        CancellationToken ct = TestContext.Current.CancellationToken;
        ResolvedAsset a = await r.ResolveAsync("o/r", "Gear-*.zip", false, ct);
        ResolvedAsset b = await r.ResolveAsync("O/R", "GEAR-*.ZIP", false, ct);
        Assert.Same(a, b);
        Assert.Equal(1, f.CountCalls(Api));
        ResolvedAsset c = await r.ResolveAsync("o/r", "Gear-*.zip", true, ct);
        Assert.Equal("v2", c.Tag);
        Assert.Equal("v1", a.Tag);
        Assert.Equal(2, f.CountCalls(Api));
    }

    [Fact]
    public async Task ResolverCachesFailuresToo()
    {
        var f = new FakeFetcher();
        var r = new GitHubReleaseResolver(f);
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<FetchException>(() => r.ResolveAsync("o/r", "x", false, ct));
        await Assert.ThrowsAsync<FetchException>(() => r.ResolveAsync("o/r", "x", false, ct));
        Assert.Equal(1, f.CountCalls(Api));
    }

    [Fact]
    public async Task ResolverReadsCommentsAndTrailingCommas()
    {
        string json = "[ // newest\n {\"tag_name\":\"v1\",\"assets\":[{\"name\":\"Gear-1.zip\",\"browser_download_url\":\"https://h/Gear-1.zip\",},],}, ]";
        Assert.Equal("v1", (await Resolve(new FakeFetcher().Add(Api, json))).Tag);
    }

    [Fact]
    public async Task ResolverRequestsThirtyReleasesFromTheApi()
    {
        FakeFetcher f = Releases(Release("v1", ["Gear-1.zip"]));
        await Resolve(f);
        Assert.Equal([Api], f.Calls.ToArray());
    }

    [Fact]
    public void CatalogRoundTripOfAnIndexWithGithubAndKindsParses()
    {
        var idx = new ModIndex
        {
            Download = new IndexDownload { Github = "o/r", Asset = "a*.zip", Prerelease = true },
            Mods = [new IndexMod { Id = "p", Kind = "plugin", Platforms = ["win"], Install = new InstallSpec { To = "x", Files = [new InstallFileSpec { From = "a", To = "b" }] } }],
        };
        ModIndex back = IndexJson.Parse(Encoding.UTF8.GetBytes(IndexJson.ToJson(idx)));
        Assert.Equal("o/r", back.Download!.Github);
        Assert.True(back.Download.Prerelease);
        Assert.Equal("x", back.Mods![0].Install!.To);
        Assert.Equal("b", back.Mods[0].Install!.Files![0].To);
        Assert.Equal(["win"], back.Mods[0].Platforms);
    }
}
