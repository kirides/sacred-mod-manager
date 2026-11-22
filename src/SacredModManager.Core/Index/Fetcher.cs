namespace SacredModManager.Core.Index;

/// <summary>Reads the bytes behind a URL (http(s) or file); a seam so tests need no network.</summary>
public interface IFetcher
{
    Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken ct);

    /// <summary>Downloads to a file. <paramref name="etag"/> is the validator of a copy the caller holds; null result means "not modified".</summary>
    Task<FetchResult?> DownloadAsync(Uri uri, string destination, long maxBytes, string? etag, IProgress<(long Done, long Total)>? progress, CancellationToken ct);
}

public sealed record FetchResult(long Bytes, string? ETag);

/// <summary>A download or request that failed. <see cref="Transient"/>: it may work on the next try (the connection dropped, the server is busy).</summary>
public sealed class FetchException(string message, Exception? inner = null, bool transient = false) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>The real fetcher: <see cref="HttpClient"/> for https, the file system for file URLs.</summary>
public sealed class Fetcher : IFetcher, IDisposable
{
    /// <summary>The pauses between the tries of a request that failed for a reason that may pass (3 tries in all).</summary>
    public static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1500)];

    private readonly HttpClient _http;
    private readonly TimeSpan[] _retryDelays;

    public Fetcher(HttpClient? http = null, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _retryDelays = [.. retryDelays ?? DefaultRetryDelays];
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(100) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SacredModManager/1.0");
        }
    }

    public void Dispose() => _http.Dispose();

    public Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken ct)
        => WithRetryAsync(() => GetBytesOnceAsync(uri, maxBytes, ct), uri, ct);

    public Task<FetchResult?> DownloadAsync(Uri uri, string destination, long maxBytes, string? etag, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
        => WithRetryAsync(() => DownloadOnceAsync(uri, destination, maxBytes, etag, progress, ct), uri, ct);

    /// <summary>Tries again (after a short pause) when the connection was dropped or the server was busy; never for "not found" and the like.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, Uri uri, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (FetchException ex) when (ex.Transient && attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false);
            }
            catch (FetchException ex) when (ex.Transient && attempt > 0)
            {
                throw new FetchException(ex.Message + " Tried " + (attempt + 1) + " times.", ex.InnerException, transient: false);
            }
        }
    }

    private async Task<byte[]> GetBytesOnceAsync(Uri uri, long maxBytes, CancellationToken ct)
    {
        try
        {
            if (uri.IsFile)
            {
                var info = new FileInfo(uri.LocalPath);
                if (!info.Exists)
                {
                    throw new FetchException("file not found: " + uri.LocalPath);
                }

                if (info.Length > maxBytes)
                {
                    throw new FetchException("the file is larger than " + (maxBytes / 1024) + " KB");
                }

                return await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false);
            }

            using HttpResponseMessage response = await SendFollowingAsync(new HttpRequestMessage(HttpMethod.Get, uri), ct).ConfigureAwait(false);
            CheckStatus(response, uri);
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new FetchException("the answer is larger than " + (maxBytes / 1024) + " KB");
            }

            await using Stream s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await CopyLimitedAsync(s, ms, maxBytes, null, 0, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FetchException(Describe(ex, uri), ex, transient: ex is not UnauthorizedAccessException);
        }
    }

    private async Task<FetchResult?> DownloadOnceAsync(Uri uri, string destination, long maxBytes, string? etag, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        string tmp = destination + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            if (uri.IsFile)
            {
                var info = new FileInfo(uri.LocalPath);
                if (!info.Exists)
                {
                    throw new FetchException("file not found: " + uri.LocalPath);
                }

                if (info.Length > maxBytes)
                {
                    throw new FetchException("the file is larger than " + (maxBytes / (1024 * 1024)) + " MB");
                }

                string stamp = info.Length + "-" + info.LastWriteTimeUtc.Ticks;
                if (etag == stamp && File.Exists(destination))
                {
                    return null;
                }

                File.Copy(uri.LocalPath, tmp, overwrite: true);
                File.Move(tmp, destination, overwrite: true);
                progress?.Report((info.Length, info.Length));
                return new FetchResult(info.Length, stamp);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(etag) && File.Exists(destination))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using HttpResponseMessage response = await SendFollowingAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                return null;
            }

            CheckStatus(response, uri);
            long total = response.Content.Headers.ContentLength ?? -1;
            if (total > maxBytes)
            {
                throw new FetchException("the download is larger than " + (maxBytes / (1024 * 1024)) + " MB");
            }

            long written;
            await using (Stream s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                written = await CopyLimitedAsync(s, file, maxBytes, progress, total, ct).ConfigureAwait(false);
            }

            File.Move(tmp, destination, overwrite: true);
            return new FetchResult(written, response.Headers.ETag?.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FetchException(Describe(ex, uri), ex, transient: ex is not UnauthorizedAccessException);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private const int MaxRedirects = 5;

    /// <summary>
    /// Sends a request and follows redirects itself: only to https addresses (a redirect to http, or to anything else, is refused), at most <see cref="MaxRedirects"/> times.
    /// The request is consumed.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Uri current = request.RequestUri!;
        HttpRequestMessage next = request;
        for (int hops = 0; ; hops++)
        {
            HttpResponseMessage response = await _http.SendAsync(next, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
            {
                return response;
            }

            Uri target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
            response.Dispose();
            if (target.Scheme != Uri.UriSchemeHttps && !(current.Scheme == Uri.UriSchemeHttp && target.IsLoopback && target.Scheme == Uri.UriSchemeHttp))
            {
                throw new FetchException($"refused: {current.Host} redirects to {target.Scheme}://{target.Host}, which is not https");
            }

            if (hops >= MaxRedirects)
            {
                throw new FetchException("too many redirects for " + request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            }

            next = new HttpRequestMessage(HttpMethod.Get, target);
            current = target;
        }
    }

    private static void CheckStatus(HttpResponseMessage response, Uri uri)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        int code = (int)response.StatusCode;
        string where = uri.Host + uri.AbsolutePath;
        string text = code switch
        {
            403 or 429 when uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) => $"GitHub refused the request ({code}): its limit for requests without a login may be reached. Try again in a while.",
            404 => $"not found: {where}",
            _ => $"{code} {response.ReasonPhrase} for {where}",
        };
        throw new FetchException(text, null, transient: code is >= 500 and <= 599);
    }

    private static async Task<long> CopyLimitedAsync(Stream from, Stream to, long max, IProgress<(long Done, long Total)>? progress, long total, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            done += n;
            if (done > max)
            {
                throw new FetchException("the download is larger than " + (max / (1024 * 1024)) + " MB");
            }

            await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            progress?.Report((done, total));
        }

        return done;
    }

    /// <summary>A message a player can act on, not the name of a socket error.</summary>
    private static string Describe(Exception ex, Uri uri)
    {
        string host = uri.IsFile ? uri.LocalPath : uri.Host;
        Exception root = ex;
        while (root.InnerException is { } inner)
        {
            root = inner;
        }

        if (root is System.Net.Sockets.SocketException socket)
        {
            switch (socket.SocketErrorCode)
            {
                case System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted:
                    return $"The connection to {host} was closed (by the server, your provider or a firewall).";
                case System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.TryAgain or System.Net.Sockets.SocketError.NoData:
                    return $"{host} cannot be found (no internet connection, or the name is blocked).";
                case System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable:
                    return $"{host} cannot be reached (check the internet connection).";
            }
        }

        if (ex is TaskCanceledException or TimeoutException)
        {
            return $"{host} did not answer in time.";
        }

        string message = root.Message;
        return message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            ? $"The connection to {host} was closed (by the server, your provider or a firewall)."
            : message;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
