using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupOrderTests
{
    [Fact]
    public void AdjacentMoves_AreSymmetric()
    {
        IList<string> movingDown = new List<string> { "a", "b", "c" };
        IList<string> movingUp = new List<string> { "a", "b", "c" };

        Assert.True(
            WidgetGroupOrder.MoveToTargetSlot(
                movingDown,
                "a",
                "b"));
        Assert.True(
            WidgetGroupOrder.MoveToTargetSlot(
                movingUp,
                "c",
                "b"));

        Assert.Equal(["b", "a", "c"], movingDown);
        Assert.Equal(["a", "c", "b"], movingUp);
    }

    [Fact]
    public void MoveToDistantTarget_UsesTargetsOriginalSlot()
    {
        IList<string> members = new List<string> { "a", "b", "c", "d" };

        Assert.True(
            WidgetGroupOrder.MoveToTargetSlot(
                members,
                "a",
                "d"));

        Assert.Equal(["b", "c", "d", "a"], members);
    }
}
