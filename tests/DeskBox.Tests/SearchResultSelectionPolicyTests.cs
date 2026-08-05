using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchResultSelectionPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void RubberBand_StartsOnlyFromRealEmptySpace(
        bool isLeftPressed,
        bool isOverRow,
        bool isShiftPressed,
        bool expected)
    {
        Assert.Equal(
            expected,
            SearchResultSelectionPolicy.ShouldStartRubberBand(
                isLeftPressed,
                isOverRow,
                isShiftPressed));
    }

    [Fact]
    public void Range_UsesStableAnchorInEitherDirection()
    {
        Assert.Equal((2, 7), SearchResultSelectionPolicy.GetRange(2, 7, 10));
        Assert.Equal((2, 7), SearchResultSelectionPolicy.GetRange(7, 2, 10));
        Assert.Equal((-1, -1), SearchResultSelectionPolicy.GetRange(10, 2, 10));
    }

    [Fact]
    public void AutoScroll_AcceleratesTowardViewportEdges()
    {
        double topEdge = SearchResultSelectionPolicy.GetAutoScrollDelta(0, 400);
        double nearTop = SearchResultSelectionPolicy.GetAutoScrollDelta(24, 400);
        double middle = SearchResultSelectionPolicy.GetAutoScrollDelta(200, 400);
        double nearBottom = SearchResultSelectionPolicy.GetAutoScrollDelta(376, 400);
        double bottomEdge = SearchResultSelectionPolicy.GetAutoScrollDelta(400, 400);

        Assert.True(topEdge < nearTop);
        Assert.True(nearTop < 0);
        Assert.Equal(0, middle);
        Assert.True(nearBottom > 0);
        Assert.True(bottomEdge > nearBottom);
    }
}
