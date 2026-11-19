using System.Security.Cryptography;
using System.Text;
using SacredModManager.Core.Index;

namespace SacredModManager.Core.App;

/// <summary>
/// Downloaded release zips, kept under <c>packages\</c> by URL so that browsing, installing and reading thumbnails share one download.
/// A zip is asked for again with its ETag (an unchanged one costs a short answer); a download that does not match the index's SHA-256
/// is thrown away. One download per URL runs at a time.
/// </summary>
public sealed class PackageCache(IFetcher fetcher, string directory)
{
    public const long MaxPackageBytes = 500L * 1024 * 1024;

    private readonly Dictionary<string, Task<string>> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Makes sure the package is on disk (downloading or refreshing it) and returns its path.</summary>
    public Task<string> GetAsync(Uri url, string? sha256, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        string key = SourceUrl.Key(url);
        lock (_lock)
        {
            if (!_inFlight.TryGetValue(key, out Task<string>? task))
            {
                task = FetchAsync(url, key, sha256, progress, CancellationToken.None);
                _inFlight[key] = task;
                _ = task.ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        _inFlight.Remove(key);
                    }
                }, TaskScheduler.Default);
            }

            return task.WaitAsync(ct);
        }
    }

    /// <summary>The cached copy's path without any network, or null.</summary>
    public string? Cached(Uri url)
    {
        string path = PathFor(SourceUrl.Key(url));
        return File.Exists(path) ? path : null;
    }

    private async Task<string> FetchAsync(Uri url, string key, string? sha256, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        string path = PathFor(key);
        string etagFile = path + ".etag";
        string? etag = File.Exists(etagFile) ? await File.ReadAllTextAsync(etagFile, ct).ConfigureAwait(false) : null;
        FetchResult? result = await fetcher.DownloadAsync(url, path, MaxPackageBytes, etag, progress, ct).ConfigureAwait(false);
        if (result is not null)
        {
            await File.WriteAllTextAsync(etagFile, result.ETag ?? string.Empty, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(sha256))
        {
            string actual;
            await using (FileStream s = File.OpenRead(path))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
            }

            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                File.Delete(etagFile);
                throw new FetchException("the download does not match the checksum of the index (it may be damaged or changed)");
            }
        }

        return path;
    }

    private string PathFor(string key)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(directory, hash + ".zip");
    }
}
