using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationTransaction
{
    internal static SemaphoreSlim OperationGate { get; } = new(1, 1);

    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly DesktopOrganizationRecoveryStore _recoveryStore;

    public DesktopOrganizationTransaction(
        SettingsService settingsService,
        FileService fileService,
        DesktopOrganizationRecoveryStore? recoveryStore = null)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _recoveryStore = recoveryStore ?? new DesktopOrganizationRecoveryStore();
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(plan, progress: null, cancellationToken);
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        IProgress<DesktopOrganizationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            ValidatePlan(plan);
            RevalidateSources(plan);
            ValidateAvailableSpace(plan);

            var settings = _settingsService.Settings;
            var originalWidgets = settings.Widgets.ToList();
            var originalRules = settings.DesktopOrganizationRules.ToList();
            var originalHistory = settings.RecentOrganizationHistory.ToList();
            var createdDirectories = new List<string>();
            var completedMoves = new List<FileService.FileTransferResult>();
            var createdWidgets = CreateCandidateWidgets(plan, settings);
            var journal = BuildJournal(plan);

            try
            {
                foreach (DesktopOrganizationTargetPlan target in plan.Targets)
                {
                    if (!Directory.Exists(target.TargetDirectoryPath))
                    {
                        Directory.CreateDirectory(target.TargetDirectoryPath);
                        createdDirectories.Add(target.TargetDirectoryPath);
                    }
                }

                ReserveDestinations(journal);
                await _recoveryStore.SaveAsync(journal);
                int totalCount = journal.Items.Count;
                int completedCount = 0;

                for (int index = 0; index < journal.Items.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DesktopOrganizationRecoveryItem journalItem = journal.Items[index];
                    var result = await _fileService.ExecuteTransferPlanAsync(
                        [new FileService.FileTransferPlan(
                            journalItem.SourcePath,
                            journalItem.DestinationPath)],
                        move: true,
                        useShellProgress: false);
                    completedMoves.Add(result.Single());
                    journalItem.Completed = true;
                    await _recoveryStore.SaveAsync(journal);
                    completedCount++;
                    DesktopOrganizationTargetPlan? progressTarget = plan.Targets.FirstOrDefault(
                        target => string.Equals(
                            target.TargetWidgetId,
                            journalItem.TargetWidgetId,
                            StringComparison.Ordinal));
                    progress?.Report(new DesktopOrganizationProgress(
                        completedCount,
                        totalCount,
                        journalItem.TargetWidgetId,
                        progressTarget?.SuggestedDisplayName ?? string.Empty));
                }

                AddRulesForNewTargets(plan, settings.DesktopOrganizationRules);
                OrganizationHistoryEntry history = CreateHistory(plan, journal);
                settings.RecentOrganizationHistory.Insert(0, history);
                if (settings.RecentOrganizationHistory.Count > SettingsService.MaxRecentOrganizationHistoryCount)
                {
                    settings.RecentOrganizationHistory.RemoveRange(
                        SettingsService.MaxRecentOrganizationHistoryCount,
                        settings.RecentOrganizationHistory.Count - SettingsService.MaxRecentOrganizationHistoryCount);
                }

                await _settingsService.SaveAsync(notifySubscribers: false);
                _recoveryStore.Clear();

                return new DesktopOrganizationExecutionResult
                {
                    History = history,
                    CreatedWidgets = createdWidgets
                };
            }
            catch
            {
                settings.Widgets = originalWidgets;
                settings.DesktopOrganizationRules = originalRules;
                settings.RecentOrganizationHistory = originalHistory;
                bool rolledBack = await RollBackMovesAsync(completedMoves);
                RemoveEmptyCreatedDirectories(createdDirectories);
                await _settingsService.SaveAsync(notifySubscribers: false);
                if (rolledBack)
                {
                    _recoveryStore.Clear();
                }
                throw;
            }
        }
        finally
        {
            OperationGate.Release();
        }
    }

    public async Task<int> RecoverPendingAsync()
    {
        DesktopOrganizationRecoveryJournal? journal = await _recoveryStore.LoadAsync();
        if (journal is null)
        {
            return 0;
        }

        int restored = 0;
        foreach (DesktopOrganizationRecoveryItem item in journal.Items.AsEnumerable().Reverse())
        {
            if (!File.Exists(item.DestinationPath))
            {
                continue;
            }

            string restorePath = FileService.GetAvailablePath(item.SourcePath);
            await _fileService.ExecuteTransferPlanAsync(
                [new FileService.FileTransferPlan(item.DestinationPath, restorePath)],
                move: true,
                useShellProgress: false);
            restored++;
        }

        if (journal.CreatedWidgetIds.Count > 0)
        {
            var createdIds = journal.CreatedWidgetIds.ToHashSet(StringComparer.Ordinal);
            _settingsService.Settings.Widgets.RemoveAll(widget =>
                createdIds.Contains(widget.Id));
            _settingsService.Settings.DesktopOrganizationRules.RemoveAll(rule =>
                createdIds.Contains(rule.TargetWidgetId));
            _settingsService.Settings.RecentOrganizationHistory.RemoveAll(entry =>
                string.Equals(entry.Id, journal.TransactionId, StringComparison.Ordinal));
            await _settingsService.SaveAsync(notifySubscribers: false);
        }

        RemoveEmptyCreatedDirectories(
            journal.Items
                .Select(item => Path.GetDirectoryName(item.DestinationPath))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase));
        _recoveryStore.Clear();
        return restored;
    }

    private static void ValidatePlan(DesktopOrganizationPlan plan)
    {
        if (plan.Targets.Count == 0 || plan.EligibleItemCount == 0)
        {
            throw new InvalidOperationException("The desktop organization plan is empty.");
        }

        string desktop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.DesktopPath));
        string storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.StorageRootPath));
        string? storageRoot = Path.GetPathRoot(storage);
        if (!string.IsNullOrWhiteSpace(storageRoot) &&
            string.Equals(
                storage,
                Path.TrimEndingDirectorySeparator(storageRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A drive root cannot be used as the managed storage folder.");
        }

        if (string.Equals(desktop, storage, StringComparison.OrdinalIgnoreCase) ||
            storage.StartsWith($"{desktop}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The managed storage root cannot be the desktop or one of its subfolders.");
        }

        foreach (DesktopOrganizationTargetPlan target in plan.Targets)
        {
            string directory = Path.GetFullPath(target.TargetDirectoryPath);
            if (FileService.PathsOverlap(directory, desktop))
            {
                throw new InvalidOperationException(
                    "An organization target cannot be the desktop or one of its subfolders.");
            }
            if (!directory.StartsWith(
                    $"{storage}{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(directory, storage, StringComparison.OrdinalIgnoreCase))
            {
                // Reused mapped widgets may intentionally live outside the
                // default root. Only new widget destinations are constrained.
                if (target.CreatesWidget)
                {
                    throw new InvalidOperationException("A new organization target is outside the managed storage root.");
                }
            }
        }
    }

    private static void ValidateAvailableSpace(DesktopOrganizationPlan plan)
    {
        foreach (var driveGroup in plan.Targets
                     .SelectMany(target => target.Items.Select(item => new
                     {
                         Target = target.TargetDirectoryPath,
                         item.Size
                     }))
                     .GroupBy(item => Path.GetPathRoot(Path.GetFullPath(item.Target)),
                         StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(driveGroup.Key) ||
                driveGroup.Key.StartsWith(@"\\", StringComparison.Ordinal))
            {
                continue;
            }

            var drive = new DriveInfo(driveGroup.Key);
            long required = driveGroup.Sum(item => item.Size);
            const long safetyMargin = 16L * 1024 * 1024;
            if (drive.AvailableFreeSpace < required + safetyMargin)
            {
                throw new IOException($"There is not enough free space on {drive.Name}.");
            }
        }
    }

    private static void RevalidateSources(DesktopOrganizationPlan plan)
    {
        foreach (DesktopOrganizationFileSnapshot item in plan.Targets.SelectMany(target => target.Items))
        {
            var file = new FileInfo(item.SourcePath);
            if (!file.Exists ||
                file.Length != item.Size ||
                file.LastWriteTimeUtc != item.LastWriteTimeUtc)
            {
                throw new IOException($"The desktop item changed after it was scanned: {item.Name}");
            }
        }
    }

    private List<WidgetConfig> CreateCandidateWidgets(
        DesktopOrganizationPlan plan,
        AppSettings settings)
    {
        var created = new List<WidgetConfig>();
        foreach (DesktopOrganizationTargetPlan target in plan.Targets.Where(target => target.CreatesWidget))
        {
            string managedFolderName = Path.GetRelativePath(plan.StorageRootPath, target.TargetDirectoryPath);
            var bounds = target.PlannedBounds;
            var config = new WidgetConfig
            {
                Id = target.TargetWidgetId,
                Name = target.SuggestedDisplayName,
                IsDefaultTitle = false,
                WidgetKind = WidgetKind.File,
                MappedFolderPath = target.TargetDirectoryPath,
                FollowsDefaultStoragePath = true,
                ManagedFolderName = managedFolderName,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                Width = settings.DefaultWidgetWidth,
                Height = settings.DefaultWidgetHeight,
                X = bounds?.X ?? 100,
                Y = bounds?.Y ?? 100,
                IsVisible = true
            };
            settings.Widgets.Add(config);
            created.Add(config);
        }

        return created;
    }

    private static DesktopOrganizationRecoveryJournal BuildJournal(DesktopOrganizationPlan plan)
    {
        return new DesktopOrganizationRecoveryJournal
        {
            TransactionId = plan.Id,
            CreatedWidgetIds = plan.Targets
                .Where(target => target.CreatesWidget)
                .Select(target => target.TargetWidgetId)
                .ToList(),
            Items = plan.Targets
                .SelectMany(target => target.Items.Select(item => new DesktopOrganizationRecoveryItem
                {
                    SourcePath = item.SourcePath,
                    TargetWidgetId = target.TargetWidgetId,
                    DestinationPath = Path.Combine(target.TargetDirectoryPath, item.Name)
                }))
                .ToList()
        };
    }

    private static void ReserveDestinations(DesktopOrganizationRecoveryJournal journal)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopOrganizationRecoveryItem item in journal.Items)
        {
            item.DestinationPath = FileService.GetAvailablePath(item.DestinationPath, reserved);
        }
    }

    private static void AddRulesForNewTargets(
        DesktopOrganizationPlan plan,
        ICollection<DesktopOrganizationRule> rules)
    {
        foreach (DesktopOrganizationTargetPlan target in plan.Targets.Where(target => target.CreatesWidget))
        {
            string[] sourceCategories = target.Items
                .Select(item => item.CategoryId)
                .Where(categoryId => !string.IsNullOrWhiteSpace(categoryId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            rules.Add(new DesktopOrganizationRule
            {
                TargetWidgetId = target.TargetWidgetId,
                CategoryIds = sourceCategories.Length > 0
                    ? sourceCategories.ToList()
                    : [target.CategoryId]
            });
        }
    }

    private static OrganizationHistoryEntry CreateHistory(
        DesktopOrganizationPlan plan,
        DesktopOrganizationRecoveryJournal journal)
    {
        var targetsById = plan.Targets.ToDictionary(target => target.TargetWidgetId, StringComparer.Ordinal);
        return new OrganizationHistoryEntry
        {
            Id = plan.Id,
            ActionType = OrganizationActionType.DesktopOrganization,
            TransferMode = "Move",
            CanUndo = true,
            WidgetName = "Desktop organization",
            Targets = plan.Targets.Select(target => new OrganizationHistoryTarget
            {
                WidgetId = target.TargetWidgetId,
                WidgetName = target.SuggestedDisplayName,
                DirectoryPath = target.TargetDirectoryPath,
                WasCreated = target.CreatesWidget
            }).ToList(),
            Items = journal.Items.Select(item => new OrganizationHistoryItem
            {
                Name = Path.GetFileName(item.DestinationPath),
                SourcePath = item.SourcePath,
                DestinationPath = item.DestinationPath,
                TargetWidgetId = item.TargetWidgetId,
                TargetWidgetName = targetsById[item.TargetWidgetId].SuggestedDisplayName
            }).ToList()
        };
    }

    private async Task<bool> RollBackMovesAsync(IReadOnlyCollection<FileService.FileTransferResult> completedMoves)
    {
        bool succeeded = true;
        foreach (FileService.FileTransferResult move in completedMoves.Reverse())
        {
            if (!File.Exists(move.DestinationPath))
            {
                continue;
            }

            try
            {
                string restorePath = FileService.GetAvailablePath(move.SourcePath);
                await _fileService.ExecuteTransferPlanAsync(
                    [new FileService.FileTransferPlan(move.DestinationPath, restorePath)],
                    move: true,
                    useShellProgress: false);
            }
            catch
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    private static void RemoveEmptyCreatedDirectories(IEnumerable<string> directories)
    {
        foreach (string directory in directories
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
            }
        }
    }
}
