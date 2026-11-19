using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class LuaManifestReaderMoreTests
{
    [Fact]
    public void MinimalManifestGetsDefaults()
    {
        ModManifest m = LuaManifestReader.Read("return {id = 'x'}");
        Assert.Equal("x", m.Id);
        Assert.Equal(string.Empty, m.Name);
        Assert.Equal("x", m.DisplayName);
        Assert.Equal(string.Empty, m.Version);
        Assert.True(m.DefaultEnabled);
        Assert.Equal(0, m.Priority);
        Assert.Empty(m.Requires);
        Assert.Empty(m.Settings);
        Assert.Empty(m.Keys);
    }

    [Theory]
    [InlineData("enabled = true", true)]
    [InlineData("enabled = false", false)]
    [InlineData("enabled = nil", true)]
    [InlineData("enabled = 0", true)]
    [InlineData("enabled = 'false'", true)]
    public void EnabledIsOffOnlyForTheBooleanFalse(string field, bool expected)
        => Assert.Equal(expected, LuaManifestReader.Read("return {id = 'x', " + field + "}").DefaultEnabled);

    [Theory]
    [InlineData("return {id = ''}")]
    [InlineData("return {id = 'has space'}")]
    [InlineData("return {id = 'a/b'}")]
    [InlineData("return {id = 'a.b'}")]
    [InlineData("return {id = 'ü'}")]
    [InlineData("return {id = '..'}")]
    [InlineData("return {id = true}")]
    [InlineData("return {id = {}}")]
    [InlineData("return {}")]
    [InlineData("return nil")]
    [InlineData("")]
    [InlineData("return 'x'")]
    [InlineData("x = =")]
    [InlineData("error('boom')")]
    public void InvalidManifestsAreRejected(string source)
        => Assert.Throws<ManifestException>(() => LuaManifestReader.Read(source));

    [Fact]
    public void IdOfMaxLengthIsAcceptedAndLongerIsNot()
    {
        Assert.Equal(64, LuaManifestReader.Read("return {id = '" + new string('a', 64) + "'}").Id.Length);
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read("return {id = '" + new string('a', 65) + "'}"));
    }

    [Fact]
    public void NumericIdIsReadAsText()
        => Assert.Equal("42", LuaManifestReader.Read("return {id = 42}").Id);

    [Fact]
    public void NumericVersionBecomesText()
        => Assert.Equal("1.5", LuaManifestReader.Read("return {id = 'x', version = 1.5}").Version);

    [Fact]
    public void LongTextIsClipped()
    {
        ModManifest m = LuaManifestReader.Read("return {id = 'x', name = '" + new string('n', 500) + "', description = '" + new string('d', 5000) + "', version = '" + new string('v', 100) + "'}");
        Assert.Equal(200, m.Name.Length);
        Assert.Equal(4000, m.Description.Length);
        Assert.Equal(64, m.Version.Length);
    }

    [Fact]
    public void RequiresKeepsOnlyStrings()
        => Assert.Equal(["a", "b"], LuaManifestReader.Read("return {id = 'x', requires = {'a', 5, {}, 'b', true}}").Requires);

    [Fact]
    public void RequiresThatIsNotATableIsIgnored()
        => Assert.Empty(LuaManifestReader.Read("return {id = 'x', requires = 'a'}").Requires);

    [Fact]
    public void MalformedSettingsAndKeysAreSkipped()
    {
        ModManifest m = LuaManifestReader.Read("""
            return {
                id = 'x',
                settings = {'junk', 5, {label = 'no id'}, {id = 's1', type = 'number', default = 0.5}, {id = 's2', default = false}, {id = 's3', type = 'text', default = 'abc'}, {id = 's4', default = {}}},
                keys = {{label = 'no id'}, 'junk', {id = 'k1'}, {id = 'k2', label = 'Second', default = 'f5'}},
            }
            """);
        Assert.Equal(["s1", "s2", "s3", "s4"], m.Settings.Select(s => s.Id).ToArray());
        Assert.Equal("0.5", m.Settings[0].DefaultText);
        Assert.Equal("number", m.Settings[0].Type);
        Assert.Equal("bool", m.Settings[1].Type);
        Assert.Equal("off", m.Settings[1].DefaultText);
        Assert.Equal("abc", m.Settings[2].DefaultText);
        Assert.Equal(string.Empty, m.Settings[3].DefaultText);
        Assert.Equal("s1", m.Settings[0].Label);
        Assert.Equal(["k1", "k2"], m.Keys.Select(k => k.Id).ToArray());
        Assert.Equal("k1", m.Keys[0].Label);
        Assert.Equal(string.Empty, m.Keys[0].Default);
        Assert.Equal("Second", m.Keys[1].Label);
    }

    [Theory]
    [InlineData("0xFF3080C0", "#FF3080C0")]
    [InlineData("0", "#00000000")]
    [InlineData("-1", "-1")]
    [InlineData("0x1FFFFFFFF", "8589934591")]
    public void ColorDefaultsAreShownAsHex(string literal, string expected)
        => Assert.Equal(expected, LuaManifestReader.Read("return {id = 'x', settings = {{id = 'c', type = 'color', default = " + literal + "}}}").Settings[0].DefaultText);

    [Fact]
    public void WhitespaceSectionAndTooltipBecomeNull()
    {
        ModManifest m = LuaManifestReader.Read("return {id = 'x', settings = {{id = 's', section = '  ', tooltip = ''}, {id = 't', section = 'Sec', tooltip = 'Tip'}}}");
        Assert.Null(m.Settings[0].Section);
        Assert.Null(m.Settings[0].Tooltip);
        Assert.Equal("Sec", m.Settings[1].Section);
        Assert.Equal("Tip", m.Settings[1].Tooltip);
    }

    [Fact]
    public void ComputedValuesAndLocalsWork()
    {
        ModManifest m = LuaManifestReader.Read("""
            -- comment
            --[[ block
            comment ]]
            local base = 'bo'
            local function twice(s) return s .. s end
            local t = {}
            for i = 1, 3 do t[#t + 1] = 'r' .. i end
            return {id = twice(base), requires = t, priority = 2 * 3 + 1, enabled = not false}
            """);
        Assert.Equal("bobo", m.Id);
        Assert.Equal(["r1", "r2", "r3"], m.Requires);
        Assert.Equal(7, m.Priority);
        Assert.True(m.DefaultEnabled);
    }

    [Fact]
    public void OnlyTheFirstReturnValueCounts()
        => Assert.Equal("a", LuaManifestReader.Read("return {id = 'a'}, {id = 'b'}").Id);

    [Fact]
    public void UnicodeTextIsKept()
    {
        ModManifest m = LuaManifestReader.Read("return {id = 'x', name = 'Drachentöter – 日本語', author = 'Jörg'}");
        Assert.Equal("Drachentöter – 日本語", m.Name);
        Assert.Equal("Jörg", m.Author);
    }

    [Fact]
    public async Task AsyncAndSyncAgree()
    {
        const string src = "return {id = 'x', version = '3'}";
        Assert.Equal(LuaManifestReader.Read(src).Version, (await LuaManifestReader.ReadAsync(src)).Version);
    }

    [Fact]
    public void ErrorMessageNamesTheChunk()
    {
        ManifestException ex = Assert.Throws<ManifestException>(() => LuaManifestReader.Read("return {", "my/mod.lua"));
        Assert.Contains("my/mod.lua", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', ex.Message);
    }

    [Fact]
    public void ReadsEveryRealModLuaOfSacredGearWhenTheRepositoryIsPresent()
    {
        // The Sacred Gear mods live outside this repository; the test is skipped on machines that do not have a checkout.
        const string src = @"E:\git\github\sacred-gear-mods\src";
        if (!Directory.Exists(src))
        {
            Assert.Skip("sacred-gear-mods checkout not found at " + src);
        }

        string[] knownUnsupported = [];
        var failures = new List<string>();
        int read = 0;
        foreach (string dir in Directory.EnumerateDirectories(src))
        {
            string file = Path.Combine(dir, "mod.lua");
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                ModManifest m = LuaManifestReader.Read(File.ReadAllText(file));
                Assert.False(string.IsNullOrWhiteSpace(m.Id));
                read++;
            }
            catch (ManifestException ex)
            {
                if (!knownUnsupported.Contains(Path.GetFileName(dir)))
                {
                    failures.Add(Path.GetFileName(dir) + ": " + ex.Message);
                }
            }
        }

        Assert.Empty(failures);
        Assert.True(read > 0, "no mod.lua found below " + src);
    }
}
