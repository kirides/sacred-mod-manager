using SacredModManager.Core.App;
using Xunit;

namespace SacredModManager.Tests;

public class GameRunningTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "GameRunningTests");

    [Fact]
    public void OnlyASacredFromTheManagedFolderCounts()
    {
        string game = Path.Combine(Root, "Sacred Gold");
        string copy = Path.Combine(Root, "Sacred Gold-test");

        Assert.False(GameLocator.IsRunningFrom(game, []));
        Assert.False(GameLocator.IsRunningFrom(game, [Path.Combine(copy, "Sacred.exe")]));
        Assert.True(GameLocator.IsRunningFrom(game, [Path.Combine(copy, "Sacred.exe"), Path.Combine(game, "Sacred.exe")]));
    }

    [Fact]
    public void PathsAreComparedFullyAndWithoutCase()
    {
        string game = Path.Combine(Root, "Sacred Gold");
        Assert.True(GameLocator.IsRunningFrom(game + Path.DirectorySeparatorChar, [Path.Combine(Root, "x", "..", "Sacred Gold", "Sacred.exe")]));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(GameLocator.IsRunningFrom(game.ToUpperInvariant(), [Path.Combine(game, "Sacred.exe")]));
        }
    }

    [Fact]
    public void AnUnreadableProcessIsAssumedToBeOurs()
    {
        Assert.True(GameLocator.IsRunningFrom(Path.Combine(Root, "g"), [null]));
    }
}
