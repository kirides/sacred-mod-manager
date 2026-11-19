using System.Text;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class GearModStateTests
{
    private const string RealFile = "{\"mods\":{\"achievements\":{\"enabled\":false},\"art-bars\":{\"enabled\":false,\"settings\":{\"bars\":1,\"debug\":false}},\"bounty\":{\"enabled\":true},\"layout\":{\"enabled\":true,\"settings\":{\"snap\":true}}}}";

    private static (TempDir Dir, GearModState State) Setup(string? content)
    {
        var dir = new TempDir();
        var state = new GearModState(dir.Path);
        if (content is not null)
        {
            File.WriteAllBytes(state.Path, new UTF8Encoding(false).GetBytes(content));
        }

        return (dir, state);
    }

    private static string Read(GearModState s) => File.ReadAllText(s.Path, Encoding.UTF8);

    private static void NoTmp(TempDir dir) => Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));

    [Fact]
    public void ReadEnabledReturnsTheChoices()
    {
        (TempDir dir, GearModState s) = Setup(RealFile);
        using (dir)
        {
            Dictionary<string, bool> e = s.ReadEnabled();
            Assert.Equal(4, e.Count);
            Assert.False(e["achievements"]);
            Assert.False(e["art-bars"]);
            Assert.True(e["bounty"]);
            Assert.True(e["layout"]);
        }
    }

    [Fact]
    public void ReadEnabledSkipsEntriesWithoutABooleanChoice()
    {
        (TempDir dir, GearModState s) = Setup("{\"mods\":{\"a\":{\"settings\":{}},\"b\":{\"enabled\":\"yes\"},\"c\":5,\"d\":{\"enabled\":null},\"e\":{\"enabled\":true}}}");
        using (dir)
        {
            Assert.Equal(new Dictionary<string, bool> { ["e"] = true }, s.ReadEnabled());
        }
    }

    [Theory]
    [InlineData("{\"mods\":{\"achievements\":{\"enabled\":false}}}", "achievements", true, "{\"mods\":{\"achievements\":{\"enabled\":true}}}")]
    [InlineData(RealFile, "bounty", false, "{\"mods\":{\"achievements\":{\"enabled\":false},\"art-bars\":{\"enabled\":false,\"settings\":{\"bars\":1,\"debug\":false}},\"bounty\":{\"enabled\":false},\"layout\":{\"enabled\":true,\"settings\":{\"snap\":true}}}}")]
    [InlineData(RealFile, "art-bars", true, "{\"mods\":{\"achievements\":{\"enabled\":false},\"art-bars\":{\"enabled\":true,\"settings\":{\"bars\":1,\"debug\":false}},\"bounty\":{\"enabled\":true},\"layout\":{\"enabled\":true,\"settings\":{\"snap\":true}}}}")]
    public void SetEnabledChangesOnlyTheTouchedValueByteForByte(string content, string id, bool value, string expected)
    {
        (TempDir dir, GearModState s) = Setup(content);
        using (dir)
        {
            s.SetEnabled(id, value);
            Assert.Equal(expected, Read(s));
            NoTmp(dir);
        }
    }

    [Fact]
    public void SetEnabledKeepsUnknownFieldsKeysMigratedAndOrder()
    {
        const string original = "{\"version\":3,\"future\":[1,2,{\"x\":null}],\"mods\":{\"zeta\":{\"enabled\":true,\"keys\":{\"panel\":\"ctrl+j\"},\"migrated\":{\"v1\":true},\"extra\":1.50},"
                                + "\"alpha\":{\"settings\":{\"n\":2.0,\"s\":\"a\\\"b\"},\"enabled\":false,\"unknown\":{\"deep\":[true,false]}}},\"tail\":\"end\"}";
        (TempDir dir, GearModState s) = Setup(original);
        using (dir)
        {
            s.SetEnabled("alpha", true);
            Assert.Equal(original.Replace("\"settings\":{\"n\":2.0,\"s\":\"a\\\"b\"},\"enabled\":false", "\"settings\":{\"n\":2.0,\"s\":\"a\\\"b\"},\"enabled\":true", StringComparison.Ordinal), Read(s));
            s.SetEnabled("zeta", false);
            Assert.Contains("\"zeta\":{\"enabled\":false,\"keys\":{\"panel\":\"ctrl+j\"},\"migrated\":{\"v1\":true},\"extra\":1.50}", Read(s), StringComparison.Ordinal);
            Assert.StartsWith("{\"version\":3,\"future\":[1,2,{\"x\":null}],\"mods\":{\"zeta\"", Read(s), StringComparison.Ordinal);
            Assert.EndsWith("\"tail\":\"end\"}", Read(s), StringComparison.Ordinal);
            NoTmp(dir);
        }
    }

    [Fact]
    public void SetEnabledAddsANewModAtTheEnd()
    {
        (TempDir dir, GearModState s) = Setup(RealFile);
        using (dir)
        {
            s.SetEnabled("new-mod", false);
            Assert.Equal(RealFile[..^2] + ",\"new-mod\":{\"enabled\":false}}}", Read(s));
        }
    }

    [Fact]
    public void SetEnabledNullRemovesTheChoiceButKeepsSettings()
    {
        (TempDir dir, GearModState s) = Setup(RealFile);
        using (dir)
        {
            s.SetEnabled("art-bars", null);
            Assert.Equal(RealFile.Replace("\"enabled\":false,\"settings\":{\"bars\"", "\"settings\":{\"bars\"", StringComparison.Ordinal), Read(s));
            Assert.False(s.ReadEnabled().ContainsKey("art-bars"));
            Assert.True(s.HasSettings("art-bars"));
        }
    }

    [Fact]
    public void SetEnabledNullDropsAnEntryThatBecomesEmpty()
    {
        (TempDir dir, GearModState s) = Setup(RealFile);
        using (dir)
        {
            s.SetEnabled("achievements", null);
            Assert.Equal(RealFile.Replace("\"achievements\":{\"enabled\":false},", string.Empty, StringComparison.Ordinal), Read(s));
        }
    }

    [Fact]
    public void SetEnabledNullForUnknownModLeavesTheFileSemanticallyUnchanged()
    {
        (TempDir dir, GearModState s) = Setup(RealFile);
        using (dir)
        {
            s.SetEnabled("nobody", null);
            Assert.Equal(RealFile, Read(s));
        }
    }

    [Fact]
    public void ForgetRemovesEverythingOfAMod()
    {
        (TempDir dir, GearModState s) = Setup(RealFile);
        using (dir)
        {
            s.Forget("art-bars");
            Assert.Equal(RealFile.Replace("\"art-bars\":{\"enabled\":false,\"settings\":{\"bars\":1,\"debug\":false}},", string.Empty, StringComparison.Ordinal), Read(s));
            Assert.False(s.HasSettings("art-bars"));
            string before = Read(s);
            s.Forget("art-bars");
            s.Forget("never-there");
            Assert.Equal(before, Read(s));
            NoTmp(dir);
        }
    }

    [Fact]
    public void ForgetOnAMissingFileDoesNotCreateIt()
    {
        (TempDir dir, GearModState s) = Setup(null);
        using (dir)
        {
            s.Forget("a");
            Assert.False(File.Exists(s.Path));
        }
    }

    [Fact]
    public void HasSettingsLooksAtSettingsAndKeys()
    {
        (TempDir dir, GearModState s) = Setup("{\"mods\":{\"a\":{\"settings\":{\"x\":1}},\"b\":{\"keys\":{\"k\":\"f1\"}},\"c\":{\"enabled\":true,\"settings\":{}},\"d\":{}}}");
        using (dir)
        {
            Assert.True(s.HasSettings("a"));
            Assert.True(s.HasSettings("b"));
            Assert.False(s.HasSettings("c"));
            Assert.False(s.HasSettings("d"));
            Assert.False(s.HasSettings("e"));
        }
    }

    [Fact]
    public void MissingFileReadsEmptyAndIsCreatedBySetEnabled()
    {
        (TempDir dir, GearModState s) = Setup(null);
        using (dir)
        {
            Assert.Empty(s.ReadEnabled());
            Assert.False(File.Exists(s.Path));
            s.SetEnabled("a", true);
            Assert.Equal("{\"mods\":{\"a\":{\"enabled\":true}}}", Read(s));
            NoTmp(dir);
        }
    }

    [Fact]
    public void SettingEnabledNullOnAMissingFileWritesAnEmptyMods()
    {
        (TempDir dir, GearModState s) = Setup(null);
        using (dir)
        {
            s.SetEnabled("a", null);
            Assert.Equal("{\"mods\":{}}", Read(s));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"mods\":{}}")]
    public void EmptyishFilesWork(string content)
    {
        (TempDir dir, GearModState s) = Setup(content);
        using (dir)
        {
            Assert.Empty(s.ReadEnabled());
            s.SetEnabled("a", false);
            Assert.Equal("{\"mods\":{\"a\":{\"enabled\":false}}}", Read(s));
        }
    }

    [Fact]
    public void EmptyFileIsHandledWithoutAnException()
    {
        (TempDir dir, GearModState s) = Setup(string.Empty);
        using (dir)
        {
            Assert.Empty(s.ReadEnabled());
            Assert.False(s.HasSettings("x"));
        }
    }

    [Theory]
    [InlineData("{\"mods\":")]
    [InlineData("{\"mods\":{\"a\":{\"enabled\":tru}}}")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("null")]
    public void CorruptFileThrowsAndIsLeftUntouched(string content)
    {
        (TempDir dir, GearModState s) = Setup(content);
        using (dir)
        {
            byte[] before = File.ReadAllBytes(s.Path);
            Assert.Throws<GearStateException>(() => s.ReadEnabled());
            Assert.Throws<GearStateException>(() => s.SetEnabled("a", true));
            Assert.Throws<GearStateException>(() => s.SetEnabled("a", null));
            Assert.Throws<GearStateException>(() => s.Forget("a"));
            Assert.Throws<GearStateException>(() => s.HasSettings("a"));
            Assert.Equal(before, File.ReadAllBytes(s.Path));
            NoTmp(dir);
        }
    }

    [Fact]
    public void NonAsciiTextSurvivesARoundTrip()
    {
        const string original = "{\"mods\":{\"a\":{\"enabled\":false,\"settings\":{\"title\":\"Grüße aus Ancaria – Drachentöter «Ärger» 日本語 ñ <b>&'\"}},\"b\":{\"enabled\":true}}}";
        (TempDir dir, GearModState s) = Setup(original);
        using (dir)
        {
            s.SetEnabled("b", false);
            Assert.Equal(original.Replace("\"b\":{\"enabled\":true}", "\"b\":{\"enabled\":false}", StringComparison.Ordinal), Read(s));
            byte[] bytes = File.ReadAllBytes(s.Path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB, "no BOM expected");
        }
    }

    [Fact]
    public void EscapedNonAsciiIsReadBackCorrectly()
    {
        (TempDir dir, GearModState s) = Setup("{\"mods\":{\"a\":{\"enabled\":false,\"settings\":{\"t\":\"\\u00fc\\ud83d\\ude00\"}}}}");
        using (dir)
        {
            s.SetEnabled("a", true);
            string text = Read(s);
            Assert.Contains("\"enabled\":true", text, StringComparison.Ordinal);
            // the value is the same string whether the writer escaped it or not
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal("ü\U0001F600", doc.RootElement.GetProperty("mods").GetProperty("a").GetProperty("settings").GetProperty("t").GetString());
        }
    }

    [Fact]
    public void ReadsAFileWithCommentsAndTrailingCommas()
    {
        (TempDir dir, GearModState s) = Setup("{ // hand edited\n \"mods\": { \"a\": { \"enabled\": true, }, }, }");
        using (dir)
        {
            Assert.True(s.ReadEnabled()["a"]);
        }
    }

    [Fact]
    public void FileWithABomIsTreatedAsUnreadableAndLeftAlone()
    {
        // Documents current behaviour: System.Text.Json rejects a UTF-8 BOM, so a hand-edited file saved by an editor that adds one
        // is reported (not overwritten). The game itself writes no BOM.
        var dir = new TempDir();
        using (dir)
        {
            var s = new GearModState(dir.Path);
            byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(RealFile)];
            File.WriteAllBytes(s.Path, bytes);
            Assert.Throws<GearStateException>(() => s.ReadEnabled());
            Assert.Equal(bytes, File.ReadAllBytes(s.Path));
        }
    }

    [Fact]
    public void ManySequentialChangesLeaveAConsistentFileAndNoTmp()
    {
        (TempDir dir, GearModState s) = Setup(RealFile);
        using (dir)
        {
            for (int i = 0; i < 50; i++)
            {
                s.SetEnabled("bounty", i % 2 == 0);
            }

            Assert.False(s.ReadEnabled()["bounty"]);
            Assert.True(s.ReadEnabled()["layout"]);
            NoTmp(dir);
        }
    }

    [Fact]
    public void PathIsInsideTheModsFolder()
    {
        var s = new GearModState(Path.Combine("x", "mods"));
        Assert.Equal(Path.Combine("x", "mods", "gear_mods.json"), s.Path);
    }
}
