using System.Formats.Tar;
using System.IO.Compression;

namespace SacredModManager.Core.Mods;

/// <summary>The archive formats <see cref="ZipPackage"/> reads.</summary>
public enum ArchiveFormat
{
    Zip,
    Tar,
    TarGzip,
    TarZstd,
}

/// <summary>Recognises the format of an archive file by its first bytes (the file extension only decides for a plain tar without a "ustar" mark).</summary>
public static class ArchiveFormats
{
    public static ArchiveFormat Detect(string path)
    {
        byte[] head = new byte[512];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        return Detect(head.AsSpan(0, n), path);
    }

    internal static ArchiveFormat Detect(ReadOnlySpan<byte> head, string fileName)
    {
        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6) || (head[2] == 7 && head[3] == 8)))
        {
            return ArchiveFormat.Zip;
        }

        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            return ArchiveFormat.TarGzip;
        }

        if (head.Length >= 4 && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD)
        {
            return ArchiveFormat.TarZstd;
        }

        if ((head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8)) || fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveFormat.Tar;
        }

        throw new PackageException("not a supported archive (zip, tar, tar.gz or tar.zst)");
    }
}

/// <summary>One format's way to list and read the files of an archive.</summary>
internal abstract class ArchiveBackend : IDisposable
{
    /// <summary>Normalized (forward slashes) names of the regular files and their sizes.</summary>
    public Dictionary<string, long> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calls <paramref name="read"/> with the content of each wanted name that exists (in the archive's own order for tar).</summary>
    public abstract void Visit(ISet<string> wanted, Action<string, Stream> read);

    public virtual void Dispose()
    {
    }
}

internal sealed class ZipBackend : ArchiveBackend
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ZipBackend(string path)
    {
        try
        {
            _zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageException("not a valid zip file: " + ex.Message);
        }
        catch (IOException ex)
        {
            throw new PackageException("the zip cannot be read: " + ex.Message);
        }

        foreach (ZipArchiveEntry e in _zip.Entries)
        {
            string name = e.FullName.Replace('\\', '/');
            if (!name.EndsWith('/'))
            {
                _byName[name] = e;
                Entries[name] = e.Length;
            }
        }

        if (Entries.Count > ZipPackage.MaxFiles)
        {
            _zip.Dispose();
            throw new PackageException($"the zip holds more than {ZipPackage.MaxFiles} files");
        }
    }

    public override void Visit(ISet<string> wanted, Action<string, Stream> read)
    {
        foreach (string name in wanted)
        {
            if (_byName.TryGetValue(name, out ZipArchiveEntry? e))
            {
                using Stream s = e.Open();
                read(name, s);
            }
        }
    }

    public override void Dispose()
    {
        _zip.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// A tar, tar.gz or tar.zst. The stream cannot be searched, so the file is scanned once on open (names, types and sizes are checked there) and
/// read again from the start for the content. Only regular files and folders are accepted: a link, device, fifo or any other kind of entry makes
/// the whole archive unusable, and so does an unsafe name.
/// </summary>
internal sealed class TarBackend : ArchiveBackend
{
    /// <summary>What a tar may unpack to when the content is skipped over while scanning (a gzip or zstd bomb stops here).</summary>
    internal const long MaxScanBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _path;
    private readonly ArchiveFormat _format;

    public TarBackend(string path, ArchiveFormat format)
    {
        _path = path;
        _format = format;
        try
        {
            Walk(null, scan: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or EndOfStreamException)
        {
            throw new PackageException("not a valid " + Describe() + " archive: " + ex.Message);
        }
    }

    private string Describe() => _format switch { ArchiveFormat.TarGzip => "tar.gz", ArchiveFormat.TarZstd => "tar.zst", _ => "tar" };

    public override void Visit(ISet<string> wanted, Action<string, Stream> read)
    {
        try
        {
            Walk((wanted, read), scan: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new PackageException("the " + Describe() + " archive cannot be read: " + ex.Message);
        }
    }

    private Stream OpenDecompressed()
    {
        var raw = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        return _format switch
        {
            ArchiveFormat.TarGzip => new GZipStream(raw, CompressionMode.Decompress),
            ArchiveFormat.TarZstd => new ZstandardStream(raw, CompressionMode.Decompress),
            _ => raw,
        };
    }

    private void Walk((ISet<string> Wanted, Action<string, Stream> Read)? visit, bool scan)
    {
        using Stream decompressed = OpenDecompressed();
        using var limited = new LimitedStream(decompressed, MaxScanBytes);
        using var reader = new TarReader(limited, leaveOpen: true);
        long declared = 0;
        int seen = 0;
        TarEntry? e;
        while ((e = reader.GetNextEntry(copyData: false)) is not null)
        {
            switch (e.EntryType)
            {
                case TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes:
                    continue;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    break;
                default:
                    throw new PackageException($"the tar holds an entry that is not a plain file or folder ({e.EntryType}: {e.Name}); links and devices are not accepted");
            }

            string name = NormalizeName(e.Name);
            if (scan)
            {
                ZipPackage.CheckName(name, e.Name);
                if (!Entries.TryAdd(name, e.Length))
                {
                    throw new PackageException("the tar holds the name twice: " + name);
                }

                declared += e.Length;
                if (Entries.Count > ZipPackage.MaxFiles)
                {
                    throw new PackageException($"the archive holds more than {ZipPackage.MaxFiles} files");
                }

                if (declared > MaxScanBytes)
                {
                    throw new PackageException("the archive unpacks to more than " + (MaxScanBytes / (1024 * 1024)) + " MB");
                }
            }
            else if (visit!.Value.Wanted.Contains(name) && e.DataStream is { } data)
            {
                visit.Value.Read(name, data);
                if (++seen >= visit.Value.Wanted.Count)
                {
                    return;
                }
            }
        }
    }

    internal static string NormalizeName(string name)
    {
        string n = name.Replace('\\', '/');
        while (n.StartsWith("./", StringComparison.Ordinal))
        {
            n = n[2..];
        }

        return n;
    }
}

/// <summary>A read-only view that throws once more than a limit of bytes was read.</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        _read += n;
        if (_read > limit)
        {
            throw new PackageException("the archive unpacks to more than " + (limit / (1024 * 1024)) + " MB");
        }

        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
