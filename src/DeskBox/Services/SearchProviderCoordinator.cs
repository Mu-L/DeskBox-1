using DeskBox.Models;

namespace DeskBox.Services;

internal sealed record SearchProviderTask(
    string Name,
    Task<IReadOnlyList<SearchResultItem>> Task);

internal sealed record SearchProviderBatchResult(
    IReadOnlyList<IReadOnlyList<SearchResultItem>> Results,
    IReadOnlyList<string> FailedProviders);

internal static class SearchProviderCoordinator
{
    public static async Task<SearchProviderBatchResult> CollectSafelyAsync(
        IReadOnlyList<SearchProviderTask> providers,
        CancellationToken cancellationToken,
        Action<string, Exception>? onFailure = null)
    {
        Task<(IReadOnlyList<SearchResultItem> Items, string? FailedProvider)>[] observers =
            providers.Select(provider => ObserveAsync(
                provider,
                cancellationToken,
                onFailure)).ToArray();
        (IReadOnlyList<SearchResultItem> Items, string? FailedProvider)[] completed =
            await Task.WhenAll(observers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new SearchProviderBatchResult(
            completed.Select(entry => entry.Items).ToList(),
            completed
                .Where(entry => entry.FailedProvider is not null)
                .Select(entry => entry.FailedProvider!)
                .ToList());
    }

    private static async Task<(IReadOnlyList<SearchResultItem>, string?)> ObserveAsync(
        SearchProviderTask provider,
        CancellationToken cancellationToken,
        Action<string, Exception>? onFailure)
    {
        try
        {
            IReadOnlyList<SearchResultItem> result =
                await provider.Task.ConfigureAwait(false);
            return (result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(provider.Name, ex);
            return ([], provider.Name);
        }
    }
}
