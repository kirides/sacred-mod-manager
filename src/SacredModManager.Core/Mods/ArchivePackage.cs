using SacredModManager.Core.App;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SacredModManager.Core.Mods;

/// <summary>An archive that is not usable (damaged, unsafe, too big).</summary>
public sealed class PackageException(string message) : Exception(message);

/// <summary>One file of a mod inside a package.</summary>
public sealed record PackageFile(string EntryName, string RelativePath, long Size);

/// <summary>
/// A release archive (any format of <see cref="ArchiveFormats"/>) opened for reading. Nothing is extracted on its own: <see cref="Extract"/> writes only below the folder it is given, and
/// every entry name is checked first (no absolute path, no '..', no drive, no reserved device names), the number of files and the
/// number of bytes are capped, and the bytes counted are the ones really read, not the ones the archive header claims.
/// </summary>
public sealed class ArchivePackage : IDisposable
{
    public const int MaxFiles = 20_000;
    public const long MaxTotalBytes = 600L * 1024 * 1024;
    public const long MaxFileBytes = 200L * 1024 * 1024;

    private readonly ArchiveBackend _archive;
    private readonly Dictionary<string, long> _entries;

    private ArchivePackage(ArchiveBackend archive, ArchiveFormat format)
    {
        _archive = archive;
        _entries = archive.Entries;
        Format = format;
    }

    /// <summary>The format found by the first bytes of the file.</summary>
    public ArchiveFormat Format { get; }

    /// <summary>The single place where an archive file is opened: every format of <see cref="ArchiveFormats"/>, told apart by the first bytes, not by the extension.</summary>
    public static ArchivePackage Open(string path)
    {
        ArchiveFormat format;
        try
        {
            format = ArchiveFormats.Detect(path);
        }
        catch (IOException ex)
        {
            throw new PackageException("the archive cannot be read: " + ex.Message);
        }

        return new ArchivePackage(format.Open(path), format);
    }

    public void Dispose() => _archive.Dispose();

    /// <summary>Folders of the archive that hold a <c>mod.lua</c> (forward slashes; "" for the zip's root).</summary>
    public IEnumerable<string> ModFolders()
    {
        foreach (string name in _entries.Keys)
        {
            if (name.Equals("mod.lua", StringComparison.OrdinalIgnoreCase))
            {
                yield return string.Empty;
            }
            else if (name.EndsWith("/mod.lua", StringComparison.OrdinalIgnoreCase))
            {
                yield return name[..^"/mod.lua".Length];
            }
        }
    }

    /// <summary>The names of the files in the archive (forward slashes).</summary>
    public IEnumerable<string> EntryNames() => _entries.Keys;

    public bool HasFolder(string folder) => _entries.ContainsKey(Join(folder, "mod.lua"));

    public string? ReadText(string entryName, int maxBytes = 512 * 1024)
    {
        byte[]? bytes = ReadBytes(entryName, maxBytes);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes.AsSpan(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));
    }

    public byte[]? ReadBytes(string entryName, long maxBytes)
    {
        string name = entryName.Replace('\\', '/');
        if (!_entries.TryGetValue(name, out long length) || length > maxBytes)
        {
            return null;
        }

        using var ms = new MemoryStream();
        _archive.Visit(new HashSet<string>([name], StringComparer.OrdinalIgnoreCase), (_, s) => CopyLimited(s, ms, maxBytes));
        return ms.ToArray();
    }

    /// <summary>The files below <paramref name="folder"/> ("" for all), with the path relative to it. Throws for an unsafe name.</summary>
    public List<PackageFile> FilesUnder(string folder)
    {
        string prefix = folder.Length == 0 ? string.Empty : folder.TrimEnd('/') + "/";
        var list = new List<PackageFile>();
        long total = 0;
        foreach ((string name, long size) in _entries)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rel = name[prefix.Length..];
            CheckName(rel, name);
            total += size;
            if (size > MaxFileBytes || total > MaxTotalBytes)
            {
                throw new PackageException("the mod is larger than " + (MaxTotalBytes / (1024 * 1024)) + " MB");
            }

            list.Add(new PackageFile(name, rel, size));
        }

        list.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return list;
    }

    /// <summary>Writes the files into <paramref name="destination"/> (created) and returns the SHA-256 (hex, lower case) of each by relative path.</summary>
    public Dictionary<string, string> Extract(IEnumerable<PackageFile> files, string destination)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = PathRules.Normalize(destination);
        Directory.CreateDirectory(root);
        var byEntry = new Dictionary<string, List<(PackageFile File, string Target)>>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageFile f in files)
        {
            if (f.RelativePath.Contains('\\', StringComparison.Ordinal))
            {
                throw new PackageException("unsafe path in the archive: " + f.RelativePath);
            }

            CheckName(f.RelativePath, f.EntryName);
            string target = PathRules.ResolveInside(root, f.RelativePath)
                ?? throw new PackageException("unsafe path in the archive: " + f.RelativePath);

            if (!_entries.ContainsKey(f.EntryName))
            {
                throw new PackageException("the archive has no entry " + f.EntryName);
            }

            if (!byEntry.TryGetValue(f.EntryName, out List<(PackageFile, string)>? list))
            {
                byEntry[f.EntryName] = list = [];
            }

            list.Add((f, target));
        }

        long total = 0;
        _archive.Visit(new HashSet<string>(byEntry.Keys, StringComparer.OrdinalIgnoreCase), (name, input) =>
        {
            string? firstTarget = null;
            string? firstHash = null;
            foreach ((PackageFile f, string target) in byEntry[name])
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (firstTarget is not null)
                {
                    File.Copy(firstTarget, target, overwrite: true);
                    hashes[f.RelativePath] = firstHash!;
                    continue;
                }

                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long fileBytes = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fileBytes += n;
                    total += n;
                    if (fileBytes > MaxFileBytes || total > MaxTotalBytes)
                    {
                        throw new PackageException("the archive unpacks to more than " + (MaxTotalBytes / (1024 * 1024)) + " MB");
                    }

                    output.Write(buffer, 0, n);
                    sha.AppendData(buffer, 0, n);
                }

                firstTarget = target;
                firstHash = hashes[f.RelativePath] = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
        });

        foreach (string name in byEntry.Keys)
        {
            if (byEntry[name].Any(x => !hashes.ContainsKey(x.File.RelativePath)))
            {
                throw new PackageException("the archive entry could not be read: " + name);
            }
        }

        return hashes;
    }

    internal static void CheckName(string rel, string full)
    {
        if (rel.Length == 0 || rel.Length > 240 || rel.StartsWith('/') || rel.Contains(':', StringComparison.Ordinal) || rel.Contains('\0', StringComparison.Ordinal))
        {
            throw new PackageException("unsafe path in the archive: " + full);
        }

        foreach (string part in rel.Split('/'))
        {
            string stem = part.Split('.')[0].TrimEnd(' ');
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || IsReservedDeviceName(stem))
            {
                throw new PackageException("unsafe path in the archive: " + full);
            }
        }
    }

    private static bool IsReservedDeviceName(string stem)
    {
        string u = stem.ToUpperInvariant();
        return u is "CON" or "PRN" or "AUX" or "NUL"
            || (u.Length == 4 && (u.StartsWith("COM", StringComparison.Ordinal) || u.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(u[3]));
    }

    private static string Join(string folder, string file) => folder.Length == 0 ? file : folder.TrimEnd('/') + "/" + file;

    private static void CopyLimited(Stream from, Stream to, long max)
    {
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            done += n;
            if (done > max)
            {
                throw new PackageException("an entry of the archive is larger than allowed");
            }

            to.Write(buffer, 0, n);
        }
    }
}
