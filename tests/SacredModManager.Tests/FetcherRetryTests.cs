using System.Net;
using System.Net.Sockets;
using SacredModManager.Core.Index;
using Xunit;

namespace SacredModManager.Tests;

public class FetcherRetryTests
{
    private sealed class Handler(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(Calls));
        }
    }

    private static readonly Uri Url = new("https://example.com/idx/mod-index.json");

    private static HttpRequestException Reset() => new("The SSL connection could not be established", new IOException("read failed", new SocketException((int)SocketError.ConnectionReset)));

    private static Fetcher Make(Handler h) => new(new HttpClient(h), [TimeSpan.Zero, TimeSpan.Zero]);

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task ADroppedConnectionIsRetriedAndThenWorks()
    {
        var h = new Handler(n => n < 3 ? throw Reset() : Ok("hello"));
        byte[] bytes = await Make(h).GetBytesAsync(Url, 1024, TestContext.Current.CancellationToken);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task AConnectionThatNeverWorksGivesAPlainMessageAfterThreeTries()
    {
        var h = new Handler(_ => throw Reset());
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => Make(h).GetBytesAsync(Url, 1024, TestContext.Current.CancellationToken));
        Assert.Equal(3, h.Calls);
        Assert.Contains("example.com", ex.Message, StringComparison.Ordinal);
        Assert.Contains("closed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("3 times", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SocketException", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForciblyClosedMessageWithoutASocketErrorIsTranslatedToo()
    {
        var h = new Handler(_ => throw new HttpRequestException("x", new IOException("An existing connection was forcibly closed by the remote host.")));
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => Make(h).GetBytesAsync(Url, 1024, TestContext.Current.CancellationToken));
        Assert.Contains("was closed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABusyServerIsRetried()
    {
        var h = new Handler(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok("fine"));
        Assert.Equal("fine", System.Text.Encoding.UTF8.GetString(await Make(h).GetBytesAsync(Url, 1024, TestContext.Current.CancellationToken)));
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task NotFoundIsNotRetried()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => Make(h).GetBytesAsync(Url, 1024, TestContext.Current.CancellationToken));
        Assert.Equal(1, h.Calls);
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubsRateLimitHasItsOwnMessageAndIsNotRetried()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => Make(h).GetBytesAsync(new Uri("https://api.github.com/repos/o/r/releases"), 1024, TestContext.Current.CancellationToken));
        Assert.Equal(1, h.Calls);
        Assert.Contains("limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRequestIsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var h = new Handler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(h).GetBytesAsync(Url, 1024, cts.Token));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task ADownloadIsRetriedAndLeavesNoPartFile()
    {
        using var dir = new TempDir();
        string target = dir.Combine("pkg.zip");
        var h = new Handler(n => n == 1 ? throw Reset() : Ok("zipdata"));
        FetchResult? result = await Make(h).DownloadAsync(Url, target, 1024, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(7, result!.Bytes);
        Assert.Equal("zipdata", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task ALocalFileThatIsMissingIsNotRetried()
    {
        using var dir = new TempDir();
        var h = new Handler(_ => Ok("x"));
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => Make(h).GetBytesAsync(new Uri(dir.Combine("nope.json")), 1024, TestContext.Current.CancellationToken));
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Calls);
    }
}
