using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public sealed class ArchiveFormatTests : IDisposable
{
    private readonly TempDir _dir = new();
    private int _counter;

    public void Dispose() => _dir.Dispose();

    public enum Kind { Zip, Tar, TarGz, TarZst }

    public sealed record Item(string Name, string Content = "", TarEntryType Type = TarEntryType.RegularFile, string? Link = null);

    private static Item F(string name, string content = "x") => new(name, content);

    /// <summary>Writes an archive of the given kind; the file name is deliberately meaningless, so the format must come from the bytes.</summary>
    private string Write(Kind kind, params Item[] items)
    {
        string path = _dir.Combine("pkg" + _counter++ + ".bin");
        if (kind == Kind.Zip)
        {
            File.WriteAllBytes(path, Pack.Zip(items.Select(i => (i.Name, i.Content)).ToArray()));
            return path;
        }

        using FileStream fs = File.Create(path);
        Stream outer = kind switch
        {
            Kind.TarGz => new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: true),
            Kind.TarZst => new ZstandardStream(fs, CompressionMode.Compress, leaveOpen: true),
            _ => fs,
        };
        using (outer)
        using (var tar = new TarWriter(outer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (Item i in items)
            {
                TarEntry e = i.Type switch
                {
                    TarEntryType.SymbolicLink or TarEntryType.HardLink => new PaxTarEntry(i.Type, i.Name) { LinkName = i.Link ?? "target" },
                    TarEntryType.Directory or TarEntryType.Fifo => new PaxTarEntry(i.Type, i.Name),
                    TarEntryType.CharacterDevice or TarEntryType.BlockDevice => new PaxTarEntry(i.Type, i.Name),
                    _ => new PaxTarEntry(TarEntryType.RegularFile, i.Name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(i.Content)) },
                };
                tar.WriteEntry(e);
            }
        }

        return path;
    }

    public static TheoryData<Kind> TarKinds => new() { Kind.Tar, Kind.TarGz, Kind.TarZst };
    public static TheoryData<Kind> AllKinds => new() { Kind.Zip, Kind.Tar, Kind.TarGz, Kind.TarZst };

    // ---- the same API for every format ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void EveryFormatGivesTheSameEntryListReadsAndExtract(Kind kind)
    {
        string path = Write(kind, new Item("mods/", Type: TarEntryType.Directory), F("mods/a/mod.lua", "return {}"), F("mods/a/sub/x.txt", "12345"), F("top.txt", "t"));
        using ArchivePackage p = ArchivePackage.Open(path);
        Assert.Equal(kind switch { Kind.Zip => ArchiveFormat.Zip, Kind.Tar => ArchiveFormat.Tar, Kind.TarGz => ArchiveFormat.TarGzip, _ => ArchiveFormat.TarZstd }, p.Format);
        Assert.Equal(["mods/a"], p.ModFolders().ToArray());
        Assert.Equal(["mod.lua", "sub/x.txt"], p.FilesUnder("mods/a").Select(f => f.RelativePath).ToArray());
        Assert.Equal([9L, 5L], p.FilesUnder("mods/a").Select(f => f.Size).ToArray());
        Assert.Equal("return {}", p.ReadText("mods/a/mod.lua"));
        Assert.Null(p.ReadText("nope.txt"));

        string dest = _dir.Combine("out" + _counter++);
        Dictionary<string, string> hashes = p.Extract(p.FilesUnder("mods/a"), dest);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(dest, "sub", "x.txt")));
        Assert.Equal(Pack.Sha256(Encoding.UTF8.GetBytes("12345")), hashes["sub/x.txt"]);
    }

    [Theory]
    [MemberData(nameof(TarKinds))]
    public void LeadingDotSlashAndBackslashesAreNormalized(Kind kind)
    {
        using ArchivePackage p = ArchivePackage.Open(Write(kind, F("./a/b.txt"), F("./c.txt")));
        Assert.Equal(["a/b.txt", "c.txt"], p.FilesUnder(string.Empty).Select(f => f.RelativePath).ToArray());
    }

    [Theory]
    [MemberData(nameof(TarKinds))]
    public void ExtractingTheSameEntryToTwoPlacesWorks(Kind kind)
    {
        using ArchivePackage p = ArchivePackage.Open(Write(kind, F("a.txt", "AAA"), F("b.txt", "B")));
        string dest = _dir.Combine("two");
        Dictionary<string, string> h = p.Extract([new PackageFile("a.txt", "one/a.txt", 3), new PackageFile("a.txt", "two/a.txt", 3), new PackageFile("b.txt", "b.txt", 1)], dest);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(dest, "two", "a.txt")));
        Assert.Equal(h["one/a.txt"], h["two/a.txt"]);
        Assert.Equal("B", File.ReadAllText(Path.Combine(dest, "b.txt")));
    }

    // ---- detection by bytes ------------------------------------------------------------------------------------

    [Fact]
    public void TheFormatComesFromTheBytesNotTheExtension()
    {
        string zipAsTarGz = _dir.Combine("looks-like.tar.gz");
        File.Copy(Write(Kind.Zip, F("a.txt")), zipAsTarGz);
        using ArchivePackage p = ArchivePackage.Open(zipAsTarGz);
        Assert.Equal(ArchiveFormat.Zip, p.Format);
    }

    [Fact]
    public void GarbageAndEmptyFilesAreNotArchives()
    {
        string junk = _dir.Combine("junk.zip");
        File.WriteAllText(junk, "this is not an archive at all");
        Assert.Throws<PackageException>(() => ArchivePackage.Open(junk));
        string empty = _dir.Combine("empty.tar.gz");
        File.WriteAllBytes(empty, []);
        Assert.Throws<PackageException>(() => ArchivePackage.Open(empty));
    }

    [Fact]
    public void AGzipThatIsNotATarIsRejected()
    {
        string path = _dir.Combine("plain.gz");
        using (FileStream fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        {
            gz.Write(Encoding.UTF8.GetBytes(new string('x', 2000)));
        }

        Assert.Throws<PackageException>(() => ArchivePackage.Open(path));
    }

    [Fact]
    public void ATruncatedTarGzIsRejectedAndNotAHang()
    {
        string path = Write(Kind.TarGz, F("a.txt", new string('q', 100_000)));
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        Assert.Throws<PackageException>(() => ArchivePackage.Open(path));
    }

    // ---- unsafe tar content ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.BlockDevice)]
    [InlineData(TarEntryType.Fifo)]
    public void LinksDevicesAndFifosMakeTheArchiveUnusable(TarEntryType type)
    {
        foreach (Kind kind in new[] { Kind.Tar, Kind.TarGz, Kind.TarZst })
        {
            string path = Write(kind, F("ok.txt"), new Item("evil", Type: type, Link: "/etc/passwd"));
            PackageException ex = Assert.Throws<PackageException>(() => ArchivePackage.Open(path));
            Assert.Contains("evil", ex.Message);
        }
    }

    [Theory]
    [MemberData(nameof(TarKinds))]
    public void ALinkWithASafeNameInsideTheModFolderIsStillRejected(Kind kind)
    {
        string path = Write(kind, F("mods/a/mod.lua"), new Item("mods/a/link.lua", Type: TarEntryType.SymbolicLink, Link: "../../outside"));
        Assert.Throws<PackageException>(() => ArchivePackage.Open(path));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a/../b.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("D:\\evil.txt")]
    [InlineData("a/con.txt")]
    [InlineData("NUL")]
    [InlineData("a/COM1.dll")]
    [InlineData("a/lpt9")]
    [InlineData("a//b.txt")]
    [InlineData("a/b.txt.")]
    [InlineData("a/b ")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/b:stream")]
    public void UnsafeTarNamesAreRejectedOnOpen(string name)
    {
        foreach (Kind kind in new[] { Kind.Tar, Kind.TarGz, Kind.TarZst })
        {
            string path = Write(kind, F("fine.txt"), F(name));
            Assert.Throws<PackageException>(() => ArchivePackage.Open(path));
        }
    }

    [Theory]
    [MemberData(nameof(TarKinds))]
    public void DuplicateTarNamesAreRejected(Kind kind)
    {
        string path = Write(kind, F("a/x.txt", "1"), F("A/X.txt", "2"));
        Assert.Throws<PackageException>(() => ArchivePackage.Open(path));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void ExtractRefusesAnUnsafeRelativePathAndAMissingEntry(Kind kind)
    {
        using ArchivePackage p = ArchivePackage.Open(Write(kind, F("a.txt")));
        Assert.Throws<PackageException>(() => p.Extract([new PackageFile("a.txt", "../a.txt", 1)], _dir.Combine("o1")));
        Assert.Throws<PackageException>(() => p.Extract([new PackageFile("a.txt", "C:/a.txt", 1)], _dir.Combine("o2")));
        Assert.Throws<PackageException>(() => p.Extract([new PackageFile("a.txt", "sub\\a.txt", 1)], _dir.Combine("o3")));
        Assert.Throws<PackageException>(() => p.Extract([new PackageFile("missing.txt", "m.txt", 1)], _dir.Combine("o4")));
    }

    // ---- caps --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Kind.TarGz)]
    [InlineData(Kind.TarZst)]
    public void AnOversizedTarFileIsRejectedByFilesUnder(Kind kind)
    {
        string content = new('0', (int)(ArchivePackage.MaxFileBytes / 1024) + 1);
        string big = string.Concat(Enumerable.Repeat(content, 1024)); // 200 MB + 1 KB of zeros, compresses to almost nothing
        using ArchivePackage p = ArchivePackage.Open(Write(kind, F("big.bin", big)));
        PackageException ex = Assert.Throws<PackageException>(() => p.FilesUnder(string.Empty));
        Assert.Contains("larger", ex.Message);
    }

    [Fact]
    public void ReadBytesOfATarEntryHonoursTheLimit()
    {
        using ArchivePackage p = ArchivePackage.Open(Write(Kind.TarGz, F("a.txt", new string('a', 1000))));
        Assert.Null(p.ReadBytes("a.txt", 10));
        Assert.Equal(1000, p.ReadBytes("a.txt", 5000)!.Length);
    }

    // ---- wildcard patterns -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("*.dll", "a.dll", true)]
    [InlineData("*.dll", "A.DLL", true)]
    [InlineData("*.dll", "a.dlls", false)]
    [InlineData("*.dll", "sub/a.dll", false)]
    [InlineData("a?.txt", "ab.txt", true)]
    [InlineData("a?.txt", "a.txt", false)]
    [InlineData("**", "a", true)]
    [InlineData("**", "a/b/c.txt", true)]
    [InlineData("bin/**", "bin/x.dll", true)]
    [InlineData("bin/**", "bin/s/x.dll", true)]
    [InlineData("bin/**", "bin", false)]
    [InlineData("bin/**", "other/x.dll", false)]
    [InlineData("**/*.dll", "x.dll", true)]
    [InlineData("**/*.dll", "a/b/x.dll", true)]
    [InlineData("**/*.dll", "a/b/x.txt", false)]
    [InlineData("lib/*/x.dll", "lib/v1/x.dll", true)]
    [InlineData("lib/*/x.dll", "lib/x.dll", false)]
    [InlineData("lib/*/x.dll", "lib/a/b/x.dll", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*", "dir/file", false)]
    public void GlobMatching(string pattern, string path, bool expected)
        => Assert.Equal(expected, PackageGlob.IsMatch(pattern, path));

    [Theory]
    [InlineData("*.dll", true)]
    [InlineData("**", true)]
    [InlineData("a/**/b/*.x", true)]
    [InlineData("plain/file.txt", true)]
    [InlineData("../*.dll", false)]
    [InlineData("a/../*.dll", false)]
    [InlineData("a/..", false)]
    [InlineData("./*.dll", false)]
    [InlineData("/*.dll", false)]
    [InlineData("a\\*.dll", false)]
    [InlineData("C:*.dll", false)]
    [InlineData("a**", false)]
    [InlineData("**b", false)]
    [InlineData("a//b", false)]
    [InlineData("a/", false)]
    [InlineData("", false)]
    public void GlobValidation(string pattern, bool valid) => Assert.Equal(valid, PackageGlob.IsValid(pattern));

    [Theory]
    [InlineData("*.dll", "")]
    [InlineData("**", "")]
    [InlineData("bin/*.dll", "bin")]
    [InlineData("a/b/**", "a/b")]
    [InlineData("a/*/c/*.x", "a")]
    public void GlobBaseFolder(string pattern, string expected) => Assert.Equal(expected, PackageGlob.BaseFolder(pattern));

    private ModStore NewStore(out string game)
    {
        game = _dir.Combine("game" + _counter++);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Sacred.exe"), "MZ");
        return new ModStore(game, _dir.Combine("backup" + _counter));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void WildcardInstallFilesWorkForEveryFormat(Kind kind)
    {
        ModStore store = NewStore(out string game);
        using ArchivePackage p = ArchivePackage.Open(Write(kind, F("pkg/a.dll", "A"), F("pkg/b.dll", "B"), F("pkg/c.txt", "C"), F("pkg/data/x/1.bin", "1"), F("pkg/data/2.bin", "2"), F("pkg/cfg/z.ini", "Z")));
        InstallOutcome o = store.InstallPlugin(p, "wild", "1", "pkg", "",
            [("*.dll", ""), ("data/**", "gamedata"), ("cfg/*.ini", "settings")], null, null);
        Assert.Equal(5, o.FilesWritten);
        Assert.Equal("A", File.ReadAllText(Path.Combine(game, "a.dll")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(game, "b.dll")));
        Assert.False(File.Exists(Path.Combine(game, "c.txt")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(game, "gamedata", "x", "1.bin")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(game, "gamedata", "2.bin")));
        Assert.Equal("Z", File.ReadAllText(Path.Combine(game, "settings", "z.ini")));
    }

    [Fact]
    public void AWildcardWithoutAMatchAndTwoMatchesToTheSameNameAreErrors()
    {
        ModStore store = NewStore(out _);
        using ArchivePackage p = ArchivePackage.Open(Write(Kind.TarGz, F("a/x.dll"), F("b/x.dll")));
        Assert.Throws<StoreException>(() => store.InstallPlugin(p, "w", "1", "", "", [("*.exe", "")], null, null));
        StoreException ex = Assert.Throws<StoreException>(() => store.InstallPlugin(p, "w", "1", "", "", [("a/x.dll", "t/x.dll"), ("b/*.dll", "t")], null, null));
        Assert.Contains("x.dll", ex.Message);
    }

    [Theory]
    [InlineData("../*.dll")]
    [InlineData("a/../*.dll")]
    public void AStorePassedAnEscapingPatternRefusesIt(string pattern)
    {
        ModStore store = NewStore(out _);
        using ArchivePackage p = ArchivePackage.Open(Write(Kind.Zip, F("x.dll")));
        Assert.Throws<StoreException>(() => store.InstallPlugin(p, "w", "1", "", "", [(pattern, "")], null, null));
    }

    [Fact]
    public void ATarWithAnUnsafeNameNeverReachesTheGameFolder()
    {
        ModStore store = NewStore(out string game);
        string path = Write(Kind.TarGz, F("ok.dll"), F("../../escaped.dll"));
        Assert.Throws<PackageException>(() => ArchivePackage.Open(path));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(game)!, "escaped.dll")));
        _ = store;
    }
}
