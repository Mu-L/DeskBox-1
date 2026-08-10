using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoMasterDetailSettingsTests
{
    [Fact]
    public void MasterPaneWidth_RoundTripsPerWidgetAndClampsToSafeRange()
    {
        var first = new WidgetConfig { Id = "first", WidgetKind = WidgetKind.Todo };
        var second = new WidgetConfig { Id = "second", WidgetKind = WidgetKind.Todo };

        Assert.True(TodoMasterDetailSettings.SetMasterPaneWidth(first, 360.25));
        Assert.False(TodoMasterDetailSettings.SetMasterPaneWidth(first, 360.25));
        Assert.Equal(360.25, TodoMasterDetailSettings.GetMasterPaneWidth(first));
        Assert.Null(TodoMasterDetailSettings.GetMasterPaneWidth(second));

        Assert.True(TodoMasterDetailSettings.SetMasterPaneWidth(first, 900));
        Assert.Equal(420, TodoMasterDetailSettings.GetMasterPaneWidth(first));
    }

    [Fact]
    public void MasterPaneWidth_IgnoresInvalidPersistedValue()
    {
        var config = new WidgetConfig { WidgetKind = WidgetKind.Todo };
        config.Metadata["Todo.MasterPaneWidth"] = "not-a-number";

        Assert.Null(TodoMasterDetailSettings.GetMasterPaneWidth(config));
    }
}
