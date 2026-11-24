using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class ChangelogTests
{
    private const string History = "## v1.2.0 (2026-11-20)\n\n- New: a map.\n- Fixed a crash.\n\n## v1.1.0 (2026-11-10)\n\n- Improved: faster loading.\n\n## v1.0.0 (2026-11-01)\n\n- First release: loot helper.\n";

    [Fact]
    public void ParsesVersionsDatesAndBullets()
    {
        IReadOnlyList<ChangelogEntry> e = Changelog.Parse(History);
        Assert.Equal(["1.2.0", "1.1.0", "1.0.0"], e.Select(x => x.Version));
        Assert.Equal("2026-11-20", e[0].Date);
        Assert.Equal(["New: a map.", "Fixed a crash."], e[0].Lines);
    }

    [Fact]
    public void ReadsCrLfAndMissingDates()
    {
        IReadOnlyList<ChangelogEntry> e = Changelog.Parse("## v0.3.0\r\n- A\r\n");
        Assert.Single(e);
        Assert.Equal("", e[0].Date);
        Assert.Equal(["A"], e[0].Lines);
        Assert.Empty(Changelog.Parse(null));
        Assert.Empty(Changelog.Parse("no headings"));
    }

    [Fact]
    public void BetweenKeepsWhatIsNewerThanInstalledUpToAvailable()
    {
        IReadOnlyList<ChangelogEntry> e = Changelog.Parse(History);
        Assert.Equal(["1.2.0", "1.1.0"], Changelog.Between(e, "1.0.0", "1.2.0").Select(x => x.Version));
        Assert.Equal(["1.1.0"], Changelog.Between(e, "1.0.0", "1.1.0").Select(x => x.Version));
        Assert.Empty(Changelog.Between(e, "1.2.0", "1.2.0"));
    }

    [Fact]
    public void BetweenWithoutInstalledGivesTheNewestEntry()
        => Assert.Equal(["1.2.0"], Changelog.Between(Changelog.Parse(History), null, "1.2.0").Select(x => x.Version));

    [Fact]
    public void FormatListsVersionHeadingsAndBullets()
    {
        string text = Changelog.Format(Changelog.Parse(History).Take(1));
        Assert.Contains("v1.2.0  (2026-11-20)", text);
        Assert.Contains("•  New: a map.", text);
    }

    [Fact]
    public async Task IndexEntryChangelogIsAZipPathOrAnHttpsUrl()
    {
        const string root = "https://example.com/idx/mod-index.json";
        var f = new FakeFetcher().Add(root, "{\"schema\":1,\"download\":{\"url\":\"pkg.zip\"},\"mods\":[{\"id\":\"a\",\"path\":\"mods/a\",\"changelog\":\"mods/a/changelog/history.md\"},{\"id\":\"b\",\"path\":\"mods/b\",\"changelog\":\"https://example.com/b.md\"},{\"id\":\"c\",\"path\":\"mods/c\"}]}");
        IndexCatalog c = await new IndexLoader(f).LoadAsync([new Uri(root)], TestContext.Current.CancellationToken);
        Assert.Equal("mods/a/changelog/history.md", c.Mods.Single(m => m.Id == "a").Changelog!.Path);
        Assert.Equal("https://example.com/b.md", c.Mods.Single(m => m.Id == "b").Changelog!.Url!.AbsoluteUri);
        Assert.Null(c.Mods.Single(m => m.Id == "c").Changelog);
    }
}
