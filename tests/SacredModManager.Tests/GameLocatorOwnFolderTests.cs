using SacredModManager.Core.App;
using Xunit;

namespace SacredModManager.Tests;

[Collection("GameLocatorStatics")]
public class GameLocatorOwnFolderTests
{
    [Fact]
    public void AManagerThatSitsNextToSacredExeFindsTheGameWithoutBeingTold()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("Sacred.exe"), "MZ");
        string? before = GameLocator.OwnFolder;
        try
        {
            GameLocator.OwnFolder = dir.Path;
            Assert.Equal(Path.GetFullPath(dir.Path), GameLocator.Find(null));
        }
        finally
        {
            GameLocator.OwnFolder = before;
        }
    }

    [Fact]
    public void ASavedFolderStillWinsOverTheManagersOwn()
    {
        using var own = new TempDir();
        using var saved = new TempDir();
        File.WriteAllText(own.Combine("Sacred.exe"), "MZ");
        File.WriteAllText(saved.Combine("Sacred.exe"), "MZ");
        string? before = GameLocator.OwnFolder;
        try
        {
            GameLocator.OwnFolder = own.Path;
            Assert.Equal(Path.GetFullPath(saved.Path), GameLocator.Find(saved.Path));
        }
        finally
        {
            GameLocator.OwnFolder = before;
        }
    }
}
