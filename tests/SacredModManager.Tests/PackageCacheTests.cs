using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using Xunit;

namespace SacredModManager.Tests;

public sealed class PackageCacheTests : IDisposable
{
    private static readonly Uri Url = new("https://example.com/pkg.zip");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private PackageCache Cache(FakeFetcher f) => new(f, _dir.Combine("packages"));

    private static readonly byte[] Bytes = Pack.Zip(("mods/a/mod.lua", Pack.ModLua("a")));

    [Fact]
    public async Task DownloadsAndReturnsThePath()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes);
        PackageCache c = Cache(f);
        Assert.Null(c.Cached(Url));
        string path = await c.GetAsync(Url, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(Bytes, File.ReadAllBytes(path));
        Assert.Equal(path, c.Cached(Url));
        Assert.StartsWith(_dir.Combine("packages"), path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".pkg", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchingChecksumPassesCaseInsensitively()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes);
        PackageCache c = Cache(f);
        string sha = Pack.Sha256(Bytes);
        Assert.True(File.Exists(await c.GetAsync(Url, sha, null, TestContext.Current.CancellationToken)));
        Assert.True(File.Exists(await c.GetAsync(Url, sha.ToUpperInvariant(), null, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ChecksumMismatchThrowsAndDeletesTheFile()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes);
        PackageCache c = Cache(f);
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => c.GetAsync(Url, new string('0', 64), null, TestContext.Current.CancellationToken));
        Assert.Contains("checksum", ex.Message, StringComparison.Ordinal);
        Assert.Null(c.Cached(Url));
        Assert.Empty(Directory.GetFiles(_dir.Combine("packages")));
    }

    [Fact]
    public async Task ChecksumMismatchAfterAnEarlierGoodDownloadAlsoDeletes()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes);
        PackageCache c = Cache(f);
        await c.GetAsync(Url, Pack.Sha256(Bytes), null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FetchException>(() => c.GetAsync(Url, new string('1', 64), null, TestContext.Current.CancellationToken));
        Assert.Null(c.Cached(Url));
    }

    [Fact]
    public async Task WithoutAChecksumNothingIsVerified()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, "anything");
        Assert.True(File.Exists(await Cache(f).GetAsync(Url, string.Empty, null, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SecondCallSendsTheEtagAndKeepsTheFileWhenNotModified()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes, etag: "\"v1\"");
        PackageCache c = Cache(f);
        string first = await c.GetAsync(Url, null, null, TestContext.Current.CancellationToken);
        Assert.Equal("\"v1\"", File.ReadAllText(first + ".etag"));
        DateTime stamp = File.GetLastWriteTimeUtc(first);

        string second = await c.GetAsync(Url, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(2, f.CountCalls(Url.AbsoluteUri));
        Assert.Equal(Bytes, File.ReadAllBytes(second));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second));
        Assert.Equal("\"v1\"", File.ReadAllText(second + ".etag"));
    }

    [Fact]
    public async Task ChangedPackageIsDownloadedAgainAndTheEtagUpdated()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes, etag: "\"v1\"");
        PackageCache c = Cache(f);
        string path = await c.GetAsync(Url, null, null, TestContext.Current.CancellationToken);
        byte[] newer = Pack.Zip(("mods/a/mod.lua", Pack.ModLua("a", "2")));
        f.Add(Url.AbsoluteUri, newer, etag: "\"v2\"");
        await c.GetAsync(Url, Pack.Sha256(newer), null, TestContext.Current.CancellationToken);
        Assert.Equal(newer, File.ReadAllBytes(path));
        Assert.Equal("\"v2\"", File.ReadAllText(path + ".etag"));
    }

    [Fact]
    public async Task NotModifiedWithAWrongChecksumStillFails()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes);
        PackageCache c = Cache(f);
        await c.GetAsync(Url, null, null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FetchException>(() => c.GetAsync(Url, new string('a', 64), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentCallsForTheSameUrlDownloadOnce()
    {
        var f = new FakeFetcher { Delay = TimeSpan.FromMilliseconds(300) }.Add(Url.AbsoluteUri, Bytes);
        PackageCache c = Cache(f);
        string[] paths = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => c.GetAsync(Url, null, null, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        Assert.Single(paths.Distinct());
        Assert.Equal(1, f.CountCalls(Url.AbsoluteUri));
        Assert.Equal(Bytes, File.ReadAllBytes(paths[0]));
    }

    [Fact]
    public async Task UrlsDifferingOnlyByFragmentShareOneDownload()
    {
        var f = new FakeFetcher { Delay = TimeSpan.FromMilliseconds(200) }.Add(Url.AbsoluteUri, Bytes);
        PackageCache c = Cache(f);
        string[] paths = await Task.WhenAll(
            c.GetAsync(Url, null, null, TestContext.Current.CancellationToken),
            c.GetAsync(new Uri(Url + "#x"), null, null, TestContext.Current.CancellationToken));
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(1, f.CountCalls(Url.AbsoluteUri));
    }

    [Fact]
    public async Task DifferentUrlsUseDifferentFilesAndDownloadSeparately()
    {
        var other = new Uri("https://example.com/other.zip");
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes).Add(other.AbsoluteUri, "other");
        PackageCache c = Cache(f);
        string a = await c.GetAsync(Url, null, null, TestContext.Current.CancellationToken);
        string b = await c.GetAsync(other, null, null, TestContext.Current.CancellationToken);
        Assert.NotEqual(a, b);
        Assert.Equal("other", File.ReadAllText(b));
        Assert.Equal(Bytes, File.ReadAllBytes(a));
    }

    [Fact]
    public async Task FailedDownloadThrowsAndCanBeRetriedLater()
    {
        var f = new FakeFetcher();
        PackageCache c = Cache(f);
        await Assert.ThrowsAsync<FetchException>(() => c.GetAsync(Url, null, null, TestContext.Current.CancellationToken));
        Assert.Null(c.Cached(Url));
        f.Add(Url.AbsoluteUri, Bytes);

        // the finished (failed) task is dropped from the in-flight table asynchronously: allow a moment for it
        string? path = null;
        for (int i = 0; i < 40 && path is null; i++)
        {
            try
            {
                path = await c.GetAsync(Url, null, null, TestContext.Current.CancellationToken);
            }
            catch (FetchException)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.NotNull(path);
        Assert.Equal(Bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ProgressIsForwarded()
    {
        var f = new FakeFetcher().Add(Url.AbsoluteUri, Bytes);
        var reports = new List<(long, long)>();
        await Cache(f).GetAsync(Url, null, new SyncProgress<(long Done, long Total)>(reports.Add), TestContext.Current.CancellationToken);
        Assert.Contains((Bytes.LongLength, Bytes.LongLength), reports);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
