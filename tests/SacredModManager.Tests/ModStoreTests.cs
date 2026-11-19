using System.Text;
using System.Text.Json;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public sealed class ModStoreTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _zips;

    public ModStoreTests()
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
    private string ModDir(string folder) => Path.Combine(Game, "mods", folder);
    private string ModFile(string folder, string rel) => Path.Combine(ModDir(folder), rel.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose() => _root.Dispose();

    private int _zipCounter;

    private ZipPackage Package(params (string Name, string Content)[] entries)
        => ZipPackage.Open(Pack.WriteZip(_zips, "z" + _zipCounter++ + ".zip", entries));

    private InstallOutcome Install(string path, string id, params (string Name, string Content)[] entries)
    {
        using ZipPackage p = Package(entries);
        return Store.Install(p, path, id, "https://example.com/mod-index.json", "https://example.com/pkg.zip");
    }

    /// <summary>mods/bounty with the given version and extra files.</summary>
    private InstallOutcome InstallBounty(string version, params (string Rel, string Content)[] files)
        => Install("mods/bounty", "bounty", [("mods/bounty/mod.lua", Pack.ModLua("bounty", version)), .. files.Select(f => ("mods/bounty/" + f.Rel, f.Content))]);

    private Task<List<InstalledMod>> Scan() => Store.ScanAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task FreshInstallWritesFilesRegistryAndShowsInScan()
    {
        InstallOutcome o = InstallBounty("1.0.0", ("main.lua", "print(1)"), ("data/x.lua", "x"));
        Assert.Equal("bounty", o.Id);
        Assert.Equal("bounty", o.Folder);
        Assert.Equal("1.0.0", o.Version);
        Assert.Null(o.PreviousVersion);
        Assert.Null(o.Backup);
        Assert.Equal(3, o.FilesWritten);
        Assert.Equal(0, o.ReplacedModified);
        Assert.Equal("print(1)", File.ReadAllText(ModFile("bounty", "main.lua")));
        Assert.Equal("x", File.ReadAllText(ModFile("bounty", "data/x.lua")));

        string registryPath = Path.Combine(Game, "mods", "sacred-mod-manager.json");
        Assert.True(File.Exists(registryPath));
        TrackedMod t = Store.Registry.Load().Mods["bounty"];
        Assert.Equal("bounty", t.Folder);
        Assert.Equal("1.0.0", t.Version);
        Assert.Equal("https://example.com/mod-index.json", t.IndexUrl);
        Assert.Equal("https://example.com/pkg.zip", t.PackageUrl);
        Assert.False(string.IsNullOrEmpty(t.InstalledAt));
        Assert.Equal(Pack.Sha256("print(1)"u8.ToArray()), t.Files["main.lua"]);
        Assert.Equal(Pack.Sha256("x"u8.ToArray()), t.Files["data/x.lua"]);
        Assert.Equal(3, t.Files.Count);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(registryPath)))
        {
            Assert.True(doc.RootElement.GetProperty("mods").GetProperty("bounty").GetProperty("files").TryGetProperty("main.lua", out _));
        }

        InstalledMod m = Assert.Single(await Scan());
        Assert.Equal("bounty", m.Id);
        Assert.Equal("Mod bounty", m.DisplayName);
        Assert.Equal("1.0.0", m.Version);
        Assert.NotNull(m.Tracked);
        Assert.Null(m.Error);
        Assert.True(m.Enabled);
        Assert.Equal(0, m.ModifiedFiles);
        Assert.Equal(0, m.MissingFiles);
        Assert.Empty(Directory.GetFiles(Path.Combine(Game, "mods"), "*.tmp"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(BackupRoot, "staging")));
        Assert.Empty(Store.Backups("bounty"));
    }

    [Fact]
    public async Task EnabledFollowsManifestDefaultAndGearModsJson()
    {
        Install("mods/off", "off", ("mods/off/mod.lua", Pack.ModLua("off", enabled: false)));
        Install("mods/on", "on", ("mods/on/mod.lua", Pack.ModLua("on", enabled: true)));
        Install("mods/plain", "plain", ("mods/plain/mod.lua", Pack.ModLua("plain")));
        Install("mods/forced", "forced", ("mods/forced/mod.lua", Pack.ModLua("forced", enabled: false)));
        Install("mods/muted", "muted", ("mods/muted/mod.lua", Pack.ModLua("muted")));
        Store.State.SetEnabled("forced", true);
        Store.State.SetEnabled("muted", false);

        Dictionary<string, bool> enabled = (await Scan()).ToDictionary(m => m.Id, m => m.Enabled);
        Assert.False(enabled["off"]);
        Assert.True(enabled["on"]);
        Assert.True(enabled["plain"]);
        Assert.True(enabled["forced"]);
        Assert.False(enabled["muted"]);
    }

    [Fact]
    public async Task ScanWorksWithCorruptStateAndRegistryFiles()
    {
        InstallBounty("1.0.0", ("main.lua", "a"));
        File.WriteAllText(Path.Combine(Game, "mods", "gear_mods.json"), "{ broken");
        File.WriteAllText(Path.Combine(Game, "mods", "sacred-mod-manager.json"), "{ broken");
        InstalledMod m = Assert.Single(await Scan());
        Assert.True(m.Enabled);
        Assert.Null(m.Tracked);
        Assert.True(File.Exists(Path.Combine(Game, "mods", "sacred-mod-manager.json.bak")));
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(Game, "mods", "gear_mods.json")));
    }

    [Fact]
    public async Task ScanWithoutAModsFolderIsEmpty() => Assert.Empty(await Scan());

    [Fact]
    public async Task ScanListsBrokenModsWithAnErrorAndSkipsUnrelatedFolders()
    {
        Directory.CreateDirectory(ModDir("broken"));
        File.WriteAllText(ModFile("broken", "mod.lua"), "return {");
        Directory.CreateDirectory(ModDir("nomanifest"));
        File.WriteAllText(ModFile("nomanifest", "main.lua"), "x");
        Directory.CreateDirectory(ModDir("random-folder"));
        File.WriteAllText(ModFile("random-folder", "notes.txt"), "x");
        Directory.CreateDirectory(ModDir("good"));
        File.WriteAllText(ModFile("good", "mod.lua"), Pack.ModLua("good"));
        File.WriteAllText(Path.Combine(Game, "mods", "loose-file.lua"), "x");

        List<InstalledMod> mods = await Scan();
        Assert.Equal(["broken", "good", "nomanifest"], mods.Select(m => m.Folder).ToArray());
        InstalledMod broken = mods[0];
        Assert.NotNull(broken.Error);
        Assert.Null(broken.Manifest);
        Assert.False(broken.Enabled);
        Assert.Equal("broken", broken.Id);
        Assert.Equal("broken", broken.DisplayName);
        Assert.Contains("mod.lua is missing", mods[2].Error, StringComparison.Ordinal);
        Assert.Null(mods[1].Error);
        Assert.Null(mods[1].Tracked);
    }

    [Fact]
    public async Task ScanCountsModifiedAndMissingFiles()
    {
        InstallBounty("1.0.0", ("main.lua", "a"), ("b.lua", "b"), ("c.lua", "c"));
        File.WriteAllText(ModFile("bounty", "main.lua"), "changed");
        File.Delete(ModFile("bounty", "b.lua"));
        File.WriteAllText(ModFile("bounty", "extra.lua"), "user file");
        InstalledMod m = Assert.Single(await Scan());
        Assert.Equal(1, m.ModifiedFiles);
        Assert.Equal(1, m.MissingFiles);
    }

    [Fact]
    public async Task UpdateRemovesDroppedFilesKeepsUserFilesAndBacksUpModifiedOnes()
    {
        InstallBounty("1.0.0", ("main.lua", "v1 main"), ("old.lua", "old"), ("data/old/deep.lua", "deep"), ("same.lua", "same"));
        File.WriteAllText(ModFile("bounty", "main.lua"), "locally modified");
        File.WriteAllText(ModFile("bounty", "config.lua"), "user config");
        Directory.CreateDirectory(ModFile("bounty", "saves"));
        File.WriteAllText(ModFile("bounty", "saves/slot1.dat"), "save");

        InstallOutcome o = InstallBounty("2.0.0", ("main.lua", "v2 main"), ("same.lua", "same"), ("new.lua", "new"));

        Assert.Equal("1.0.0", o.PreviousVersion);
        Assert.Equal("2.0.0", o.Version);
        Assert.Equal(4, o.FilesWritten);
        Assert.Equal(1, o.ReplacedModified);
        Assert.NotNull(o.Backup);
        Assert.Equal("1.0.0", o.Backup.Version);
        Assert.Equal("update", o.Backup.Reason);

        Assert.Equal("v2 main", File.ReadAllText(ModFile("bounty", "main.lua")));
        Assert.Equal("new", File.ReadAllText(ModFile("bounty", "new.lua")));
        Assert.False(File.Exists(ModFile("bounty", "old.lua")));
        Assert.False(File.Exists(ModFile("bounty", "data/old/deep.lua")));
        Assert.False(Directory.Exists(ModFile("bounty", "data")));
        Assert.Equal("user config", File.ReadAllText(ModFile("bounty", "config.lua")));
        Assert.Equal("save", File.ReadAllText(ModFile("bounty", "saves/slot1.dat")));

        string backedUp = Path.Combine(o.Backup.Directory, "files");
        Assert.Equal("locally modified", File.ReadAllText(Path.Combine(backedUp, "main.lua")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(backedUp, "old.lua")));
        Assert.Equal("user config", File.ReadAllText(Path.Combine(backedUp, "config.lua")));
        Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(o.Backup.Directory, "backup.json")), StringComparison.Ordinal);

        TrackedMod t = Store.Registry.Load().Mods["bounty"];
        Assert.Equal("2.0.0", t.Version);
        Assert.Equal(["main.lua", "mod.lua", "new.lua", "same.lua"], t.Files.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Single(Store.Registry.Load().Mods);

        InstalledMod m = Assert.Single(await Scan());
        Assert.Equal("2.0.0", m.Version);
        Assert.Equal(0, m.ModifiedFiles);
        Assert.Single(Store.Backups("bounty"));
    }

    [Fact]
    public void UpdateOfAnUnmodifiedModCountsNoReplacedFiles()
    {
        InstallBounty("1.0.0", ("main.lua", "v1"));
        InstallOutcome o = InstallBounty("1.1.0", ("main.lua", "v2"));
        Assert.Equal(0, o.ReplacedModified);
    }

    [Fact]
    public void ReinstallingTheSameVersionIsAnUpdateWithABackup()
    {
        InstallBounty("1.0.0", ("main.lua", "v1"));
        InstallOutcome o = InstallBounty("1.0.0", ("main.lua", "v1"));
        Assert.Equal("1.0.0", o.PreviousVersion);
        Assert.NotNull(o.Backup);
    }

    [Fact]
    public async Task RestoreBringsBackTheOldVersionAndItsRegistryEntry()
    {
        InstallBounty("1.0.0", ("main.lua", "v1 main"), ("old.lua", "old"));
        File.WriteAllText(ModFile("bounty", "config.lua"), "user config");
        string v1Hash = Store.Registry.Load().Mods["bounty"].Files["main.lua"];
        InstallOutcome o = InstallBounty("2.0.0", ("main.lua", "v2 main"), ("new.lua", "new"));

        Store.Restore(o.Backup!);

        Assert.Contains("1.0.0", File.ReadAllText(ModFile("bounty", "mod.lua")), StringComparison.Ordinal);
        Assert.Equal("v1 main", File.ReadAllText(ModFile("bounty", "main.lua")));
        Assert.Equal("old", File.ReadAllText(ModFile("bounty", "old.lua")));
        Assert.Equal("user config", File.ReadAllText(ModFile("bounty", "config.lua")));
        Assert.False(File.Exists(ModFile("bounty", "new.lua")));
        TrackedMod t = Store.Registry.Load().Mods["bounty"];
        Assert.Equal("1.0.0", t.Version);
        Assert.Equal(v1Hash, t.Files["main.lua"]);
        Assert.False(t.Files.ContainsKey("new.lua"));
        InstalledMod m = Assert.Single(await Scan());
        Assert.Equal("1.0.0", m.Version);
        Assert.Equal(0, m.ModifiedFiles);
        Assert.Equal(0, m.MissingFiles);

        // the state before the restore was backed up too, so the restore can be undone
        List<BackupInfo> backups = Store.Backups("bounty");
        Assert.Equal(2, backups.Count);
        Assert.Contains(backups, b => b.Reason.StartsWith("before restoring", StringComparison.Ordinal) && b.Version == "2.0.0");
        Store.Restore(backups.First(b => b.Version == "2.0.0"));
        Assert.Equal("v2 main", File.ReadAllText(ModFile("bounty", "main.lua")));
        Assert.Equal("2.0.0", Store.Registry.Load().Mods["bounty"].Version);
    }

    [Fact]
    public void RestoreOfAHandInstalledBackupUntracksTheMod()
    {
        Directory.CreateDirectory(ModDir("bounty"));
        File.WriteAllText(ModFile("bounty", "mod.lua"), Pack.ModLua("bounty", "0.5.0"));
        File.WriteAllText(ModFile("bounty", "hand.lua"), "hand");
        InstallOutcome o = InstallBounty("1.0.0", ("main.lua", "v1"));
        Assert.Equal("0.5.0", o.PreviousVersion);
        Assert.Contains("by hand", o.Backup!.Reason, StringComparison.Ordinal);
        Assert.True(Store.Registry.Load().Mods.ContainsKey("bounty"));

        Store.Restore(o.Backup);

        Assert.False(Store.Registry.Load().Mods.ContainsKey("bounty"));
        Assert.Equal("hand", File.ReadAllText(ModFile("bounty", "hand.lua")));
        Assert.Contains("0.5.0", File.ReadAllText(ModFile("bounty", "mod.lua")), StringComparison.Ordinal);
        Assert.False(File.Exists(ModFile("bounty", "main.lua")));
    }

    [Fact]
    public void RestoreAfterUninstallRecreatesTheFolder()
    {
        InstallBounty("1.0.0", ("main.lua", "v1"));
        InstalledMod mod = ScanSync().Single();
        BackupInfo b = Store.Uninstall(mod, removeData: true, forgetSettings: false)!;
        Assert.False(Directory.Exists(ModDir("bounty")));
        Store.Restore(b);
        Assert.Equal("v1", File.ReadAllText(ModFile("bounty", "main.lua")));
        Assert.Equal("1.0.0", Store.Registry.Load().Mods["bounty"].Version);
    }

    private List<InstalledMod> ScanSync() => Store.ScanAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    [Fact]
    public void UninstallKeepsUserDataByDefault()
    {
        InstallBounty("1.0.0", ("main.lua", "v1"), ("lib/util.lua", "u"));
        File.WriteAllText(ModFile("bounty", "config.lua"), "user config");
        Directory.CreateDirectory(ModFile("bounty", "saves"));
        File.WriteAllText(ModFile("bounty", "saves/slot.dat"), "s");
        Store.State.SetEnabled("bounty", false);

        BackupInfo? b = Store.Uninstall(ScanSync().Single(), removeData: false, forgetSettings: false);

        Assert.NotNull(b);
        Assert.Equal("removed", b.Reason);
        Assert.False(File.Exists(ModFile("bounty", "mod.lua")));
        Assert.False(File.Exists(ModFile("bounty", "main.lua")));
        Assert.False(Directory.Exists(ModFile("bounty", "lib")));
        Assert.Equal("user config", File.ReadAllText(ModFile("bounty", "config.lua")));
        Assert.Equal("s", File.ReadAllText(ModFile("bounty", "saves/slot.dat")));
        Assert.Empty(Store.Registry.Load().Mods);
        Assert.True(Store.State.ReadEnabled().ContainsKey("bounty"));
        Assert.True(File.Exists(Path.Combine(b.Directory, "files", "main.lua")));
    }

    [Fact]
    public void UninstallWithoutLeftoversRemovesTheFolder()
    {
        InstallBounty("1.0.0", ("main.lua", "v1"), ("a/b/c.lua", "x"));
        Store.Uninstall(ScanSync().Single(), removeData: false, forgetSettings: false);
        Assert.False(Directory.Exists(ModDir("bounty")));
    }

    [Fact]
    public void UninstallWithRemoveDataDeletesTheWholeFolder()
    {
        InstallBounty("1.0.0", ("main.lua", "v1"));
        File.WriteAllText(ModFile("bounty", "config.lua"), "user config");
        BackupInfo b = Store.Uninstall(ScanSync().Single(), removeData: true, forgetSettings: false)!;
        Assert.False(Directory.Exists(ModDir("bounty")));
        Assert.Equal("user config", File.ReadAllText(Path.Combine(b.Directory, "files", "config.lua")));
    }

    [Fact]
    public void UninstallForgetSettingsRemovesTheEntryInGearModsJson()
    {
        InstallBounty("1.0.0");
        Install("mods/other", "other", ("mods/other/mod.lua", Pack.ModLua("other")));
        File.WriteAllText(Path.Combine(Game, "mods", "gear_mods.json"), "{\"mods\":{\"bounty\":{\"enabled\":false,\"settings\":{\"a\":1}},\"other\":{\"enabled\":true}}}");
        Store.Uninstall(ScanSync().Single(m => m.Id == "bounty"), removeData: false, forgetSettings: true);
        Assert.Equal("{\"mods\":{\"other\":{\"enabled\":true}}}", File.ReadAllText(Path.Combine(Game, "mods", "gear_mods.json")));
    }

    [Fact]
    public void UninstallWithoutForgetKeepsTheEntry()
    {
        InstallBounty("1.0.0");
        File.WriteAllText(Path.Combine(Game, "mods", "gear_mods.json"), "{\"mods\":{\"bounty\":{\"enabled\":false}}}");
        Store.Uninstall(ScanSync().Single(), removeData: true, forgetSettings: false);
        Assert.Contains("bounty", File.ReadAllText(Path.Combine(Game, "mods", "gear_mods.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void UntrackedHandInstalledModIsRemovedCompletely()
    {
        Directory.CreateDirectory(ModFile("hand", "sub"));
        File.WriteAllText(ModFile("hand", "mod.lua"), Pack.ModLua("hand"));
        File.WriteAllText(ModFile("hand", "main.lua"), "x");
        File.WriteAllText(ModFile("hand", "sub/y.lua"), "y");
        InstalledMod mod = ScanSync().Single();
        Assert.Null(mod.Tracked);

        BackupInfo? b = Store.Uninstall(mod, removeData: false, forgetSettings: false);

        Assert.False(Directory.Exists(ModDir("hand")));
        Assert.NotNull(b);
        Assert.True(File.Exists(Path.Combine(b.Directory, "files", "sub", "y.lua")));
        Assert.Single(Store.Backups("hand"));
    }

    [Fact]
    public void UninstallRefusesFoldersOutsideTheModsFolderOrMissingOnes()
    {
        string outside = _root.Combine("elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "x");
        var fake = new InstalledMod { Folder = "elsewhere", Directory = outside };
        Assert.Throws<StoreException>(() => Store.Uninstall(fake, removeData: true, forgetSettings: false));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));

        var sibling = new InstalledMod { Folder = "x", Directory = Game + "-evil" };
        Assert.Throws<StoreException>(() => Store.Uninstall(sibling, removeData: true, forgetSettings: false));

        var viaDots = new InstalledMod { Folder = "..", Directory = Path.Combine(Game, "mods", "..", "elsewhere") };
        Assert.Throws<StoreException>(() => Store.Uninstall(viaDots, removeData: true, forgetSettings: false));

        var gone = new InstalledMod { Folder = "gone", Directory = ModDir("gone") };
        Assert.Throws<StoreException>(() => Store.Uninstall(gone, removeData: true, forgetSettings: false));
    }

    [Fact]
    public void IdMismatchBetweenIndexAndZipIsRejectedAndNothingIsWritten()
    {
        var ex = Assert.Throws<StoreException>(() => Install("mods/bounty", "promised", ("mods/bounty/mod.lua", Pack.ModLua("bounty")), ("mods/bounty/main.lua", "x")));
        Assert.Contains("promised", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(ModDir("bounty")));
        Assert.False(File.Exists(Path.Combine(Game, "mods", "sacred-mod-manager.json")));
    }

    [Fact]
    public void ExpectedIdNullAcceptsAnyId()
    {
        using ZipPackage p = Package(("mods/a/mod.lua", Pack.ModLua("whatever")));
        Assert.Equal("whatever", Store.Install(p, "mods/a", null, null, null).Id);
        Assert.Null(Store.Registry.Load().Mods["whatever"].IndexUrl);
    }

    [Fact]
    public void ExpectedIdIsCaseSensitive()
    {
        Assert.Throws<StoreException>(() => Install("mods/a", "Bounty", ("mods/a/mod.lua", Pack.ModLua("bounty"))));
    }

    [Fact]
    public void MissingModLuaAtThePathIsRejected()
    {
        Assert.Throws<StoreException>(() => Install("mods/none", "x", ("mods/a/mod.lua", Pack.ModLua("x"))));
    }

    [Fact]
    public void BrokenModLuaInTheZipIsAStoreException()
    {
        Assert.Throws<StoreException>(() => Install("mods/a", "a", ("mods/a/mod.lua", "return {")));
        Assert.Throws<StoreException>(() => Install("mods/a", "a", ("mods/a/mod.lua", "return {name='x'}")));
        Assert.False(Directory.Exists(ModDir("a")));
    }

    [Fact]
    public void UnsafeEntryInTheZipIsRejectedAndNothingIsWritten()
    {
        Assert.Throws<PackageException>(() => Install("mods/a", "a", ("mods/a/mod.lua", Pack.ModLua("a")), ("mods/a/../../evil.lua", "x")));
        Assert.False(Directory.Exists(ModDir("a")));
        Assert.False(File.Exists(Path.Combine(Game, "evil.lua")));
        Assert.False(File.Exists(Path.Combine(Game, "mods", "evil.lua")));
    }

    [Fact]
    public void RootLevelModUsesTheIdAsFolderName()
    {
        InstallOutcome o = Install(string.Empty, "rootmod", ("mod.lua", Pack.ModLua("rootmod")), ("main.lua", "x"), ("lib/u.lua", "u"));
        Assert.Equal("rootmod", o.Folder);
        Assert.True(File.Exists(ModFile("rootmod", "lib/u.lua")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*")]
    [InlineData("name.")]
    [InlineData("name ")]
    public void BadFolderNamesAreRejected(string name)
        => Assert.Throws<StoreException>(() => ModStore.CheckFolderName(name));

    [Theory]
    [InlineData("bounty")]
    [InlineData("art-bars")]
    [InlineData("a.b")]
    [InlineData("Mod_1")]
    [InlineData("ü")]
    public void GoodFolderNamesAreAccepted(string name) => ModStore.CheckFolderName(name);

    [Theory]
    [InlineData("mods/..")]
    [InlineData("mods/.hidden")]
    public void InstallRejectsBadFolderNamesFromThePathInTheZip(string path)
    {
        Assert.Throws<StoreException>(() => Install(path, "a", (path + "/mod.lua", Pack.ModLua("a")), (path + "/x.lua", "x")));
        Assert.False(File.Exists(Path.Combine(Game, "mods", "x.lua")));
        Assert.False(File.Exists(Path.Combine(Game, "x.lua")));
        Assert.False(File.Exists(Path.Combine(Game, "mods", "mod.lua")));
    }

    [Fact]
    public void SameModIdInAnotherFolderIsRejected()
    {
        Directory.CreateDirectory(ModDir("oldname"));
        File.WriteAllText(ModFile("oldname", "mod.lua"), Pack.ModLua("bounty"));
        var ex = Assert.Throws<StoreException>(() => InstallBounty("1.0.0", ("main.lua", "x")));
        Assert.Contains("oldname", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(ModDir("bounty")));
    }

    [Fact]
    public void SameModIdInAnotherFolderIsFoundIgnoringFolderCaseOfTheTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "folders that differ only by case are different folders on Linux");
        InstallBounty("1.0.0");
        // updating from a zip whose folder name differs only by case is the same folder, not a clash
        InstallOutcome o = Install("mods/Bounty", "bounty", ("mods/Bounty/mod.lua", Pack.ModLua("bounty", "1.1.0")));
        Assert.Equal("1.0.0", o.PreviousVersion);
    }

    [Fact]
    public void FolderOfAnotherTrackedModIsRejected()
    {
        Install("mods/shared", "first", ("mods/shared/mod.lua", Pack.ModLua("first")));
        var ex = Assert.Throws<StoreException>(() => Install("mods/shared", "second", ("mods/shared/mod.lua", Pack.ModLua("second"))));
        Assert.Contains("first", ex.Message, StringComparison.Ordinal);
        Assert.Contains("first", File.ReadAllText(ModFile("shared", "mod.lua")), StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableModLuaOfAnotherFolderIsNotAClash()
    {
        Directory.CreateDirectory(ModDir("broken"));
        File.WriteAllText(ModFile("broken", "mod.lua"), "return {");
        InstallBounty("1.0.0");
        Assert.True(File.Exists(ModFile("bounty", "mod.lua")));
    }

    [Fact]
    public void BackupsArePrunedToTheLimitKeepingTheNewest()
    {
        const int installs = 9;
        for (int i = 1; i <= installs; i++)
        {
            InstallBounty(i + ".0.0", ("main.lua", "v" + i));
            Thread.Sleep(3); // backup folders are named by millisecond
        }

        List<BackupInfo> backups = Store.Backups("bounty");
        Assert.Equal(ModStore.BackupsKept, backups.Count);
        // backups hold the previous versions: after 9 installs the newest backups are 8.0.0 down to 4.0.0
        Assert.Equal(["8.0.0", "7.0.0", "6.0.0", "5.0.0", "4.0.0"], backups.Select(b => b.Version).ToArray());
        Assert.Equal(ModStore.BackupsKept, Directory.EnumerateDirectories(Path.Combine(BackupRoot, "mods", "bounty")).Count());
    }

    [Fact]
    public void BackupsOfOneModDoNotPruneThoseOfAnother()
    {
        Install("mods/other", "other", ("mods/other/mod.lua", Pack.ModLua("other", "1")));
        Install("mods/other", "other", ("mods/other/mod.lua", Pack.ModLua("other", "2")));
        for (int i = 1; i <= 8; i++)
        {
            InstallBounty(i + ".0.0");
            Thread.Sleep(3);
        }

        Assert.Single(Store.Backups("other"));
        Assert.Equal(ModStore.BackupsKept, Store.Backups("bounty").Count);
    }

    [Fact]
    public void BackupsIgnoresUnreadableBackupFolders()
    {
        Assert.Empty(Store.Backups("nothing"));
        InstallBounty("1.0.0");
        InstallBounty("2.0.0");
        string baseDir = Path.Combine(BackupRoot, "mods", "bounty");
        Directory.CreateDirectory(Path.Combine(baseDir, "junk"));
        Directory.CreateDirectory(Path.Combine(baseDir, "junk2", "files"));
        File.WriteAllText(Path.Combine(baseDir, "junk2", "backup.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(baseDir, "junk3", "files"));
        File.WriteAllText(Path.Combine(baseDir, "junk3", "backup.json"), "{\"when\":\"yesterday-ish\"}");
        Assert.Single(Store.Backups("bounty"));
    }

    [Fact]
    public void InstallFailureBecauseOfALockedFileIsAStoreExceptionAndKeepsTheOldRecord()
    {
        InstallBounty("1.0.0", ("main.lua", "v1"));
        using (new FileStream(ModFile("bounty", "main.lua"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<StoreException>(() => InstallBounty("2.0.0", ("main.lua", "v2")));
            Assert.Contains("Is the game running", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal("1.0.0", Store.Registry.Load().Mods["bounty"].Version);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(BackupRoot, "staging")));
    }

    [Fact]
    public void InstallNeverWritesOutsideTheModFolderAndTheRegistry()
    {
        string[] before = Directory.EnumerateFileSystemEntries(Game).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        InstallBounty("1.0.0", ("main.lua", "x"));
        string[] after = Directory.EnumerateFileSystemEntries(Game).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal([.. before, "mods"], after);
        Assert.Equal(["bounty", "sacred-mod-manager.json"], Directory.EnumerateFileSystemEntries(Path.Combine(Game, "mods")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SeveralModsFromOneZipAreInstalledIndependently()
    {
        byte[] zip = Pack.Zip(("mods/a/mod.lua", Pack.ModLua("a")), ("mods/b/mod.lua", Pack.ModLua("b")), ("mods/b/main.lua", "b"));
        string path = Path.Combine(_zips, "multi.zip");
        File.WriteAllBytes(path, zip);
        using (ZipPackage p = ZipPackage.Open(path))
        {
            Store.Install(p, "mods/a", "a", null, null);
            Store.Install(p, "mods/b", "b", null, null);
        }

        List<InstalledMod> mods = await Scan();
        Assert.Equal(["a", "b"], mods.Select(m => m.Id).ToArray());
        Assert.False(File.Exists(ModFile("a", "main.lua")));
        Assert.Equal(2, Store.Registry.Load().Mods.Count);
    }

    [Fact]
    public void NonAsciiFileNamesAndContentSurvive()
    {
        byte[] content = Encoding.UTF8.GetBytes("Grüße – 日本語");
        using ZipPackage p = ZipPackage.Open(WriteBinaryZip(("mods/a/mod.lua", Encoding.UTF8.GetBytes(Pack.ModLua("a"))), ("mods/a/Drachentöter.txt", content)));
        Store.Install(p, "mods/a", "a", null, null);
        Assert.Equal(content, File.ReadAllBytes(ModFile("a", "Drachentöter.txt")));
        Assert.Equal(Pack.Sha256(content), Store.Registry.Load().Mods["a"].Files["Drachentöter.txt"]);
    }

    private string WriteBinaryZip(params (string Name, byte[] Content)[] entries)
    {
        string path = Path.Combine(_zips, "bin" + _zipCounter++ + ".zip");
        File.WriteAllBytes(path, Pack.Zip(entries));
        return path;
    }

    [Fact]
    public void RegistrySurvivesARoundTripWithoutLosingOtherMods()
    {
        InstallBounty("1.0.0");
        Install("mods/other", "other", ("mods/other/mod.lua", Pack.ModLua("other")));
        InstallBounty("1.1.0");
        InstallRegistryFile r = Store.Registry.Load();
        Assert.Equal(["bounty", "other"], r.Mods.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(1, r.Version);
    }
}
