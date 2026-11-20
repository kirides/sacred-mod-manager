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
    public void ModsGearSkipsAreLeftOut()
    {
        Assert.Equal("b", Order([E("a", 0, "nothing"), E("b")]));
        Assert.Equal("", Order([E("x", 0, "y"), E("y", 0, "x")]));
        // a mod needing a mod that cannot load cannot load either
        Assert.Equal("base,ok", Order([E("c", 0, "a"), E("a", 0, "nothing"), E("base"), E("ok", -1, "base")], "ok", "base"));
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

    [Fact]
    public void MoveToPlacesAModAtTheGivenIndex()
    {
        string[] list = ["a", "b", "c", "d"];
        Assert.Equal(["d", "a", "b", "c"], LoadOrder.MoveTo(list, "d", 0));
        Assert.Equal(["b", "c", "d", "a"], LoadOrder.MoveTo(list, "a", 3));
        Assert.Equal(["a", "c", "b", "d"], LoadOrder.MoveTo(list, "b", 2));
        Assert.Equal(list, LoadOrder.MoveTo(list, "b", 1));
        Assert.Null(LoadOrder.MoveTo(list, "b", 4));
        Assert.Null(LoadOrder.MoveTo(list, "b", -1));
        Assert.Null(LoadOrder.MoveTo(list, "zzz", 0));
    }

    [Fact]
    public void ADropAgainstARequirementComesBackUnchangedFromCompute()
    {
        LoadOrderEntry[] mods = [E("a"), E("b", 0, "a"), E("c")];
        List<string> before = LoadOrder.Compute(mods, []);
        List<string> dropped = LoadOrder.MoveTo(before, "b", 0)!; // b before the a it needs
        Assert.Equal(before, LoadOrder.Compute(mods, dropped));
    }
}
