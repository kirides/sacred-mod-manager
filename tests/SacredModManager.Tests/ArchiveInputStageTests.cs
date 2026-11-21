using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Links;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

/// <summary>The single archive input stage: every place that reads an archive goes through <see cref="ArchiveFormats"/> and <see cref="ArchivePackage"/>.</summary>
public sealed class ArchiveInputStageTests : IDisposable
{
    private const string PkgUrl = "https://example.com/release.tar.gz";
    private readonly TempDir _root = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly AppPaths _paths;
    private readonly ModManagerService _service;

    public ArchiveInputStageTests()
    {
        _paths = new AppPaths(Path.Combine(_root.Path, "home"));
        _service = new ModManagerService(_paths, _fetcher);
        Game = _root.Combine("game");
        Directory.CreateDirectory(Game);
        Store = new ModStore(Game, _paths.Backups);
    }

    private string Game { get; }
    private ModStore Store { get; }
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    private static byte[] TarBytes(Func<Stream, Stream>? wrap, params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        Stream outer = wrap is null ? new NonClosing(ms) : wrap(ms);
        using (outer)
        using (var tar = new TarWriter(outer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach ((string name, string content) in files)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }
        }

        return ms.ToArray();
    }

    /// <summary>A write-only view that does not close the stream below it.</summary>
    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) => base.Dispose(false);
    }

    private static byte[] TarGz(params (string, string)[] files) => TarBytes(s => new GZipStream(s, CompressionLevel.Fastest, leaveOpen: true), files);

    // ---- the format list ---------------------------------------------------------------------------------------

    [Fact]
    public void ExtensionsPatternsAndDescriptionComeFromTheRegisteredFormats()
    {
        Assert.Contains(".zip", ArchiveFormats.Extensions);
        Assert.Contains(".tar.gz", ArchiveFormats.Extensions);
        Assert.Contains(".tgz", ArchiveFormats.Extensions);
        Assert.Contains(".tar.zst", ArchiveFormats.Extensions);
        Assert.Contains(".tar", ArchiveFormats.Extensions);
        Assert.Contains("*.tar.gz", ArchiveFormats.FilePatterns);
        Assert.Equal("zip, tar.gz, tar.zst or tar", ArchiveFormats.Describe());
        Assert.True(ArchiveFormats.HasArchiveExtension("C:\\x\\Mod.TGZ"));
        Assert.False(ArchiveFormats.HasArchiveExtension("mod.rar"));
    }

    [Fact]
    public void IsArchiveFileJudgesByBytesAndNeverThrows()
    {
        string zipNamedBin = _root.Combine("a.bin");
        File.WriteAllBytes(zipNamedBin, Pack.Zip(("a.txt", "x")));
        string text = _root.Combine("b.txt");
        File.WriteAllText(text, "just text");
        string gz = _root.Combine("c.dat");
        File.WriteAllBytes(gz, TarGz(("a.txt", "x")));
        Assert.True(ArchiveFormats.IsArchiveFile(zipNamedBin));
        Assert.True(ArchiveFormats.IsArchiveFile(gz));
        Assert.False(ArchiveFormats.IsArchiveFile(text));
        Assert.False(ArchiveFormats.IsArchiveFile(_root.Combine("missing.zip")));
        Assert.False(ArchiveFormats.IsArchiveFile(_root.Path)); // a folder
    }

    [Fact]
    public void ANewFormatIsAddedByRegisteringItAndEveryPlaceSeesIt()
    {
        // a "tar.dfl": a tar in a raw deflate stream (no magic bytes), recognised by its name
        ArchiveFormat dfl = ArchiveFormat.TarOf("tar.dfl", [".tar.dfl"], (_, name) => name.EndsWith(".tar.dfl", StringComparison.OrdinalIgnoreCase),
            raw => new DeflateStream(raw, CompressionMode.Decompress));
        string path = _root.Combine("pack.tar.dfl");
        File.WriteAllBytes(path, TarBytes(s => new DeflateStream(s, CompressionLevel.Fastest, leaveOpen: true), ("mods/a/mod.lua", Pack.ModLua("a"))));
        Assert.False(ArchiveFormats.IsArchiveFile(path));
        ArchiveFormats.Register(dfl);
        try
        {
            Assert.Throws<InvalidOperationException>(() => ArchiveFormats.Register(dfl));
            Assert.Contains(".tar.dfl", ArchiveFormats.Extensions);
            Assert.Contains("tar.dfl", ArchiveFormats.Describe());
            Assert.True(ArchiveFormats.IsArchiveFile(path));
            Assert.Equal(LaunchKind.ArchiveFile, LaunchTarget.Classify(path));
            Assert.Equal("a", ModManagerService.InspectArchive(path).Single().Manifest.Id);
        }
        finally
        {
            ArchiveFormats.Unregister(dfl);
        }

        Assert.False(ArchiveFormats.IsArchiveFile(path));
    }

    // ---- every user of archives uses the same stage ---------------------------------------------------------------

    [Fact]
    public void LocalInspectAndInstallWorkForATarGz()
    {
        string path = _root.Combine("local.tar.gz");
        File.WriteAllBytes(path, TarGz(("mods/a/mod.lua", Pack.ModLua("a", "2.0")), ("mods/a/main.lua", "x")));
        ModManagerService.LocalMod found = Assert.Single(ModManagerService.InspectArchive(path));
        Assert.Equal("mods/a", found.PathInArchive);
        _service.InstallLocal(Store, path, found.PathInArchive);
        Assert.True(File.Exists(Path.Combine(Game, "mods", "a", "main.lua")));
    }

    [Fact]
    public void PluginHintWorksForATarGz()
    {
        string path = _root.Combine("plug.tgz");
        File.WriteAllBytes(path, TarGz(("x/ddraw.dll", "d")));
        Assert.Equal(["ddraw.dll"], ModManagerService.FindPluginFiles(path));
    }

    [Fact]
    public async Task ADownloadedTarGzIsCachedUnderANeutralNameAndInstalls()
    {
        _fetcher.Add(PkgUrl, TarGz(("mods/a/mod.lua", Pack.ModLua("a")), ("mods/a/main.lua", "y")));
        var mod = new CatalogMod { Id = "a", Path = "mods/a", DownloadUrl = new Uri(PkgUrl), IndexUrl = new Uri("https://example.com/mod-index.json") };
        await _service.InstallAsync(Store, mod, null, Ct);
        Assert.True(File.Exists(Path.Combine(Game, "mods", "a", "main.lua")));
        string cached = Directory.GetFiles(_paths.Packages).Single(f => !f.EndsWith(".etag", StringComparison.Ordinal));
        Assert.EndsWith(".pkg", cached, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACacheFileFromAnOlderVersionWithTheZipNameIsStillUsed()
    {
        var url = new Uri("https://example.com/old.zip");
        var cache = new PackageCache(_fetcher, _paths.Packages);
        _fetcher.Add(url.AbsoluteUri, Pack.Zip(("a.txt", "1")));
        string fresh = await cache.GetAsync(url, null, null, Ct);
        string legacy = Path.ChangeExtension(fresh, ".zip");
        File.Move(fresh, legacy);
        Assert.Equal(fresh, cache.Cached(url));
        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(legacy));
    }

    // ---- file open on the command line / second start -----------------------------------------------------------------

    [Fact]
    public void LaunchTargetTellsLinksArchivesAndOtherThings()
    {
        string tgz = _root.Combine("m.tar.gz");
        File.WriteAllBytes(tgz, TarGz(("a.txt", "x")));
        string text = _root.Combine("t.txt");
        File.WriteAllText(text, "hello");
        Assert.Equal(LaunchKind.Link, LaunchTarget.Classify("gear-mod-manager://install?mod=a"));
        Assert.Equal(LaunchKind.ArchiveFile, LaunchTarget.Classify(tgz));
        Assert.Equal(LaunchKind.None, LaunchTarget.Classify(text));
        Assert.Equal(LaunchKind.None, LaunchTarget.Classify(null));
        Assert.Equal(tgz, LaunchTarget.FirstTarget(["--game", text, tgz]));
        Assert.Null(LaunchTarget.FirstTarget(["--game", text]));
    }

    // ---- two plugins that own the same files ----------------------------------------------------------------------

    private static CatalogMod Plugin(string id, string folder)
        => new()
        {
            Id = id, Kind = ItemKind.Plugin, Path = folder, DownloadUrl = new Uri("https://example.com/p.zip"), IndexUrl = new Uri("https://example.com/mod-index.json"),
            InstallFiles = [("dsound.dll", "dsound.dll"), ("alsoft.ini", "alsoft.ini")],
        };

    private void ServePlugins()
        => _fetcher.Add("https://example.com/p.zip", Pack.Zip(("speakers/dsound.dll", "SPK"), ("speakers/alsoft.ini", "SPK-INI"), ("hp/dsound.dll", "HP"), ("hp/alsoft.ini", "HP-INI")));

    private string Read(string name) => File.ReadAllText(Path.Combine(Game, name));

    private async Task<List<BatchItem>> Install(CatalogMod mod, Func<PluginConflictException, Task<bool>>? resolve)
        => await _service.InstallPlanAsync(Store, ModManagerService.Plan([mod], [mod], []), null, null, Ct, resolve);

    [Fact]
    public async Task AConflictingPluginIsNamedAndTheSwapRemovesTheOtherAndInstallsThisOne()
    {
        ServePlugins();
        File.WriteAllText(Path.Combine(Game, "dsound.dll"), "ORIGINAL");
        Assert.Null((await Install(Plugin("speakers", "speakers"), null)).Single().Error);
        Assert.Equal("SPK", Read("dsound.dll"));

        PluginConflictException? seen = null;
        List<BatchItem> r = await Install(Plugin("headphones", "hp"), c => { seen = c; return Task.FromResult(true); });

        Assert.NotNull(seen);
        Assert.Equal("speakers", seen.ConflictingId);
        Assert.Equal("headphones", seen.PluginId);
        Assert.Contains("dsound.dll", seen.Files);
        Assert.Null(r.Single().Error);
        Assert.Equal("HP", Read("dsound.dll"));
        Assert.Equal("HP-INI", Read("alsoft.ini"));
        Assert.False(Store.Registry.Load().Mods.ContainsKey("speakers"));
        Assert.True(Store.Registry.Load().Mods.ContainsKey("headphones"));

        // removing the second one brings the very first original back (the swap saved it again for the new plugin)
        Store.Uninstall((await Store.ScanAsync(Ct)).Single(m => m.Id == "headphones"), false, false);
        Assert.Equal("ORIGINAL", Read("dsound.dll"));
        Assert.False(File.Exists(Path.Combine(Game, "alsoft.ini")));
    }

    [Fact]
    public async Task AbortingAConflictChangesNothing()
    {
        ServePlugins();
        File.WriteAllText(Path.Combine(Game, "dsound.dll"), "ORIGINAL");
        await Install(Plugin("speakers", "speakers"), null);
        string[] filesBefore = Directory.GetFiles(Game, "*", SearchOption.AllDirectories).Order().ToArray();
        string[] backupBefore = Directory.GetFiles(_paths.Backups, "*", SearchOption.AllDirectories).Order().ToArray();

        int asked = 0;
        List<BatchItem> r = await Install(Plugin("headphones", "hp"), _ => { asked++; return Task.FromResult(false); });

        Assert.Equal(1, asked);
        BatchItem item = r.Single();
        Assert.True(item.Aborted);
        Assert.Null(item.Outcome);
        Assert.Equal("SPK", Read("dsound.dll"));
        Assert.Equal("SPK-INI", Read("alsoft.ini"));
        Assert.Equal(filesBefore, Directory.GetFiles(Game, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(backupBefore, Directory.GetFiles(_paths.Backups, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.True(Store.Registry.Load().Mods.ContainsKey("speakers"));
        Assert.False(Store.Registry.Load().Mods.ContainsKey("headphones"));
    }

    // ---- base plugin + two ini plugins (the shape of the DSOAL package) ---------------------------------------------------

    private static CatalogMod BasePlugin()
        => new()
        {
            Id = "base", Kind = ItemKind.Plugin, Path = "base", DownloadUrl = new Uri("https://example.com/p3.zip"), IndexUrl = new Uri("https://example.com/mod-index.json"),
            InstallFiles = [("dsound.dll", "dsound.dll")],
        };

    private static CatalogMod IniPlugin(string id, string folder)
        => new()
        {
            Id = id, Kind = ItemKind.Plugin, Path = folder, DownloadUrl = new Uri("https://example.com/p3.zip"), IndexUrl = new Uri("https://example.com/mod-index.json"),
            InstallFiles = [("alsoft.ini", "alsoft.ini")], Requires = ["base"],
        };

    private void ServeThree()
        => _fetcher.Add("https://example.com/p3.zip", Pack.Zip(("base/dsound.dll", "DLL"), ("spk/alsoft.ini", "SPK-INI"), ("hp/alsoft.ini", "HP-INI")));

    [Fact]
    public async Task AnIniPluginPullsInTheBaseFirstAndAnExistingIniIsBackedUpAndRestored()
    {
        ServeThree();
        File.WriteAllText(Path.Combine(Game, "alsoft.ini"), "MY-INI");
        CatalogMod spk = IniPlugin("speakers", "spk"), hp = IniPlugin("headphones", "hp");
        CatalogMod[] catalog = [BasePlugin(), spk, hp];

        InstallPlan plan = ModManagerService.Plan([spk], catalog, []);
        Assert.Equal(["base", "speakers"], plan.Install.Select(m => m.Id).ToArray());
        Assert.All(await _service.InstallPlanAsync(Store, plan, null, null, Ct), r => Assert.Null(r.Error));
        Assert.Equal("DLL", Read("dsound.dll"));
        Assert.Equal("SPK-INI", Read("alsoft.ini"));
        Assert.Equal(["speakers"], ModManagerService.Plan([spk], catalog, ["base"]).Install.Select(m => m.Id).ToArray());

        // swap through the conflict path: the base stays
        List<BatchItem> r2 = await _service.InstallPlanAsync(Store, ModManagerService.Plan([hp], catalog, ["base", "speakers"]), null, null, Ct, _ => Task.FromResult(true));
        Assert.Null(r2.Single().Error);
        Assert.Equal("HP-INI", Read("alsoft.ini"));
        Assert.Equal("DLL", Read("dsound.dll"));

        foreach (string id in new[] { "headphones", "base" })
        {
            Store.Uninstall((await Store.ScanAsync(Ct)).Single(m => m.Id == id), false, false);
        }

        Assert.Equal("MY-INI", Read("alsoft.ini"));
        Assert.False(File.Exists(Path.Combine(Game, "dsound.dll")));
    }

    [Fact]
    public async Task WithoutAnAnswerHandlerAConflictIsAnOrdinaryError()
    {
        ServePlugins();
        await Install(Plugin("speakers", "speakers"), null);
        BatchItem item = (await Install(Plugin("headphones", "hp"), null)).Single();
        Assert.NotNull(item.Error);
        Assert.Contains("speakers", item.Error);
        Assert.Equal("SPK", Read("dsound.dll"));
    }
}
