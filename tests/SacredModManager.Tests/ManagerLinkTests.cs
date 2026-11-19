using SacredModManager.Core.Index;
using SacredModManager.Core.Links;
using Xunit;

namespace SacredModManager.Tests;

public class ManagerLinkTests
{
    private const string Index = "https://example.com/mods/mod-index.json";

    private static T Parsed<T>(string? text)
        where T : ManagerLink
    {
        Assert.True(ManagerLink.TryParse(text, out ManagerLink? link, out string? error), error);
        Assert.Null(error);
        return Assert.IsType<T>(link);
    }

    private static void Rejected(string? text)
    {
        Assert.False(ManagerLink.TryParse(text, out ManagerLink? link, out string? error));
        Assert.Null(link);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("gear-mod-manager://add-index?url=https://example.com/mods/mod-index.json")]
    [InlineData("gear-mod-manager://add?url=https://example.com/mods/mod-index.json")]
    [InlineData("gear-mod-manager://addindex?url=https://example.com/mods/mod-index.json")]
    [InlineData("GEAR-MOD-MANAGER://ADD-INDEX?URL=https://example.com/mods/mod-index.json")]
    [InlineData("Gear-Mod-Manager://Add-Index?Url=https://example.com/mods/mod-index.json")]
    [InlineData("gear-mod-manager:add-index?url=https://example.com/mods/mod-index.json")]
    [InlineData("gear-mod-manager:///add-index?url=https://example.com/mods/mod-index.json")]
    [InlineData("gear-mod-manager://add-index/?url=https://example.com/mods/mod-index.json")]
    [InlineData("gear-mod-manager://add-index?url=https%3A%2F%2Fexample.com%2Fmods%2Fmod-index.json")]
    [InlineData("gear-mod-manager://add-index?url=https://example.com/mods/mod-index.json#ignored")]
    [InlineData("gear-mod-manager://add-index?url=https://example.com/mods/mod-index.json&other=1")]
    [InlineData("gear-mod-manager://add-index?x=1&url=https://example.com/mods/mod-index.json")]
    public void AddIndexForms(string text)
        => Assert.Equal(Index, Parsed<ManagerLink.AddIndex>(text).Url.AbsoluteUri);

    [Fact]
    public void AddIndexWithGitHubRepositoryBecomesTheRawIndex()
        => Assert.Equal("https://raw.githubusercontent.com/o/r/HEAD/mod-index.json",
            Parsed<ManagerLink.AddIndex>("gear-mod-manager://add-index?url=https://github.com/o/r").Url.AbsoluteUri);

    [Fact]
    public void AddIndexWithEncodedGitHubRepository()
        => Assert.Equal("https://raw.githubusercontent.com/o/r/HEAD/mod-index.json",
            Parsed<ManagerLink.AddIndex>("gear-mod-manager://add-index?url=https%3A%2F%2Fgithub.com%2Fo%2Fr").Url.AbsoluteUri);

    [Fact]
    public void QueryAfterTheFragmentIsNotPartOfTheLink()
        => Parsed<ManagerLink.AddIndex>("gear-mod-manager://add-index?url=" + Index + "#x?url=http://evil.example/");

    [Fact]
    public void InstallWithModOnly()
    {
        var link = Parsed<ManagerLink.Install>("gear-mod-manager://install?mod=my_mod-1");
        Assert.Equal("my_mod-1", link.ModId);
        Assert.Null(link.Index);
    }

    [Fact]
    public void InstallWithIndex()
    {
        var link = Parsed<ManagerLink.Install>("gear-mod-manager://install?mod=MyMod&index=https%3A%2F%2Fexample.com%2Fmods%2Fmod-index.json");
        Assert.Equal("MyMod", link.ModId);
        Assert.Equal(Index, link.Index!.AbsoluteUri);
    }

    [Fact]
    public void InstallWithGitHubIndex()
    {
        var link = Parsed<ManagerLink.Install>("gear-mod-manager://install?mod=m&index=https://github.com/o/r");
        Assert.Equal("https://raw.githubusercontent.com/o/r/HEAD/mod-index.json", link.Index!.AbsoluteUri);
    }

    [Fact]
    public void InstallIsCaseInsensitiveInActionAndKeys()
        => Assert.Equal("m", Parsed<ManagerLink.Install>("gear-mod-manager://INSTALL?MOD=m").ModId);

    [Theory]
    [InlineData("gear-mod-manager://")]
    [InlineData("gear-mod-manager:")]
    [InlineData("gear-mod-manager:///")]
    [InlineData("gear-mod-manager://activate")]
    [InlineData("gear-mod-manager://activate/")]
    [InlineData("gear-mod-manager://open")]
    [InlineData("gear-mod-manager:activate")]
    [InlineData("GEAR-MOD-MANAGER://ACTIVATE")]
    [InlineData("gear-mod-manager://activate?x=1")]
    public void Activate(string text) => Parsed<ManagerLink.Activate>(text);

    [Theory]
    [InlineData("https://example.com/mod-index.json")]
    [InlineData("other-scheme://add-index?url=https://example.com/mod-index.json")]
    [InlineData("add-index?url=https://example.com/mod-index.json")]
    [InlineData("gear-mod-managerx://add-index?url=https://example.com/mod-index.json")]
    [InlineData("--flag")]
    [InlineData("C:\\Games\\Sacred\\mod.zip")]
    public void OtherSchemesAreRejected(string text) => Rejected(text);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData(" \" \" ")]
    public void EmptyIsRejected(string? text) => Rejected(text);

    [Fact]
    public void TooLongIsRejected()
    {
        string prefix = "gear-mod-manager://add-index?url=https://example.com/";
        string ok = prefix + new string('a', ManagerLink.MaxLength - prefix.Length);
        Assert.Equal(ManagerLink.MaxLength, ok.Length);
        Parsed<ManagerLink.AddIndex>(ok);

        Assert.False(ManagerLink.TryParse(ok + "a", out ManagerLink? link, out string? error));
        Assert.Null(link);
        Assert.Contains("long", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gear-mod-manager://delete?mod=x")]
    [InlineData("gear-mod-manager://run?cmd=calc")]
    [InlineData("gear-mod-manager://add-index-more?url=https://example.com/x.json")]
    [InlineData("gear-mod-manager://a/b?url=https://example.com/x.json")]
    public void UnknownActionIsRejected(string text) => Rejected(text);

    [Fact]
    public void UnknownLongActionIsReportedTruncated()
    {
        Assert.False(ManagerLink.TryParse("gear-mod-manager://" + new string('x', 500), out _, out string? error));
        Assert.True(error!.Length < 200);
    }

    [Theory]
    [InlineData("gear-mod-manager://add-index")]
    [InlineData("gear-mod-manager://add-index?")]
    [InlineData("gear-mod-manager://add-index?url")]
    [InlineData("gear-mod-manager://add-index?url=")]
    [InlineData("gear-mod-manager://add-index?url=%20")]
    [InlineData("gear-mod-manager://add-index?uri=https://example.com/x.json")]
    [InlineData("gear-mod-manager://install")]
    [InlineData("gear-mod-manager://install?mod=")]
    [InlineData("gear-mod-manager://install?index=https://example.com/x.json")]
    [InlineData("gear-mod-manager://install?url=https://example.com/x.json")]
    public void MissingArgumentsAreRejected(string text) => Rejected(text);

    [Theory]
    [InlineData("../x")]
    [InlineData("..%2Fx")]
    [InlineData("a%2Fb")]
    [InlineData("a%5Cb")]
    [InlineData("a%20b")]
    [InlineData("a+b")]
    [InlineData("a.b")]
    [InlineData("m%C3%A4")]
    [InlineData("%00")]
    [InlineData("a%0Ab")]
    public void InvalidModIdIsRejected(string id) => Rejected("gear-mod-manager://install?mod=" + id);

    [Fact]
    public void TooLongModIdIsRejected()
    {
        Parsed<ManagerLink.Install>("gear-mod-manager://install?mod=" + new string('a', 64));
        Rejected("gear-mod-manager://install?mod=" + new string('a', 65));
    }

    [Theory]
    [InlineData("http://example.com/mod-index.json")]
    [InlineData("http://localhost/mod-index.json")]
    [InlineData("http://127.0.0.1:8080/mod-index.json")]
    [InlineData("file:///C:/x/mod-index.json")]
    [InlineData("file://server/share/mod-index.json")]
    [InlineData("C:\\x")]
    [InlineData("C:\\x\\mod-index.json")]
    [InlineData("C:/x/mod-index.json")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\server\\share\\mod-index.json")]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript://example.com/%0Aalert(1)")]
    [InlineData("data:text/plain,hello")]
    [InlineData("ftp://example.com/x.json")]
    [InlineData("not a url at all %%%")]
    [InlineData("https://")]
    [InlineData("://")]
    public void NonHttpsOrLocalIndexAddressesAreRejected(string url)
    {
        string escaped = Uri.EscapeDataString(url);
        Rejected("gear-mod-manager://add-index?url=" + escaped);
        Rejected("gear-mod-manager://add-index?url=" + url);
        Rejected("gear-mod-manager://install?mod=m&index=" + escaped);
    }

    [Fact]
    public void InstallWithBadIndexIsRejectedEvenWhenModIsFine()
        => Rejected("gear-mod-manager://install?mod=m&index=http://example.com/x.json");

    [Fact]
    public void DuplicateKeysKeepTheFirst()
    {
        Assert.Equal(Index, Parsed<ManagerLink.AddIndex>("gear-mod-manager://add-index?url=" + Index + "&url=http://evil.example/x.json").Url.AbsoluteUri);
        Assert.Equal("first", Parsed<ManagerLink.Install>("gear-mod-manager://install?mod=first&mod=second").ModId);
        Assert.Equal(Index, Parsed<ManagerLink.Install>("gear-mod-manager://install?mod=m&index=" + Index + "&INDEX=https://evil.example/x.json").Index!.AbsoluteUri);
    }

    [Fact]
    public void DuplicateKeyFirstBadStillRejects()
        => Rejected("gear-mod-manager://add-index?url=http://evil.example/x.json&url=" + Index);

    [Theory]
    [InlineData("  gear-mod-manager://add-index?url=https://example.com/mods/mod-index.json  ")]
    [InlineData("\"gear-mod-manager://add-index?url=https://example.com/mods/mod-index.json\"")]
    [InlineData("  \"gear-mod-manager://add-index?url=https://example.com/mods/mod-index.json\"  ")]
    [InlineData("\tgear-mod-manager://add-index?url=https://example.com/mods/mod-index.json\r\n")]
    public void QuotesAndWhitespaceAroundTheLinkAreTrimmed(string text)
        => Assert.Equal(Index, Parsed<ManagerLink.AddIndex>(text).Url.AbsoluteUri);

    [Fact]
    public void WhitespaceAroundValuesIsTrimmed()
    {
        Assert.Equal("m", Parsed<ManagerLink.Install>("gear-mod-manager://install?mod=%20m%20").ModId);
        Assert.Equal(Index, Parsed<ManagerLink.AddIndex>("gear-mod-manager://add-index?url=%20" + Index + "%20").Url.AbsoluteUri);
    }

    [Fact]
    public void InvalidPercentEscapeDoesNotThrow()
    {
        Rejected("gear-mod-manager://install?mod=%ZZ");
        Rejected("gear-mod-manager://install?mod=%");
    }

    [Fact]
    public void EmptyPairsAreIgnored()
        => Assert.Equal("m", Parsed<ManagerLink.Install>("gear-mod-manager://install?&&mod=m&").ModId);

    [Theory]
    [InlineData("https://example.com/mod-index.json")]
    [InlineData("https://example.com/mod-index.json?a=1&b=2")]
    [InlineData("https://example.com/mod-index.json?q=a b&x=%26")]
    [InlineData("https://example.com/mod-index.json?redirect=https://other.example/x?y=1#frag")]
    [InlineData("https://example.com/m\u00e4/\u65e5\u672c\u8a9e.json")]
    [InlineData("https://example.com/mods/mod-index.json?name=caf\u00e9&emoji=\U0001F600")]
    [InlineData("https://example.com/a+b/c%20d.json")]
    [InlineData("https://github.com/owner/repo")]
    public void ForAddIndexRoundTrips(string address)
    {
        string link = ManagerLink.ForAddIndex(address);
        Assert.True(ManagerLink.LooksLikeLink(link));
        Assert.True(link.Length < ManagerLink.MaxLength);
        Assert.DoesNotContain('#', link);

        Assert.True(SourceUrl.TryParse(address, out Uri? expected, out _));
        Assert.Equal(expected!.AbsoluteUri, Parsed<ManagerLink.AddIndex>(link).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("mod_1", null)]
    [InlineData("mod-2", "")]
    [InlineData("mod", "https://example.com/mod-index.json")]
    [InlineData("mod", "https://example.com/mod-index.json?a=1&b=2#frag")]
    [InlineData("mod", "https://example.com/caf\u00e9/\u65e5\u672c.json?x=y&z=\u00fc")]
    [InlineData("mod", "https://github.com/o/r")]
    public void ForInstallRoundTrips(string mod, string? index)
    {
        string link = ManagerLink.ForInstall(mod, index);
        var parsed = Parsed<ManagerLink.Install>(link);
        Assert.Equal(mod, parsed.ModId);
        if (string.IsNullOrEmpty(index))
        {
            Assert.Null(parsed.Index);
            Assert.DoesNotContain("index=", link, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(SourceUrl.TryParse(index, out Uri? expected, out _));
            Assert.Equal(expected!.AbsoluteUri, parsed.Index!.AbsoluteUri);
        }
    }

    [Fact]
    public void ForInstallWithInvalidModIdProducesALinkThatIsRejected()
        => Rejected(ManagerLink.ForInstall("../evil"));

    [Fact]
    public void ForAddIndexKeepsAmpersandsInsideTheValue()
    {
        string link = ManagerLink.ForAddIndex("https://example.com/i.json?a=1&url=https://evil.example/x.json");
        Assert.Equal("https://example.com/i.json?a=1&url=https://evil.example/x.json", Parsed<ManagerLink.AddIndex>(link).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("gear-mod-manager://add-index?url=x", true)]
    [InlineData("gear-mod-manager:", true)]
    [InlineData("GEAR-MOD-MANAGER:activate", true)]
    [InlineData("Gear-Mod-Manager://x", true)]
    [InlineData("gear-mod-manager", false)]
    [InlineData("gear-mod-manager-x://a", false)]
    [InlineData(" gear-mod-manager://x", false)]
    [InlineData("\"gear-mod-manager://x\"", false)]
    [InlineData("--minimized", false)]
    [InlineData("https://gear-mod-manager:", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeLink(string? text, bool expected) => Assert.Equal(expected, ManagerLink.LooksLikeLink(text));
}
