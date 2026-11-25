using SacredModManager.Core.App;
using Xunit;

namespace SacredModManager.Tests;

public class AppChangelogTests
{
    private const string History = "## 2026-11-25 (41b7797)\n\n- New: a thing.\n\n## 2026-11-24 (ebf3cc3)\n\n- Improved: another.\n\n## 2026-11-22 (bbcaae1)\n\n- New: old.\n";

    [Fact]
    public void ParsesTheBuildsNewestFirst()
    {
        IReadOnlyList<AppChangelogEntry> e = AppChangelog.Parse(History);
        Assert.Equal(["41b7797", "ebf3cc3", "bbcaae1"], e.Select(x => x.Sha));
        Assert.StartsWith("## 2026-11-25 (41b7797)", e[0].Markdown);
        Assert.Contains("- New: a thing.", e[0].Markdown);
        Assert.DoesNotContain("another", e[0].Markdown);
    }

    [Fact]
    public void SinceGivesTheBuildsAfterTheLastSeenOneUpToTheCurrent()
    {
        IReadOnlyList<AppChangelogEntry> e = AppChangelog.Parse(History);
        Assert.Equal(["41b7797", "ebf3cc3"], AppChangelog.Since(e, "41b7797abcdef", "bbcaae1")!.Select(x => x.Sha));
        Assert.Equal(["ebf3cc3"], AppChangelog.Since(e, "ebf3cc3", "bbcaae1")!.Select(x => x.Sha));
        Assert.Empty(AppChangelog.Since(e, "ebf3cc3", "41b7797")!);
        Assert.Empty(AppChangelog.Since(e, "ebf3cc3", "ebf3cc3")!);
    }

    [Fact]
    public void SinceWithoutAKnownLastSeenBuildGivesTheCurrentOne()
    {
        IReadOnlyList<AppChangelogEntry> e = AppChangelog.Parse(History);
        Assert.Equal(["ebf3cc3"], AppChangelog.Since(e, "ebf3cc3", null)!.Select(x => x.Sha));
        Assert.Equal(["ebf3cc3"], AppChangelog.Since(e, "ebf3cc3", "unknown1")!.Select(x => x.Sha));
    }

    [Fact]
    public void SinceIsNullWhenTheCurrentBuildIsNotInTheChangelog()
        => Assert.Null(AppChangelog.Since(AppChangelog.Parse(History), "1234567", "bbcaae1"));

    [Fact]
    public void EmptyTextHasNoEntries()
    {
        Assert.Empty(AppChangelog.Parse(null));
        Assert.Empty(AppChangelog.Parse("just text"));
    }
}
