using SacredModManager.Core.App;
using Xunit;

namespace SacredModManager.Tests;

public class PluginZipHintTests
{
    private static string Zip(TempDir dir, params (string Name, string Text)[] files)
    {
        string path = dir.Combine("x.zip");
        File.WriteAllBytes(path, Pack.Zip(files));
        return path;
    }

    [Fact]
    public void SacredBildAndSacredGearPackagesAreRecognisedAsPlugins()
    {
        using var dir = new TempDir();
        Assert.Equal(["ddraw.dll"], ModManagerService.FindPluginFiles(Zip(dir, ("ddraw.dll", "x"), ("SacredBild.ini", "y"), ("SacredBild/LICENSE", "z"))));
        Assert.Equal(["SacredGear.dll"], ModManagerService.FindPluginFiles(Zip(dir, ("SacredGear.dll", "x"), ("Gear.md", "y"))));
    }

    [Fact]
    public void TheFileNameCountsWhateverTheFolderOrCase()
    {
        using var dir = new TempDir();
        List<string> found = ModManagerService.FindPluginFiles(Zip(dir, ("pkg/DDRAW.DLL", "x"), ("pkg/sub/sacredgear.dll", "y")));
        Assert.Equal(["ddraw.dll", "sacredgear.dll"], found.Select(f => f.ToLowerInvariant()).ToArray());
    }

    [Fact]
    public void AZipWithoutThoseFilesIsNotAPlugin()
    {
        using var dir = new TempDir();
        Assert.Empty(ModManagerService.FindPluginFiles(Zip(dir, ("readme.txt", "x"), ("lib/other.dll", "y"))));
    }
}
