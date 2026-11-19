using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SacredModManager.Core.Index;

namespace SacredModManager.Tests;

/// <summary>A unique temp directory that is deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>An in-memory <see cref="IFetcher"/>: URL (as <see cref="Uri.AbsoluteUri"/>) to bytes; counts every call.</summary>
internal sealed class FakeFetcher : IFetcher
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _etags = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _calls = new();

    public TimeSpan Delay { get; set; }

    public IReadOnlyCollection<string> Calls => _calls;

    public int CountCalls(string url) => _calls.Count(c => c == new Uri(url).AbsoluteUri);

    public FakeFetcher Add(string url, byte[] bytes, string? etag = null)
    {
        string key = new Uri(url).AbsoluteUri;
        _files[key] = bytes;
        _etags[key] = etag ?? "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..8] + "\"";
        return this;
    }

    public FakeFetcher Add(string url, string text, string? etag = null) => Add(url, Encoding.UTF8.GetBytes(text), etag);

    public async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken ct)
    {
        _calls.Enqueue(uri.AbsoluteUri);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (!_files.TryGetValue(uri.AbsoluteUri, out byte[]? bytes))
        {
            throw new FetchException("404 Not Found for " + uri.Host + uri.AbsolutePath);
        }

        if (bytes.Length > maxBytes)
        {
            throw new FetchException("too large");
        }

        return bytes;
    }

    public async Task<FetchResult?> DownloadAsync(Uri uri, string destination, long maxBytes, string? etag, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        _calls.Enqueue(uri.AbsoluteUri);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (!_files.TryGetValue(uri.AbsoluteUri, out byte[]? bytes))
        {
            throw new FetchException("404 Not Found for " + uri.Host + uri.AbsolutePath);
        }

        string current = _etags[uri.AbsoluteUri];
        if (etag == current && File.Exists(destination))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, bytes, ct);
        progress?.Report((bytes.Length, bytes.Length));
        return new FetchResult(bytes.Length, current);
    }
}

/// <summary>Builds zips and mod.lua texts for tests.</summary>
internal static class Pack
{
    public static byte[] Zip(params (string Name, string Content)[] entries)
        => Zip(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using Stream s = e.Open();
                s.Write(content);
            }
        }

        return ms.ToArray();
    }

    public static string WriteZip(string dir, string fileName, params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Zip(entries));
        return path;
    }

    public static string ModLua(string id, string version = "1.0.0", bool? enabled = null, string? requires = null)
        => "return { id = '" + id + "', name = 'Mod " + id + "', version = '" + version + "'"
           + (enabled is { } e ? ", enabled = " + (e ? "true" : "false") : string.Empty)
           + (requires is null ? string.Empty : ", requires = {" + requires + "}") + " }";

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
