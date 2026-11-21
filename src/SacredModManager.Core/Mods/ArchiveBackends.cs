using System.Formats.Tar;
using System.IO.Compression;

namespace SacredModManager.Core.Mods;

/// <summary>Decides whether the first bytes of a file (and its name) are this format.</summary>
public delegate bool ArchiveSniffer(ReadOnlySpan<byte> head, string fileName);

/// <summary>
/// One archive format the manager reads: how to recognise it, which file extensions it usually has, and how to open it. Formats are listed in
/// <see cref="ArchiveFormats"/>; a new one (tar.xz, ...) is added by registering it there, nothing else needs to know about it.
/// </summary>
public sealed class ArchiveFormat
{
    public ArchiveFormat(string id, string displayName, IReadOnlyList<string> extensions, ArchiveSniffer sniff, Func<string, ArchiveBackend> open)
    {
        Id = id;
        DisplayName = displayName;
        Extensions = extensions;
        Sniff = sniff;
        Open = open;
    }

    /// <summary>Short stable name, for example <c>tar.gz</c>.</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>File name endings, lower case with the dot (<c>.tar.gz</c>, <c>.tgz</c>).</summary>
    public IReadOnlyList<string> Extensions { get; }

    internal ArchiveSniffer Sniff { get; }

    internal Func<string, ArchiveBackend> Open { get; }

    public override string ToString() => Id;

    public static readonly ArchiveFormat Zip = new("zip", "zip", [".zip"],
        (h, _) => h.Length >= 4 && h[0] == 'P' && h[1] == 'K' && ((h[2] == 3 && h[3] == 4) || (h[2] == 5 && h[3] == 6) || (h[2] == 7 && h[3] == 8)),
        path => new ZipBackend(path));

    public static readonly ArchiveFormat TarGzip = TarOf("tar.gz", [".tar.gz", ".tgz"], (h, _) => h.Length >= 2 && h[0] == 0x1F && h[1] == 0x8B,
        raw => new GZipStream(raw, CompressionMode.Decompress));

    public static readonly ArchiveFormat TarZstd = TarOf("tar.zst", [".tar.zst", ".tzst"], (h, _) => h.Length >= 4 && h[0] == 0x28 && h[1] == 0xB5 && h[2] == 0x2F && h[3] == 0xFD,
        raw => new ZstandardStream(raw, CompressionMode.Decompress));

    /// <summary>A plain tar has no magic at the start: the "ustar" mark, or else the file name decides.</summary>
    public static readonly ArchiveFormat Tar = TarOf("tar", [".tar"],
        (h, name) => (h.Length >= 262 && h.Slice(257, 5).SequenceEqual("ustar"u8)) || name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase),
        raw => raw);

    /// <summary>A tar inside a compression stream (or none): <paramref name="decompress"/> wraps the raw file stream. This is all a new tar.* format needs.</summary>
    public static ArchiveFormat TarOf(string id, IReadOnlyList<string> extensions, ArchiveSniffer sniff, Func<Stream, Stream> decompress)
        => new(id, id, extensions, sniff, path => new TarBackend(path, id, decompress));
}

/// <summary>
/// The one place that knows which archives the manager reads: recognises the format of a file by its first bytes (the extension only decides for a
/// plain tar without a "ustar" mark), and lists the extensions for file pickers, drag and drop and messages.
/// </summary>
public static class ArchiveFormats
{
    private static readonly Lock Gate = new();
    private static List<ArchiveFormat> _formats = [ArchiveFormat.Zip, ArchiveFormat.TarGzip, ArchiveFormat.TarZstd, ArchiveFormat.Tar];

    /// <summary>The registered formats, in the order they are tried.</summary>
    public static IReadOnlyList<ArchiveFormat> All
    {
        get
        {
            lock (Gate)
            {
                return _formats;
            }
        }
    }

    /// <summary>Adds a format (tried before the plain tar, whose test is the weakest). A second format with the same id is refused.</summary>
    public static void Register(ArchiveFormat format)
    {
        lock (Gate)
        {
            if (_formats.Any(f => string.Equals(f.Id, format.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("the archive format '" + format.Id + "' is already registered");
            }

            var next = new List<ArchiveFormat>(_formats);
            next.Insert(Math.Max(0, next.IndexOf(ArchiveFormat.Tar)), format);
            _formats = next;
        }
    }

    /// <summary>Removes a format again (for tests of <see cref="Register"/>).</summary>
    internal static void Unregister(ArchiveFormat format)
    {
        lock (Gate)
        {
            _formats = _formats.Where(f => f != format).ToList();
        }
    }

    /// <summary>All file name endings of the registered formats, longest first.</summary>
    public static IReadOnlyList<string> Extensions => All.SelectMany(f => f.Extensions).OrderByDescending(e => e.Length).ThenBy(e => e, StringComparer.Ordinal).ToList();

    /// <summary>The same as file picker patterns (<c>*.zip</c>).</summary>
    public static IReadOnlyList<string> FilePatterns => Extensions.Select(e => "*" + e).ToList();

    /// <summary>"zip, tar.gz, tar.zst or tar": for messages.</summary>
    public static string Describe()
    {
        List<string> ids = All.Select(f => f.Id).ToList();
        return ids.Count == 1 ? ids[0] : string.Join(", ", ids.Take(ids.Count - 1)) + " or " + ids[^1];
    }

    /// <summary>Does the file name end like one of the formats (no file is read)?</summary>
    public static bool HasArchiveExtension(string path)
        => Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Is this file an archive the manager can open (judged by its first bytes, like <see cref="Detect(string)"/>)? Never throws.</summary>
    public static bool IsArchiveFile(string path)
    {
        try
        {
            return File.Exists(path) && Detect(path) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PackageException)
        {
            return false;
        }
    }

    /// <summary>The format of the file, found by its first bytes. Throws <see cref="PackageException"/> when it is none of the registered ones.</summary>
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
        foreach (ArchiveFormat f in All)
        {
            if (f.Sniff(head, fileName))
            {
                return f;
            }
        }

        throw new PackageException("not a supported archive (" + Describe() + ")");
    }
}

/// <summary>One format's way to list and read the files of an archive.</summary>
public abstract class ArchiveBackend : IDisposable
{
    /// <summary>Normalized (forward slashes) names of the regular files and their sizes.</summary>
    public Dictionary<string, long> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calls <paramref name="read"/> with the content of each wanted name that exists (in the archive's own order for tar).</summary>
    public abstract void Visit(ISet<string> wanted, Action<string, Stream> read);

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

public sealed class ZipBackend : ArchiveBackend
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

        if (Entries.Count > ArchivePackage.MaxFiles)
        {
            _zip.Dispose();
            throw new PackageException($"the zip holds more than {ArchivePackage.MaxFiles} files");
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
public sealed class TarBackend : ArchiveBackend
{
    /// <summary>What a tar may unpack to when the content is skipped over while scanning (a gzip or zstd bomb stops here).</summary>
    internal const long MaxScanBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _path;
    private readonly string _formatId;
    private readonly Func<Stream, Stream> _decompress;

    public TarBackend(string path, string formatId, Func<Stream, Stream> decompress)
    {
        _path = path;
        _formatId = formatId;
        _decompress = decompress;
        try
        {
            Walk(null, scan: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or EndOfStreamException)
        {
            throw new PackageException("not a valid " + Describe() + " archive: " + ex.Message);
        }
    }

    private string Describe() => _formatId;

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
        return _decompress(raw);
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
                ArchivePackage.CheckName(name, e.Name);
                if (!Entries.TryAdd(name, e.Length))
                {
                    throw new PackageException("the tar holds the name twice: " + name);
                }

                declared += e.Length;
                if (Entries.Count > ArchivePackage.MaxFiles)
                {
                    throw new PackageException($"the archive holds more than {ArchivePackage.MaxFiles} files");
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
