using System.IO.Compression;
using System.Security.Cryptography;

namespace SacredModManager.Core.Mods;

/// <summary>A zip that is not usable (damaged, unsafe, too big).</summary>
public sealed class PackageException(string message) : Exception(message);

/// <summary>One file of a mod inside a package.</summary>
public sealed record PackageFile(string EntryName, string RelativePath, long Size);

/// <summary>
/// A release zip opened for reading. Nothing is extracted on its own: <see cref="Extract"/> writes only below the folder it is given, and
/// every entry name is checked first (no absolute path, no '..', no drive, no reserved device names), the number of files and the
/// number of bytes are capped, and the bytes counted are the ones really read, not the ones the zip header claims.
/// </summary>
public sealed class ZipPackage : IDisposable
{
    public const int MaxFiles = 20_000;
    public const long MaxTotalBytes = 600L * 1024 * 1024;
    public const long MaxFileBytes = 200L * 1024 * 1024;

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private ZipPackage(ZipArchive zip)
    {
        _zip = zip;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            string name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue;
            }

            _entries[name] = e;
        }

        if (_entries.Count > MaxFiles)
        {
            zip.Dispose();
            throw new PackageException($"the zip holds more than {MaxFiles} files");
        }
    }

    public static ZipPackage Open(string path)
    {
        try
        {
            return new ZipPackage(ZipFile.OpenRead(path));
        }
        catch (InvalidDataException ex)
        {
            throw new PackageException("not a valid zip file: " + ex.Message);
        }
        catch (IOException ex)
        {
            throw new PackageException("the zip cannot be read: " + ex.Message);
        }
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>Folders of the zip that hold a <c>mod.lua</c> (forward slashes; "" for the zip's root).</summary>
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

    public bool HasFolder(string folder) => _entries.ContainsKey(Join(folder, "mod.lua"));

    public string? ReadText(string entryName, int maxBytes = 512 * 1024)
    {
        byte[]? bytes = ReadBytes(entryName, maxBytes);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes.AsSpan(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));
    }

    public byte[]? ReadBytes(string entryName, long maxBytes)
    {
        if (!_entries.TryGetValue(entryName.Replace('\\', '/'), out ZipArchiveEntry? e))
        {
            return null;
        }

        if (e.Length > maxBytes)
        {
            return null;
        }

        using Stream s = e.Open();
        using var ms = new MemoryStream();
        CopyLimited(s, ms, maxBytes);
        return ms.ToArray();
    }

    /// <summary>The files below <paramref name="folder"/> ("" for all), with the path relative to it. Throws for an unsafe name.</summary>
    public List<PackageFile> FilesUnder(string folder)
    {
        string prefix = folder.Length == 0 ? string.Empty : folder.TrimEnd('/') + "/";
        var list = new List<PackageFile>();
        long total = 0;
        foreach ((string name, ZipArchiveEntry e) in _entries)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rel = name[prefix.Length..];
            CheckName(rel, name);
            total += e.Length;
            if (e.Length > MaxFileBytes || total > MaxTotalBytes)
            {
                throw new PackageException("the mod is larger than " + (MaxTotalBytes / (1024 * 1024)) + " MB");
            }

            list.Add(new PackageFile(name, rel, e.Length));
        }

        list.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return list;
    }

    /// <summary>Writes the files into <paramref name="destination"/> (created) and returns the SHA-256 (hex, lower case) of each by relative path.</summary>
    public Dictionary<string, string> Extract(IEnumerable<PackageFile> files, string destination)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        long total = 0;
        foreach (PackageFile f in files)
        {
            string target = Path.GetFullPath(Path.Combine(root, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("unsafe path in the zip: " + f.RelativePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = _entries[f.EntryName].Open();
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
                    throw new PackageException("the zip unpacks to more than " + (MaxTotalBytes / (1024 * 1024)) + " MB");
                }

                output.Write(buffer, 0, n);
                sha.AppendData(buffer, 0, n);
            }

            hashes[f.RelativePath] = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        return hashes;
    }

    internal static void CheckName(string rel, string full)
    {
        if (rel.Length == 0 || rel.Length > 240 || rel.StartsWith('/') || rel.Contains(':', StringComparison.Ordinal) || rel.Contains('\0', StringComparison.Ordinal))
        {
            throw new PackageException("unsafe path in the zip: " + full);
        }

        foreach (string part in rel.Split('/'))
        {
            string stem = part.Split('.')[0].TrimEnd(' ');
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || IsReservedDeviceName(stem))
            {
                throw new PackageException("unsafe path in the zip: " + full);
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
                throw new PackageException("an entry of the zip is larger than allowed");
            }

            to.Write(buffer, 0, n);
        }
    }
}
