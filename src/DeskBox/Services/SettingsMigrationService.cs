using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Defines a single settings migration step from one schema version to the next.
/// </summary>
public interface ISettingsMigration
{
    /// <summary>The source schema version this migration upgrades from.</summary>
    int FromVersion { get; }

    /// <summary>Applies the migration to the given settings instance.</summary>
    void Migrate(AppSettings settings);
}

/// <summary>
/// Pipeline that executes registered settings migrations in version order.
/// </summary>
public sealed class SettingsMigrationPipeline
{
    /// <summary>The current schema version that the application expects.</summary>
    public const int CurrentSchemaVersion = 6;

    private readonly List<ISettingsMigration> _migrations = [];

    public SettingsMigrationPipeline()
    {
        // Register migrations in order
        _migrations.Add(new Migration_0_To_1());
        _migrations.Add(new Migration_1_To_2());
        _migrations.Add(new Migration_2_To_3());
        _migrations.Add(new Migration_3_To_4());
        _migrations.Add(new Migration_4_To_5());
        _migrations.Add(new Migration_5_To_6());
    }

    /// <summary>
    /// Runs all necessary migrations to bring the settings from their current
    /// schema version up to <see cref="CurrentSchemaVersion"/>.
    /// Returns true if any migration was applied.
    /// </summary>
    public bool RunMigrations(AppSettings settings)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return false;
        }

        bool anyApplied = false;
        int version = settings.SchemaVersion;

        foreach (var migration in _migrations.OrderBy(m => m.FromVersion))
        {
            if (migration.FromVersion >= version && migration.FromVersion < CurrentSchemaVersion)
            {
                try
                {
                    migration.Migrate(settings);
                    version = migration.FromVersion + 1;
                    anyApplied = true;
                    App.Log($"[SettingsMigration] Applied migration from version {migration.FromVersion} to {version}");
                }
                catch (Exception ex)
                {
                    App.Log($"[SettingsMigration] Migration from {migration.FromVersion} failed: {ex.Message}");
                }
            }
        }

        settings.SchemaVersion = CurrentSchemaVersion;
        return anyApplied;
    }
}

/// <summary>
/// Initial migration: handles legacy settings that predate the schema versioning system.
/// Consolidates scattered migration logic (WidgetCompactSettingsVersion, legacy WidgetCollapsedStyle, etc.)
/// into a single versioned step.
/// </summary>
internal sealed class Migration_0_To_1 : ISettingsMigration
{
    public int FromVersion => 0;

    public void Migrate(AppSettings settings)
    {
        // Legacy migration: ensure WidgetCompactSettingsVersion is at least 1
        // (older settings may have version 0 which used a different compact layout)
        if (settings.WidgetCompactSettingsVersion < 1)
        {
            settings.WidgetCompactSettingsVersion = 1;
        }

        // Legacy migration: normalize any obsolete WidgetCollapsedStyle values
        // The old "Collapsed" style was replaced by "Click" behavior
        if (string.Equals(settings.WidgetCollapseBehavior, "Collapsed", StringComparison.OrdinalIgnoreCase))
        {
            settings.WidgetCollapseBehavior = SettingsService.WidgetCollapseBehaviorClick;
        }

        // Ensure FeatureWidgetEnabledStates dictionary is initialized
        settings.FeatureWidgetEnabledStates ??= [];

        // Ensure Widgets list is initialized
        settings.Widgets ??= [];

        // Ensure widget groups are initialized. Older settings have no groups.
        settings.WidgetGroups ??= [];

        // Ensure DeletedWidgetIds list is initialized
        settings.DeletedWidgetIds ??= [];

        // Ensure RecentOrganizationHistory is initialized
        settings.RecentOrganizationHistory ??= [];
    }
}

/// <summary>
/// Removes the implicit wheel-off override written by the early Tabs
/// compatibility migration. A group whose navigation follows the application
/// default must also be able to follow the application's wheel setting.
/// Explicit navigation styles and future per-group choices remain untouched.
/// </summary>
internal sealed class Migration_1_To_2 : ISettingsMigration
{
    public int FromVersion => 1;

    public void Migrate(AppSettings settings)
    {
        settings.WidgetGroups ??= [];
        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (string.Equals(
                    WidgetGroupNavigationStyles.Normalize(
                        group.NavigationStyle,
                        allowFollowDefault: true),
                    WidgetGroupNavigationStyles.FollowDefault,
                    StringComparison.Ordinal) &&
                group.WheelSwitchEnabled == false)
            {
                group.WheelSwitchEnabled = null;
            }
        }
    }
}

/// <summary>
/// Repairs groups changed from Tabs to FollowDefault after schema version 2.
/// Those groups could retain the compatibility wheel-off value even though
/// the application-level wheel setting was enabled.
/// </summary>
internal sealed class Migration_2_To_3 : ISettingsMigration
{
    public int FromVersion => 2;

    public void Migrate(AppSettings settings)
    {
        settings.WidgetGroups ??= [];
        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (string.Equals(
                    WidgetGroupNavigationStyles.Normalize(
                        group.NavigationStyle,
                        allowFollowDefault: true),
                    WidgetGroupNavigationStyles.FollowDefault,
                    StringComparison.Ordinal) &&
                group.WheelSwitchEnabled == false)
            {
                group.WheelSwitchEnabled = null;
            }
        }
    }
}

/// <summary>
/// Marks the legacy default file-widget experience as already resolved. Existing
/// profiles must never receive a new default widget merely because they currently
/// contain no file widgets. SettingsService resets this flag only when it knows
/// that the settings file did not exist and a genuinely new profile was created.
/// </summary>
internal sealed class Migration_3_To_4 : ISettingsMigration
{
    public int FromVersion => 3;

    public void Migrate(AppSettings settings)
    {
        settings.HasResolvedInitialFileWidgetSetup = true;
    }
}

/// <summary>
/// Replaces presentation-implementation switches with user-facing Quick
/// Capture preferences while preserving the visible behavior of existing
/// profiles.
/// </summary>
internal sealed class Migration_4_To_5 : ISettingsMigration
{
    public int FromVersion => 4;

    public void Migrate(AppSettings settings)
    {
        settings.QuickCaptureDefaultFormat = "Markdown";
        settings.QuickCaptureExistingNoteOpenMode = "Read";
        settings.QuickCaptureDefaultLayout = "Auto";
        settings.QuickCaptureWideEditorView = "Source";
        settings.QuickCaptureListDensity = settings.QuickCaptureItemPreviewLineCount <= 2
            ? "Compact"
            : settings.QuickCaptureItemPreviewLineCount >= 4
                ? "Comfortable"
                : "Standard";
        settings.QuickCaptureTimeDisplay = settings.QuickCaptureShowCreatedTime
            ? "Created"
            : "Updated";
        settings.QuickCaptureTrashEnabled = true;
        settings.QuickCaptureTrashRetentionDays = 30;
        settings.QuickCaptureRevisionHistoryEnabled = true;
        settings.QuickCaptureRevisionRetentionDays = 30;
        settings.QuickCaptureRevisionLimitPerNote = 50;
        settings.QuickCaptureClipboardRetentionDays = 30;
        settings.QuickCaptureClipboardExcludedApps ??= [];
    }
}

/// <summary>
/// Moves the legacy flat Todo presentation switches into stable, nested
/// product settings. Flat values are deliberately retained as read-only
/// migration input for two releases.
/// </summary>
internal sealed class Migration_5_To_6 : ISettingsMigration
{
    public int FromVersion => 5;

    public void Migrate(AppSettings settings)
    {
        settings.Todo ??= new TodoSettings();
        if (settings.Todo.SchemaVersion >= TodoSettings.CurrentSchemaVersion)
        {
            return;
        }

        settings.Todo.QuickRecord.NewTaskPosition = settings.TodoNewTaskPosition;
        settings.Todo.QuickRecord.DefaultSmartView = settings.TodoDefaultFilter switch
        {
            SettingsService.TodoDefaultFilterToday => TodoSmartView.Today,
            SettingsService.TodoDefaultFilterImportant => TodoSmartView.Important,
            SettingsService.TodoDefaultFilterCompleted => TodoSmartView.Completed,
            _ => TodoSmartView.Today
        };
        settings.Todo.RemindersAndRecurrence.Enabled = settings.TodoReminderEnabled;
        settings.Todo.RemindersAndRecurrence.DefaultOffsetMinutes = settings.TodoDefaultReminderOffsetMinutes;
        settings.Todo.NotesAndAttachments.AttachmentStorageMode = settings.AttachmentStorageMode;
        settings.Todo.CompletionAndData.CompletedVisibility = settings.TodoShowCompletedTasks
            ? TodoCompletedVisibility.Inline
            : TodoCompletedVisibility.Collapsed;
        settings.Todo.SchemaVersion = TodoSettings.CurrentSchemaVersion;
    }
}
