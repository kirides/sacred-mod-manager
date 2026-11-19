using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("0.4.0", "0.10.0", -1)]
    [InlineData("0.10.0", "0.4.0", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("v1.2", "1.2.0", 0)]
    [InlineData("V1.2", "1.2", 0)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2", "10", -1)]
    [InlineData("1.2.1", "1.2", 1)]
    [InlineData("1.2.0.0.1", "1.2", 1)]
    [InlineData(" 1.2 ", "1.2", 0)]
    [InlineData("1_2_3", "1.2.3", 0)]
    [InlineData("1.2+build", "1.2.build", 0)]
    public void Numeric(string a, string b, int expected) => Assert.Equal(expected, VersionCompare.Compare(a, b));

    [Theory]
    [InlineData("1.0-beta", "1.0-alpha", 1)]
    [InlineData("1.0-alpha", "1.0-beta", -1)]
    [InlineData("1.0-beta", "1.0-beta", 0)]
    [InlineData("1.0-beta.2", "1.0-beta.10", -1)]
    public void TextPartsAreOrdinal(string a, string b, int expected) => Assert.Equal(expected, VersionCompare.Compare(a, b));

    [Fact]
    public void NumberAfterTextCountsAsNewerThanText()
    {
        // documented behaviour: "1.0-beta" vs "1.0.1": the part "beta" is text, "1" numeric: the numeric one wins
        Assert.True(VersionCompare.Compare("1.0.1", "1.0-beta") > 0);
        Assert.True(VersionCompare.Compare("1.0-beta", "1.0.1") < 0);
    }

    [Fact]
    public void ReleaseIsNotNewerThanItsPreReleaseSuffixWhenTextSortsHigher()
    {
        // "1.0" vs "1.0-beta": the missing part counts as 0 (numeric), which beats the text "beta": 1.0 > 1.0-beta
        Assert.True(VersionCompare.Compare("1.0", "1.0-beta") > 0);
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("", "", 0)]
    [InlineData("  ", "", 0)]
    [InlineData("", "0", 0)]
    [InlineData("", "0.0.0", 0)]
    [InlineData("", "1", -1)]
    [InlineData("1", "", 1)]
    [InlineData(null, "0.1", -1)]
    public void EmptyCountsAsZero(string? a, string? b, int expected) => Assert.Equal(expected, VersionCompare.Compare(a, b));

    [Fact]
    public void CompareIsAntisymmetric()
    {
        string[] versions = ["", "0", "0.4.0", "0.10.0", "1", "1.0-beta", "1.0", "v2", "2.0.1", "x", "1.x"];
        foreach (string a in versions)
        {
            foreach (string b in versions)
            {
                Assert.Equal(-VersionCompare.Compare(a, b), VersionCompare.Compare(b, a));
            }
        }
    }

    [Theory]
    [InlineData("0.10.0", "0.4.0", true)]
    [InlineData("0.4.0", "0.10.0", false)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("", "1.0", false)]
    [InlineData(null, "1.0", false)]
    [InlineData("  ", "1.0", false)]
    [InlineData("1.0", "", true)]
    [InlineData("1.0", null, true)]
    [InlineData("0", "", false)]
    public void IsNewer(string? available, string? installed, bool expected) => Assert.Equal(expected, VersionCompare.IsNewer(available, installed));
}
