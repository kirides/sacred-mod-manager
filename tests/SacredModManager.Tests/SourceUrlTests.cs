using SacredModManager.Core.Index;
using Xunit;

namespace SacredModManager.Tests;

public class SourceUrlTests
{
    private static Uri Ok(string text)
    {
        Assert.True(SourceUrl.TryParse(text, out Uri? uri, out string? error), error);
        Assert.Null(error);
        return uri!;
    }

    private static void Rejected(string? text)
    {
        Assert.False(SourceUrl.TryParse(text, out Uri? uri, out string? error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/")]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://www.github.com/owner/repo")]
    [InlineData("https://GitHub.com/owner/repo")]
    [InlineData("github.com/owner/repo")]
    [InlineData("  \"https://github.com/owner/repo\"  ")]
    [InlineData("https://github.com/owner/repo#readme")]
    public void GitHubRepositoryUrlsBecomeTheRawIndex(string text)
        => Assert.Equal("https://raw.githubusercontent.com/owner/repo/HEAD/mod-index.json", Ok(text).AbsoluteUri);

    [Theory]
    [InlineData("https://github.com/o/r/blob/main/mod-index.json", "https://raw.githubusercontent.com/o/r/main/mod-index.json")]
    [InlineData("https://github.com/o/r/blob/main/sub/dir/my.json", "https://raw.githubusercontent.com/o/r/main/sub/dir/my.json")]
    [InlineData("https://github.com/o/r/raw/v1/mod-index.json", "https://raw.githubusercontent.com/o/r/v1/mod-index.json")]
    public void GitHubBlobUrlsBecomeRawUrls(string text, string expected)
        => Assert.Equal(expected, Ok(text).AbsoluteUri);

    [Fact]
    public void OtherGitHubPagesAreLeftAlone()
        => Assert.Equal("https://github.com/o/r/tree/main", Ok("https://github.com/o/r/tree/main").AbsoluteUri);

    [Fact]
    public void GitHubOwnerOnlyIsLeftAlone()
        => Assert.Equal("https://github.com/owner", Ok("https://github.com/owner").AbsoluteUri);

    [Fact]
    public void GitHubNameWithStrangeCharactersIsNotRewritten()
        => Assert.Equal("https://github.com/ow%20ner/repo", Ok("https://github.com/ow%20ner/repo").AbsoluteUri);

    [Theory]
    [InlineData("example.com/mods/mod-index.json", "https://example.com/mods/mod-index.json")]
    [InlineData("https://example.com/mod-index.json", "https://example.com/mod-index.json")]
    [InlineData("HTTPS://EXAMPLE.COM/Mod-Index.json", "https://example.com/Mod-Index.json")]
    [InlineData("https://example.com/mod-index.json#frag", "https://example.com/mod-index.json")]
    [InlineData("https://example.com/mod-index.json?x=1#frag", "https://example.com/mod-index.json?x=1")]
    public void HttpsAddressesAreKeptAndBareHostsGetHttps(string text, string expected)
        => Assert.Equal(expected, Ok(text).AbsoluteUri);

    [Theory]
    [InlineData("http://localhost/mod-index.json")]
    [InlineData("http://localhost:8080/mod-index.json")]
    [InlineData("http://127.0.0.1:5000/x.json")]
    [InlineData("http://[::1]/x.json")]
    public void HttpIsAllowedForLoopbackOnly(string text) => Assert.Equal(Uri.UriSchemeHttp, Ok(text).Scheme);

    [Theory]
    [InlineData("http://example.com/mod-index.json")]
    [InlineData("http://192.168.1.5/mod-index.json")]
    [InlineData("http://github.com/o/r")]
    [InlineData("ftp://example.com/x")]
    [InlineData("ws://example.com/x")]
    public void InsecureOrOtherSchemesAreRejected(string text) => Rejected(text);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("://")]
    [InlineData("https://")]
    [InlineData("not a url with spaces")]
    [InlineData("javascript:alert(1)")]
    public void GarbageIsRejected(string? text) => Rejected(text);

    [Fact]
    public void FolderPathBecomesItsIndexFileUri()
    {
        using var dir = new TempDir();
        Uri u = Ok(dir.Path);
        Assert.True(u.IsFile);
        Assert.Equal(Path.Combine(dir.Path, "mod-index.json"), u.LocalPath, ignoreCase: true);
        Assert.Equal(u.LocalPath, Ok("\"" + dir.Path + "\"").LocalPath);
        Assert.Equal(u.LocalPath, Ok(dir.Path + Path.DirectorySeparatorChar).LocalPath, ignoreCase: true);
    }

    [Fact]
    public void FilePathStaysAFileUri()
    {
        using var dir = new TempDir();
        string file = dir.Combine("my-index.json");
        File.WriteAllText(file, "{}");
        Uri u = Ok(file);
        Assert.True(u.IsFile);
        Assert.Equal(file, u.LocalPath, ignoreCase: true);
    }

    [Fact]
    public void MissingLocalPathIsStillAFileUri()
    {
        using var dir = new TempDir();
        string file = dir.Combine("nope", "x.json");
        Assert.Equal(file, Ok(file).LocalPath, ignoreCase: true);
    }

    [Fact]
    public void FileUrlOfAFolderMeansItsIndex()
    {
        using var dir = new TempDir();
        Uri u = Ok(new Uri(dir.Path).AbsoluteUri);
        Assert.Equal(Path.Combine(dir.Path, "mod-index.json"), u.LocalPath, ignoreCase: true);
    }

    [Fact]
    public void ParseThrowsIndexExceptionOnGarbage()
    {
        Assert.Throws<IndexException>(() => SourceUrl.Parse(""));
        Assert.Equal("https://example.com/a.json", SourceUrl.Parse("example.com/a.json").AbsoluteUri);
    }

    [Fact]
    public void KeyIgnoresFragmentAndHostCase()
    {
        Assert.Equal(SourceUrl.Key(new Uri("https://EXAMPLE.com/a#x")), SourceUrl.Key(new Uri("https://example.com/a")));
        Assert.NotEqual(SourceUrl.Key(new Uri("https://example.com/a?x=1")), SourceUrl.Key(new Uri("https://example.com/a")));
    }

    [Theory]
    [InlineData("https://example.com/a.zip", true)]
    [InlineData("http://example.com/a.zip", false)]
    [InlineData("http://localhost/a.zip", true)]
    [InlineData("ftp://example.com/a.zip", false)]
    [InlineData("file:///C:/a.zip", true)]
    public void IsAllowedDownload(string url, bool expected) => Assert.Equal(expected, SourceUrl.IsAllowedDownload(new Uri(url)));
}
