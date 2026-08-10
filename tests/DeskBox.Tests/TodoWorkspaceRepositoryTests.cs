using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.Data.Sqlite;

namespace DeskBox.Tests;

public sealed class TodoWorkspaceRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DeskBox-TodoWorkspace-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitializeAndRoundTrip_PreservesRichTaskData()
    {
        using var repository = CreateRepository();
        await repository.InitializeAsync();
        var task = new TodoTask
        {
            Id = "task-1",
            Title = "prepare release",
            ListId = TodoWorkspaceDefaults.InboxListId,
            Priority = TodoPriority.High,
            IsImportant = true,
            Schedule = new TodoSchedule
            {
                Date = new DateOnly(2026, 8, 10),
                Time = new TimeOnly(15, 30),
                TimeZoneId = "China Standard Time",
                DurationMinutes = 45
            },
            DeadlineAt = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.FromHours(8)),
            Notes = "**ship it**",
            TagIds = ["work"],
            Steps = [new TodoStep { Id = "step", Text = "test", SortOrder = 0 }],
            Attachments =
            [
                new TodoAttachment
                {
                    Id = "attachment",
                    FilePath = "D:\\release.txt",
                    DisplayName = "release.txt"
                }
            ],
            Reminders =
            [
                new TodoReminderRule
                {
                    Id = "reminder",
                    Target = TodoReminderTarget.Schedule,
                    OffsetMinutes = 10
                }
            ],
            RecurrenceRule = new TodoRecurrenceRule
            {
                Id = "rule",
                Frequency = TodoRecurrenceFrequency.Weekly,
                WeekDays = [DayOfWeek.Monday, DayOfWeek.Friday]
            }
        };
        await repository.UpsertTagAsync(new TodoTag { Id = "work", Name = "Work" });
        await repository.UpsertTaskAsync(task);

        TodoWorkspaceSnapshot snapshot = await repository.LoadSnapshotAsync();
        TodoTask loaded = Assert.Single(snapshot.Tasks);
        Assert.Equal(task.Title, loaded.Title);
        Assert.Equal(task.Schedule.Date, loaded.Schedule?.Date);
        Assert.Equal(task.Schedule.Time, loaded.Schedule?.Time);
        Assert.Equal(task.DeadlineAt, loaded.DeadlineAt);
        Assert.Equal(TodoPriority.High, loaded.Priority);
        Assert.Equal("work", Assert.Single(loaded.TagIds));
        Assert.Equal("test", Assert.Single(loaded.Steps).Text);
        Assert.Equal("release.txt", Assert.Single(loaded.Attachments).DisplayName);
        Assert.Equal(TodoReminderTarget.Schedule, Assert.Single(loaded.Reminders).Target);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], loaded.RecurrenceRule?.WeekDays);
        Assert.Contains(snapshot.Lists, list => list.Id == TodoWorkspaceDefaults.InboxListId && list.IsSystem);
        Assert.DoesNotContain(snapshot.Lists, list =>
            list.Id == TodoWorkspaceDefaults.LegacyDefaultListId && !list.IsArchived);
    }

    [Fact]
    public async Task DeleteRestoreAndPurge_UsesRecoverableTrash()
    {
        using var repository = CreateRepository();
        await repository.UpsertTaskAsync(new TodoTask { Id = "task", Title = "recover me" });

        Assert.True(await repository.SoftDeleteTaskAsync("task", DateTimeOffset.UtcNow.AddDays(-31)));
        Assert.Empty((await repository.LoadSnapshotAsync()).Tasks);
        Assert.Single((await repository.LoadSnapshotAsync(includeDeleted: true)).Tasks);

        Assert.True(await repository.RestoreTaskAsync("task"));
        Assert.Single((await repository.LoadSnapshotAsync()).Tasks);

        await repository.SoftDeleteTaskAsync("task", DateTimeOffset.UtcNow.AddDays(-31));
        Assert.Equal(1, await repository.PurgeDeletedBeforeAsync(DateTimeOffset.UtcNow.AddDays(-30)));
        Assert.Empty((await repository.LoadSnapshotAsync(includeDeleted: true)).Tasks);
    }

    [Fact]
    public async Task ReplaceTasks_SoftDeletesOnlyMissingActiveTasks()
    {
        using var repository = CreateRepository();
        await repository.ReplaceTasksAsync(
            [new TodoTask { Id = "one", Title = "one" }, new TodoTask { Id = "two", Title = "two" }],
            softDeleteMissing: false);

        await repository.ReplaceTasksAsync(
            [new TodoTask { Id = "one", Title = "updated" }],
            softDeleteMissing: true);

        TodoWorkspaceSnapshot active = await repository.LoadSnapshotAsync();
        Assert.Equal("updated", Assert.Single(active.Tasks).Title);
        TodoWorkspaceSnapshot all = await repository.LoadSnapshotAsync(includeDeleted: true);
        Assert.Equal(2, all.Tasks.Count);
        Assert.NotNull(all.Tasks.Single(task => task.Id == "two").DeletedAt);
    }

    [Fact]
    public async Task Schema2_MergesLegacyTasksListIntoInbox()
    {
        string workspaceRoot = Path.Combine(_root, "schema-2-workspace");
        string databasePath;
        using (var initial = new SqliteTodoWorkspaceRepository(workspaceRoot))
        {
            await initial.InitializeAsync();
            databasePath = initial.DatabasePath;
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO todo_lists(id, name, sort_rank, is_system, is_archived)
                VALUES('tasks', 'Tasks', 1, 1, 0);
                INSERT INTO todo_sections(id, list_id, name, sort_rank, is_archived)
                VALUES('legacy-section', 'tasks', 'Legacy', 0, 0);
                INSERT INTO todo_tasks(id, title, list_id, section_id, created_at, updated_at)
                VALUES('legacy-list-task', 'legacy list task', 'tasks', 'legacy-section',
                       '2026-08-09T00:00:00.0000000+00:00',
                       '2026-08-09T00:00:00.0000000+00:00');
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var migrated = new SqliteTodoWorkspaceRepository(workspaceRoot);
        TodoWorkspaceSnapshot snapshot = await migrated.LoadSnapshotAsync();

        Assert.Equal(TodoWorkspaceDefaults.InboxListId, Assert.Single(snapshot.Tasks).ListId);
        Assert.Equal(TodoWorkspaceDefaults.InboxListId, Assert.Single(snapshot.Sections).ListId);
        Assert.True(snapshot.Lists.Single(list => list.Id == TodoWorkspaceDefaults.LegacyDefaultListId).IsArchived);
    }

    [Fact]
    public async Task QueryToday_GroupsOverdueCarryOverScheduleAndDeadline()
    {
        using var repository = CreateRepository();
        DateTimeOffset now = new(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8));
        await repository.ReplaceTasksAsync(
        [
            new TodoTask { Id = "overdue", Title = "overdue", DeadlineAt = now.AddDays(-1) },
            new TodoTask { Id = "carry", Title = "carry", Schedule = new TodoSchedule { Date = new DateOnly(2026, 8, 8) } },
            new TodoTask { Id = "planned", Title = "planned", Schedule = new TodoSchedule { Date = new DateOnly(2026, 8, 9) } },
            new TodoTask { Id = "due", Title = "due", DeadlineAt = now.AddHours(5) },
            new TodoTask { Id = "future", Title = "future", Schedule = new TodoSchedule { Date = new DateOnly(2026, 8, 10) } }
        ], false);
        var queryService = new TodoQueryService(repository);

        IReadOnlyList<TodoTask> result = await queryService.QueryAsync(
            new TodoQuery { SmartView = TodoSmartView.Today },
            now);

        Assert.Equal(["overdue", "carry", "planned", "due"], result.Select(task => task.Id));
    }

    [Fact]
    public async Task Migration_IsIdempotentAndCreatesLegacyBackup()
    {
        string workspaceRoot = Path.Combine(_root, "workspace");
        string legacyRoot = Path.Combine(_root, "widgets");
        string backupRoot = Path.Combine(_root, "backups");
        string widgetRoot = Path.Combine(legacyRoot, "widget-1");
        Directory.CreateDirectory(widgetRoot);
        var data = new TodoWidgetData
        {
            Version = 3,
            Items =
            [
                new TodoItem
                {
                    Id = "legacy-task",
                    Text = "legacy",
                    IsImportant = true,
                    DueDate = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.FromHours(8))
                }
            ]
        };
        await File.WriteAllTextAsync(
            Path.Combine(widgetRoot, "todo.json"),
            JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        using var repository = new SqliteTodoWorkspaceRepository(workspaceRoot);
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var migrator = new TodoWorkspaceMigrator(repository, settings, legacyRoot, backupRoot);
        TodoWorkspaceMigrationResult first = await migrator.MigrateAsync();
        TodoWorkspaceMigrationResult second = await migrator.MigrateAsync();

        Assert.Equal(1, first.ImportedSources);
        Assert.Equal(1, first.ImportedTasks);
        Assert.Equal(0, second.ImportedSources);
        TodoTask task = Assert.Single((await repository.LoadSnapshotAsync()).Tasks);
        Assert.Equal(TodoPriority.High, task.Priority);
        Assert.Equal(task.DueDate, task.DeadlineAt);
        Assert.True(File.Exists(Path.Combine(first.BackupDirectory!, "widget-1", "todo.json")));
    }

    private SqliteTodoWorkspaceRepository CreateRepository() =>
        new(Path.Combine(_root, "workspace"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
