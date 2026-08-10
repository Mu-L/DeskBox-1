using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoWorkspaceV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"DeskBox-TodoV2-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("Inbox")]
    [InlineData("Tasks")]
    [InlineData("收件箱")]
    [InlineData("任务")]
    public async Task EnsureList_InboxAliasesDoNotCreateDuplicateSystemLists(string alias)
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);

        TodoList list = await workspace.EnsureListAsync(alias);
        TodoWorkspaceSnapshot snapshot = await workspace.LoadSnapshotAsync();

        Assert.Equal(TodoWorkspaceDefaults.InboxListId, list.Id);
        Assert.Single(snapshot.Lists.Where(candidate => !candidate.IsArchived));
    }

    [Fact]
    public async Task FixedRecurrence_CompletesOnlyRequestedOccurrence()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        var series = CreateDailySeries("series", new DateOnly(2026, 8, 1));
        await repository.UpsertTaskAsync(series);

        TodoOccurrence presented = Assert.Single(new TodoRecurrenceExpansionService().Expand(
            [series],
            new DateOnly(2026, 8, 3),
            new DateOnly(2026, 8, 3)));
        await workspace.CompleteTaskAsync(presented.Task);

        TodoWorkspaceSnapshot snapshot = await repository.LoadSnapshotAsync(includeDeleted: true);
        TodoTask persistedSeries = snapshot.Tasks.Single(task => task.Id == "series");
        Assert.Equal(TodoTaskStatus.Open, persistedSeries.Status);
        Assert.Single(snapshot.RecurrenceExceptions);
        IReadOnlyList<TodoOccurrence> occurrences = new TodoRecurrenceExpansionService().Expand(
            snapshot.Tasks,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 4),
            snapshot.RecurrenceExceptions);
        Assert.Equal(4, occurrences.Count);
        Assert.Equal(TodoTaskStatus.Completed, occurrences.Single(item => item.Date == new DateOnly(2026, 8, 3)).Task.Status);
        Assert.Equal(TodoTaskStatus.Open, occurrences.Single(item => item.Date == new DateOnly(2026, 8, 4)).Task.Status);
    }

    [Fact]
    public async Task RepeatedOccurrenceEdits_ReplaceTheSameExceptionInsteadOfNestingSeries()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        DateOnly date = new(2026, 8, 10);
        TodoTask series = CreateDailySeries("series", date);
        series.Recurrence = new TodoRecurrence { Mode = TodoRecurrenceMode.Daily };
        await repository.UpsertTaskAsync(series);

        TodoTask edited = await workspace.ApplyRecurrenceEditAsync(
            series.Id,
            date,
            TodoRecurrenceEditScope.Occurrence,
            task => task.ColorMarker = "yellow");
        string exceptionTaskId = edited.Id;
        edited = await workspace.ApplyRecurrenceEditAsync(
            edited.Id,
            date,
            TodoRecurrenceEditScope.Occurrence,
            task => task.ColorMarker = "pink");
        edited = await workspace.ApplyRecurrenceEditAsync(
            edited.Id,
            date,
            TodoRecurrenceEditScope.Occurrence,
            task => task.ColorMarker = "teal");

        TodoWorkspaceSnapshot snapshot = await repository.LoadSnapshotAsync(includeDeleted: true);
        Assert.Equal(exceptionTaskId, edited.Id);
        Assert.Equal(2, snapshot.Tasks.Count);
        TodoRecurrenceException exception = Assert.Single(snapshot.RecurrenceExceptions);
        Assert.Equal(exceptionTaskId, exception.TaskId);
        TodoTask exceptionTask = snapshot.Tasks.Single(task => task.Id == exceptionTaskId);
        Assert.Equal("teal", exceptionTask.ColorMarker);
        Assert.Null(exceptionTask.RecurrenceRule);
        Assert.Null(exceptionTask.Recurrence);
    }

    [Fact]
    public async Task AfterCompletionTask_OccurrenceEditUpdatesTheConcreteTaskOnly()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        DateOnly date = new(2026, 8, 10);
        var task = new TodoTask
        {
            Id = "after-completion",
            Title = "weekly review",
            DeadlineAt = new DateTimeOffset(2026, 8, 10, 23, 59, 0, TimeSpan.FromHours(8)),
            Recurrence = new TodoRecurrence { Mode = TodoRecurrenceMode.Weekly },
            RecurrenceRule = new TodoRecurrenceRule
            {
                Id = "after-completion-rule",
                Frequency = TodoRecurrenceFrequency.Weekly,
                Anchor = TodoRecurrenceAnchor.Deadline,
                GenerationMode = TodoRecurrenceGenerationMode.AfterCompletion
            }
        };
        await repository.UpsertTaskAsync(task);

        foreach (string marker in new[] { "yellow", "pink", "teal" })
        {
            TodoTask edited = await workspace.ApplyRecurrenceEditAsync(
                task.Id,
                date,
                TodoRecurrenceEditScope.Occurrence,
                candidate => candidate.ColorMarker = marker);
            Assert.Equal(task.Id, edited.Id);
        }

        TodoWorkspaceSnapshot snapshot = await repository.LoadSnapshotAsync(includeDeleted: true);
        Assert.Empty(snapshot.RecurrenceExceptions);
        Assert.Equal("teal", Assert.Single(snapshot.Tasks).ColorMarker);
    }

    [Fact]
    public void RecurrenceExpansion_CollapsesLegacyNestedExceptionChainToLatestTask()
    {
        DateOnly date = new(2026, 8, 10);
        string occurrenceKey = TodoRecurrenceExpansionService.BuildOccurrenceKey("series", date);
        var root = new TodoTask
        {
            Id = "series",
            Title = "1221",
            DeadlineAt = new DateTimeOffset(2026, 8, 10, 23, 59, 0, TimeSpan.FromHours(8)),
            RecurrenceRule = new TodoRecurrenceRule
            {
                Id = "series-rule",
                Frequency = TodoRecurrenceFrequency.Weekly,
                Anchor = TodoRecurrenceAnchor.Deadline,
                GenerationMode = TodoRecurrenceGenerationMode.AfterCompletion
            }
        };
        TodoTask yellow = root.CloneTask();
        yellow.Id = "yellow";
        yellow.ColorMarker = "yellow";
        yellow.RecurrenceSeriesId = root.Id;
        TodoTask pink = yellow.CloneTask();
        pink.Id = "pink";
        pink.ColorMarker = "pink";
        pink.RecurrenceSeriesId = yellow.Id;
        TodoTask teal = pink.CloneTask();
        teal.Id = "teal";
        teal.ColorMarker = "teal";
        teal.RecurrenceSeriesId = pink.Id;

        IReadOnlyList<TodoOccurrence> occurrences = new TodoRecurrenceExpansionService().Expand(
            [root, yellow, pink, teal],
            date,
            date,
            [
                new TodoRecurrenceException
                {
                    SeriesId = root.Id,
                    OccurrenceKey = occurrenceKey,
                    TaskId = yellow.Id
                },
                new TodoRecurrenceException
                {
                    SeriesId = yellow.Id,
                    OccurrenceKey = occurrenceKey,
                    TaskId = pink.Id
                },
                new TodoRecurrenceException
                {
                    SeriesId = pink.Id,
                    OccurrenceKey = occurrenceKey,
                    TaskId = teal.Id
                }
            ]);

        TodoOccurrence occurrence = Assert.Single(occurrences);
        Assert.Equal(root.Id, occurrence.SeriesTaskId);
        Assert.Equal(teal.Id, occurrence.Task.Id);
        Assert.Equal("teal", occurrence.Task.ColorMarker);
    }

    [Fact]
    public async Task FutureRecurrenceEdit_SplitsSeriesAtOccurrence()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        await repository.UpsertTaskAsync(CreateDailySeries("series", new DateOnly(2026, 8, 1)));

        TodoTask future = await workspace.ApplyRecurrenceEditAsync(
            "series",
            new DateOnly(2026, 8, 4),
            TodoRecurrenceEditScope.Future,
            task => task.Title = "new schedule");

        TodoWorkspaceSnapshot snapshot = await repository.LoadSnapshotAsync(includeDeleted: true);
        TodoTask oldSeries = snapshot.Tasks.Single(task => task.Id == "series");
        Assert.Equal(new DateOnly(2026, 8, 3), oldSeries.RecurrenceRule?.EndDate);
        Assert.NotEqual(oldSeries.Id, future.Id);
        Assert.Equal(new DateOnly(2026, 8, 4), future.Schedule?.Date);
        IReadOnlyList<TodoOccurrence> occurrences = new TodoRecurrenceExpansionService().Expand(
            snapshot.Tasks,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 6),
            snapshot.RecurrenceExceptions);
        Assert.Equal("daily standup", occurrences.Single(item => item.Date == new DateOnly(2026, 8, 3)).Task.Title);
        Assert.Equal("new schedule", occurrences.Single(item => item.Date == new DateOnly(2026, 8, 4)).Task.Title);
    }

    [Fact]
    public async Task MovedOccurrence_AppearsOnTargetDateAndSubsequentEditDoesNotLeakTask()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        await repository.UpsertTaskAsync(CreateDailySeries("series", new DateOnly(2026, 8, 1)));

        await workspace.ApplyRecurrenceEditAsync(
            "series",
            new DateOnly(2026, 8, 2),
            TodoRecurrenceEditScope.Occurrence,
            task => task.Schedule = new TodoSchedule
            {
                Date = new DateOnly(2026, 8, 5),
                Time = new TimeOnly(11, 0),
                DurationMinutes = 45
            });

        TodoWorkspaceSnapshot first = await repository.LoadSnapshotAsync(includeDeleted: true);
        IReadOnlyList<TodoOccurrence> oldDate = new TodoRecurrenceExpansionService().Expand(
            first.Tasks,
            new DateOnly(2026, 8, 2),
            new DateOnly(2026, 8, 2),
            first.RecurrenceExceptions);
        Assert.DoesNotContain(oldDate, item => item.Task.PresentedOccurrenceDate == new DateOnly(2026, 8, 2));
        IReadOnlyList<TodoOccurrence> targetDate = new TodoRecurrenceExpansionService().Expand(
            first.Tasks,
            new DateOnly(2026, 8, 5),
            new DateOnly(2026, 8, 5),
            first.RecurrenceExceptions);
        Assert.Contains(targetDate, item =>
            item.Task.PresentedOccurrenceDate == new DateOnly(2026, 8, 2) &&
            item.Task.Schedule?.Time == new TimeOnly(11, 0));

        await workspace.ApplyRecurrenceEditAsync(
            "series",
            new DateOnly(2026, 8, 2),
            TodoRecurrenceEditScope.Occurrence,
            task => task.Schedule!.DurationMinutes = 90);

        TodoWorkspaceSnapshot second = await repository.LoadSnapshotAsync(includeDeleted: true);
        Assert.Equal(2, second.Tasks.Count);
        Assert.Single(second.RecurrenceExceptions);
        Assert.Equal(90, second.Tasks.Single(task => task.Id != "series").Schedule?.DurationMinutes);
    }

    [Fact]
    public async Task CompletingRawFixedSeries_CompletesNextActionableOccurrence()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        TodoTask series = CreateDailySeries("series", today.AddDays(-2));
        await repository.UpsertTaskAsync(series);

        await workspace.CompleteTaskAsync(series);

        TodoWorkspaceSnapshot snapshot = await repository.LoadSnapshotAsync(includeDeleted: true);
        TodoRecurrenceException exception = Assert.Single(snapshot.RecurrenceExceptions);
        Assert.Equal(TodoRecurrenceExpansionService.BuildOccurrenceKey("series", today), exception.OccurrenceKey);
        Assert.Equal(TodoTaskStatus.Open, snapshot.Tasks.Single(task => task.Id == "series").Status);
        Assert.Equal(
            TodoTaskStatus.Completed,
            snapshot.Tasks.Single(task => task.Id == exception.TaskId).Status);
    }

    [Fact]
    public async Task PurgeTasks_IsAtomicAndDoesNotTouchActiveSeries()
    {
        using var repository = CreateRepository();
        await repository.UpsertTaskAsync(CreateDailySeries("active", new DateOnly(2026, 8, 1)));
        await repository.UpsertRecurrenceExceptionAsync(new TodoRecurrenceException
        {
            SeriesId = "active",
            OccurrenceKey = TodoRecurrenceExpansionService.BuildOccurrenceKey("active", new DateOnly(2026, 8, 2)),
            IsCancelled = true
        });
        await repository.UpsertTaskAsync(new TodoTask { Id = "deleted-a", Title = "a" });
        await repository.UpsertTaskAsync(new TodoTask { Id = "deleted-b", Title = "b" });
        await repository.SoftDeleteTaskAsync("deleted-a", DateTimeOffset.UtcNow);
        await repository.SoftDeleteTaskAsync("deleted-b", DateTimeOffset.UtcNow);

        Assert.Equal(0, await repository.PurgeTasksAsync(["active"]));
        Assert.Equal(2, await repository.PurgeTasksAsync(["deleted-a", "deleted-b"]));

        TodoWorkspaceSnapshot snapshot = await repository.LoadSnapshotAsync(includeDeleted: true);
        Assert.Equal("active", Assert.Single(snapshot.Tasks).Id);
        Assert.Single(snapshot.RecurrenceExceptions);
    }

    [Fact]
    public async Task PurgeAndClear_RemoveOnlyManagedAttachmentFiles()
    {
        using var repository = CreateRepository();
        await repository.InitializeAsync();
        Directory.CreateDirectory(repository.AttachmentDirectory);
        string managedDirectory = Path.Combine(repository.AttachmentDirectory, "task");
        Directory.CreateDirectory(managedDirectory);
        string managedFile = Path.Combine(managedDirectory, "managed.txt");
        string linkedFile = Path.Combine(_root, "linked.txt");
        await File.WriteAllTextAsync(managedFile, "managed");
        await File.WriteAllTextAsync(linkedFile, "linked");
        await repository.UpsertTaskAsync(new TodoTask
        {
            Id = "with-attachments",
            Title = "attachments",
            Attachments =
            [
                new TodoAttachment
                {
                    Id = "managed",
                    FilePath = managedFile,
                    DisplayName = "managed.txt",
                    StorageMode = TodoAttachment.ManagedStorageMode
                },
                new TodoAttachment
                {
                    Id = "linked",
                    FilePath = linkedFile,
                    DisplayName = "linked.txt",
                    StorageMode = TodoAttachment.LinkedStorageMode
                }
            ]
        });
        await repository.SoftDeleteTaskAsync("with-attachments", DateTimeOffset.UtcNow);

        Assert.True(await repository.PurgeTaskAsync("with-attachments"));
        Assert.False(File.Exists(managedFile));
        Assert.True(File.Exists(linkedFile));

        string orphanedManagedFile = Path.Combine(repository.AttachmentDirectory, "orphaned.txt");
        await File.WriteAllTextAsync(orphanedManagedFile, "orphaned");
        await repository.ClearAsync();

        Assert.False(File.Exists(orphanedManagedFile));
        Assert.True(File.Exists(linkedFile));
    }

    [Fact]
    public async Task BatchDeleteRestore_ChangesAllTasksInOneRepositoryOperation()
    {
        using var repository = CreateRepository();
        await repository.ReplaceTasksAsync(
            [new TodoTask { Id = "one", Title = "one" }, new TodoTask { Id = "two", Title = "two" }],
            softDeleteMissing: false);

        Assert.Equal(2, await repository.SetTasksDeletedAtAsync(["one", "two"], DateTimeOffset.UtcNow));
        Assert.Empty((await repository.LoadSnapshotAsync()).Tasks);
        Assert.Equal(2, await repository.SetTasksDeletedAtAsync(["one", "two"], null));
        Assert.Equal(2, (await repository.LoadSnapshotAsync()).Tasks.Count);
    }

    [Fact]
    public async Task SectionsTagsAndCompoundQuery_AreUserManageable()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        TodoList list = await workspace.EnsureListAsync("Work");
        TodoSection section = await workspace.EnsureSectionAsync(list.Id, "Launch");
        TodoTag tag = await workspace.EnsureTagAsync("urgent");
        await repository.UpsertTaskAsync(new TodoTask
        {
            Id = "matching",
            Title = "Ship desktop release",
            Notes = "customer launch",
            ListId = list.Id,
            SectionId = section.Id,
            TagIds = [tag.Id],
            Priority = TodoPriority.High
        });
        await repository.UpsertTaskAsync(new TodoTask { Id = "other", Title = "Other" });

        IReadOnlyList<TodoTask> result = await workspace.QueryAsync(new TodoQuery
        {
            SmartView = TodoSmartView.All,
            ListId = list.Id,
            SectionId = section.Id,
            TagIds = [tag.Id],
            MinimumPriority = TodoPriority.Medium,
            SearchText = "launch"
        });
        Assert.Equal("matching", Assert.Single(result).Id);

        await workspace.DeleteTagAsync(tag.Id);
        TodoWorkspaceSnapshot snapshot = await workspace.LoadSnapshotAsync();
        Assert.Empty(snapshot.Tags);
        Assert.Empty(snapshot.Tasks.Single(task => task.Id == "matching").TagIds);
    }

    [Fact]
    public async Task Initialize_QuarantinesCorruptDatabaseAndCreatesUsableWorkspace()
    {
        using var repository = CreateRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(repository.DatabasePath)!);
        await File.WriteAllBytesAsync(repository.DatabasePath, [0x13, 0x37, 0x00, 0x42]);

        await repository.InitializeAsync();
        await repository.UpsertTaskAsync(new TodoTask { Id = "after-recovery", Title = "safe" });

        Assert.True(await SqliteTodoWorkspaceRepository.ValidateDatabaseAsync(repository.DatabasePath));
        Assert.Single((await repository.LoadSnapshotAsync()).Tasks);
        Assert.Single(Directory.GetFiles(
            Path.GetDirectoryName(repository.DatabasePath)!,
            "todo-corrupt-*.db"));
    }

    [Fact]
    public async Task Repository_SerializesConcurrentWrites()
    {
        using var repository = CreateRepository();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            repository.UpsertTaskAsync(new TodoTask
            {
                Id = $"task-{index}",
                Title = $"Task {index}"
            })));

        Assert.Equal(40, (await repository.LoadSnapshotAsync()).Tasks.Count);
    }

    [Fact]
    public async Task Workspace_SerializesConcurrentTagCreation()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);

        TodoTag[] tags = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => workspace.EnsureTagAsync("shared")));

        Assert.Single(tags.Select(tag => tag.Id).Distinct(StringComparer.Ordinal));
        Assert.Single((await workspace.LoadSnapshotAsync()).Tags);
    }

    [Fact]
    public async Task CreateTaskFromLinkedFile_WritesTaskAndAttachmentTogether()
    {
        using var repository = CreateRepository();
        using var workspace = new TodoWorkspaceService(repository, migrator: null);
        Directory.CreateDirectory(_root);
        string filePath = Path.Combine(_root, "release.txt");
        await File.WriteAllTextAsync(filePath, "ready");

        TodoTask created = await workspace.CreateTaskFromLinkedFileAsync(filePath);

        TodoTask persisted = Assert.Single((await workspace.LoadSnapshotAsync()).Tasks);
        Assert.Equal(created.Id, persisted.Id);
        TodoAttachment attachment = Assert.Single(persisted.Attachments);
        Assert.Equal(Path.GetFullPath(filePath), attachment.FilePath);
        Assert.Equal(TodoAttachment.LinkedStorageMode, attachment.StorageMode);
    }

    [Fact]
    public async Task ReplaceTasks_RollsBackWholeTransactionWhenChildWriteFails()
    {
        using var repository = CreateRepository();
        var valid = new TodoTask
        {
            Id = "valid",
            Title = "valid",
            Steps = [new TodoStep { Id = "duplicate-step", Text = "first" }]
        };
        var invalid = new TodoTask
        {
            Id = "invalid",
            Title = "invalid",
            Steps = [new TodoStep { Id = "duplicate-step", Text = "second" }]
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            repository.ReplaceTasksAsync([valid, invalid], softDeleteMissing: false));

        Assert.Empty((await repository.LoadSnapshotAsync(includeDeleted: true)).Tasks);
    }

    [Fact]
    public void ResponsiveResolver_HonorsAllBreakpointsAndPreferences()
    {
        Assert.Equal(TodoWorkspaceLayoutMode.Micro,
            TodoResponsiveLayoutResolver.Resolve(150, 900, TodoResponsivePreference.Auto));
        Assert.Equal(TodoWorkspaceLayoutMode.Compact,
            TodoResponsiveLayoutResolver.Resolve(320, 500, TodoResponsivePreference.Auto));
        Assert.Equal(TodoWorkspaceLayoutMode.Enhanced,
            TodoResponsiveLayoutResolver.Resolve(600, 500, TodoResponsivePreference.Auto));
        Assert.Equal(TodoWorkspaceLayoutMode.Split,
            TodoResponsiveLayoutResolver.Resolve(720, 360, TodoResponsivePreference.Auto));
        Assert.Equal(TodoWorkspaceLayoutMode.ThreePane,
            TodoResponsiveLayoutResolver.Resolve(960, 500, TodoResponsivePreference.Auto));
        Assert.Equal(TodoWorkspaceLayoutMode.Enhanced,
            TodoResponsiveLayoutResolver.Resolve(1200, 900, TodoResponsivePreference.SingleColumn));
        Assert.Equal(TodoWorkspaceLayoutMode.Split,
            TodoResponsiveLayoutResolver.Resolve(500, 500, TodoResponsivePreference.PreferSplit));
        Assert.False(TodoResponsiveLayoutResolver.HasCrossedHysteresis(
            730,
            500,
            TodoResponsivePreference.Auto,
            TodoWorkspaceLayoutMode.Enhanced,
            TodoWorkspaceLayoutMode.Split,
            24));
        Assert.True(TodoResponsiveLayoutResolver.HasCrossedHysteresis(
            744,
            500,
            TodoResponsivePreference.Auto,
            TodoWorkspaceLayoutMode.Enhanced,
            TodoWorkspaceLayoutMode.Split,
            24));
    }

    [Theory]
    [InlineData(367, 401, false, false, 0)]
    [InlineData(367, 247, false, false, 0)]
    [InlineData(700, 400, false, false, 1)]
    [InlineData(700, 500, false, false, 2)]
    [InlineData(900, 650, false, false, 3)]
    [InlineData(600, 500, false, true, 0)]
    [InlineData(700, 180, false, false, -1)]
    [InlineData(560, 500, true, false, 0)]
    public void MonthCellCapacity_UsesTheActualCalendarViewport(
        double hostWidth,
        double hostHeight,
        bool showWeekNumbers,
        bool stacksSelectedDay,
        int expected)
    {
        Assert.Equal(
            expected,
            TodoResponsiveLayoutResolver.ResolveMonthTaskLineCapacity(
                hostWidth,
                hostHeight,
                showWeekNumbers,
                stacksSelectedDay));
    }

    [Fact]
    public async Task IcsSource_ReadsTimedAllDayRecurringAndExcludedEvents()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "calendar.ics");
        await File.WriteAllTextAsync(path,
            "BEGIN:VCALENDAR\r\n" +
            "BEGIN:VEVENT\r\nUID:timed\r\nSUMMARY:Design review\r\nDTSTART:20260810T090000\r\nDTEND:20260810T103000\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:holiday\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20260811\r\nDTEND;VALUE=DATE:20260812\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:daily\r\nSUMMARY:Daily sync\r\nDESCRIPTION:Folded line\r\n continued\r\nDTSTART:20260809T080000\r\nDURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEXDATE:20260811T080000\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n");
        var source = new TodoCalendarSourceSettings
        {
            Id = "calendar",
            Name = "Team",
            SourcePath = path
        };

        IReadOnlyList<TodoCalendarEvent> events = await new IcsTodoCalendarSource().ReadAsync(
            source,
            new DateOnly(2026, 8, 9),
            new DateOnly(2026, 8, 12));

        Assert.Equal(5, events.Count);
        TodoCalendarEvent timed = events.Single(item => item.Id.Contains("timed", StringComparison.Ordinal));
        Assert.Equal(new TimeOnly(9, 0), timed.StartTime);
        Assert.Equal(90, timed.DurationMinutes);
        Assert.True(events.Single(item => item.Id.Contains("holiday", StringComparison.Ordinal)).IsAllDay);
        Assert.DoesNotContain(events, item => item.Title == "Daily sync" && item.Date == new DateOnly(2026, 8, 11));
        Assert.Contains(events, item => item.Description == "Folded linecontinued");
    }

    [Fact]
    public void SettingsMigration_CreatesNestedTodoSettingsAndPreservesUserChoices()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 5,
            Todo = new TodoSettings { SchemaVersion = 0 },
            TodoDefaultFilter = SettingsService.TodoDefaultFilterImportant,
            TodoNewTaskPosition = SettingsService.TodoNewTaskPositionBottom,
            TodoReminderEnabled = false,
            TodoDefaultReminderOffsetMinutes = 30,
            TodoShowCompletedTasks = true,
            AttachmentStorageMode = SettingsService.AttachmentStorageModeCopy
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));
        Assert.Equal(TodoSettings.CurrentSchemaVersion, settings.Todo.SchemaVersion);
        Assert.Equal(TodoSmartView.Important, settings.Todo.QuickRecord.DefaultSmartView);
        Assert.Equal(SettingsService.TodoNewTaskPositionBottom, settings.Todo.QuickRecord.NewTaskPosition);
        Assert.False(settings.Todo.RemindersAndRecurrence.Enabled);
        Assert.Equal(30, settings.Todo.RemindersAndRecurrence.DefaultOffsetMinutes);
        Assert.Equal(TodoCompletedVisibility.Inline, settings.Todo.CompletionAndData.CompletedVisibility);
        Assert.Equal(SettingsService.AttachmentStorageModeCopy, settings.Todo.NotesAndAttachments.AttachmentStorageMode);
    }

    [Fact]
    public void TodayQuery_IncludesTasksCompletedTodayAndPlacesThemLast()
    {
        DateTimeOffset now = new(2026, 8, 9, 18, 0, 0, TimeSpan.FromHours(8));
        var snapshot = new TodoWorkspaceSnapshot
        {
            Tasks =
            [
                new TodoTask
                {
                    Id = "open",
                    Title = "open",
                    Schedule = new TodoSchedule { Date = new DateOnly(2026, 8, 9) }
                },
                new TodoTask
                {
                    Id = "done",
                    Title = "done",
                    Status = TodoTaskStatus.Completed,
                    IsCompleted = true,
                    CompletedAt = now.AddHours(-1)
                },
                new TodoTask
                {
                    Id = "old-done",
                    Title = "old",
                    Status = TodoTaskStatus.Completed,
                    IsCompleted = true,
                    CompletedAt = now.AddDays(-1)
                }
            ]
        };

        IReadOnlyList<TodoTask> result = TodoQueryService.Apply(
            snapshot,
            new TodoQuery { SmartView = TodoSmartView.Today },
            now);
        Assert.Equal(["open", "done"], result.Select(task => task.Id));
    }

    private SqliteTodoWorkspaceRepository CreateRepository() =>
        new(Path.Combine(_root, "workspace"));

    private static TodoTask CreateDailySeries(string id, DateOnly start) => new()
    {
        Id = id,
        Title = "daily standup",
        Schedule = new TodoSchedule
        {
            Date = start,
            Time = new TimeOnly(9, 0),
            TimeZoneId = TimeZoneInfo.Local.Id,
            DurationMinutes = 30
        },
        RecurrenceSeriesId = id,
        RecurrenceRule = new TodoRecurrenceRule
        {
            Id = $"{id}-rule",
            Frequency = TodoRecurrenceFrequency.Daily,
            Anchor = TodoRecurrenceAnchor.Schedule,
            GenerationMode = TodoRecurrenceGenerationMode.FixedSchedule
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
