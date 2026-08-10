using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Coordinates search across all layers: DeskBox internal data, custom file index,
/// and (future) Windows Search Index.
/// </summary>
public sealed class SearchEngineService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly SearchIndexService _indexService;
    private readonly WindowsIndexSearchService _windowsIndexService;
    private readonly UsnJournalIndexService? _usnIndexService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly TodoWorkspaceService? _todoWorkspaceService;
    private readonly QuickCaptureMarkdownService _quickCaptureMarkdownService = new();
    private bool _isDisposed;

    public SearchEngineService(
        SettingsService settingsService,
        LocalizationService localizationService,
        SearchIndexService indexService,
        WindowsIndexSearchService windowsIndexService,
        UsnJournalIndexService? usnIndexService = null,
        QuickCaptureService? quickCaptureService = null,
        TodoWorkspaceService? todoWorkspaceService = null)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _indexService = indexService;
        _windowsIndexService = windowsIndexService;
        _usnIndexService = usnIndexService;
        _quickCaptureService = quickCaptureService ?? new QuickCaptureService();
        _todoWorkspaceService = todoWorkspaceService;
        _indexService.IndexUpdated += OnIndexUpdated;
        _indexService.ProgressChanged += OnIndexProgressChanged;
        if (_usnIndexService is not null)
        {
            _usnIndexService.IndexUpdated += OnIndexUpdated;
            _usnIndexService.ProgressChanged += OnIndexProgressChanged;
        }
    }

    public SearchIndexService IndexService => _indexService;

    public int IndexedItemCount => _usnIndexService is { IsAvailable: true }
        ? _usnIndexService.EntryCount
        : _indexService.EntryCount;

    public bool IsCustomIndexing => _indexService.IsScanning ||
                                    _indexService.IsLoading ||
                                    _usnIndexService is { IsScanning: true };

    public bool IsIndexPaused => _indexService.IsPaused ||
                                 _usnIndexService is { IsPaused: true };

    public DateTime? LastScanTime => _indexService.LastScanTime;
    public bool IsCustomIndexResident => _indexService.IsIndexResident;

    public bool IsUsnIndexAvailable => _usnIndexService?.IsAvailable == true;
    public bool IsUsnIndexScanning => _usnIndexService?.IsScanning == true;
    public bool IsUsnIndexIncrementalSyncing =>
        _usnIndexService?.IsIncrementalSyncing == true;

    public event Action? IndexUpdated;

    /// <summary>Raised periodically during indexing with the current total entry count.</summary>
    public event Action<int>? IndexProgressChanged;

    private void OnIndexUpdated() => IndexUpdated?.Invoke();

    private void OnIndexProgressChanged(int _)
    {
        // Aggregate both services' counts and forward to subscribers.
        IndexProgressChanged?.Invoke(IndexedItemCount);
    }

    public void SetCustomIndexingEnabled(bool enabled)
    {
        if (enabled)
        {
            _ = StartCustomIndexingAsync();
        }
        else
        {
            _indexService.StopIndexing();
            _usnIndexService?.StopIndexing();
        }
    }

    /// <summary>
    /// Restores the persisted custom index away from the UI thread, then starts its
    /// reconciliation/watchers. Safe to call concurrently with popup preloading.
    /// </summary>
    public async Task StartCustomIndexingAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed ||
            !_settingsService.Settings.SearchCustomIndexerEnabled)
        {
            return;
        }

        await _indexService.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (_isDisposed)
        {
            return;
        }

        _indexService.StartIndexing();
        _usnIndexService?.StartIndexing();
    }

    /// <summary>
    /// Begins restoring an idle-unloaded index as soon as the popup is invoked.
    /// Search itself also awaits this task, so an unusually fast first keystroke
    /// cannot observe an empty custom index.
    /// </summary>
    public Task<bool> PrepareForPopupAsync(
        CancellationToken cancellationToken = default) =>
        _indexService.EnsureLoadedAsync(cancellationToken);

    public Task<bool> TryUnloadCustomIndexForIdleAsync(
        CancellationToken cancellationToken = default) =>
        _indexService.TryUnloadForIdleAsync(cancellationToken);

    /// <summary>Pauses all in-progress indexing.</summary>
    public void PauseIndexing()
    {
        _indexService.PauseIndexing();
        _usnIndexService?.PauseIndexing();
    }

    /// <summary>Resumes paused indexing.</summary>
    public void ResumeIndexing()
    {
        _indexService.ResumeIndexing();
        _usnIndexService?.ResumeIndexing();
    }

    /// <summary>Clears and rebuilds the index from scratch.</summary>
    public void RebuildIndex()
    {
        _indexService.RebuildIndex();
        // USN journal index is ephemeral (no disk persistence); just restart it.
        if (_usnIndexService is not null)
        {
            _usnIndexService.StopIndexing();
            _usnIndexService.StartIndexing();
        }
    }

    /// <summary>Returns the on-disk storage size (bytes) of the persisted index.</summary>
    public long GetIndexStorageBytes() => _indexService.GetIndexStorageBytes();

    /// <summary>
    /// Performs a unified search across all enabled layers.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        SearchResponse? latest = null;
        await foreach (SearchResponse stage in SearchStagedAsync(
                           query,
                           cancellationToken))
        {
            latest = stage;
        }

        return latest ?? BuildSearchResponse(
            query,
            [],
            Math.Clamp(_settingsService.Settings.SearchMaxResults, 10, 200),
            TimeSpan.Zero,
            isComplete: true);
    }

    public async IAsyncEnumerable<SearchResponse> SearchStagedAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = _settingsService.Settings;
        int maxResults = Math.Clamp(settings.SearchMaxResults, 10, 200);

        var immediateProviders = new List<SearchProviderTask>();
        var extendedProviders = new List<SearchProviderTask>();

        // Start all enabled providers together, but publish the lightweight
        // DeskBox/action stage before waiting for system and disk indexes.
        if (settings.SearchIncludeDeskBoxContent)
        {
            immediateProviders.Add(new SearchProviderTask(
                "deskbox-content",
                SearchDeskBoxContentAsync(query, maxResults, cancellationToken)));
        }

        immediateProviders.Add(new SearchProviderTask(
            "actions",
            Task.FromResult(SearchActions(query))));

        // Layer 2: Windows Search Index (system-indexed locations)
        if (settings.SearchIncludeSystemIndex)
        {
            extendedProviders.Add(new SearchProviderTask(
                "windows-index",
                _windowsIndexService.SearchAsync(query, maxResults, cancellationToken)));
        }

        // Layer 3: File indexes. The USN journal is fast and broad when available,
        // but it can be incomplete while a volume is offline, capped, or being
        // refreshed. Always query the directory index as well so a stale/partial
        // USN snapshot is never the sole authority; the ranker de-duplicates paths.
        if (settings.SearchCustomIndexerEnabled)
        {
            if (_usnIndexService is { IsAvailable: true })
            {
                extendedProviders.Add(new SearchProviderTask(
                    "usn-index",
                    Task.Run<IReadOnlyList<SearchResultItem>>(
                        () => _usnIndexService.Search(query, maxResults, cancellationToken)
                            .Where(item => PathExists(item.DetailPath))
                            .ToList(),
                        cancellationToken)));
            }

            extendedProviders.Add(new SearchProviderTask(
                "custom-index",
                SearchCustomIndexAsync(query, maxResults, cancellationToken)));
        }

        SearchProviderBatchResult immediate = await SearchProviderCoordinator.CollectSafelyAsync(
            immediateProviders,
            cancellationToken,
            LogProviderFailure);
        cancellationToken.ThrowIfCancellationRequested();

        if (extendedProviders.Count == 0)
        {
            yield return BuildSearchResponse(
                query,
                immediate.Results,
                maxResults,
                stopwatch.Elapsed,
                isComplete: true);
            yield break;
        }

        yield return BuildSearchResponse(
            query,
            immediate.Results,
            maxResults,
            stopwatch.Elapsed,
            isComplete: false);

        SearchProviderBatchResult extended = await SearchProviderCoordinator.CollectSafelyAsync(
            extendedProviders,
            cancellationToken,
            LogProviderFailure);
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();

        yield return BuildSearchResponse(
            query,
            immediate.Results.Concat(extended.Results),
            maxResults,
            stopwatch.Elapsed,
            isComplete: true);
    }

    private SearchResponse BuildSearchResponse(
        string query,
        IEnumerable<IReadOnlyList<SearchResultItem>> providerResults,
        int maxResults,
        TimeSpan elapsed,
        bool isComplete)
    {
        IReadOnlyList<SearchResultItem> rankedItems = SearchResultRanker.MergeAndRank(
            providerResults.SelectMany(items => items),
            query.Trim(),
            maxResults);
        IReadOnlyList<SearchResultGroup> groups = BuildGroups(rankedItems);

        return new SearchResponse
        {
            Query = query,
            RankedItems = rankedItems,
            Groups = groups,
            TotalResultCount = rankedItems.Count,
            Elapsed = elapsed,
            IsComplete = isComplete
        };
    }

    private static void LogProviderFailure(string provider, Exception ex)
    {
        App.Log($"[Search] Provider '{provider}' failed; returning partial results: {ex}");
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchCustomIndexAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        bool loaded = await _indexService
            .EnsureLoadedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!loaded)
        {
            return [];
        }

        return await Task.Run(
            () => _indexService.Search(query, maxResults, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool PathExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets recommendations for the empty-state view.
    /// </summary>
    public async Task<IReadOnlyList<SearchRecommendationItem>> GetRecommendationsAsync(
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
            () => BuildApplicationRecommendations(cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<SearchRecommendationItem> BuildApplicationRecommendations(
        CancellationToken cancellationToken)
    {
        var recommendations = new List<SearchRecommendationItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddShortcut(string path, string subtitle)
        {
            if (cancellationToken.IsCancellationRequested ||
                !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!seenPaths.Add(fullPath))
            {
                return;
            }

            recommendations.Add(new SearchRecommendationItem
            {
                Kind = SearchResultKind.File,
                Title = Path.GetFileName(fullPath),
                Subtitle = subtitle,
                DetailPath = fullPath
            });
        }

        // The user's widgets are an explicit curation signal, so every shortcut shown
        // by an enabled file widget comes before generic Start menu applications.
        foreach (var widget in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in widget.Items.OrderBy(item => item.SortOrder))
            {
                AddShortcut(item.Path, widget.Name);
            }

            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                foreach (string shortcut in EnumerateShortcutFilesSafely(
                             widget.MappedFolderPath, recursive: false, cancellationToken))
                {
                    AddShortcut(shortcut, widget.Name);
                }
            }
        }

        string startMenuLabel = _localizationService.T("Search.Recommend.StartMenu");
        string[] startMenuRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];

        const int MaxStartMenuApps = 40;
        int startMenuCount = 0;
        foreach (string root in startMenuRoots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string shortcut in EnumerateShortcutFilesSafely(
                         root, recursive: true, cancellationToken)
                     .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                int before = recommendations.Count;
                AddShortcut(shortcut, startMenuLabel);
                if (recommendations.Count > before && ++startMenuCount >= MaxStartMenuApps)
                {
                    return recommendations;
                }
            }
        }

        return recommendations;
    }

    private static IEnumerable<string> EnumerateShortcutFilesSafely(
        string root,
        bool recursive,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            string current = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(current, "*.lnk", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return file;
            }

            if (!recursive)
            {
                continue;
            }

            try
            {
                foreach (string directory in Directory.GetDirectories(current))
                {
                    pending.Push(directory);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Keep results already found in accessible Start menu folders.
            }
        }
    }

    private async Task<IReadOnlyList<SearchRecommendationItem>> GetRecentNotesAsync(
        CancellationToken cancellationToken)
    {
        var recommendations = new List<SearchRecommendationItem>();

        try
        {
            var store = new QuickCaptureStore();
            var data = await store.LoadAsync();

            var recent = data.Items
                .Where(i => !i.IsDeleted)
                .OrderByDescending(i => i.UpdatedAt)
                .Take(3);

            foreach (var item in recent)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                recommendations.Add(new SearchRecommendationItem
                {
                    Kind = SearchResultKind.QuickCapture,
                    Title = !string.IsNullOrWhiteSpace(item.Title)
                        ? item.Title
                        : TruncateText(item.Body, 60),
                    Subtitle = item.Type.ToString(),
                    Glyph = "\uE70F",
                    QuickCaptureItemId = item.Id
                });
            }
        }
        catch
        {
            // Skip if QuickCapture data fails to load
        }

        return recommendations;
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchDeskBoxContentAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();

        var todoTask = SearchTodosAsync(query, maxResults / 2, cancellationToken);
        var noteTask = SearchQuickCaptureAsync(query, maxResults / 2, cancellationToken);
        await Task.WhenAll(todoTask, noteTask);
        results.AddRange(await todoTask);
        results.AddRange(await noteTask);

        return results;
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchTodosAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();
        var settings = _settingsService.Settings;

        if (_todoWorkspaceService is not null)
        {
            try
            {
                TodoWorkspaceSnapshot snapshot = await _todoWorkspaceService.LoadSnapshotAsync(
                    includeDeleted: false,
                    cancellationToken);
                string? targetWidgetId = settings.Widgets.FirstOrDefault(widget =>
                    widget.WidgetKind == WidgetKind.Todo && !widget.IsDisabled)?.Id;
                var listNames = snapshot.Lists.ToDictionary(
                    list => list.Id,
                    list => list.Name,
                    StringComparer.Ordinal);

                foreach (TodoTask item in snapshot.Tasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool matches = item.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   (item.Notes?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                   item.Steps.Any(step => step.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
                    if (!matches)
                    {
                        continue;
                    }

                    double score = ComputeTextRelevance(item.Text, query);
                    string subtitle = item.DeadlineAt is { } deadline
                        ? $"{_localizationService.T("Search.Todo.Due")}: {deadline:yyyy-MM-dd}"
                        : listNames.GetValueOrDefault(item.ListId, _localizationService.T("Todo.Title"));
                    results.Add(new SearchResultItem
                    {
                        Kind = SearchResultKind.Todo,
                        Title = item.Text,
                        Subtitle = subtitle,
                        TodoWidgetId = targetWidgetId,
                        TodoItemId = item.Id,
                        TodoIsCompleted = item.Status == TodoTaskStatus.Completed,
                        Glyph = "\uE9D5",
                        ModifiedAt = item.UpdatedAt,
                        RelevanceScore = score + (item.Status == TodoTaskStatus.Completed ? -20 : 10)
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Log($"[Search] Shared Todo workspace search failed: {ex.Message}");
            }

            return results.OrderByDescending(result => result.RelevanceScore).Take(maxResults).ToList();
        }

        // Production construction always supplies the shared workspace. Keeping a
        // service-null instance useful for file-only unit tests must not resurrect
        // per-widget JSON reads.
        return [];
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchQuickCaptureAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();

        try
        {
            IReadOnlyList<QuickCaptureSearchHit> hits =
                await _quickCaptureService.SearchAsync(query, maxResults, cancellationToken);

            foreach (QuickCaptureSearchHit hit in hits)
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                QuickCaptureItem item = hit.Item;
                string displayTitle = !string.IsNullOrWhiteSpace(item.Title)
                    ? item.Title
                    : _quickCaptureMarkdownService.CreateDerivedTitle(
                        item.Title,
                        item.Body,
                        item.ContentFormat);

                double score = ComputeTextRelevance(displayTitle, query);
                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.QuickCapture,
                    Title = displayTitle,
                    Subtitle = _quickCaptureMarkdownService.CreateExcerpt(
                        item.Body,
                        item.ContentFormat,
                        120),
                    QuickCaptureItemId = item.Id,
                    Glyph = "\uE70F",
                    ModifiedAt = item.UpdatedAt,
                    RelevanceScore = score + (item.IsPinned ? 5 : 0) - hit.Rank
                });
            }
        }
        catch
        {
            // Skip if QuickCapture data fails to load
        }

        return results.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
    }

    private IReadOnlyList<SearchResultItem> SearchActions(string query)
    {
        var actions = new (string Id, string NameKey, string Glyph)[]
        {
            ("new-todo", "Search.Action.NewTodo", "\uE9D5"),
            ("new-note", "Search.Action.NewNote", "\uE70F"),
            ("open-settings", "Search.Action.OpenSettings", "\uE713"),
            ("toggle-widgets", "Search.Action.ToggleWidgets", "\uE8A5"),
            ("toggle-theme", "Search.Action.ToggleTheme", "\uE793")
        };

        var results = new List<SearchResultItem>();
        foreach (var (id, nameKey, glyph) in actions)
        {
            string name = _localizationService.T(nameKey);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.Action,
                    Title = name,
                    ActionId = id,
                    Glyph = glyph,
                    RelevanceScore = ComputeTextRelevance(name, query) + 5
                });
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<SearchRecommendationItem>> GetUpcomingTodosAsync(
        CancellationToken cancellationToken)
    {
        var recommendations = new List<SearchRecommendationItem>();
        var settings = _settingsService.Settings;

        if (_todoWorkspaceService is not null)
        {
            TodoWorkspaceSnapshot snapshot = await _todoWorkspaceService.LoadSnapshotAsync(
                includeDeleted: false,
                cancellationToken);
            string? targetWidgetId = settings.Widgets.FirstOrDefault(widget =>
                widget.WidgetKind == WidgetKind.Todo && !widget.IsDisabled)?.Id;
            DateTimeOffset now = DateTimeOffset.Now;
            foreach (TodoTask item in snapshot.Tasks
                         .Where(item => item.Status == TodoTaskStatus.Open &&
                                        item.DeadlineAt is { } deadline &&
                                        deadline >= now &&
                                        deadline <= now.AddDays(7))
                         .OrderBy(item => item.DeadlineAt)
                         .Take(3))
            {
                recommendations.Add(new SearchRecommendationItem
                {
                    Kind = SearchResultKind.Todo,
                    Title = item.Text,
                    Subtitle = $"{_localizationService.T("Search.Todo.Due")}: {item.DeadlineAt!.Value:MM-dd}",
                    Glyph = "\uE9D5",
                    TodoWidgetId = targetWidgetId,
                    TodoItemId = item.Id
                });
            }

            return recommendations;
        }

        return recommendations;
    }

    private IReadOnlyList<SearchResultGroup> BuildGroups(
        IReadOnlyList<SearchResultItem> rankedResults)
    {
        var groups = new List<SearchResultGroup>();

        var groupOrder = new[]
        {
            (SearchResultKind.Action, _localizationService.T("Search.Group.Actions")),
            (SearchResultKind.Todo, _localizationService.T("Search.Group.Todos")),
            (SearchResultKind.QuickCapture, _localizationService.T("Search.Group.Notes")),
            (SearchResultKind.File, _localizationService.T("Search.Group.Files")),
            (SearchResultKind.Folder, _localizationService.T("Search.Group.Folders"))
        };

        foreach (var (kind, displayName) in groupOrder)
        {
            var items = rankedResults
                .Where(r => r.Kind == kind)
                .ToList();

            if (items.Count > 0)
            {
                groups.Add(new SearchResultGroup
                {
                    Kind = kind,
                    DisplayName = displayName,
                    Items = items,
                    TotalCount = items.Count
                });
            }
        }

        return groups;
    }

    private static double ComputeTextRelevance(string text, string query)
    {
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return 30;
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "...";
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _indexService.IndexUpdated -= OnIndexUpdated;
        _indexService.ProgressChanged -= OnIndexProgressChanged;
        if (_usnIndexService is not null)
        {
            _usnIndexService.IndexUpdated -= OnIndexUpdated;
            _usnIndexService.ProgressChanged -= OnIndexProgressChanged;
        }
        _indexService.Dispose();
        _usnIndexService?.Dispose();
    }
}
