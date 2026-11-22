using SacredModManager.Core.App;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public sealed class PathRulesTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void NormalizeDropsTrailingSeparatorsButKeepsARoot()
    {
        string dir = _root.Combine("game");
        Assert.Equal(dir, PathRules.Normalize(dir + Path.DirectorySeparatorChar));
        Assert.Equal(dir, PathRules.Normalize(dir + Path.AltDirectorySeparatorChar + Path.AltDirectorySeparatorChar));
        string root = Path.GetPathRoot(dir)!;
        Assert.Equal(root, PathRules.Normalize(root));
    }

    [Fact]
    public void InsideIgnoresATrailingSlashOnTheRootAndBlocksTraversal()
    {
        string game = _root.Combine("game");
        string slashed = game + Path.DirectorySeparatorChar;
        Assert.True(PathRules.IsInside(slashed, Path.Combine(game, "ddraw.dll")));
        Assert.True(PathRules.IsInside(game, Path.Combine(slashed, "sub", "a.ini")));
        Assert.False(PathRules.IsInside(slashed, game));
        Assert.False(PathRules.IsInside(slashed, Path.Combine(game, "..", "x.ini")));
        Assert.False(PathRules.IsInside(slashed, game + "-other" + Path.DirectorySeparatorChar + "x.ini"));
        Assert.Null(PathRules.ResolveInside(slashed, "../x.ini"));
        Assert.Null(PathRules.ResolveInside(slashed, "a/../../x.ini"));
        foreach (string bad in new[] { "..\\x.ini", "a\\b.ini", "/x.ini", "C:/x.ini", "C:x.ini", string.Empty })
        {
            Assert.Null(PathRules.ResolveInside(slashed, bad));
        }

        Assert.Equal(Path.Combine(game, "ddraw.dll"), PathRules.ResolveInside(slashed, "ddraw.dll"));
    }

    [Fact]
    public void ComparisonIsCaseInsensitiveOnWindowsOnly()
    {
        string game = _root.Combine("Game");
        Assert.Equal(OperatingSystem.IsWindows(), PathRules.IsInside(game.ToUpperInvariant(), Path.Combine(game, "a.ini")));
        Assert.Equal(OperatingSystem.IsWindows(), PathRules.AreSame(game, game.ToLowerInvariant()));
    }

    [Fact]
    public void AStoreWithATrailingSlashInstallsAndStillRefusesTraversal()
    {
        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        var store = new ModStore(game + Path.DirectorySeparatorChar, _root.Combine("backups"), _root.Combine("tools"));
        Assert.Equal(game, store.GameFolder);
        byte[] bytes = [1, 2, 3];
        var ok = new LooseFileData("ddraw.dll", bytes, Pack.Sha256(bytes), "https://x.example/ddraw.dll");
        store.InstallLoosePlugin("spk", "1", string.Empty, [ok], null);
        Assert.True(File.Exists(Path.Combine(game, "ddraw.dll")));

        var bad = new LooseFileData("../x.ini", bytes, Pack.Sha256(bytes), "https://x.example/x.ini");
        Assert.ThrowsAny<Exception>(() => store.InstallLoosePlugin("spk2", "1", string.Empty, [bad], null));
        Assert.False(File.Exists(_root.Combine("x.ini")));
    }

    [Fact]
    public void GameLocatorReturnsANormalizedFolder()
    {
        string game = _root.Combine("game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Sacred.exe"), "MZ");
        Assert.Equal(game, GameLocator.Find(game + Path.DirectorySeparatorChar));
    }
}
