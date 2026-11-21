using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public sealed class PluginStoreTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _zips;
    private int _zipCounter;

    public PluginStoreTests()
    {
        Game = _root.Combine("game");
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "Sacred.exe"), "MZ");
        _zips = _root.Combine("zips");
        Directory.CreateDirectory(_zips);
        BackupRoot = _root.Combine("backup");
        Store = new ModStore(Game, BackupRoot);
    }

    private string Game { get; }
    private string BackupRoot { get; }
    private ModStore Store { get; }

    public void Dispose() => _root.Dispose();

    private string G(string rel) => Path.Combine(Game, rel.Replace('/', Path.DirectorySeparatorChar));
    private string Read(string rel) => File.ReadAllText(G(rel));
    private void Put(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(G(rel))!);
        File.WriteAllText(G(rel), content);
    }

    private ArchivePackage Package(params (string Name, string Content)[] entries)
        => ArchivePackage.Open(Pack.WriteZip(_zips, "z" + _zipCounter++ + ".zip", entries));

    private InstallOutcome Plugin(string id, string version, string path, string to, (string From, string To)[] files, params (string Name, string Content)[] entries)
    {
        using ArchivePackage p = Package(entries);
        return Store.InstallPlugin(p, id, version, path, to, files, "https://example.com/mod-index.json", "https://example.com/pkg.zip");
    }

    /// <summary>All files of the zip's root, to the game folder.</summary>
    private InstallOutcome PluginAll(string id, string version, params (string Name, string Content)[] entries)
        => Plugin(id, version, "", "", [], entries);

    private Task<List<InstalledMod>> Scan() => Store.ScanAsync(TestContext.Current.CancellationToken);

    private async Task<InstalledMod> Find(string id) => (await Scan()).Single(m => m.Id == id);

    private TrackedMod Tracked(string id) => Store.Registry.Load().Mods[id];

    // ---- what is copied where ----------------------------------------------------------------------------------

    [Fact]
    public void ExplicitFilesAreMappedBelowInstallTo()
    {
        InstallOutcome o = Plugin("p", "1.0", "pkg", "sub", [("bin/ddraw.dll", "ddraw.dll"), ("readme.txt", "docs/readme.txt")],
            ("pkg/bin/ddraw.dll", "D"), ("pkg/readme.txt", "R"), ("pkg/other.txt", "O"));
        Assert.Equal(2, o.FilesWritten);
        Assert.Equal("p", o.Id);
        Assert.Equal("1.0", o.Version);
        Assert.Equal("D", Read("sub/ddraw.dll"));
        Assert.Equal("R", Read("sub/docs/readme.txt"));
        Assert.False(File.Exists(G("sub/other.txt")));
        Assert.False(File.Exists(G("other.txt")));
        Assert.Equal(["sub/ddraw.dll", "sub/docs/readme.txt"], Tracked("p").Files.Keys.Order().ToArray());
    }

    [Fact]
    public void EmptyFileListInstallsEveryFileKeepingRelativePaths()
    {
        InstallOutcome o = PluginAll("p", "1", ("a.dll", "A"), ("x/y/z.txt", "Z"), ("x/b.txt", "B"));
        Assert.Equal(3, o.FilesWritten);
        Assert.Equal("A", Read("a.dll"));
        Assert.Equal("Z", Read("x/y/z.txt"));
        Assert.Equal("B", Read("x/b.txt"));
    }

    [Fact]
    public void EmptyFileListWithAPathAndInstallToStripsThePath()
    {
        Plugin("p", "1", "pkg/inner", "tools/sub", [], ("pkg/inner/a.dll", "A"), ("pkg/inner/d/b.txt", "B"), ("pkg/outside.txt", "N"));
        Assert.Equal("A", Read("tools/sub/a.dll"));
        Assert.Equal("B", Read("tools/sub/d/b.txt"));
        Assert.False(File.Exists(G("tools/sub/outside.txt")));
        Assert.False(File.Exists(G("outside.txt")));
    }

    [Fact]
    public void AFolderAsFromCopiesEverythingBelowIt()
    {
        Plugin("p", "1", "", "", [("data", "d")], ("data/a.txt", "A"), ("data/sub/b.txt", "B"), ("dataother/c.txt", "C"));
        Assert.Equal("A", Read("d/a.txt"));
        Assert.Equal("B", Read("d/sub/b.txt"));
        Assert.False(File.Exists(G("d/c.txt")));
        Assert.False(Directory.Exists(G("dataother")));
        Assert.Equal(2, Tracked("p").Files.Count);
    }

    [Fact]
    public void FromMatchesCaseInsensitively()
    {
        Plugin("p", "1", "", "", [("BIN/DdRaw.dll", "ddraw.dll")], ("bin/ddraw.dll", "D"));
        Assert.Equal("D", Read("ddraw.dll"));
    }

    [Fact]
    public void AFileAndAFolderOfTheSameNameInTheListAreBothCopied()
    {
        Plugin("p", "1", "", "", [("a.txt", "a.txt"), ("lib", "lib")], ("a.txt", "A"), ("lib/x.txt", "X"));
        Assert.Equal("A", Read("a.txt"));
        Assert.Equal("X", Read("lib/x.txt"));
    }

    [Fact]
    public void TheSameFileListedTwiceIsRefusedAsTwoFilesToOneTarget()
    {
        StoreException ex = Assert.Throws<StoreException>(() => Plugin("p", "1", "", "", [("a.txt", "a.txt"), ("a.txt", "a.txt")], ("a.txt", "A")));
        Assert.Contains("a.txt", ex.Message);
    }

    [Fact]
    public void MissingFromIsAStoreExceptionAndNothingIsWritten()
    {
        StoreException ex = Assert.Throws<StoreException>(() => Plugin("p", "1", "", "", [("a.txt", "a.txt"), ("nope.txt", "n.txt")], ("a.txt", "A")));
        Assert.Contains("nope.txt", ex.Message);
        Assert.False(File.Exists(G("a.txt")));
        Assert.False(File.Exists(Store.Registry.Path));
    }

    [Fact]
    public void AnEmptyPackageFolderIsAStoreException()
    {
        Assert.Throws<StoreException>(() => Plugin("p", "1", "nothing-here", "", [], ("a.txt", "A")));
        Assert.Throws<StoreException>(() => Plugin("p", "1", "", "", []));
    }

    [Fact]
    public void TwoFilesToOneTargetIsAStoreException()
    {
        StoreException ex = Assert.Throws<StoreException>(() => Plugin("p", "1", "", "", [("a.dll", "x.dll"), ("b.dll", "x.dll")], ("a.dll", "A"), ("b.dll", "B")));
        Assert.Contains("x.dll", ex.Message);
        Assert.False(File.Exists(G("x.dll")));
    }

    [Fact]
    public void TwoTargetsDifferingOnlyInCaseCollide()
    {
        Assert.Throws<StoreException>(() => Plugin("p", "1", "", "", [("a.dll", "x.dll"), ("b.dll", "X.DLL")], ("a.dll", "A"), ("b.dll", "B")));
    }

    [Theory]
    [InlineData("../evil.txt", "")]
    [InlineData("a/../../evil.txt", "")]
    [InlineData("C:/evil.txt", "")]
    [InlineData("/evil.txt", "")]
    [InlineData("evil.txt", "..")]
    [InlineData("evil.txt", "../outside")]
    [InlineData("evil.txt", "C:/x")]
    [InlineData("evil.txt", "/abs")]
    [InlineData("con.txt", "")]
    public void UnsafeTargetsAreRejected(string to, string installTo)
    {
        Exception ex = Assert.ThrowsAny<Exception>(() => Plugin("p", "1", "", installTo, [("a.txt", to)], ("a.txt", "A")));
        Assert.True(ex is StoreException or PackageException, ex.GetType().Name);
        Assert.False(File.Exists(Path.Combine(_root.Path, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root.Path, "outside", "evil.txt")));
        Assert.False(File.Exists(Store.Registry.Path));
    }

    [Fact]
    public void AZipEntryWithUnsafeNameIsRejectedWhenAllFilesAreTaken()
    {
        using ArchivePackage p = Package(("ok.txt", "A"), ("../evil.txt", "E"));
        Exception ex = Assert.ThrowsAny<Exception>(() => Store.InstallPlugin(p, "p", "1", "", "", [], null, null));
        Assert.True(ex is StoreException or PackageException);
        Assert.False(File.Exists(Path.Combine(_root.Path, "evil.txt")));
        Assert.False(File.Exists(G("ok.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("../x")]
    [InlineData("a/b")]
    public void InvalidIdsAreRefused(string id)
        => Assert.Throws<StoreException>(() => PluginAll(id, "1", ("a.txt", "A")));

    // ---- protected files ---------------------------------------------------------------------------------------

    [Fact]
    public void AnExistingExeIsNeverReplaced()
    {
        StoreException ex = Assert.Throws<StoreException>(() => PluginAll("p", "1", ("Sacred.exe", "EVIL"), ("other.dll", "O")));
        Assert.Contains("Sacred.exe", ex.Message);
        Assert.Equal("MZ", Read("Sacred.exe"));
        Assert.False(File.Exists(G("other.dll")));
    }

    [Fact]
    public void AnExistingExeInASubfolderIsNeverReplacedEither()
    {
        Put("bin/Tool.EXE", "ORIG");
        Assert.Throws<StoreException>(() => PluginAll("p", "1", ("bin/Tool.EXE", "NEW")));
        Assert.Equal("ORIG", Read("bin/Tool.EXE"));
    }

    [Fact]
    public void ANewExeThatDoesNotExistYetMayBeInstalledAndUpdated()
    {
        PluginAll("p", "1", ("Patch.exe", "V1"));
        Assert.Equal("V1", Read("Patch.exe"));
        PluginAll("p", "2", ("Patch.exe", "V2")); // it is the plugin's own file now
        Assert.Equal("V2", Read("Patch.exe"));
    }

    [Theory]
    [InlineData("mods/gear_mods.json")]
    [InlineData("mods/sacred-mod-manager.json")]
    [InlineData("MODS/Gear_Mods.json")]
    [InlineData("Mods/SACRED-MOD-MANAGER.JSON")]
    public void TheManagersAndGearsOwnFilesAreNeverReplaced(string target)
    {
        Put("mods/gear_mods.json", "{}");
        StoreException ex = Assert.Throws<StoreException>(() => PluginAll("p", "1", (target, "EVIL")));
        Assert.Contains("cannot replace", ex.Message);
        Assert.Equal("{}", Read("mods/gear_mods.json"));
    }

    [Fact]
    public void ThePluginMayWriteOtherFilesInTheModsFolder()
    {
        PluginAll("p", "1", ("mods/extra.txt", "X"));
        Assert.Equal("X", Read("mods/extra.txt"));
    }

    // ---- replaced files ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExistingFileIsSavedAndPutBackOnUninstall()
    {
        Put("ddraw.dll", "ORIGINAL");
        Put("keep.txt", "K");
        PluginAll("p", "1", ("ddraw.dll", "PLUGIN"), ("new.dll", "N"));
        Assert.Equal("PLUGIN", Read("ddraw.dll"));
        Assert.Equal(["ddraw.dll"], Tracked("p").Replaced);

        Store.Uninstall(await Find("p"), removeData: false, forgetSettings: false);
        Assert.Equal("ORIGINAL", Read("ddraw.dll"));
        Assert.False(File.Exists(G("new.dll")));
        Assert.Equal("K", Read("keep.txt"));
        Assert.DoesNotContain("p", Store.Registry.Load().Mods.Keys);
        Assert.False(Directory.Exists(Path.Combine(BackupRoot, "items", "p")));
    }

    [Fact]
    public async Task ARestoredFileIsByteIdentical()
    {
        byte[] original = [0, 1, 2, 3, 250, 251, 252, 253, 254, 255, 13, 10];
        Directory.CreateDirectory(Game);
        File.WriteAllBytes(G("binary.dll"), original);
        PluginAll("p", "1", ("binary.dll", "TEXT"));
        Store.Uninstall(await Find("p"), false, false);
        Assert.Equal(original, File.ReadAllBytes(G("binary.dll")));
    }

    [Fact]
    public async Task AReplacedFileInASubfolderComesBackAndItsFolderStays()
    {
        Put("lib/x.dll", "ORIG");
        PluginAll("p", "1", ("lib/x.dll", "NEW"));
        Store.Uninstall(await Find("p"), false, false);
        Assert.Equal("ORIG", Read("lib/x.dll"));
    }

    [Fact]
    public async Task AnUpdateKeepsTheFirstOriginal()
    {
        Put("ddraw.dll", "ORIGINAL");
        PluginAll("p", "1", ("ddraw.dll", "V1"));
        InstallOutcome o = PluginAll("p", "2", ("ddraw.dll", "V2"));
        Assert.Equal("1", o.PreviousVersion);
        Assert.Equal("V2", Read("ddraw.dll"));
        Assert.Equal(["ddraw.dll"], Tracked("p").Replaced);
        Store.Uninstall(await Find("p"), false, false);
        Assert.Equal("ORIGINAL", Read("ddraw.dll"));
    }

    [Fact]
    public async Task AnUpdateNeverDeletesAndRemoveCleansTheLeftoversAndRestoresReplacedOnes()
    {
        Put("a.dll", "ORIG-A");
        PluginAll("p", "1", ("a.dll", "A1"), ("b.dll", "B1"), ("sub/c.dll", "C1"));
        Assert.Equal(3, Tracked("p").Files.Count);

        PluginAll("p", "2", ("b.dll", "B2"));
        // an update only overwrites: the files the new version dropped stay where they are
        Assert.Equal("A1", Read("a.dll"));
        Assert.Equal("B2", Read("b.dll"));
        Assert.Equal("C1", Read("sub/c.dll"));
        TrackedMod t = Tracked("p");
        Assert.Equal(["a.dll", "b.dll", "sub/c.dll"], t.Files.Keys.Order().ToArray());
        Assert.Equal(["a.dll"], t.Replaced!.ToArray());
        Assert.Equal("2", t.Version);

        // Remove takes everything the plugin put there away, and the player's own file comes back
        Store.Uninstall(await Find("p"), false, false);
        Assert.Equal("ORIG-A", Read("a.dll"));
        Assert.False(File.Exists(G("b.dll")));
        Assert.False(File.Exists(G("sub/c.dll")));
        Assert.False(Directory.Exists(G("sub")));
    }

    [Fact]
    public void AnUpdateOverwritesTheFilesAndRehashes()
    {
        PluginAll("p", "1", ("a.dll", "A1"));
        string h1 = Tracked("p").Files["a.dll"];
        PluginAll("p", "2", ("a.dll", "A2"));
        Assert.Equal(Pack.Sha256("A2"u8.ToArray()), Tracked("p").Files["a.dll"]);
        Assert.NotEqual(h1, Tracked("p").Files["a.dll"]);
        Assert.Equal("A2", Read("a.dll"));
    }

    [Fact]
    public void AnUpdateThatNowAlsoCoversAFileThePlayerHasSavesIt()
    {
        PluginAll("p", "1", ("a.dll", "A1"));
        Put("b.dll", "PLAYER-B");
        PluginAll("p", "2", ("a.dll", "A2"), ("b.dll", "B2"));
        Assert.Equal(["b.dll"], Tracked("p").Replaced);
    }

    [Fact]
    public void AFailedInstallLeavesTheExistingPluginUntouched()
    {
        PluginAll("p", "1", ("a.dll", "A1"));
        Assert.Throws<StoreException>(() => Plugin("p", "2", "", "", [("missing.dll", "m.dll")], ("a.dll", "A2")));
        Assert.Equal("A1", Read("a.dll"));
        Assert.Equal("1", Tracked("p").Version);
    }

    // ---- ownership ---------------------------------------------------------------------------------------------

    [Fact]
    public void TwoPluginsClaimingTheSameTargetIsAStoreExceptionNamingTheOwner()
    {
        PluginAll("first", "1", ("shared.dll", "ONE"));
        StoreException ex = Assert.Throws<PluginConflictException>(() => PluginAll("second", "1", ("shared.dll", "TWO"), ("mine.dll", "M")));
        Assert.Contains("first", ex.Message);
        Assert.Contains("shared.dll", ex.Message);
        Assert.Equal("ONE", Read("shared.dll"));
        Assert.False(File.Exists(G("mine.dll")));
        Assert.DoesNotContain("second", Store.Registry.Load().Mods.Keys);
    }

    [Fact]
    public void TheOwnerCheckIgnoresCase()
    {
        PluginAll("first", "1", ("Shared.dll", "ONE"));
        Assert.Throws<PluginConflictException>(() => PluginAll("second", "1", ("SHARED.DLL", "TWO")));
    }

    [Fact]
    public async Task AfterTheOwnerIsRemovedAnotherPluginMayTakeTheTarget()
    {
        PluginAll("first", "1", ("shared.dll", "ONE"));
        Store.Uninstall(await Find("first"), false, false);
        PluginAll("second", "1", ("shared.dll", "TWO"));
        Assert.Equal("TWO", Read("shared.dll"));
    }

    [Fact]
    public async Task APluginDoesNotRestoreAFileAnotherPluginNowOwnsAndKeepsItsOwnReplacementsApart()
    {
        Put("a.dll", "ORIG-A");
        PluginAll("one", "1", ("a.dll", "ONE"));
        PluginAll("two", "1", ("b.dll", "TWO"));
        Store.Uninstall(await Find("two"), false, false);
        Assert.Equal("ONE", Read("a.dll"));
        Assert.Equal(["a.dll"], Tracked("one").Replaced);
    }

    [Fact]
    public void AnInstalledIdOfAnotherKindIsRefused()
    {
        using (ArchivePackage p = Package(("t.exe", "X"), ("a.txt", "A")))
        {
            Store.InstallTool(p, "same", "1", "", "t.exe", null, null);
        }

        StoreException ex = Assert.Throws<StoreException>(() => PluginAll("same", "1", ("c.dll", "C")));
        Assert.Contains("tool", ex.Message);
        Assert.False(File.Exists(G("c.dll")));
        Assert.Equal("tool", Tracked("same").Kind);
    }

    [Fact]
    public void ADifferentKindThanAnInstalledPluginIsRefusedForToolsToo()
    {
        PluginAll("same", "1", ("c.dll", "C"));
        using ArchivePackage p = Package(("t.exe", "X"));
        StoreException ex = Assert.Throws<StoreException>(() => Store.InstallTool(p, "same", "1", "", "t.exe", null, null));
        Assert.Contains("plugin", ex.Message);
        Assert.False(Directory.Exists(Store.ToolDirectory("same")));
    }

    // ---- registry and scan -------------------------------------------------------------------------------------

    [Fact]
    public void RegistryRecordHasKindHashesAndSources()
    {
        Put("r.dll", "ORIG");
        PluginAll("p", "1.5", ("r.dll", "R"), ("n/x.dll", "X"));
        TrackedMod t = Tracked("p");
        Assert.Equal("plugin", t.Kind);
        Assert.Equal(ItemKind.Plugin, t.ItemKind);
        Assert.Equal("p", t.Id);
        Assert.Equal("p", t.Folder);
        Assert.Equal("1.5", t.Version);
        Assert.Equal("https://example.com/mod-index.json", t.IndexUrl);
        Assert.Equal("https://example.com/pkg.zip", t.PackageUrl);
        Assert.NotNull(t.InstalledAt);
        Assert.Null(t.Launch);
        Assert.Equal(["r.dll"], t.Replaced);
        Assert.Equal(Pack.Sha256("R"u8.ToArray()), t.Files["r.dll"]);
        Assert.Equal(Pack.Sha256("X"u8.ToArray()), t.Files["n/x.dll"]);
    }

    [Fact]
    public void RegistryRecordOfAPluginWithoutReplacementsHasNoReplacedList()
    {
        PluginAll("p", "1", ("n.dll", "N"));
        Assert.Null(Tracked("p").Replaced);
    }

    [Fact]
    public async Task ScanListsThePluginWithKindDirectoryAndCounts()
    {
        PluginAll("p", "1", ("a.dll", "A"), ("b.dll", "B"), ("c/d.dll", "D"));
        InstalledMod m = await Find("p");
        Assert.Equal(ItemKind.Plugin, m.Kind);
        Assert.Equal(Path.GetFullPath(Game), m.Directory);
        Assert.Equal("p", m.Folder);
        Assert.Equal("1", m.Version);
        Assert.Equal("p", m.DisplayName);
        Assert.Null(m.Manifest);
        Assert.NotNull(m.Tracked);
        Assert.True(m.Enabled);
        Assert.Equal((0, 0), (m.ModifiedFiles, m.MissingFiles));
        Assert.Null(m.Error);

        File.WriteAllText(G("a.dll"), "EDITED");
        File.Delete(G("b.dll"));
        m = await Find("p");
        Assert.Equal((1, 1), (m.ModifiedFiles, m.MissingFiles));
        Assert.Null(m.Error);

        File.Delete(G("a.dll"));
        File.Delete(G("c/d.dll"));
        m = await Find("p");
        Assert.Equal(3, m.MissingFiles);
        Assert.Equal("its files are gone", m.Error);
    }

    [Fact]
    public async Task ScanListsPluginsInIdOrderWithoutMods()
    {
        PluginAll("zeta", "1", ("z.dll", "Z"));
        PluginAll("Alpha", "1", ("a.dll", "A"));
        List<InstalledMod> all = await Scan();
        Assert.Equal(["Alpha", "zeta"], all.Select(m => m.Id).ToArray());
        Assert.All(all, m => Assert.Equal(ItemKind.Plugin, m.Kind));
    }

    [Fact]
    public async Task ScanWorksWhenTheModsFolderHasNoModsAndWhenItDoesNotExist()
    {
        Assert.Empty(await Scan());
        PluginAll("p", "1", ("a.dll", "A"));
        Assert.Single(await Scan());
    }

    [Fact]
    public void InstallWorksWhenTheModsFolderDoesNotExistYet()
    {
        Assert.False(Directory.Exists(Store.ModsDir));
        PluginAll("p", "1", ("a.dll", "A"));
        Assert.True(File.Exists(Store.Registry.Path));
        Assert.Equal("A", Read("a.dll"));
    }

    // ---- uninstall ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UninstallRemovesEmptyFoldersItCreatedButNotOnesWithOtherFiles()
    {
        Put("shared/user.txt", "U");
        PluginAll("p", "1", ("shared/p.dll", "P"), ("deep/er/x.dll", "X"), ("deep/y.dll", "Y"));
        Store.Uninstall(await Find("p"), false, false);
        Assert.False(Directory.Exists(G("deep")));
        Assert.True(Directory.Exists(G("shared")));
        Assert.Equal("U", Read("shared/user.txt"));
        Assert.False(File.Exists(G("shared/p.dll")));
    }

    [Fact]
    public async Task UninstallNeverRemovesTheGameFolder()
    {
        File.Delete(G("Sacred.exe"));
        PluginAll("p", "1", ("only/x.dll", "X"));
        Store.Uninstall(await Find("p"), true, true);
        Assert.True(Directory.Exists(Game));
        Assert.False(Directory.Exists(G("only")));
    }

    [Fact]
    public async Task UninstallLeavesFilesThatChangedOrWereAddedByHand()
    {
        PluginAll("p", "1", ("a.dll", "A"));
        File.WriteAllText(G("a.dll"), "EDITED");
        Store.Uninstall(await Find("p"), false, false);
        // the plugin's files go, even edited ones (the record says they are the plugin's)
        Assert.False(File.Exists(G("a.dll")));
    }

    [Fact]
    public async Task UninstallOfAPluginWhoseFilesAreAlreadyGoneStillClearsTheRecord()
    {
        PluginAll("p", "1", ("a.dll", "A"), ("d/b.dll", "B"));
        File.Delete(G("a.dll"));
        Store.Uninstall(await Find("p"), false, false);
        Assert.Empty(Store.Registry.Load().Mods);
        Assert.False(Directory.Exists(G("d")));
    }

    [Fact]
    public async Task UninstallingAPluginTwiceViaAStaleEntryUsesTheRegistryAndRefusesWhenUnknown()
    {
        PluginAll("p", "1", ("a.dll", "A"));
        InstalledMod stale = await Find("p");
        Store.Uninstall(stale, false, false);
        var gone = new InstalledMod { Kind = ItemKind.Plugin, Folder = "p", Directory = Game };
        Assert.Throws<StoreException>(() => Store.Uninstall(gone, false, false));
    }

    [Fact]
    public async Task UninstallOfAPluginDoesNotTouchOtherRegistryEntries()
    {
        PluginAll("p", "1", ("a.dll", "A"));
        PluginAll("q", "1", ("b.dll", "B"));
        Store.Uninstall(await Find("p"), false, false);
        Assert.Equal(["q"], Store.Registry.Load().Mods.Keys.ToArray());
        Assert.Equal("B", Read("b.dll"));
    }

    [Fact]
    public async Task APluginCanBeInstalledAgainAfterUninstall()
    {
        Put("a.dll", "ORIG");
        PluginAll("p", "1", ("a.dll", "A1"));
        Store.Uninstall(await Find("p"), false, false);
        PluginAll("p", "2", ("a.dll", "A2"));
        Assert.Equal(["a.dll"], Tracked("p").Replaced);
        Store.Uninstall(await Find("p"), false, false);
        Assert.Equal("ORIG", Read("a.dll"));
    }
}
