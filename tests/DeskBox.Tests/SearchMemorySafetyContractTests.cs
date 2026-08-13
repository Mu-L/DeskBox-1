using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchMemorySafetyContractTests
{
    [Fact]
    public void UsnFullVolumeIndexing_IsDisabledByDefault()
    {
        using var service = new UsnJournalIndexService();

        Assert.False(service.IsFullVolumeIndexingAllowed);
    }

    [Fact]
    public void UsnFullVolumeIndexing_RequiresAnExplicitInternalOptIn()
    {
        using var service = new UsnJournalIndexService(
            allowFullVolumeIndexing: true);

        Assert.True(service.IsFullVolumeIndexingAllowed);
    }

    [Fact]
    public void SearchIndexContentUpdates_DoNotScheduleForcedMemoryCleanup()
    {
        string appSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.xaml.cs"));
        const string handlerSignature = "private void OnSearchIndexUpdated()";
        const string nextMemberSignature = "private Task ToggleSearchPopupAsync()";
        int handlerStart = appSource.IndexOf(
            handlerSignature,
            StringComparison.Ordinal);
        int handlerEnd = appSource.IndexOf(
            nextMemberSignature,
            handlerStart,
            StringComparison.Ordinal);

        Assert.True(handlerStart >= 0, "Search index update handler was not found.");
        Assert.True(handlerEnd > handlerStart, "Search index update handler boundary was not found.");

        string handler = appSource[handlerStart..handlerEnd];
        Assert.DoesNotContain("ScheduleLightMemoryCleanup", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("GC.Collect", handler, StringComparison.Ordinal);
        Assert.Contains("cleanup=none reason=content-update", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshIndexReconciliation_IsDelayedOutOfTheStartupWindow()
    {
        Assert.True(
            SearchIndexService.FreshIndexReconciliationDelay >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void FixedDriveDiscovery_RemainsDynamicAndMachineSpecific()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/SearchIndexService.cs"));

        Assert.Contains("Directory.GetLogicalDrives()", source, StringComparison.Ordinal);
        Assert.Contains("info.IsReady", source, StringComparison.Ordinal);
        Assert.Contains("info.DriveType == DriveType.Fixed", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, true)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(false, false, true, false, false, false, false)]
    [InlineData(false, false, false, true, false, false, false)]
    [InlineData(false, false, false, false, true, false, false)]
    [InlineData(false, false, false, false, false, true, false)]
    public void ReconciliationPolicy_RunsOnlyWhenInteractiveSurfacesAreIdle(
        bool isWidgetInteractionActive,
        bool isSettingsOpen,
        bool isOnboardingOpen,
        bool isSearchPopupVisible,
        bool isDeskBoxForeground,
        bool isPointerOverDeskBox,
        bool expected)
    {
        var snapshot = new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: true,
            isWidgetInteractionActive,
            isSettingsOpen,
            isOnboardingOpen,
            isSearchPopupVisible,
            isDeskBoxForeground,
            isPointerOverDeskBox);

        Assert.Equal(
            expected,
            MemoryCleanupPolicy.CanRunSearchIndexReconciliation(snapshot));
    }
}
