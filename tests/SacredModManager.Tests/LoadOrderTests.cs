using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

public class LoadOrderTests
{
    private static LoadOrderEntry E(string id, double priority = 0, params string[] requires) => new(id, priority, requires);

    private static string Order(IEnumerable<LoadOrderEntry> mods, params string[] saved) => string.Join(",", LoadOrder.Compute(mods, saved));

    [Fact]
    public void WithoutASavedOrderPriorityThenIdDecides()
    {
        Assert.Equal("a,c,d,b", Order([E("b", 5), E("d"), E("a", -5), E("c")]));
    }

    [Fact]
    public void ListedModsComeFirstInListOrderTheRestByPriority()
    {
        Assert.Equal("d,b,a,c", Order([E("a", -5), E("b", 5), E("c"), E("d")], "d", "b", "gone", "d"));
    }

    [Fact]
    public void ARequirementLoadsBeforeTheModThatNeedsIt()
    {
        Assert.Equal("a,c,b,d", Order([E("a", -5), E("b", 5), E("c", 0, "a"), E("d")], "c", "b", "a", "d"));
    }

    [Fact]
    public void MissingRequirementsAndCyclesDoNotLoopOrDropMods()
    {
        Assert.Equal("a,b", Order([E("a", 0, "nothing"), E("b")]));
        Assert.Equal(2, LoadOrder.Compute([E("x", 0, "y"), E("y", 0, "x")], []).Count);
    }

    [Fact]
    public void MoveShiftsOneModAndRefusesTheEnds()
    {
        string[] list = ["a", "b", "c"];
        Assert.Equal(["b", "a", "c"], LoadOrder.Move(list, "b", -1));
        Assert.Equal(["a", "c", "b"], LoadOrder.Move(list, "b", 1));
        Assert.Null(LoadOrder.Move(list, "a", -1));
        Assert.Null(LoadOrder.Move(list, "c", 1));
        Assert.Null(LoadOrder.Move(list, "zzz", 1));
    }
}
