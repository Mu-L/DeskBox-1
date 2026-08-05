using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchProviderCoordinatorTests
{
    [Fact]
    public async Task CollectSafelyAsync_keepsSuccessfulProvidersWhenOneFails()
    {
        var item = new SearchResultItem
        {
            Kind = SearchResultKind.File,
            Title = "DeskBox"
        };
        var failures = new List<string>();
        SearchProviderTask[] providers =
        [
            new("fast", Task.FromResult<IReadOnlyList<SearchResultItem>>([item])),
            new("broken", Task.FromException<IReadOnlyList<SearchResultItem>>(
                new IOException("provider failed")))
        ];

        SearchProviderBatchResult result = await SearchProviderCoordinator.CollectSafelyAsync(
            providers,
            CancellationToken.None,
            (name, _) => failures.Add(name));

        Assert.Same(item, Assert.Single(result.Results.SelectMany(items => items)));
        Assert.Equal(["broken"], result.FailedProviders);
        Assert.Equal(["broken"], failures);
    }

    [Fact]
    public async Task CollectSafelyAsync_propagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        SearchProviderTask[] providers =
        [
            new("cancelled", Task.FromCanceled<IReadOnlyList<SearchResultItem>>(cts.Token))
        ];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SearchProviderCoordinator.CollectSafelyAsync(
                providers,
                cts.Token));
    }
}
