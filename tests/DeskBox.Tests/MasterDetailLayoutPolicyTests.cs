using DeskBox.Controls;

namespace DeskBox.Tests;

public sealed class MasterDetailLayoutPolicyTests
{
    private readonly MasterDetailLayoutPolicy _policy = new();

    [Theory]
    [InlineData(679, false, MasterDetailLayoutMode.SinglePane)]
    [InlineData(700, false, MasterDetailLayoutMode.SinglePane)]
    [InlineData(720, false, MasterDetailLayoutMode.DualPane)]
    [InlineData(700, true, MasterDetailLayoutMode.DualPane)]
    [InlineData(679, true, MasterDetailLayoutMode.SinglePane)]
    public void Resolve_AppliesEnterExitHysteresis(
        double width,
        bool wasDualPane,
        MasterDetailLayoutMode expected)
    {
        MasterDetailLayoutSnapshot result = _policy.Resolve(width, wasDualPane);

        Assert.Equal(expected, result.Mode);
    }

    [Fact]
    public void Resolve_ClampsPersistedWidthAndProtectsDetailPane()
    {
        MasterDetailLayoutSnapshot narrowMaster = _policy.Resolve(720, false, 100);
        MasterDetailLayoutSnapshot wideMaster = _policy.Resolve(800, false, 700);

        Assert.Equal(240, narrowMaster.MasterWidth);
        Assert.Equal(420, wideMaster.MasterWidth);
        Assert.Equal(20, wideMaster.SplitterWidth);
        Assert.Equal(360, wideMaster.DetailWidth);
    }

    [Fact]
    public void Resolve_UsesDefaultWidthAndSupportsForcedSinglePane()
    {
        MasterDetailLayoutSnapshot automatic = _policy.Resolve(900, false, double.NaN);
        MasterDetailLayoutSnapshot forced = _policy.Resolve(900, true, 300, forceSinglePane: true);

        Assert.Equal(300, automatic.MasterWidth);
        Assert.Equal(580, automatic.DetailWidth);
        Assert.False(forced.IsDualPane);
        Assert.Equal(0, forced.SplitterWidth);
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData(double.NaN, 300)]
    [InlineData(100.0, 240.0)]
    [InlineData(500.0, 420.0)]
    [InlineData(318.0, 318.0)]
    public void NormalizePersistedMasterWidth_UsesSafeRange(double? value, double expected)
    {
        Assert.Equal(expected, _policy.NormalizePersistedMasterWidth(value));
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        var options = new MasterDetailLayoutOptions(
            DualPaneEnterWidth: 680,
            DualPaneExitWidth: 720);

        Assert.Throws<ArgumentOutOfRangeException>(() => new MasterDetailLayoutPolicy(options));
    }
}
