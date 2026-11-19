using System.Text;
using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class ItemKindJsonTests
{
    private static ModIndex Parse(string json) => IndexJson.Parse(Encoding.UTF8.GetBytes(json));

    private static List<InstallFileSpec?> Files(string filesJson)
    {
        ModIndex i = Parse("{\"schema\":1,\"mods\":[{\"id\":\"p\",\"kind\":\"plugin\",\"install\":{\"files\":" + filesJson + "}}]}");
        return i.Mods![0].Install!.Files!.Cast<InstallFileSpec?>().ToList();
    }

    // ---- InstallFileSpec converter ---------------------------------------------------------------------------

    [Fact]
    public void FileSpecAsStringMeansSameFromAndTo()
    {
        InstallFileSpec f = Assert.Single(Files("[\"ddraw.dll\"]"))!;
        Assert.Equal("ddraw.dll", f.From);
        Assert.Equal("ddraw.dll", f.To);
    }

    [Fact]
    public void FileSpecAsObjectKeepsFromAndTo()
    {
        InstallFileSpec f = Assert.Single(Files("[{\"from\":\"bin/ddraw.dll\",\"to\":\"ddraw.dll\"}]"))!;
        Assert.Equal("bin/ddraw.dll", f.From);
        Assert.Equal("ddraw.dll", f.To);
    }

    [Fact]
    public void FileSpecObjectWithOnlyFromDefaultsToToFrom()
    {
        InstallFileSpec f = Assert.Single(Files("[{\"from\":\"a/b.dll\"}]"))!;
        Assert.Equal("a/b.dll", f.From);
        Assert.Equal("a/b.dll", f.To);
    }

    [Fact]
    public void FileSpecObjectPropertyNamesAreCaseInsensitiveAndUnknownOnesIgnored()
    {
        InstallFileSpec f = Assert.Single(Files("[{\"FROM\":\"a\",\"To\":\"b\",\"extra\":{\"x\":[1,2]},\"more\":[1]}]"))!;
        Assert.Equal("a", f.From);
        Assert.Equal("b", f.To);
    }

    [Fact]
    public void FileSpecObjectWithOnlyToHasNoFromAndKeepsTo()
    {
        InstallFileSpec f = Assert.Single(Files("[{\"to\":\"b\"}]"))!;
        Assert.Null(f.From);
        Assert.Equal("b", f.To);
    }

    [Fact]
    public void FileSpecEmptyObjectHasNothing()
    {
        InstallFileSpec f = Assert.Single(Files("[{}]"))!;
        Assert.Null(f.From);
        Assert.Null(f.To);
    }

    [Fact]
    public void FileSpecNonStringValuesInsideAnObjectAreIgnored()
    {
        InstallFileSpec f = Assert.Single(Files("[{\"from\":5,\"to\":\"x\"}]"))!;
        Assert.Null(f.From);
        Assert.Equal("x", f.To);
    }

    [Fact]
    public void FileSpecNullNumberBoolAndArrayEntriesBecomeNull()
    {
        List<InstallFileSpec?> list = Files("[null, 5, true, [1,2], \"ok.dll\"]");
        Assert.Equal(5, list.Count);
        Assert.Null(list[0]);
        Assert.Null(list[1]);
        Assert.Null(list[2]);
        Assert.Null(list[3]);
        Assert.Equal("ok.dll", list[4]!.From);
    }

    [Fact]
    public void FileSpecWriteUsesAStringWhenToEqualsFromElseAnObject()
    {
        var idx = new ModIndex
        {
            Mods = [new IndexMod { Id = "p", Kind = "plugin", Install = new InstallSpec { Files = [new InstallFileSpec { From = "a", To = "a" }, new InstallFileSpec { From = "b", To = "c" }, new InstallFileSpec { From = "d" }] } }],
        };
        string json = IndexJson.ToJson(idx);
        Assert.Contains("\"a\"", json);
        Assert.Contains("\"from\": \"b\"", json);
        Assert.Contains("\"to\": \"c\"", json);
        Assert.DoesNotContain("\"from\": \"a\"", json);
        Assert.DoesNotContain("\"from\": \"d\"", json); // a spec without To is written as a plain string too

        List<InstallFileSpec> back = Parse(json).Mods![0].Install!.Files!;
        Assert.Equal(3, back.Count);
        Assert.Equal(("a", "a"), (back[0].From, back[0].To));
        Assert.Equal(("b", "c"), (back[1].From, back[1].To));
        Assert.Equal(("d", "d"), (back[2].From, back[2].To));
    }

    // ---- full entries ------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesAFullPluginEntry()
    {
        ModIndex i = Parse("""
            {
              "schema": 1,
              "mods": [{
                "id": "sacred-bild", "kind": "plugin", "path": "", "platforms": ["win-x64", "win"],
                "name": "Sacred-Bild", "version": "1.0", "requires": ["gear"],
                "install": {"to": "", "files": ["ddraw.dll", {"from": "bin/x.dll", "to": "y.dll"}]},
                "download": {"github": "o/r", "asset": "Bild-*.zip", "prerelease": true}
              }]
            }
            """);
        IndexMod m = Assert.Single(i.Mods!);
        Assert.Equal("plugin", m.Kind);
        Assert.Equal("", m.Path);
        Assert.Equal(["win-x64", "win"], m.Platforms);
        Assert.Equal("", m.Install!.To);
        Assert.Equal(2, m.Install.Files!.Count);
        Assert.Equal("y.dll", m.Install.Files[1].To);
        Assert.Null(m.Install.Launch);
        Assert.Equal("o/r", m.Download!.Github);
        Assert.Equal("Bild-*.zip", m.Download.Asset);
        Assert.True(m.Download.Prerelease);
        Assert.Null(m.Download.Url);
        Assert.Equal(["gear"], m.Requires);
    }

    [Fact]
    public void ParsesAToolEntry()
    {
        ModIndex i = Parse("""
            {"schema":1,"mods":[{"id":"class-editor","kind":"Tool","platforms":["win"],"install":{"launch":"ClassEditor.exe"},
              "download":{"url":"https://x/y.zip"}}]}
            """);
        IndexMod m = Assert.Single(i.Mods!);
        Assert.Equal("Tool", m.Kind);
        Assert.Equal("ClassEditor.exe", m.Install!.Launch);
        Assert.Null(m.Install.Files);
        Assert.Null(m.Path);
        Assert.Null(m.Download!.Github);
        Assert.Null(m.Download.Prerelease);
    }

    [Fact]
    public void EntriesWithoutKindInstallOrPlatformsStillParse()
    {
        IndexMod m = Assert.Single(Parse("{\"schema\":1,\"mods\":[{\"id\":\"a\",\"path\":\"a\"}]}").Mods!);
        Assert.Null(m.Kind);
        Assert.Null(m.Platforms);
        Assert.Null(m.Install);
    }

    // ---- ItemKinds ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("mod", ItemKind.Mod)]
    [InlineData("MOD", ItemKind.Mod)]
    [InlineData("mods", ItemKind.Mod)]
    [InlineData("", ItemKind.Mod)]
    [InlineData("   ", ItemKind.Mod)]
    [InlineData(null, ItemKind.Mod)]
    [InlineData("plugin", ItemKind.Plugin)]
    [InlineData("Plugins", ItemKind.Plugin)]
    [InlineData(" PLUGIN ", ItemKind.Plugin)]
    [InlineData("tool", ItemKind.Tool)]
    [InlineData("TOOLS", ItemKind.Tool)]
    public void KindParse(string? text, ItemKind expected) => Assert.Equal(expected, ItemKinds.Parse(text));

    [Theory]
    [InlineData("addon")]
    [InlineData("modd")]
    [InlineData("plug-in")]
    [InlineData("mod,tool")]
    public void KindParseUnknownIsNull(string text) => Assert.Null(ItemKinds.Parse(text));

    [Fact]
    public void KindNamesRoundTrip()
    {
        foreach (ItemKind k in Enum.GetValues<ItemKind>())
        {
            Assert.Equal(k, ItemKinds.Parse(ItemKinds.ToJson(k)));
            Assert.Equal(k, ItemKinds.Parse(ItemKinds.Singular(k)));
            Assert.Equal(k, ItemKinds.Parse(ItemKinds.Plural(k)));
        }

        Assert.Equal("Plugins", ItemKinds.Plural(ItemKind.Plugin));
        Assert.Equal("Tool", ItemKinds.Singular(ItemKind.Tool));
    }

    [Fact]
    public void TrackedModKindDefaultsToModAndReadsTheName()
    {
        Assert.Equal(ItemKind.Mod, new TrackedMod().ItemKind);
        Assert.Equal(ItemKind.Tool, new TrackedMod { Kind = "tool" }.ItemKind);
        Assert.Equal(ItemKind.Mod, new TrackedMod { Kind = "garbage" }.ItemKind);
    }

    // ---- PlatformInfo ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("win", "win-x64", true)]
    [InlineData("win", "win-arm64", true)]
    [InlineData("win-x64", "win-x64", true)]
    [InlineData("win-arm64", "win-x64", false)]
    [InlineData("linux", "win-x64", false)]
    [InlineData("linux-x64", "linux-x64", true)]
    [InlineData("osx", "osx-arm64", true)]
    [InlineData(" WIN ", "win-x64", true)]
    [InlineData("Win-X64", "win-x64", true)]
    [InlineData("wi", "win-x64", false)]
    [InlineData("win-x6", "win-x64", false)]
    [InlineData("win-x64-extra", "win-x64", false)]
    public void PlatformMatchesSingle(string platform, string rid, bool expected)
        => Assert.Equal(expected, PlatformInfo.Matches([platform], rid));

    [Fact]
    public void PlatformEmptyOrNullListMatchesEverything()
    {
        Assert.True(PlatformInfo.Matches(null, "linux-x64"));
        Assert.True(PlatformInfo.Matches([], "linux-x64"));
        Assert.True(PlatformInfo.Matches(["", "  "], "linux-x64")); // only blank entries: no restriction
    }

    [Fact]
    public void PlatformAnyOfSeveralMatches()
    {
        Assert.True(PlatformInfo.Matches(["linux", "win"], "win-x64"));
        Assert.True(PlatformInfo.Matches(["", "win"], "win-x64"));
        Assert.False(PlatformInfo.Matches(["linux", "osx"], "win-x64"));
        Assert.False(PlatformInfo.Matches(["", "linux"], "win-x64"));
    }

    [Fact]
    public void CurrentRidIsOsDashArch()
    {
        string rid = PlatformInfo.CurrentRid;
        Assert.Matches("^(win|linux|osx|other)-(x64|x86|arm64|arm|other)$", rid);
        Assert.True(PlatformInfo.Matches([rid], rid));
    }
}
