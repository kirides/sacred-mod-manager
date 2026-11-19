using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class LuaManifestReaderTests
{
    [Fact]
    public void ReadsAFullManifest()
    {
        ModManifest m = LuaManifestReader.Read("""
            ---@type GearModManifest
            return {
                id = 'bounty', name = 'Bounty board', version = '0.4.0', author = 'Sacred Gear',
                description = 'Bounties ' .. 'to hunt',
                enabled = false, priority = 5, requires = {'a', 'b'},
                settings = {
                    {id = 'own', type = 'bool', label = 'Own', default = true, tooltip = 'tip'},
                    {id = 'radius', type = 'number', label = 'R', default = 25, section = 'General'},
                    {id = 'tint', type = 'color', label = 'T', default = 0xFF3080C0},
                    {type = 'bool'},
                },
                keys = {{id = 'panel', label = 'Open', default = 'ctrl+j'}},
                config = {x = 1},
            }
            """);
        Assert.Equal("bounty", m.Id);
        Assert.Equal("Bounty board", m.Name);
        Assert.Equal("Bounties to hunt", m.Description);
        Assert.False(m.DefaultEnabled);
        Assert.Equal(["a", "b"], m.Requires);
        Assert.Equal(3, m.Settings.Count);
        Assert.Equal("on", m.Settings[0].DefaultText);
        Assert.Equal("25", m.Settings[1].DefaultText);
        Assert.Equal("#FF3080C0", m.Settings[2].DefaultText);
        Assert.Equal("ctrl+j", m.Keys[0].Default);
    }

    [Theory]
    [InlineData("return io.open('x')")]
    [InlineData("return os.execute('calc')")]
    [InlineData("return require('x')")]
    [InlineData("return loadfile('x')")]
    [InlineData("return dofile('x')")]
    [InlineData("return load('return 1')()")]
    [InlineData("return string.rep('a', 10)")]
    [InlineData("return ('a'):rep(10)")]
    [InlineData("return {id = print}")]
    [InlineData("return debug.getinfo(1)")]
    public void NoLibrariesAreReachable(string source)
    {
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read(source));
    }

    [Fact]
    public void EmptyGlobalTable()
    {
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read("local n = 0 for _ in pairs(_G) do n = n + 1 end return {id = 'x' .. n}"));
        Assert.Equal("a", LuaManifestReader.Read("return {id = _G == nil and 'a' or 'b'}").Id);
    }

    [Fact]
    public void EndlessLoopIsStopped()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ManifestException>(() => LuaManifestReader.Read("while true do end"));
        Assert.Contains("stopped", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), sw.Elapsed.ToString());
    }

    [Fact]
    public void MemoryBombIsStopped()
    {
        var ex = Assert.Throws<ManifestException>(() => LuaManifestReader.Read("local s = 'xxxxxxxxxxxxxxxx' while true do s = s .. s end"));
        Assert.Contains("stopped", ex.Message);
    }

    [Fact]
    public void TableBombIsStopped()
    {
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read("local t = {} for i = 1, 1e9 do t[i] = {i} end return t"));
    }

    [Fact]
    public void RunawayRecursionIsStopped()
    {
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read("local function f() return f() + 1 end return f()"));
    }

    [Fact]
    public void BadInputGivesAReadableError()
    {
        Assert.Contains("table", Assert.Throws<ManifestException>(() => LuaManifestReader.Read("return 5")).Message);
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read("return {"));
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read("return {name = 'x'}"));
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read("return {id = '../evil'}"));
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read(new string(' ', LuaManifestReader.MaxSourceChars + 1)));
    }
}
