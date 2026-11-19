using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public sealed class ToolStoreTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _zips;
    private int _zipCounter;

    public ToolStoreTests()
    {
        Game = _root.Combine("game");
        Directory.CreateDirectory(Game);
        _zips = _root.Combine("zips");
        Directory.CreateDirectory(_zips);
        BackupRoot = _root.Combine("data", "backups");
        Store = new ModStore(Game, BackupRoot);
    }

    private string Game { get; }
    private string BackupRoot { get; }
    private ModStore Store { get; }

    public void Dispose() => _root.Dispose();

    private ZipPackage Package(params (string Name, string Content)[] entries)
        => ZipPackage.Open(Pack.WriteZip(_zips, "z" + _zipCounter++ + ".zip", entries));

    private InstallOutcome Tool(string id, string version, string path, string? launch, params (string Name, string Content)[] entries)
        => Tool(Store, id, version, path, launch, entries);

    private InstallOutcome Tool(ModStore store, string id, string version, string path, string? launch, params (string Name, string Content)[] entries)
    {
        using ZipPackage p = Package(entries);
        return store.InstallTool(p, id, version, path, launch, "https://example.com/mod-index.json", "https://example.com/tool.zip");
    }

    private string T(string id, string rel) => Path.Combine(Store.ToolDirectory(id), rel.Replace('/', Path.DirectorySeparatorChar));

    private Task<List<InstalledMod>> Scan() => Store.ScanAsync(TestContext.Current.CancellationToken);

    private async Task<InstalledMod> Find(string id) => (await Scan()).Single(m => m.Id == id);

    private TrackedMod Tracked(string id) => Store.Registry.Load().Mods[id];

    [Fact]
    public void ToolsRootDefaultsToToolsNextToTheBackupRoot()
    {
        Assert.Equal(Path.GetFullPath(_root.Combine("data", "tools")), Store.ToolsRoot);
        Assert.Equal(Path.Combine(Store.ToolsRoot, "ed"), Store.ToolDirectory("ed"));
    }

    [Fact]
    public void ACustomToolsRootIsUsed()
    {
        string custom = _root.Combine("mytools");
        var store = new ModStore(Game, BackupRoot, custom);
        Assert.Equal(Path.GetFullPath(custom), store.ToolsRoot);
        Tool(store, "ed", "1", "", "Ed.exe", ("Ed.exe", "X"), ("lib/a.dll", "A"));
        Assert.True(File.Exists(Path.Combine(custom, "ed", "Ed.exe")));
        Assert.True(File.Exists(Path.Combine(custom, "ed", "lib", "a.dll")));
        Assert.False(Directory.Exists(_root.Combine("data", "tools")));
    }

    [Fact]
    public void InstallUnpacksBelowTheToolsRootAndNotIntoTheGameFolder()
    {
        InstallOutcome o = Tool("ed", "2.0", "", "Ed.exe", ("Ed.exe", "X"), ("data/x.cfg", "C"));
        Assert.Equal(2, o.FilesWritten);
        Assert.Equal("ed", o.Id);
        Assert.Equal("2.0", o.Version);
        Assert.Null(o.PreviousVersion);
        Assert.Equal("X", File.ReadAllText(T("ed", "Ed.exe")));
        Assert.Equal("C", File.ReadAllText(T("ed", "data/x.cfg")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Game).Select(Path.GetFileName), n => n != "mods");
    }

    [Fact]
    public void OnlyTheFilesBelowThePathAreUnpackedWithoutIt()
    {
        Tool("ed", "1", "tools/ed", "Ed.exe", ("tools/ed/Ed.exe", "X"), ("tools/ed/sub/y.txt", "Y"), ("tools/other/z.txt", "Z"), ("top.txt", "T"));
        Assert.True(File.Exists(T("ed", "Ed.exe")));
        Assert.True(File.Exists(T("ed", "sub/y.txt")));
        Assert.False(File.Exists(T("ed", "z.txt")));
        Assert.False(File.Exists(T("ed", "top.txt")));
        Assert.False(File.Exists(T("ed", "tools/other/z.txt")));
    }

    [Fact]
    public void RegistryRecordHasKindLaunchHashesAndSources()
    {
        Tool("ed", "2.0", "", "bin/Ed.exe", ("bin/Ed.exe", "X"), ("a.txt", "A"));
        TrackedMod t = Tracked("ed");
        Assert.Equal("tool", t.Kind);
        Assert.Equal(ItemKind.Tool, t.ItemKind);
        Assert.Equal("bin/Ed.exe", t.Launch);
        Assert.Equal("ed", t.Folder);
        Assert.Equal("2.0", t.Version);
        Assert.Equal("https://example.com/tool.zip", t.PackageUrl);
        Assert.Equal(Pack.Sha256("X"u8.ToArray()), t.Files["bin/Ed.exe"]);
        Assert.Equal(2, t.Files.Count);
        Assert.Null(t.Replaced);
    }

    [Fact]
    public void LaunchMatchesTheFileCaseInsensitivelyAndMayBeOmitted()
    {
        Tool("ed", "1", "", "ed.EXE", ("Ed.exe", "X"));
        Assert.Equal("ed.EXE", Tracked("ed").Launch);
        Tool("other", "1", "", null, ("a.txt", "A"));
        Assert.Null(Tracked("other").Launch);
    }

    [Fact]
    public void ALaunchFileThatIsNotInThePackageIsAStoreException()
    {
        StoreException ex = Assert.Throws<StoreException>(() => Tool("ed", "1", "", "Ed.exe", ("Other.exe", "X")));
        Assert.Contains("Ed.exe", ex.Message);
        Assert.False(Directory.Exists(Store.ToolDirectory("ed")));
        Assert.False(File.Exists(Store.Registry.Path));
    }

    [Fact]
    public void ALaunchFileOutsideThePathIsNotFound()
    {
        Assert.Throws<StoreException>(() => Tool("ed", "1", "tools/ed", "Ed.exe", ("tools/ed/a.txt", "A"), ("Ed.exe", "X")));
    }

    [Fact]
    public void AnEmptyFolderInThePackageIsAStoreException()
    {
        Assert.Throws<StoreException>(() => Tool("ed", "1", "nothing", null, ("a.txt", "A")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a b")]
    public void InvalidIdsAreRefused(string id)
        => Assert.Throws<StoreException>(() => Tool(id, "1", "", null, ("a.txt", "A")));

    [Fact]
    public void AnUpdateRemovesOldFilesThatAreNotShippedAndKeepsUserMadeOnes()
    {
        Tool("ed", "1", "", "Ed.exe", ("Ed.exe", "X1"), ("old.txt", "O"), ("sub/old2.txt", "O2"), ("keep/shipped.txt", "S1"));
        File.WriteAllText(T("ed", "user.cfg"), "MINE");
        Directory.CreateDirectory(Path.GetDirectoryName(T("ed", "saves/slot1.sav"))!);
        File.WriteAllText(T("ed", "saves/slot1.sav"), "SAVE");

        InstallOutcome o = Tool("ed", "2", "", "Ed.exe", ("Ed.exe", "X2"), ("keep/shipped.txt", "S2"), ("new.txt", "N"));
        Assert.Equal("1", o.PreviousVersion);
        Assert.Equal("X2", File.ReadAllText(T("ed", "Ed.exe")));
        Assert.Equal("S2", File.ReadAllText(T("ed", "keep/shipped.txt")));
        Assert.Equal("N", File.ReadAllText(T("ed", "new.txt")));
        Assert.False(File.Exists(T("ed", "old.txt")));
        Assert.False(File.Exists(T("ed", "sub/old2.txt")));
        Assert.False(Directory.Exists(T("ed", "sub")));
        Assert.Equal("MINE", File.ReadAllText(T("ed", "user.cfg")));
        Assert.Equal("SAVE", File.ReadAllText(T("ed", "saves/slot1.sav")));
        Assert.Equal(["Ed.exe", "keep/shipped.txt", "new.txt"], Tracked("ed").Files.Keys.Order().ToArray());
        Assert.Equal("2", Tracked("ed").Version);
    }

    [Fact]
    public void AnUpdateCanChangeTheLaunchFile()
    {
        Tool("ed", "1", "", "A.exe", ("A.exe", "X"));
        Tool("ed", "2", "", "B.exe", ("B.exe", "Y"));
        Assert.Equal("B.exe", Tracked("ed").Launch);
        Assert.False(File.Exists(T("ed", "A.exe")));
    }

    [Fact]
    public async Task UninstallKeepsUserFilesUnlessRemoveData()
    {
        Tool("ed", "1", "", "Ed.exe", ("Ed.exe", "X"), ("lib/a.dll", "A"));
        File.WriteAllText(T("ed", "user.cfg"), "MINE");
        Store.Uninstall(await Find("ed"), removeData: false, forgetSettings: false);
        Assert.False(File.Exists(T("ed", "Ed.exe")));
        Assert.False(Directory.Exists(T("ed", "lib")));
        Assert.Equal("MINE", File.ReadAllText(T("ed", "user.cfg")));
        Assert.Empty(Store.Registry.Load().Mods);
    }

    [Fact]
    public async Task UninstallWithoutUserFilesRemovesTheFolder()
    {
        Tool("ed", "1", "", "Ed.exe", ("Ed.exe", "X"), ("lib/a.dll", "A"));
        Store.Uninstall(await Find("ed"), false, false);
        Assert.False(Directory.Exists(Store.ToolDirectory("ed")));
    }

    [Fact]
    public async Task UninstallWithRemoveDataDeletesTheFolder()
    {
        Tool("ed", "1", "", "Ed.exe", ("Ed.exe", "X"));
        File.WriteAllText(T("ed", "user.cfg"), "MINE");
        Directory.CreateDirectory(T("ed", "saves"));
        Store.Uninstall(await Find("ed"), removeData: true, forgetSettings: false);
        Assert.False(Directory.Exists(Store.ToolDirectory("ed")));
        Assert.Empty(Store.Registry.Load().Mods);
    }

    [Fact]
    public async Task UninstallDoesNotTouchOtherToolsOrTheToolsRoot()
    {
        Tool("ed", "1", "", "Ed.exe", ("Ed.exe", "X"));
        Tool("other", "1", "", "O.exe", ("O.exe", "Y"));
        Store.Uninstall(await Find("ed"), true, false);
        Assert.True(File.Exists(T("other", "O.exe")));
        Assert.True(Directory.Exists(Store.ToolsRoot));
        Assert.Equal(["other"], Store.Registry.Load().Mods.Keys.ToArray());
    }

    [Fact]
    public async Task UninstallOfAToolWhoseFolderIsGoneStillClearsTheRecord()
    {
        Tool("ed", "1", "", "Ed.exe", ("Ed.exe", "X"));
        Directory.Delete(Store.ToolDirectory("ed"), true);
        Store.Uninstall(await Find("ed"), false, false);
        Assert.Empty(Store.Registry.Load().Mods);
    }

    [Fact]
    public async Task ScanListsTheToolWithKindDirectoryAndCounts()
    {
        Tool("ed", "3.1", "", "Ed.exe", ("Ed.exe", "X"), ("a.txt", "A"), ("b.txt", "B"));
        InstalledMod m = await Find("ed");
        Assert.Equal(ItemKind.Tool, m.Kind);
        Assert.Equal(Store.ToolDirectory("ed"), m.Directory);
        Assert.Equal("ed", m.Folder);
        Assert.Equal("3.1", m.Version);
        Assert.Null(m.Manifest);
        Assert.Equal("ed", m.Tracked!.Id);
        Assert.Equal((0, 0), (m.ModifiedFiles, m.MissingFiles));
        Assert.Null(m.Error);

        File.WriteAllText(T("ed", "a.txt"), "EDIT");
        File.Delete(T("ed", "b.txt"));
        m = await Find("ed");
        Assert.Equal((1, 1), (m.ModifiedFiles, m.MissingFiles));

        Directory.Delete(Store.ToolDirectory("ed"), true);
        m = await Find("ed");
        Assert.Equal(3, m.MissingFiles);
        Assert.Equal("its files are gone", m.Error);
    }

    [Fact]
    public async Task AnUnixLaunchFileGetsTheExecuteBit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes only exist on Linux and macOS");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Tool("ed", "1", "", "bin/ed", ("bin/ed", "#!/bin/sh"), ("data.txt", "D"));
        UnixFileMode mode = File.GetUnixFileMode(T("ed", "bin/ed"));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        Assert.False(File.GetUnixFileMode(T("ed", "data.txt")).HasFlag(UnixFileMode.UserExecute));
        Assert.Single(await Scan());
    }

    [Fact]
    public void ANewToolInstallDoesNotNeedTheModsFolder()
    {
        Assert.False(Directory.Exists(Store.ModsDir));
        Tool("ed", "1", "", null, ("a.txt", "A"));
        Assert.True(File.Exists(Store.Registry.Path));
    }

    [Fact]
    public void AFailedToolInstallLeavesTheExistingOneAlone()
    {
        Tool("ed", "1", "", "Ed.exe", ("Ed.exe", "X1"));
        Assert.Throws<StoreException>(() => Tool("ed", "2", "", "Missing.exe", ("Ed.exe", "X2")));
        Assert.Equal("X1", File.ReadAllText(T("ed", "Ed.exe")));
        Assert.Equal("1", Tracked("ed").Version);
    }
}
