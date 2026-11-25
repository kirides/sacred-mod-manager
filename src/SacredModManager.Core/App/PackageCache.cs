using System.Security.Cryptography;
using System.Text;
using SacredModManager.Core.Index;

namespace SacredModManager.Core.App;

/// <summary>
/// Downloaded release archives (any supported format), kept under <c>packages\</c> by URL so that browsing, installing and reading thumbnails share one download.
/// An archive is asked for again with its ETag (an unchanged one costs a short answer); a download that does not match the index's SHA-256
/// is thrown away. One download per URL runs at a time.
/// </summary>
public sealed class PackageCache(IFetcher fetcher, string directory)
{
    public const long MaxPackageBytes = 500L * 1024 * 1024;

    private readonly Dictionary<string, Task<string>> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Makes sure the package is on disk (downloading or refreshing it) and returns its path.
    /// <paramref name="force"/> downloads it again without asking whether the copy on disk is current (for a copy known to be behind, e.g. one made while the host still served the previous release).
    /// </summary>
    public Task<string> GetAsync(Uri url, string? sha256, IProgress<(long Done, long Total)>? progress, CancellationToken ct, bool force = false)
    {
        string key = SourceUrl.Key(url);
        string flightKey = force ? key + "|force" : key;
        lock (_lock)
        {
            if (!_inFlight.TryGetValue(flightKey, out Task<string>? task))
            {
                task = RunAsync(url, key, flightKey, sha256, progress, force);
                _inFlight[flightKey] = task;
            }

            return task.WaitAsync(ct);
        }
    }

    /// <summary>The download itself, not tied to the first caller's token; it leaves the in-flight table before it completes, so the next call starts a new one.</summary>
    private async Task<string> RunAsync(Uri url, string key, string flightKey, string? sha256, IProgress<(long Done, long Total)>? progress, bool force)
    {
        await Task.Yield();
        try
        {
            return await FetchAsync(url, key, sha256, progress, force, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _inFlight.Remove(flightKey);
            }
        }
    }

    /// <summary>The cached copy's path without any network, or null.</summary>
    public string? Cached(Uri url)
    {
        string path = PathFor(SourceUrl.Key(url));
        return File.Exists(path) ? path : null;
    }

    private async Task<string> FetchAsync(Uri url, string key, string? sha256, IProgress<(long Done, long Total)>? progress, bool force, CancellationToken ct)
    {
        string path = PathFor(key);
        MigrateLegacy(path);
        string etagFile = path + ".etag";
        string? etag = !force && File.Exists(etagFile) ? await File.ReadAllTextAsync(etagFile, ct).ConfigureAwait(false) : null;
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

    /// <summary>The cache file is named by the hash of the URL only, not by a format: the format is found from the bytes when the file is opened.</summary>
    private string PathFor(string key)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        string path = Path.Combine(directory, hash + ".pkg");
        MigrateLegacy(path);
        return path;
    }

    /// <summary>Older versions named the cache file <c>.zip</c>: move it (and its etag) to the new name once.</summary>
    private static void MigrateLegacy(string path)
    {
        string legacy = Path.ChangeExtension(path, ".zip");
        try
        {
            if (!File.Exists(path) && File.Exists(legacy))
            {
                File.Move(legacy, path);
                if (File.Exists(legacy + ".etag"))
                {
                    File.Move(legacy + ".etag", path + ".etag", overwrite: true);
                }
            }
        }
        catch (IOException)
        {
            // the old file stays; the package is downloaded again
        }
    }
}
