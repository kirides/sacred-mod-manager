using System.Text;
using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class MinVersionTests
{
    private static CatalogMod Cat(string id, string version = "1.0", string requires = "", params (string Id, string Min)[] mins)
        => new()
        {
            Id = id, Path = "mods/" + id, Version = version, DownloadUrl = new Uri("https://example.com/p.zip"), IndexUrl = new Uri("https://example.com/mod-index.json"),
            Requires = requires.Length == 0 ? [] : requires.Split(','), MinVersions = mins.ToDictionary(m => m.Id, m => m.Min),
        };

    private static Dictionary<string, string> Have(params (string, string)[] v) => v.ToDictionary(x => x.Item1, x => x.Item2);

    [Theory]
    [InlineData("a", "a", null)]
    [InlineData(" a ", "a", null)]
    [InlineData("sacred-gear>=0.4.1", "sacred-gear", "0.4.1")]
    [InlineData("sacred-gear >= v0.4.1", "sacred-gear", "v0.4.1")]
    public void ParsesPlainAndVersionedRequirements(string text, string id, string? min)
    {
        Assert.True(ModRequirement.TryParse(text, out ModRequirement r));
        Assert.Equal((id, min), (r.Id, r.MinVersion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a>1.0")]
    [InlineData("a<=1.0")]
    [InlineData("a>=")]
    [InlineData(">=1.0")]
    [InlineData("a>=1 0")]
    [InlineData("bad id>=1")]
    public void RejectsMalformedRequirements(string text) => Assert.False(ModRequirement.TryParse(text, out _));

    [Fact]
    public void MetWhenEqualOrNewerOrUnknown()
    {
        Assert.True(ModRequirement.IsMet("0.4.1", "0.4.1"));
        Assert.True(ModRequirement.IsMet("0.4.1", "0.10.0"));
        Assert.False(ModRequirement.IsMet("0.4.1", "0.4.0"));
        Assert.True(ModRequirement.IsMet("0.4.1", ""));
        Assert.True(ModRequirement.IsMet(null, "0.0.1"));
    }

    [Fact]
    public async Task LoaderReadsMinimumsKeepsPlainEntriesAndWarnsAboutBadOnes()
    {
        const string json = """
            {"schema":1,"download":{"url":"p.zip"},"mods":[
              {"id":"m","path":"mods/m","requires":["a","sacred-gear>=0.4.1","sacred-gear >= 0.5","b>2","a>=1"]}]}
            """;
        var f = new FakeFetcher();
        f.Add("https://example.com/i/mod-index.json", Encoding.UTF8.GetBytes(json));
        IndexCatalog c = await new IndexLoader(f).LoadAsync([new Uri("https://example.com/i/mod-index.json")], TestContext.Current.CancellationToken);
        CatalogMod m = Assert.Single(c.Mods);
        Assert.Equal(["a", "sacred-gear"], m.Requires.ToArray());
        Assert.Equal("0.5", m.MinVersions["sacred-gear"]);
        Assert.Equal("1", m.MinVersions["a"]);
        Assert.Contains(c.Warnings, w => w.Contains("'b>2'"));
    }

    [Fact]
    public void PlanAcceptsAnInstalledVersionThatIsNewEnough()
    {
        CatalogMod ui = Cat("ui", requires: "gear", mins: ("gear", "0.4.1"));
        InstallPlan p = ModManagerService.Plan([ui], [ui], Have(("gear", "0.4.1")));
        Assert.Empty(p.Unmet);
        Assert.Equal(["ui"], p.Install.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void PlanUpdatesATooOldRequirementWhenASourceOffersAnewEnoughOne()
    {
        CatalogMod gear = Cat("gear", "0.4.1"), ui = Cat("ui", requires: "gear", mins: ("gear", "0.4.1"));
        InstallPlan p = ModManagerService.Plan([ui], [gear, ui], Have(("gear", "0.4.0")));
        Assert.Empty(p.Unmet);
        Assert.Equal(["gear", "ui"], p.Install.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void PlanReportsAndBlocksWhenNothingIsNewEnough()
    {
        CatalogMod gear = Cat("gear", "0.4.0"), ui = Cat("ui", requires: "gear", mins: ("gear", "0.4.1")), top = Cat("top", requires: "ui");
        InstallPlan p = ModManagerService.Plan([top], [gear, ui, top], Have(("gear", "0.3.0")));
        UnmetRequirement u = Assert.Single(p.Unmet);
        Assert.Equal(("gear", "ui", "0.4.1", "0.3.0", "0.4.0"), (u.Id, u.RequiredBy, u.MinVersion, u.InstalledVersion, u.OfferedVersion));
        Assert.Contains("ui needs gear 0.4.1 or newer, you have 0.3.0", u.Message);
        Assert.Empty(p.Install); // neither the mod nor what needs it is installed
    }

    [Fact]
    public void PlanReportsATooOldRequirementThatNoSourceOffers()
    {
        CatalogMod ui = Cat("ui", requires: "gear", mins: ("gear", "0.4.1"));
        InstallPlan p = ModManagerService.Plan([ui], [ui], Have(("gear", "0.4.0")));
        Assert.Contains("no newer one is offered", Assert.Single(p.Unmet).Message);
    }

    [Fact]
    public void PlanTreatsANotInstalledRequirementWithATooOldOfferAsUnmet()
    {
        CatalogMod gear = Cat("gear", "0.4.0"), ui = Cat("ui", requires: "gear", mins: ("gear", "0.4.1"));
        InstallPlan p = ModManagerService.Plan([ui], [gear, ui], new Dictionary<string, string>());
        Assert.Equal("not installed", Assert.Single(p.Unmet).InstalledVersion is null ? "not installed" : "installed");
        Assert.Empty(p.Install);
    }

    [Fact]
    public void PlainRequirementsAndTheIdOnlyOverloadStayAsTheyWere()
    {
        CatalogMod ui = Cat("ui", requires: "gear", mins: ("gear", "9.9"));
        Assert.Empty(ModManagerService.Plan([ui], [ui], ["gear"]).Unmet); // versions unknown: not checked
        CatalogMod plain = Cat("plain", requires: "gear");
        Assert.Empty(ModManagerService.Plan([plain], [plain], Have(("gear", "0.0.1"))).Unmet);
    }
}
