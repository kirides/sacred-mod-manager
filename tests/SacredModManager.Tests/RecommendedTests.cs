using System.Text;
using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class RecommendedTests
{
    private static CatalogMod Cat(string id, string version = "1.0", string requires = "", string recommended = "", params (string Id, string Min)[] recMins)
        => new()
        {
            Id = id, Path = "mods/" + id, Version = version, DownloadUrl = new Uri("https://example.com/p.zip"), IndexUrl = new Uri("https://example.com/mod-index.json"),
            Requires = requires.Length == 0 ? [] : requires.Split(','),
            Recommended = recommended.Length == 0 ? [] : recommended.Split(','), RecommendedMinVersions = recMins.ToDictionary(m => m.Id, m => m.Min),
        };

    private static Dictionary<string, string> Have(params (string, string)[] v) => v.ToDictionary(x => x.Item1, x => x.Item2);

    private static async Task<IndexCatalog> Load(string json)
    {
        var f = new FakeFetcher();
        f.Add("https://example.com/i/mod-index.json", Encoding.UTF8.GetBytes(json));
        return await new IndexLoader(f).LoadAsync([new Uri("https://example.com/i/mod-index.json")], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LoaderReadsRecommendedWithMinimumsAndDropsSelfAndRequired()
    {
        IndexCatalog c = await Load("""
            {"schema":1,"download":{"url":"p.zip"},"mods":[
              {"id":"m","path":"mods/m","requires":["gear>=0.4.1"],"recommended":["theme","theme>=0.3.0","m","gear","bad id","x>1"]}]}
            """);
        CatalogMod m = Assert.Single(c.Mods);
        Assert.Equal(["theme"], m.Recommended.ToArray());
        Assert.Equal("0.3.0", m.RecommendedMinVersions["theme"]);
        Assert.Equal(["gear", "sacred-gear"], m.Requires.ToArray());
        Assert.Contains(c.Warnings, w => w.Contains("recommendation 'x>1'"));
    }

    [Fact]
    public async Task AnEntryWithoutRecommendedHasNone()
    {
        IndexCatalog c = await Load("""{"schema":1,"download":{"url":"p.zip"},"mods":[{"id":"m","path":"mods/m"}]}""");
        Assert.Empty(Assert.Single(c.Mods).Recommended);
    }

    [Fact]
    public void LuaManifestReadsRecommended()
    {
        ModManifest m = LuaManifestReader.Read("return { id = 'a', name = 'A', version = '1', requires = {'x'}, recommended = {'sacred-theme'} }");
        Assert.Equal(["sacred-theme"], m.Recommended);
        Assert.Equal(["x"], m.Requires);
        Assert.Empty(LuaManifestReader.Read("return { id = 'a', name = 'A', version = '1' }").Recommended);
    }

    [Fact]
    public void RecommendedIsNeverPartOfThePlan()
    {
        CatalogMod theme = Cat("theme"), ui = Cat("ui", recommended: "theme");
        InstallPlan p = ModManagerService.Plan([ui], [theme, ui], Have());
        Assert.Equal(["ui"], p.Install.Select(m => m.Id).ToArray());
        Assert.Empty(p.Missing);
    }

    [Fact]
    public void OffersAMissingRecommendationOnceForAllMods()
    {
        CatalogMod theme = Cat("theme"), a = Cat("a", recommended: "theme"), b = Cat("b", recommended: "theme");
        InstallPlan p = ModManagerService.Plan([a, b], [theme, a, b], Have());
        Recommendation r = Assert.Single(ModManagerService.Recommend(p, [theme, a, b], Have()));
        Assert.Equal("theme", r.Mod.Id);
        Assert.Equal(["a", "b"], r.RecommendedBy.ToArray());
    }

    [Fact]
    public void AcceptingItInstallsTheRecommendedWithWhatItRequires()
    {
        CatalogMod gear = Cat("gear"), theme = Cat("theme", requires: "gear"), ui = Cat("ui", recommended: "theme");
        InstallPlan p = ModManagerService.Plan([ui], [gear, theme, ui], Have());
        Recommendation r = Assert.Single(ModManagerService.Recommend(p, [gear, theme, ui], Have()));
        InstallPlan all = ModManagerService.Plan([ui, r.Mod], [gear, theme, ui], Have());
        Assert.Equal(["ui", "gear", "theme"], all.Install.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void NothingToOfferWhenInstalledAlreadyInThePlanNotOfferedOrTooOld()
    {
        CatalogMod theme = Cat("theme", "0.3.0"), ui = Cat("ui", recommended: "theme", recMins: ("theme", "0.3.0"));
        InstallPlan p = ModManagerService.Plan([ui], [theme, ui], Have());
        Assert.Single(ModManagerService.Recommend(p, [theme, ui], Have()));
        Assert.Empty(ModManagerService.Recommend(p, [theme, ui], Have(("theme", "0.3.0")))); // installed and new enough
        Assert.Empty(ModManagerService.Recommend(ModManagerService.Plan([ui, theme], [theme, ui], Have()), [theme, ui], Have())); // wanted anyway
        Assert.Empty(ModManagerService.Recommend(ModManagerService.Plan([ui], [ui], Have()), [ui], Have())); // no source offers it
        CatalogMod oldTheme = Cat("theme", "0.2.0");
        Assert.Empty(ModManagerService.Recommend(ModManagerService.Plan([ui], [oldTheme, ui], Have()), [oldTheme, ui], Have())); // offered version is too old
        Assert.Single(ModManagerService.Recommend(p, [theme, ui], Have(("theme", "0.2.0")))); // installed but too old, a newer one is offered
    }

    [Fact]
    public void AnUpdateOfAnInstalledModDoesNotAskAgain()
    {
        CatalogMod theme = Cat("theme"), ui = Cat("ui", "2.0", recommended: "theme");
        InstallPlan p = ModManagerService.Plan([ui], [theme, ui], Have(("ui", "1.0")));
        Assert.Empty(ModManagerService.Recommend(p, [theme, ui], Have(("ui", "1.0"))));
    }
}
