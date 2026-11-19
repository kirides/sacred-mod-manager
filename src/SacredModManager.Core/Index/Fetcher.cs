namespace SacredModManager.Core.Index;

/// <summary>Reads the bytes behind a URL (http(s) or file); a seam so tests need no network.</summary>
public interface IFetcher
{
    Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken ct);

    /// <summary>Downloads to a file. <paramref name="etag"/> is the validator of a copy the caller holds; null result means "not modified".</summary>
    Task<FetchResult?> DownloadAsync(Uri uri, string destination, long maxBytes, string? etag, IProgress<(long Done, long Total)>? progress, CancellationToken ct);
}

public sealed record FetchResult(long Bytes, string? ETag);

public sealed class FetchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The real fetcher: <see cref="HttpClient"/> for https, the file system for file URLs.</summary>
public sealed class Fetcher : IFetcher, IDisposable
{
    private readonly HttpClient _http;

    public Fetcher(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(100) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SacredModManager/1.0");
        }
    }

    public void Dispose() => _http.Dispose();

    public async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken ct)
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

            using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
            throw new FetchException(Describe(ex), ex);
        }
    }

    public async Task<FetchResult?> DownloadAsync(Uri uri, string destination, long maxBytes, string? etag, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
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

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
            throw new FetchException(Describe(ex), ex);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void CheckStatus(HttpResponseMessage response, Uri uri)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new FetchException($"{(int)response.StatusCode} {response.ReasonPhrase} for {uri.Host}{uri.AbsolutePath}");
        }
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

    private static string Describe(Exception ex) => ex is HttpRequestException { InnerException: { } inner } ? inner.Message : ex.Message;

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
