using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using Xunit;

namespace SacredModManager.Tests;

public class UpdateCheckerTests
{
    private const string Api = "https://api.github.com/repos/kirides/sacred-mod-manager/releases?per_page=20";

    private static string Release(string tag, string name, string published, bool pre = false, bool draft = false)
        => "{\"tag_name\":\"" + tag + "\",\"name\":\"" + name + "\",\"html_url\":\"https://github.com/kirides/sacred-mod-manager/releases/tag/" + tag + "\",\"published_at\":\"" + published
           + "\",\"prerelease\":" + (pre ? "true" : "false") + ",\"draft\":" + (draft ? "true" : "false") + ",\"assets\":[]}";

    private static Task<UpdateInfo?> Check(string current, params string[] releases)
    {
        var f = new FakeFetcher().Add(Api, "[" + string.Join(",", releases) + "]");
        return new UpdateChecker(f).CheckAsync(BuildInfo.Parse(current), UpdateChecker.Repository, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("nightly-20261120-abc1234", BuildKind.Nightly)]
    [InlineData("nightly-20261120-abc1234+sourcehash", BuildKind.Nightly)]
    [InlineData("1.2.3", BuildKind.Release)]
    [InlineData("v2.0", BuildKind.Release)]
    [InlineData("dev", BuildKind.Development)]
    [InlineData("", BuildKind.Development)]
    [InlineData(null, BuildKind.Development)]
    [InlineData("nightly-garbage", BuildKind.Development)]
    [InlineData("nightly-20261340-abc1234", BuildKind.Development)]
    public void BuildInfoParsesTheStamp(string? text, BuildKind kind) => Assert.Equal(kind, BuildInfo.Parse(text).Kind);

    [Fact]
    public void NightlyStampKeepsTheShaAndTheDay()
    {
        BuildInfo b = BuildInfo.Parse("nightly-20261120-ABC1234");
        Assert.Equal("abc1234", b.Sha);
        Assert.Equal(new DateOnly(2026, 11, 20), b.Built);
    }

    [Fact]
    public async Task ANewerNightlyIsOffered()
    {
        UpdateInfo? u = await Check("nightly-20261119-aaaaaaa", Release("nightly-bbbbbbb", "Nightly - 2026-11-20 02:17 - bbbbbbb", "2026-11-20T02:17:00Z", pre: true), Release("nightly-aaaaaaa", "old", "2026-11-19T02:17:00Z", pre: true));
        Assert.NotNull(u);
        Assert.Equal("nightly-bbbbbbb", u.Tag);
        Assert.Equal("https://github.com/kirides/sacred-mod-manager/releases/tag/nightly-bbbbbbb", u.Url.AbsoluteUri);
        Assert.Contains("2026-11-20", u.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNewestByPublicationWinsWhateverOrderGitHubListsThem()
    {
        // GitHub lists by the date of the tagged commit, which can put an older release first
        UpdateInfo? u = await Check("nightly-20261118-aaaaaaa", Release("nightly-bbbbbbb", "older", "2026-11-19T02:17:00Z", pre: true), Release("nightly-ccccccc", "newest", "2026-11-20T02:17:00Z", pre: true));
        Assert.Equal("nightly-ccccccc", u!.Tag);
    }

    [Fact]
    public async Task TheSameBuildIsUpToDate()
        => Assert.Null(await Check("nightly-20261119-aaaaaaa", Release("nightly-aaaaaaa", "x", "2026-11-19T02:17:00Z", pre: true)));

    [Fact]
    public async Task ABuildNewerThanTheNewestNightlyIsNotAskedToGoBack()
        => Assert.Null(await Check("nightly-20261125-ccccccc", Release("nightly-bbbbbbb", "x", "2026-11-20T02:17:00Z", pre: true)));

    [Fact]
    public async Task ADraftAndARegularReleaseAreNotNightlies()
    {
        UpdateInfo? u = await Check("nightly-20261119-aaaaaaa", Release("nightly-ddddddd", "draft", "2026-11-21T00:00:00Z", pre: true, draft: true), Release("v1.0.0", "stable", "2026-11-21T00:00:00Z"));
        Assert.Null(u);
    }

    [Fact]
    public async Task AReleaseBuildLooksAtPublishedReleasesOnly()
    {
        UpdateInfo? u = await Check("1.0.0", Release("nightly-bbbbbbb", "n", "2026-11-20T00:00:00Z", pre: true), Release("v1.1.0", "Gear Mod Manager 1.1.0", "2026-11-19T00:00:00Z"), Release("v1.0.0", "old", "2026-11-01T00:00:00Z"));
        Assert.NotNull(u);
        Assert.Equal("v1.1.0", u.Tag);
        Assert.Null(await Check("1.1.0", Release("v1.1.0", "same", "2026-11-19T00:00:00Z")));
        Assert.Null(await Check("2.0.0", Release("v1.1.0", "older", "2026-11-19T00:00:00Z")));
    }

    [Fact]
    public async Task ADevelopmentBuildNeverAsks()
    {
        var f = new FakeFetcher();
        Assert.Null(await new UpdateChecker(f).CheckAsync(BuildInfo.Parse("dev"), UpdateChecker.Repository, TestContext.Current.CancellationToken));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task GarbageFromGitHubMeansNoUpdate()
    {
        var f = new FakeFetcher().Add(Api, "<html>rate limited</html>");
        Assert.Null(await new UpdateChecker(f).CheckAsync(BuildInfo.Parse("1.0.0"), UpdateChecker.Repository, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AReleaseWithoutAnHttpsPageIsIgnored()
    {
        string bad = "{\"tag_name\":\"v9.0.0\",\"name\":\"x\",\"html_url\":\"http://evil.example/x\",\"published_at\":\"2026-11-20T00:00:00Z\",\"prerelease\":false,\"draft\":false}";
        Assert.Null(await Check("1.0.0", bad));
    }
}
