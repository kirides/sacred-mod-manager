using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class LuaStringMethodTests
{
    [Fact]
    public void FormatAndFriendsWorkAsMethodsOfAString()
    {
        ModManifest m = LuaManifestReader.Read("""
            local keys = {}
            for i = 1, 3 do keys[#keys + 1] = {id = ('bar%d_%d'):format(i, 2), label = ('Bar'):upper():sub(1, 2) .. ('-'):rep(3), default = 'ctrl+' .. ('x'):byte() } end
            return {id = 'a', keys = keys}
            """);
        Assert.Equal(3, m.Keys.Count);
        Assert.Equal("bar1_2", m.Keys[0].Id);
        Assert.Equal("BA---", m.Keys[0].Label);
        Assert.Equal("ctrl+120", m.Keys[0].Default);
    }

    [Theory]
    [InlineData("return {id = ('a'):gsub('a', 'b')}")]
    [InlineData("return {id = ('a'):match('a')}")]
    [InlineData("return {id = ('a'):find('a')}")]
    [InlineData("return {id = ('a'):gmatch('a')}")]
    [InlineData("return {id = string.format('x')}")]
    [InlineData("return {id = ('a'):dump()}")]
    public void PatternFunctionsAndTheGlobalTableAreNotThere(string source)
    {
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read(source));
    }

    [Theory]
    [InlineData("return {id = ('a'):rep(1000000000)}")]
    [InlineData("return {id = ('abc'):rep(30000)}")]
    [InlineData("return {id = ('%999999999d'):format(1)}")]
    [InlineData("return {id = ('%.999f'):format(1)}")]
    [InlineData("local s = ('a'):rep(60000) .. ('a'):rep(60000) return {id = s:upper()}")]
    public void StringMethodsCannotBuildBigStrings(string source)
    {
        Assert.Throws<ManifestException>(() => LuaManifestReader.Read(source));
    }
}
