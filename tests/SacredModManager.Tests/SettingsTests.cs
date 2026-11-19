using SacredModManager.Core.App;
using Xunit;

namespace SacredModManager.Tests;

public class SettingsTests
{
    [Fact]
    public void RoundTripKeepsEveryField()
    {
        using var dir = new TempDir();
        string path = dir.Combine("sub", "settings.json");
        var s = new AppSettings
        {
            GameFolder = @"C:\Games\Sacred Gold – Ünï", Theme = "dark", Sources = ["https://a.example/mod-index.json", @"C:\mods\local"],
            WindowWidth = 999.5, WindowHeight = 640, AskBeforeRemove = false,
        };
        SettingsFile.Save(path, s);
        AppSettings back = SettingsFile.Load(path);
        Assert.Equal(s.GameFolder, back.GameFolder);
        Assert.Equal("dark", back.Theme);
        Assert.Equal(s.Sources, back.Sources);
        Assert.Equal(999.5, back.WindowWidth);
        Assert.Equal(640, back.WindowHeight);
        Assert.False(back.AskBeforeRemove);
        Assert.Empty(Directory.GetFiles(dir.Combine("sub"), "*.tmp"));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        using var dir = new TempDir();
        AppSettings s = SettingsFile.Load(dir.Combine("none.json"));
        Assert.Equal([AppSettings.DefaultSource], s.Sources);
        Assert.Equal("system", s.Theme);
        Assert.True(s.AskBeforeRemove);
        Assert.Equal(string.Empty, s.GameFolder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("{\"windowWidth\":\"wide\"}")]
    [InlineData("{\"sources\":[1,2]}")]
    public void CorruptFileGivesDefaults(string content)
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        File.WriteAllText(path, content);
        AppSettings s = SettingsFile.Load(path);
        Assert.Equal([AppSettings.DefaultSource], s.Sources);
        Assert.Equal("system", s.Theme);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void NullSourcesDoNotCrashTheLoad()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        File.WriteAllText(path, "{\"theme\":\"dark\",\"sources\":null}");
        AppSettings s = SettingsFile.Load(path);
        Assert.Equal("dark", s.Theme);
        Assert.NotNull(s.Sources);
    }

    [Fact]
    public void LoadCleansTheSourceList()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        File.WriteAllText(path, "{\"sources\":[\"https://a/x\",\"\",\"  \",\"HTTPS://A/X\",\"https://b/y\"]}");
        Assert.Equal(["https://a/x", "https://b/y"], SettingsFile.Load(path).Sources);
    }

    [Fact]
    public void AnExplicitEmptySourceListStaysEmpty()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        File.WriteAllText(path, "{\"sources\":[]}");
        Assert.Empty(SettingsFile.Load(path).Sources);
    }

    [Fact]
    public void ToleratesCommentsTrailingCommasAndUnknownFields()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ // hi\n \"theme\": \"light\", \"somethingNew\": {\"a\": 1}, }");
        Assert.Equal("light", SettingsFile.Load(path).Theme);
    }

    [Fact]
    public void SaveWritesCamelCaseIndentedJsonWithoutLeavingTmp()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        SettingsFile.Save(path, new AppSettings());
        string text = File.ReadAllText(path);
        Assert.Contains("\"gameFolder\"", text, StringComparison.Ordinal);
        Assert.Contains("\"askBeforeRemove\"", text, StringComparison.Ordinal);
        Assert.Contains('\n', text);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void SaveOverwritesAnExistingFile()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        SettingsFile.Save(path, new AppSettings { Theme = "dark" });
        SettingsFile.Save(path, new AppSettings { Theme = "light" });
        Assert.Equal("light", SettingsFile.Load(path).Theme);
    }

    [Fact]
    public void SaveToAnUnwritableLocationIsSilent()
    {
        using var dir = new TempDir();
        string blocker = dir.Combine("file");
        File.WriteAllText(blocker, "x");
        SettingsFile.Save(Path.Combine(blocker, "settings.json"), new AppSettings());
        Assert.Equal("x", File.ReadAllText(blocker));
    }

    [Fact]
    public void AppPathsWithAHomeKeepsEverythingBelowIt()
    {
        var p = new AppPaths(Path.Combine("h", "home"));
        Assert.Equal(Path.Combine("h", "home", "settings.json"), p.Settings);
        Assert.Equal(Path.Combine("h", "home", "data"), p.Data);
        Assert.Equal(Path.Combine("h", "home", "data", "packages"), p.Packages);
        Assert.Equal(Path.Combine("h", "home", "data", "thumbnails"), p.Thumbnails);
        Assert.Equal(Path.Combine("h", "home", "data", "backups"), p.Backups);
    }

    [Fact]
    public void AppPathsWithoutAHomeUsesAppData()
    {
        string? saved = Environment.GetEnvironmentVariable("SACRED_MODMANAGER_HOME");
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return; // the environment overrides the location; covered by the test above
        }

        var p = new AppPaths(null);
        Assert.EndsWith(Path.Combine("SacredModManager", "settings.json"), p.Settings, StringComparison.Ordinal);
        Assert.EndsWith("SacredModManager", p.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void IsGameFolderNeedsSacredExe()
    {
        using var dir = new TempDir();
        Assert.False(GameLocator.IsGameFolder(dir.Path));
        Assert.False(GameLocator.IsGameFolder(null));
        Assert.False(GameLocator.IsGameFolder(""));
        Assert.False(GameLocator.IsGameFolder(dir.Combine("missing")));
        File.WriteAllText(dir.Combine("Sacred.exe"), "MZ");
        Assert.True(GameLocator.IsGameFolder(dir.Path));
        Assert.Equal(Path.GetFullPath(dir.Path), GameLocator.Find(dir.Path));
    }
}
