using DeskBox.Models;

namespace DeskBox.Services;

public sealed class TodoWorkspaceService : IDisposable
{
    private readonly ITodoWorkspaceRepository _repository;
    private readonly TodoWorkspaceMigrator? _migrator;
    private readonly SettingsService? _settingsService;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _structureGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public TodoWorkspaceService(
        ITodoWorkspaceRepository repository,
        SettingsService settingsService)
        : this(repository, new TodoWorkspaceMigrator(repository, settingsService))
    {
        _settingsService = settingsService;
    }

    internal TodoWorkspaceService(
        ITodoWorkspaceRepository repository,
        TodoWorkspaceMigrator? migrator)
    {
        _repository = repository;
        _migrator = migrator;
        _repository.Changed += Repository_Changed;
    }

    public event EventHandler<TodoWorkspaceChangedEventArgs>? Changed;

    public string AttachmentDirectory => _repository.AttachmentDirectory;

    public string DatabasePath => _repository.DatabasePath;

    internal ITodoWorkspaceRepository Repository => _repository;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _repository.InitializeAsync(cancellationToken);
            if (_migrator is not null)
            {
                await _migrator.MigrateAsync(cancellationToken);
            }

            TodoCompletionAndDataSettings? dataSettings =
                _settingsService?.Settings.Todo.CompletionAndData;
            if (dataSettings is { AutoPurgeTrash: true })
            {
                DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-dataSettings.TrashRetentionDays);
                await _repository.PurgeDeletedBeforeAsync(cutoff, cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<TodoWorkspaceSnapshot> LoadSnapshotAsync(
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.LoadSnapshotAsync(includeDeleted, cancellationToken);
    }

    public async Task<TodoTask?> GetTaskAsync(
        string taskId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.GetTaskAsync(taskId, includeDeleted, cancellationToken);
    }

    public async Task SaveTaskAsync(TodoTask task, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.UpsertTaskAsync(task, cancellationToken);
    }

    public async Task SaveTasksAsync(
        IReadOnlyCollection<TodoTask> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        foreach (TodoTask task in tasks)
        {
            task.UpdatedAt = updatedAt;
        }

        await _repository.ReplaceTasksAsync(tasks, softDeleteMissing: false, cancellationToken);
    }

    public async Task<IReadOnlyList<TodoTask>> QueryAsync(
        TodoQuery query,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        TodoWorkspaceSnapshot snapshot = await _repository.LoadSnapshotAsync(
            query.IncludeDeleted,
            cancellationToken);
        return TodoQueryService.Apply(snapshot, query, now ?? DateTimeOffset.Now);
    }

    public async Task<TodoList> EnsureListAsync(
        string? name,
        CancellationToken cancellationToken = default)
    {
        string normalized = string.IsNullOrWhiteSpace(name) ? "Inbox" : name.Trim();
        await _structureGate.WaitAsync(cancellationToken);
        try
        {
            TodoWorkspaceSnapshot snapshot = await LoadSnapshotAsync(false, cancellationToken);
            if (IsInboxAlias(normalized))
            {
                return snapshot.Lists.First(list =>
                    string.Equals(list.Id, TodoWorkspaceDefaults.InboxListId, StringComparison.Ordinal));
            }
            TodoList? existing = snapshot.Lists.FirstOrDefault(list =>
                !list.IsArchived &&
                string.Equals(list.Name, normalized, StringComparison.CurrentCultureIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var list = new TodoList
            {
                Name = normalized,
                SortRank = snapshot.Lists.Count,
                IsSystem = false
            };
            await _repository.UpsertListAsync(list, cancellationToken);
            return list;
        }
        finally
        {
            _structureGate.Release();
        }
    }

    private static bool IsInboxAlias(string name) =>
        name.Equals("Inbox", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Tasks", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("收件箱", StringComparison.Ordinal) ||
        name.Equals("任务", StringComparison.Ordinal);

    public async Task<TodoTag> EnsureTagAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string normalized = name?.Trim().TrimStart('#') ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Todo tag name cannot be empty.", nameof(name));
        }

        await _structureGate.WaitAsync(cancellationToken);
        try
        {
            TodoWorkspaceSnapshot snapshot = await LoadSnapshotAsync(false, cancellationToken);
            TodoTag? existing = snapshot.Tags.FirstOrDefault(tag =>
                string.Equals(tag.Name, normalized, StringComparison.CurrentCultureIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var tag = new TodoTag
            {
                Name = normalized,
                SortRank = snapshot.Tags.Count
            };
            await _repository.UpsertTagAsync(tag, cancellationToken);
            return tag;
        }
        finally
        {
            _structureGate.Release();
        }
    }

    public async Task SaveListAsync(TodoList list, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        await InitializeAsync(cancellationToken);
        await _repository.UpsertListAsync(list, cancellationToken);
    }

    public async Task<TodoSection> EnsureSectionAsync(
        string listId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        string normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Todo section name cannot be empty.", nameof(name));
        }

        await _structureGate.WaitAsync(cancellationToken);
        try
        {
            TodoWorkspaceSnapshot snapshot = await LoadSnapshotAsync(false, cancellationToken);
            TodoSection? existing = snapshot.Sections.FirstOrDefault(section =>
                string.Equals(section.ListId, listId, StringComparison.Ordinal) &&
                string.Equals(section.Name, normalized, StringComparison.CurrentCultureIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var section = new TodoSection
            {
                ListId = listId.Trim(),
                Name = normalized,
                SortRank = snapshot.Sections.Count(item => string.Equals(item.ListId, listId, StringComparison.Ordinal))
            };
            await _repository.UpsertSectionAsync(section, cancellationToken);
            return section;
        }
        finally
        {
            _structureGate.Release();
        }
    }

    public async Task SaveSectionAsync(TodoSection section, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(section);
        await InitializeAsync(cancellationToken);
        await _repository.UpsertSectionAsync(section, cancellationToken);
    }

    public async Task SaveTagAsync(TodoTag tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        await InitializeAsync(cancellationToken);
        await _repository.UpsertTagAsync(tag, cancellationToken);
    }

    public async Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _repository.DeleteTagAsync(tagId, cancellationToken);
    }

    public async Task SaveSavedViewAsync(TodoSavedView savedView, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savedView);
        await InitializeAsync(cancellationToken);
        await _repository.UpsertSavedViewAsync(savedView, cancellationToken);
    }

    public async Task DeleteSavedViewAsync(string savedViewId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _repository.DeleteSavedViewAsync(savedViewId, cancellationToken);
    }

    public async Task<TodoTask> CreateParsedTaskAsync(
        TodoQuickAddResult parsed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        TodoList list = string.IsNullOrWhiteSpace(parsed.ListName)
            ? (await LoadSnapshotAsync(false, cancellationToken)).Lists.First(list =>
                string.Equals(list.Id, TodoWorkspaceDefaults.InboxListId, StringComparison.Ordinal))
            : await EnsureListAsync(parsed.ListName, cancellationToken);
        TodoTask task = await CreateTaskAsync(
            parsed.Title,
            list.Id,
            parsed.Schedule,
            parsed.Priority,
            cancellationToken);
        foreach (string tagName in parsed.TagNames)
        {
            TodoTag tag = await EnsureTagAsync(tagName, cancellationToken);
            task.TagIds.Add(tag.Id);
        }

        if (task.TagIds.Count > 0)
        {
            await SaveTaskAsync(task, cancellationToken);
        }

        return task;
    }

    public async Task<TodoAttachment> AddAttachmentAsync(
        string taskId,
        string sourcePath,
        bool copyToWorkspace,
        CancellationToken cancellationToken = default)
    {
        TodoTask task = await GetTaskAsync(taskId, false, cancellationToken) ??
            throw new KeyNotFoundException($"Todo task '{taskId}' does not exist.");
        string fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath) && !Directory.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Todo attachment source does not exist.", fullSourcePath);
        }

        string targetPath = fullSourcePath;
        string storageMode = TodoAttachment.LinkedStorageMode;
        if (copyToWorkspace && File.Exists(fullSourcePath))
        {
            string taskDirectory = Path.Combine(AttachmentDirectory, task.Id);
            Directory.CreateDirectory(taskDirectory);
            targetPath = GetAvailableAttachmentPath(taskDirectory, Path.GetFileName(fullSourcePath));
            File.Copy(fullSourcePath, targetPath, overwrite: false);
            storageMode = TodoAttachment.ManagedStorageMode;
        }

        var attachment = new TodoAttachment
        {
            FilePath = targetPath,
            DisplayName = Path.GetFileName(fullSourcePath.TrimEnd(Path.DirectorySeparatorChar)),
            Type = Directory.Exists(fullSourcePath) ? "folder" : "file",
            StorageMode = storageMode,
            AddedAt = DateTimeOffset.UtcNow
        };
        task.Attachments.Add(attachment);
        await SaveTaskAsync(task, cancellationToken);
        return attachment;
    }

    public async Task CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _repository.CreateBackupAsync(destinationPath, cancellationToken);
    }

    public async Task<TodoTask> CreateTaskAsync(
        string title,
        string? listId = null,
        TodoSchedule? schedule = null,
        TodoPriority priority = TodoPriority.None,
        CancellationToken cancellationToken = default)
    {
        string normalizedTitle = title?.Trim() ?? string.Empty;
        if (normalizedTitle.Length == 0)
        {
            throw new ArgumentException("Todo title cannot be empty.", nameof(title));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var task = new TodoTask
        {
            Title = normalizedTitle,
            ListId = string.IsNullOrWhiteSpace(listId) ? TodoWorkspaceDefaults.InboxListId : listId.Trim(),
            Schedule = schedule?.Clone(),
            Priority = priority,
            IsImportant = priority == TodoPriority.High,
            CreatedAt = now,
            UpdatedAt = now
        };
        TodoReminderAndRecurrenceSettings? reminderDefaults =
            _settingsService?.Settings.Todo.RemindersAndRecurrence;
        if (reminderDefaults is { Enabled: true, AddDefaultReminder: true } &&
            (schedule?.Time is not null || task.DeadlineAt is not null))
        {
            task.Reminders.Add(new TodoReminderRule
            {
                Target = schedule?.Time is not null
                    ? TodoReminderTarget.Schedule
                    : TodoReminderTarget.Deadline,
                OffsetMinutes = reminderDefaults.DefaultOffsetMinutes
            });
        }
        await InitializeAsync(cancellationToken);
        TodoWorkspaceSnapshot snapshot = await _repository.LoadSnapshotAsync(false, cancellationToken);
        task.SortOrder = snapshot.Tasks.Count;
        await _repository.UpsertTaskAsync(task, cancellationToken);
        return task;
    }

    public async Task<TodoTask> CreateTaskFromLinkedFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Todo attachment source does not exist.", fullPath);
        }

        await InitializeAsync(cancellationToken);
        TodoWorkspaceSnapshot snapshot = await _repository.LoadSnapshotAsync(false, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string fileName = Path.GetFileName(fullPath);
        var task = new TodoTask
        {
            Title = fileName,
            Notes = fullPath,
            ListId = TodoWorkspaceDefaults.InboxListId,
            SortOrder = snapshot.Tasks.Count,
            CreatedAt = now,
            UpdatedAt = now,
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = fullPath,
                    DisplayName = fileName,
                    Type = "file",
                    StorageMode = TodoAttachment.LinkedStorageMode,
                    AddedAt = now
                }
            ]
        };
        await _repository.UpsertTaskAsync(task, cancellationToken);
        return task;
    }

    public async Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.SoftDeleteTaskAsync(taskId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<bool> RestoreTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.RestoreTaskAsync(taskId, cancellationToken);
    }

    public async Task<int> DeleteTasksAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.SetTasksDeletedAtAsync(taskIds, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<int> RestoreTasksAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.SetTasksDeletedAtAsync(taskIds, null, cancellationToken);
    }

    public async Task<TodoTask> ApplyRecurrenceEditAsync(
        string seriesTaskId,
        DateOnly occurrenceDate,
        TodoRecurrenceEditScope scope,
        Action<TodoTask> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesTaskId);
        ArgumentNullException.ThrowIfNull(update);
        TodoWorkspaceSnapshot snapshot = await LoadSnapshotAsync(includeDeleted: true, cancellationToken);
        TodoTask series = snapshot.Tasks.FirstOrDefault(task =>
                              string.Equals(task.Id, seriesTaskId, StringComparison.Ordinal)) ??
                          throw new KeyNotFoundException($"Todo recurrence series '{seriesTaskId}' does not exist.");
        TodoRecurrenceException? owningException = snapshot.RecurrenceExceptions.FirstOrDefault(item =>
            string.Equals(item.TaskId, series.Id, StringComparison.Ordinal));
        if (scope == TodoRecurrenceEditScope.Occurrence && owningException is not null)
        {
            // A single-occurrence override is a concrete task. It must never
            // become a recurrence series itself, even when old compatibility
            // data accidentally restored a recurrence rule on it.
            TodoTask editedExceptionTask = series.CloneTask();
            update(editedExceptionTask);
            editedExceptionTask.RecurrenceRule = null;
            editedExceptionTask.Recurrence = null;
            editedExceptionTask.RecurrenceSeriesId = owningException.SeriesId;
            editedExceptionTask.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.ApplyRecurrenceMutationAsync(
                [editedExceptionTask],
                new TodoRecurrenceException
                {
                    SeriesId = owningException.SeriesId,
                    OccurrenceKey = owningException.OccurrenceKey,
                    TaskId = editedExceptionTask.Id
                },
                cancellationToken: cancellationToken);
            return editedExceptionTask;
        }

        if (series.RecurrenceRule is null ||
            series.RecurrenceRule.GenerationMode == TodoRecurrenceGenerationMode.AfterCompletion)
        {
            update(series);
            await SaveTaskAsync(series, cancellationToken);
            return series;
        }

        if (scope == TodoRecurrenceEditScope.Series)
        {
            update(series);
            await SaveTaskAsync(series, cancellationToken);
            return series;
        }

        string occurrenceKey = TodoRecurrenceExpansionService.BuildOccurrenceKey(series.Id, occurrenceDate);
        TodoRecurrenceException? existingException = snapshot.RecurrenceExceptions.FirstOrDefault(item =>
            string.Equals(item.SeriesId, series.Id, StringComparison.Ordinal) &&
            string.Equals(item.OccurrenceKey, occurrenceKey, StringComparison.Ordinal));
        if (scope == TodoRecurrenceEditScope.Occurrence &&
            existingException?.TaskId is { } existingTaskId &&
            snapshot.Tasks.FirstOrDefault(task => task.Id == existingTaskId) is { } existingExceptionTask)
        {
            TodoTask editedExceptionTask = existingExceptionTask.CloneTask();
            update(editedExceptionTask);
            editedExceptionTask.RecurrenceRule = null;
            editedExceptionTask.Recurrence = null;
            editedExceptionTask.RecurrenceSeriesId = series.Id;
            editedExceptionTask.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.ApplyRecurrenceMutationAsync(
                [editedExceptionTask],
                new TodoRecurrenceException
                {
                    SeriesId = series.Id,
                    OccurrenceKey = occurrenceKey,
                    TaskId = editedExceptionTask.Id
                },
                cancellationToken: cancellationToken);
            return editedExceptionTask;
        }

        TodoOccurrence occurrence = new TodoRecurrenceExpansionService()
            .Expand([series], occurrenceDate, occurrenceDate, snapshot.RecurrenceExceptions)
            .FirstOrDefault() ??
            throw new InvalidOperationException("The requested date is not an occurrence in this series.");

        if (scope == TodoRecurrenceEditScope.Future)
        {
            DateOnly anchor = GetRecurrenceAnchorDate(series);
            if (occurrenceDate <= anchor)
            {
                update(series);
                await SaveTaskAsync(series, cancellationToken);
                return series;
            }

            TodoTask future = occurrence.Task.CloneTask();
            future.Id = Guid.NewGuid().ToString("N");
            future.RecurrenceSeriesId = future.Id;
            future.RecurrenceRule = series.RecurrenceRule.Clone();
            future.RecurrenceRule.Id = Guid.NewGuid().ToString("N");
            future.CreatedAt = DateTimeOffset.UtcNow;
            future.UpdatedAt = future.CreatedAt;
            update(future);
            series.RecurrenceRule.EndDate = occurrenceDate.AddDays(-1);
            series.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.ApplyRecurrenceMutationAsync(
                [series, future],
                clearSeriesId: series.Id,
                clearFromDate: occurrenceDate,
                cancellationToken: cancellationToken);
            return future;
        }

        TodoTask exceptionTask = occurrence.Task.CloneTask();
        exceptionTask.Id = Guid.NewGuid().ToString("N");
        exceptionTask.RecurrenceRule = null;
        exceptionTask.Recurrence = null;
        exceptionTask.RecurrenceSeriesId = series.Id;
        exceptionTask.CreatedAt = DateTimeOffset.UtcNow;
        exceptionTask.UpdatedAt = exceptionTask.CreatedAt;
        update(exceptionTask);
        await _repository.ApplyRecurrenceMutationAsync(
            [exceptionTask],
            new TodoRecurrenceException
            {
                SeriesId = series.Id,
                OccurrenceKey = occurrence.OccurrenceKey,
                TaskId = exceptionTask.Id
            },
            cancellationToken: cancellationToken);
        return exceptionTask;
    }

    public async Task CancelRecurrenceOccurrenceAsync(
        string seriesTaskId,
        DateOnly occurrenceDate,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _repository.UpsertRecurrenceExceptionAsync(new TodoRecurrenceException
        {
            SeriesId = seriesTaskId,
            OccurrenceKey = TodoRecurrenceExpansionService.BuildOccurrenceKey(seriesTaskId, occurrenceDate),
            IsCancelled = true
        }, cancellationToken);
    }

    public async Task RestoreRecurrenceOccurrenceAsync(
        string seriesTaskId,
        DateOnly occurrenceDate,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _repository.RemoveRecurrenceExceptionAsync(
            seriesTaskId,
            TodoRecurrenceExpansionService.BuildOccurrenceKey(seriesTaskId, occurrenceDate),
            cancellationToken);
    }

    public async Task<TodoTask> EndRecurrenceBeforeAsync(
        string seriesTaskId,
        DateOnly occurrenceDate,
        CancellationToken cancellationToken = default)
    {
        TodoTask series = await GetTaskAsync(seriesTaskId, includeDeleted: true, cancellationToken) ??
                          throw new KeyNotFoundException($"Todo recurrence series '{seriesTaskId}' does not exist.");
        if (series.RecurrenceRule is null)
        {
            return series.CloneTask();
        }
        TodoTask original = series.CloneTask();
        series.RecurrenceRule.EndDate = occurrenceDate.AddDays(-1);
        series.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.ApplyRecurrenceMutationAsync([series], cancellationToken: cancellationToken);
        return original;
    }

    public async Task<TodoTask> CompleteTaskAsync(
        TodoTask presentedTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentedTask);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (presentedTask.RecurrenceRule is { GenerationMode: TodoRecurrenceGenerationMode.FixedSchedule })
        {
            DateOnly occurrenceDate;
            if (presentedTask.PresentedOccurrenceDate is { } presentedDate)
            {
                occurrenceDate = presentedDate;
            }
            else
            {
                DateOnly today = DateOnly.FromDateTime(DateTime.Today);
                TodoWorkspaceSnapshot snapshot = await LoadSnapshotAsync(includeDeleted: true, cancellationToken);
                TodoOccurrence? nextOpenOccurrence = new TodoRecurrenceExpansionService()
                    .Expand(
                        snapshot.Tasks,
                        today,
                        today.AddYears(10),
                        snapshot.RecurrenceExceptions)
                    .FirstOrDefault(occurrence =>
                        string.Equals(occurrence.SeriesTaskId, presentedTask.Id, StringComparison.Ordinal) &&
                        occurrence.Task.Status != TodoTaskStatus.Completed &&
                        occurrence.Task.Status != TodoTaskStatus.Cancelled);
                occurrenceDate = nextOpenOccurrence?.Date ??
                    presentedTask.Schedule?.Date ??
                    (presentedTask.DeadlineAt is { } deadline
                        ? DateOnly.FromDateTime(deadline.LocalDateTime)
                        : today);
            }
            return await ApplyRecurrenceEditAsync(
                presentedTask.Id,
                occurrenceDate,
                TodoRecurrenceEditScope.Occurrence,
                task =>
                {
                    task.Status = TodoTaskStatus.Completed;
                    task.IsCompleted = true;
                    task.CompletedAt = now;
                },
                cancellationToken);
        }

        TodoTask editable = await GetTaskAsync(presentedTask.Id, includeDeleted: false, cancellationToken) ??
                            presentedTask.CloneTask();
        editable.Status = TodoTaskStatus.Completed;
        editable.IsCompleted = true;
        editable.CompletedAt = now;
        editable.SnoozedUntil = null;
        editable.UpdatedAt = now;

        if (editable.RecurrenceRule is { GenerationMode: TodoRecurrenceGenerationMode.AfterCompletion } rule)
        {
            TodoTask next = CreateAfterCompletionTask(editable, rule, now);
            editable.GeneratedNextItemId = next.Id;
            await _repository.ApplyRecurrenceMutationAsync([editable, next], cancellationToken: cancellationToken);
        }
        else
        {
            await SaveTaskAsync(editable, cancellationToken);
        }
        return editable;
    }

    public async Task<int> PurgeExpiredTrashAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        int retentionDays = _settingsService?.Settings.Todo.CompletionAndData.TrashRetentionDays ??
                            TodoWorkspaceDefaults.TrashRetentionDays;
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        return await _repository.PurgeDeletedBeforeAsync(cutoff, cancellationToken);
    }

    public async Task<bool> PurgeTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.PurgeTaskAsync(taskId, cancellationToken);
    }

    public async Task<int> PurgeTasksAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _repository.PurgeTasksAsync(taskIds, cancellationToken);
    }

    internal async Task<TodoWidgetData> LoadCompatibilityDataAsync(CancellationToken cancellationToken = default)
    {
        TodoWorkspaceSnapshot snapshot = await LoadSnapshotAsync(false, cancellationToken);
        return new TodoWidgetData
        {
            Version = 3,
            Items = snapshot.Tasks.Select(TodoTaskMapper.ToLegacy).ToList()
        };
    }

    internal async Task SaveCompatibilityDataAsync(
        TodoWidgetData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        await InitializeAsync(cancellationToken);
        TodoWorkspaceSnapshot existingSnapshot = await _repository.LoadSnapshotAsync(true, cancellationToken);
        var existing = existingSnapshot.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var tasks = (data.Items ?? [])
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => TodoTaskMapper.MergeLegacyState(
                existing.GetValueOrDefault(item.Id),
                item))
            .ToList();
        await _repository.ReplaceTasksAsync(tasks, softDeleteMissing: true, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _repository.ClearAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _repository.Changed -= Repository_Changed;
        _initializeGate.Dispose();
        _structureGate.Dispose();
    }

    private void Repository_Changed(object? sender, TodoWorkspaceChangedEventArgs e) =>
        Changed?.Invoke(this, e);

    private static string GetAvailableAttachmentPath(string directory, string fileName)
    {
        string safeName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName;
        string candidate = Path.Combine(directory, safeName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(safeName);
        string extension = Path.GetExtension(safeName);
        for (int index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static DateOnly GetRecurrenceAnchorDate(TodoTask task) =>
        task.RecurrenceRule?.Anchor == TodoRecurrenceAnchor.Deadline && task.DeadlineAt is { } deadline
            ? DateOnly.FromDateTime(deadline.LocalDateTime)
            : task.Schedule?.Date ??
              (task.DeadlineAt is { } fallback
                  ? DateOnly.FromDateTime(fallback.LocalDateTime)
                  : DateOnly.FromDateTime(DateTime.Today));

    private static TodoTask CreateAfterCompletionTask(
        TodoTask completed,
        TodoRecurrenceRule rule,
        DateTimeOffset completedAt)
    {
        TodoTask next = completed.CloneTask();
        next.Id = Guid.NewGuid().ToString("N");
        next.Status = TodoTaskStatus.Open;
        next.IsCompleted = false;
        next.CompletedAt = null;
        next.GeneratedNextItemId = null;
        next.Reminders.ForEach(reminder =>
        {
            reminder.LastNotifiedAt = null;
            reminder.OccurrenceKey = null;
            reminder.SnoozedUntil = null;
            reminder.SnoozeLastNotifiedAt = null;
        });
        int interval = Math.Max(1, rule.Interval);
        DateOnly completedDate = DateOnly.FromDateTime(completedAt.LocalDateTime);
        DateOnly nextDate = rule.Frequency switch
        {
            TodoRecurrenceFrequency.Weekly => completedDate.AddDays(7 * interval),
            TodoRecurrenceFrequency.Monthly => completedDate.AddMonths(interval),
            TodoRecurrenceFrequency.Yearly => completedDate.AddYears(interval),
            _ => completedDate.AddDays(interval)
        };
        if (next.Schedule is { } schedule)
        {
            schedule.Date = nextDate;
        }
        if (next.DeadlineAt is { } deadline)
        {
            TimeOnly time = TimeOnly.FromDateTime(deadline.LocalDateTime);
            DateTime local = nextDate.ToDateTime(time, DateTimeKind.Unspecified);
            next.DeadlineAt = new DateTimeOffset(local, deadline.Offset);
            next.DueDate = next.DeadlineAt;
        }
        next.RecurrenceSeriesId = completed.RecurrenceSeriesId ?? completed.Id;
        next.CreatedAt = completedAt;
        next.UpdatedAt = completedAt;
        return next;
    }
}

public sealed class TodoWorkspaceStoreAdapter(TodoWorkspaceService workspace) : ITodoStore
{
    private readonly TodoWorkspaceService _workspace = workspace;

    public string AttachmentDirectory => _workspace.AttachmentDirectory;

    public TodoWorkspaceService Workspace => _workspace;

    public Task<TodoWidgetData> LoadAsync() => _workspace.LoadCompatibilityDataAsync();

    public Task SaveAsync(TodoWidgetData data) => _workspace.SaveCompatibilityDataAsync(data);

    public Task ClearAsync() => _workspace.ClearAsync();
}
