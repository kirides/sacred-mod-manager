using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class MarkdownLiteTests
{
    [Fact]
    public void ParsesHeadingsBulletsAndBlankLines()
    {
        IReadOnlyList<MdBlock> b = MarkdownLite.ParseBlocks("## v1.0 (2026-11-01)\n\n- New: a map\n* Fixed: a crash\nplain");
        Assert.Equal([MdBlockKind.Heading, MdBlockKind.Blank, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Paragraph], b.Select(x => x.Kind));
        Assert.Equal(2, b[0].Level);
        Assert.Equal("v1.0 (2026-11-01)", b[0].Spans[0].Text);
        Assert.Equal("a crash", b[3].Spans[0].Text.Replace("Fixed: ", ""));
    }

    [Fact]
    public void BoldItalicAndCodeSpans()
    {
        IReadOnlyList<MdSpan> s = MarkdownLite.ParseInline("a **bold** and *it* and _it2_ and `x*y` end");
        Assert.Contains(s, x => x is { Text: "bold", Bold: true });
        Assert.Contains(s, x => x is { Text: "it", Italic: true });
        Assert.Contains(s, x => x is { Text: "it2", Italic: true });
        Assert.Contains(s, x => x is { Text: "x*y", Code: true });
        Assert.Equal("a bold and it and it2 and x*y end", string.Concat(s.Select(x => x.Text)));
    }

    [Fact]
    public void LooseMarkersStayAsText()
    {
        Assert.Equal("2 * 3 = 6", string.Concat(MarkdownLite.ParseInline("2 * 3 = 6").Select(x => x.Text)));
        Assert.Equal("snake_case_name", string.Concat(MarkdownLite.ParseInline("snake_case_name").Select(x => x.Text)));
        Assert.Equal("**open", string.Concat(MarkdownLite.ParseInline("**open").Select(x => x.Text)));
        Assert.All(MarkdownLite.ParseInline("snake_case_name"), x => Assert.False(x.Italic));
    }

    [Fact]
    public void ToMarkdownRoundTripsThroughTheParser()
    {
        string md = Changelog.ToMarkdown(Changelog.Parse("## v1.2.0 (2026-11-20)\n\n- **New**: a map.\n\n## v1.1.0\n\n- Fixed.\n"));
        IReadOnlyList<MdBlock> b = MarkdownLite.ParseBlocks(md);
        Assert.Equal(2, b.Count(x => x.Kind == MdBlockKind.Heading));
        Assert.Contains(b.SelectMany(x => x.Spans), x => x is { Text: "New", Bold: true });
    }
}
