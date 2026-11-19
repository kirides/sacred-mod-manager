using System.Buffers.Binary;
using System.Text;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class ZipPackageTests
{
    private static ZipPackage Open(TempDir dir, params (string Name, string Content)[] entries)
        => ZipPackage.Open(Pack.WriteZip(dir.Path, Guid.NewGuid().ToString("N") + ".zip", entries));

    /// <summary>Rewrites the uncompressed size a zip's central directory claims for one entry (the data itself is not touched).</summary>
    private static byte[] ClaimSize(byte[] zip, string entryName, uint claimed)
    {
        byte[] copy = (byte[])zip.Clone();
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        for (int i = 0; i + 46 + name.Length <= copy.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(i)) == 0x02014B50 && copy.AsSpan(i + 46, name.Length).SequenceEqual(name))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(i + 24), claimed);
                return copy;
            }
        }

        throw new InvalidOperationException("entry not found in the central directory");
    }

    [Fact]
    public void ModFoldersFindsEveryFolderWithAModLua()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mods/a/mod.lua", "x"), ("mods/b/mod.lua", "x"), ("mods/b/main.lua", "x"), ("mods/c/readme.md", "x"), ("mods/d/sub/MOD.LUA", "x"));
        Assert.Equal(["mods/a", "mods/b", "mods/d/sub"], p.ModFolders().Order(StringComparer.Ordinal).ToArray());
        Assert.True(p.HasFolder("mods/a"));
        Assert.True(p.HasFolder("mods/A"));
        Assert.False(p.HasFolder("mods/c"));
        Assert.False(p.HasFolder("mods"));
    }

    [Fact]
    public void RootLevelModLuaIsTheEmptyFolder()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mod.lua", "return {}"), ("main.lua", "x"), ("lib/util.lua", "y"));
        Assert.Equal([string.Empty], p.ModFolders().ToArray());
        Assert.True(p.HasFolder(string.Empty));
        Assert.Equal(["lib/util.lua", "main.lua", "mod.lua"], p.FilesUnder(string.Empty).Select(f => f.RelativePath).ToArray());
    }

    [Fact]
    public void BackslashNamesAndDirectoryEntriesAreNormalized()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mods\\a\\mod.lua", "x"), ("mods/a/dir/", string.Empty), ("mods\\a\\sub\\f.txt", "y"));
        Assert.Equal(["mods/a"], p.ModFolders().ToArray());
        Assert.Equal(["mod.lua", "sub/f.txt"], p.FilesUnder("mods/a").Select(f => f.RelativePath).ToArray());
    }

    [Fact]
    public void FilesUnderGivesRelativeSortedPathsAndSizesAndSkipsSiblings()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mods/a/z.lua", "12345"), ("mods/a/mod.lua", "1"), ("mods/a/sub/deep/x.txt", "12"), ("mods/ab/other.lua", "x"), ("mods/b/mod.lua", "x"), ("top.txt", "x"));
        List<PackageFile> files = p.FilesUnder("mods/a");
        Assert.Equal(["mod.lua", "sub/deep/x.txt", "z.lua"], files.Select(f => f.RelativePath).ToArray());
        Assert.Equal([1L, 2L, 5L], files.Select(f => f.Size).ToArray());
        Assert.Equal("mods/a/sub/deep/x.txt", files[1].EntryName);
        Assert.Equal(files.Select(f => f.RelativePath).ToArray(), p.FilesUnder("mods/a/").Select(f => f.RelativePath).ToArray());
        Assert.Empty(p.FilesUnder("mods/none"));
    }

    [Fact]
    public void ReadTextStripsABomAndReturnsNullForMissingOrOversized()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "t.zip");
        File.WriteAllBytes(path, Pack.Zip(("bom.txt", [.. Encoding.UTF8.GetPreamble(), .. "h\u00e9llo"u8.ToArray()]), ("plain.txt", "abc"u8.ToArray()), ("big.txt", new byte[1000])));
        using ZipPackage p = ZipPackage.Open(path);
        Assert.Equal("h\u00e9llo", p.ReadText("bom.txt"));
        Assert.Equal("abc", p.ReadText("plain.txt"));
        Assert.Null(p.ReadText("nope.txt"));
        Assert.Null(p.ReadText("big.txt", 999));
        Assert.Equal(1000, p.ReadText("big.txt", 1000)!.Length);
        Assert.Equal("abc"u8.ToArray(), p.ReadBytes("PLAIN.TXT", 10));
        Assert.Null(p.ReadBytes("plain.txt", 2));
    }

    public static TheoryData<string> UnsafeNames => new()
    {
        "../evil",
        "..\\evil",
        "mods/a/../../x",
        "mods\\a\\..\\..\\x",
        "mods/a/..",
        "mods/./a/x",
        "mods//a/x",
        "/x",
        "\\x",
        "C:\\x",
        "C:/x",
        "c:x",
        "mods/a/file:stream",
        "mods/a/CON.txt",
        "mods/a/con",
        "mods/a/Prn.lua",
        "AUX.lua",
        "mods/a/NUL",
        "mods/a/nul .txt",
        "mods/a/COM1.lua",
        "mods/a/lpt9.txt",
        "CON/file.lua",
        "mods/a/trailing.",
        "mods/a/trailing ",
        "mods/a/dir./x.lua",
        "mods/a/dir /x.lua",
        "mods/a/x.lua.",
    };

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public void UnsafeEntryNamesAreRejectedByFilesUnderOfTheRoot(string name)
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mod.lua", "x"), (name, "evil"));
        Assert.Throws<PackageException>(() => p.FilesUnder(string.Empty));
    }

    [Fact]
    public void UnsafeEntryInsideTheModFolderIsRejected()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mods/a/mod.lua", "x"), ("mods/a/../../x", "evil"));
        Assert.Throws<PackageException>(() => p.FilesUnder("mods/a"));
    }

    [Fact]
    public void UnsafeEntryOutsideTheModFolderDoesNotAffectIt()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mods/a/mod.lua", "x"), ("../evil", "evil"), ("mods/b/CON.txt", "x"));
        Assert.Single(p.FilesUnder("mods/a"));
        Assert.Throws<PackageException>(() => p.FilesUnder("mods/b"));
    }

    [Fact]
    public void OverlongNameIsRejected()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mod.lua", "x"), (new string('a', 241), "x"));
        Assert.Throws<PackageException>(() => p.FilesUnder(string.Empty));
    }

    [Theory]
    [InlineData("mods/a/CONSOLE.txt")]
    [InlineData("mods/a/com10.txt")]
    [InlineData("mods/a/.hidden")]
    [InlineData("mods/a/a.b.c")]
    [InlineData("mods/a/ü ber.lua")]
    [InlineData("mods/a/x..y")]
    public void OrdinaryNamesAreAccepted(string name)
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mods/a/mod.lua", "x"), (name, "ok"));
        Assert.Equal(2, p.FilesUnder("mods/a").Count);
    }

    [Fact]
    public void TooManyFilesIsRejectedOnOpen()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "many.zip");
        File.WriteAllBytes(path, Pack.Zip(Enumerable.Range(0, ZipPackage.MaxFiles + 1).Select(i => ("f" + i, Array.Empty<byte>())).ToArray()));
        Assert.Throws<PackageException>(() => ZipPackage.Open(path));
    }

    [Fact]
    public void ExactlyMaxFilesIsAccepted()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "many.zip");
        File.WriteAllBytes(path, Pack.Zip(Enumerable.Range(0, ZipPackage.MaxFiles).Select(i => ("f" + i, Array.Empty<byte>())).ToArray()));
        using ZipPackage p = ZipPackage.Open(path);
        Assert.Equal(ZipPackage.MaxFiles, p.FilesUnder(string.Empty).Count);
    }

    [Fact]
    public void FileLargerThanTheFileCapIsRejectedByItsDeclaredSize()
    {
        using var dir = new TempDir();
        byte[] zip = ClaimSize(Pack.Zip(("mod.lua", "x"), ("big.bin", "x")), "big.bin", (uint)ZipPackage.MaxFileBytes + 1);
        string path = Path.Combine(dir.Path, "big.zip");
        File.WriteAllBytes(path, zip);
        using ZipPackage p = ZipPackage.Open(path);
        Assert.Throws<PackageException>(() => p.FilesUnder(string.Empty));
        Assert.Null(p.ReadBytes("big.bin", 1024));
    }

    [Fact]
    public void FileAtTheFileCapIsAcceptedUntilTheTotalCapIsReached()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "total.zip");
        byte[] zip = Pack.Zip(("a.bin", "x"), ("b.bin", "x"), ("c.bin", "x"), ("d.bin", "x"));
        foreach (string n in new[] { "a.bin", "b.bin", "c.bin" })
        {
            zip = ClaimSize(zip, n, (uint)ZipPackage.MaxFileBytes);
        }

        File.WriteAllBytes(path, zip);
        using ZipPackage p = ZipPackage.Open(path);
        // 3 x 200 MB = 600 MB is exactly the cap; the fourth (1 byte) goes over it
        Assert.Throws<PackageException>(() => p.FilesUnder(string.Empty));
    }

    [Fact]
    public void TotalCapAcceptsExactlyTheLimit()
    {
        using var dir = new TempDir();
        byte[] zip = Pack.Zip(("a.bin", "x"), ("b.bin", "x"), ("c.bin", "x"));
        foreach (string n in new[] { "a.bin", "b.bin", "c.bin" })
        {
            zip = ClaimSize(zip, n, (uint)ZipPackage.MaxFileBytes);
        }

        string path = Path.Combine(dir.Path, "total.zip");
        File.WriteAllBytes(path, zip);
        using ZipPackage p = ZipPackage.Open(path);
        Assert.Equal(3, p.FilesUnder(string.Empty).Count);
    }

    [Fact]
    public void HugeCompressibleEntryIsRejectedWithoutReadingIt()
    {
        // a real 201 MB entry of zeros deflates to ~200 KB; building it takes a second or two with the fastest level
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "bomb.zip");
        using (var fs = File.Create(path))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (Stream s = zip.CreateEntry("mod.lua").Open())
            {
                s.Write("x"u8);
            }

            using Stream big = zip.CreateEntry("big.bin", System.IO.Compression.CompressionLevel.Fastest).Open();
            var chunk = new byte[1024 * 1024];
            for (int i = 0; i < 201; i++)
            {
                big.Write(chunk);
            }
        }

        Assert.True(new FileInfo(path).Length < 5 * 1024 * 1024);
        using ZipPackage p = ZipPackage.Open(path);
        Assert.Throws<PackageException>(() => p.FilesUnder(string.Empty));
        Assert.Null(p.ReadBytes("big.bin", 1024 * 1024));
    }

    [Fact]
    public void ExtractWritesTheFilesAndReturnsTheirHashes()
    {
        using var dir = new TempDir();
        byte[] binary = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        string path = Path.Combine(dir.Path, "p.zip");
        File.WriteAllBytes(path, Pack.Zip(("mods/a/mod.lua", "return {}"u8.ToArray()), ("mods/a/data/blob.bin", binary), ("mods/a/empty.txt", Array.Empty<byte>()), ("mods/other/x.txt", "no"u8.ToArray())));
        using ZipPackage p = ZipPackage.Open(path);
        string dest = dir.Combine("out", "a");
        Dictionary<string, string> hashes = p.Extract(p.FilesUnder("mods/a"), dest);
        Assert.Equal(3, hashes.Count);
        Assert.Equal(Pack.Sha256(binary), hashes["data/blob.bin"]);
        Assert.Equal(Pack.Sha256("return {}"u8.ToArray()), hashes["mod.lua"]);
        Assert.Equal(Pack.Sha256([]), hashes["empty.txt"]);
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(dest, "data", "blob.bin")));
        Assert.Equal(0, new FileInfo(Path.Combine(dest, "empty.txt")).Length);
        Assert.Equal(["data", "empty.txt", "mod.lua"], Directory.EnumerateFileSystemEntries(dest).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.False(Directory.Exists(dir.Combine("out", "other")));
    }

    [Fact]
    public void ExtractOverwritesExistingFilesAndKeepsOthers()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("m/mod.lua", "new"));
        string dest = dir.Combine("dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "mod.lua"), "old, longer content");
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "keep");
        p.Extract(p.FilesUnder("m"), dest);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "mod.lua")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(dest, "keep.txt")));
    }

    [Theory]
    [InlineData("../x.txt")]
    [InlineData("sub/../../x.txt")]
    [InlineData("..\\x.txt")]
    [InlineData("C:/Windows/x.txt")]
    [InlineData("C:\\x.txt")]
    [InlineData("/x.txt")]
    [InlineData("\\\\server\\share\\x.txt")]
    public void ExtractRefusesRelativePathsThatLeaveTheDestination(string rel)
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("a.txt", "payload"));
        string dest = dir.Combine("dest", "inner");
        string outside = dir.Combine("dest", "x.txt");
        Assert.Throws<PackageException>(() => p.Extract([new PackageFile("a.txt", rel, 7)], dest));
        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(dir.Path, "x.txt")));
    }

    [Fact]
    public void ExtractRefusesASiblingFolderWithTheSamePrefix()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("a.txt", "payload"));
        string dest = dir.Combine("mod");
        Assert.Throws<PackageException>(() => p.Extract([new PackageFile("a.txt", "../mod-evil/x.txt", 7)], dest));
        Assert.False(Directory.Exists(dir.Combine("mod-evil")));
    }

    [Fact]
    public void OpenWrapsBadFilesInPackageException()
    {
        using var dir = new TempDir();
        string notZip = Path.Combine(dir.Path, "bad.zip");
        File.WriteAllText(notZip, "this is not a zip");
        Assert.Throws<PackageException>(() => ZipPackage.Open(notZip));
        string empty = Path.Combine(dir.Path, "empty.zip");
        File.WriteAllBytes(empty, []);
        Assert.Throws<PackageException>(() => ZipPackage.Open(empty));
        Assert.Throws<PackageException>(() => ZipPackage.Open(Path.Combine(dir.Path, "missing.zip")));
    }

    [Fact]
    public void TruncatedZipIsAPackageException()
    {
        using var dir = new TempDir();
        byte[] zip = Pack.Zip(("mods/a/mod.lua", new string('x', 2000)));
        string path = Path.Combine(dir.Path, "cut.zip");
        File.WriteAllBytes(path, zip[..(zip.Length / 2)]);
        Assert.Throws<PackageException>(() => ZipPackage.Open(path));
    }

    [Fact]
    public void ZipWithOnlyDirectoriesHasNoFiles()
    {
        using var dir = new TempDir();
        using ZipPackage p = Open(dir, ("mods/", string.Empty), ("mods/a/", string.Empty));
        Assert.Empty(p.ModFolders());
        Assert.Empty(p.FilesUnder(string.Empty));
    }

    [Fact]
    public void DisposeReleasesTheFile()
    {
        using var dir = new TempDir();
        string path = Pack.WriteZip(dir.Path, "d.zip", ("mod.lua", "x"));
        ZipPackage.Open(path).Dispose();
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
