using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuickCapturePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void MarkdownProjection_StripsSyntaxAndTogglesTasks()
    {
        var service = new QuickCaptureMarkdownService();
        const string source = "# 标题\n\n**重点** 与 [链接](https://example.com)\n- [ ] 任务";

        string plain = service.ToPlainText(source, QuickCaptureContentFormat.Markdown);
        bool toggled = service.TryToggleTask(source, 0, out string updated);

        Assert.Contains("标题", plain);
        Assert.Contains("重点", plain);
        Assert.DoesNotContain("**", plain);
        Assert.True(toggled);
        Assert.Contains("- [x] 任务", updated);
    }

    [Fact]
    public void MarkdownTaskToggle_UsesParsedSourceSpanAndIgnoresCodeBlocks()
    {
        var service = new QuickCaptureMarkdownService();
        const string source = "```md\n- [ ] 代码示例\n```\n\n- [ ] 真正任务";

        bool toggled = service.TryToggleTask(source, 0, out string updated);

        Assert.True(toggled);
        Assert.Contains("```md\n- [ ] 代码示例\n```", updated);
        Assert.EndsWith("- [x] 真正任务", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownPreview_BlocksUnsafeProtocolsAndReferenceImagesButKeepsSourceUntouched()
    {
        Directory.CreateDirectory(_root);
        string attachmentPath = Path.Combine(_root, "local image.png");
        File.WriteAllBytes(attachmentPath, [1, 2, 3]);
        var attachment = new TodoAttachment
        {
            Id = "owned",
            FilePath = attachmentPath,
            DisplayName = "local image.png",
            Type = "image"
        };
        const string source = "[安全](https://example.com) [危险](javascript:alert(1)) " +
            "![远程][remote] ![本地](deskbox-attachment://owned)\n\n" +
            "[remote]: https://example.com/tracker.png";
        var service = new QuickCaptureMarkdownService();

        string preview = service.SanitizeForPreview(source, [attachment], allowRemoteImages: false);
        string html = service.ToHtml(
            source,
            QuickCaptureContentFormat.Markdown,
            [attachment],
            allowRemoteImages: false);

        Assert.Contains("https://example.com", preview);
        Assert.DoesNotContain("javascript:", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("![远程][remote]", preview, StringComparison.Ordinal);
        Assert.Contains("远程图片已阻止", preview);
        Assert.Contains(new Uri(attachmentPath).AbsoluteUri, preview);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("javascript:alert", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_FirstLoadMigratesLegacyJsonWithoutInterpretingItAsMarkdown()
    {
        Directory.CreateDirectory(_root);
        var store = new QuickCaptureStore(_root);
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Version = 3,
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "legacy",
                    Body = "# literal heading marker",
                    ContentFormat = QuickCaptureContentFormat.Markdown,
                    CreatedAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z")
                }
            ]
        });
        var repository = new SqliteQuickCaptureRepository(store);

        QuickCaptureStoreData migrated = await repository.LoadAsync();

        QuickCaptureItem item = Assert.Single(migrated.Items);
        Assert.Equal(QuickCaptureContentFormat.PlainText, item.ContentFormat);
        Assert.True(File.Exists(repository.DatabasePath));
        Assert.True(File.Exists(store.StorePath + ".migrated.bak"));
        Assert.True(await SqliteQuickCaptureRepository.ValidateDatabaseAsync(repository.DatabasePath));
    }

    [Fact]
    public async Task Service_PersistsMarkdownSearchDraftRevisionTrashAndRestore()
    {
        Directory.CreateDirectory(_root);
        var store = new QuickCaptureStore(_root);
        var repository = new SqliteQuickCaptureRepository(store);
        var service = new QuickCaptureService(store, repository);

        QuickCaptureItem created = await service.AddDetailedItemAsync(
            "计划",
            "# 今日\n\n- [ ] 完成迁移",
            QuickCaptureAppearancePreset.Paper,
            QuickCaptureContentFormat.Markdown);
        await service.SetTagsAsync(created.Id, ["工作", "迁移"]);
        await service.SaveDraftAsync(
            created.Id,
            "计划",
            "# 今日\n\n- [x] 完成迁移",
            QuickCaptureContentFormat.Markdown);
        long? revisionId = await service.CreateRevisionAsync(created.Id);

        IReadOnlyList<QuickCaptureSearchHit> hits = await service.SearchAsync("完成");
        QuickCaptureDraft? draft = await service.GetDraftAsync(created.Id);
        IReadOnlyList<QuickCaptureRevision> revisions = await service.GetRevisionsAsync(created.Id);
        QuickCaptureDeletedItemSnapshot? deleted = await service.DeleteItemAsync(created.Id);
        IReadOnlyList<QuickCaptureItem> trash = await service.GetTrashAsync();
        bool restored = await service.RestoreTrashItemAsync(created.Id);

        Assert.Single(hits);
        Assert.Equal(created.Id, hits[0].Item.Id);
        Assert.NotNull(draft);
        Assert.NotNull(revisionId);
        Assert.Contains(revisions, revision => revision.Id == revisionId);
        Assert.NotNull(deleted);
        Assert.Contains(trash, item => item.Id == created.Id && item.IsDeleted);
        Assert.True(restored);

        var reloaded = new QuickCaptureService(new QuickCaptureStore(_root));
        QuickCaptureItem saved = Assert.Single((await reloaded.GetDataAsync()).Items);
        Assert.Equal(QuickCaptureContentFormat.Markdown, saved.ContentFormat);
        Assert.Contains("迁移", saved.Tags);
    }

    [Fact]
    public async Task Repository_BackupCreatesIntegrityCheckedSnapshot()
    {
        Directory.CreateDirectory(_root);
        var store = new QuickCaptureStore(_root);
        var service = new QuickCaptureService(store);
        await service.AddItemAsync("backup note");
        var repository = new SqliteQuickCaptureRepository(store);
        await repository.LoadAsync();
        string backupPath = Path.Combine(_root, "backup", "quick-capture.db");

        await repository.CreateBackupAsync(backupPath);

        Assert.True(await SqliteQuickCaptureRepository.ValidateDatabaseAsync(backupPath));
    }

    [Fact]
    public async Task Service_PreservesMarkdownWhitespaceAcrossReload()
    {
        Directory.CreateDirectory(_root);
        const string source = "\n    缩进代码\n\n尾部空行\n\n";
        var service = new QuickCaptureService(new QuickCaptureStore(_root));

        QuickCaptureItem created = await service.AddDetailedItemAsync(
            "无损",
            source,
            QuickCaptureAppearancePreset.Default,
            QuickCaptureContentFormat.Markdown);
        var reloaded = new QuickCaptureService(new QuickCaptureStore(_root));
        QuickCaptureItem saved = Assert.Single((await reloaded.GetDataAsync()).Items);

        Assert.Equal(created.Id, saved.Id);
        Assert.Equal(source, saved.Body);
    }

    [Fact]
    public async Task Service_ImportsMarkdownAttachmentsAndPermanentDeleteRemovesManagedCopy()
    {
        Directory.CreateDirectory(_root);
        string importDirectory = Directory.CreateDirectory(Path.Combine(_root, "import")).FullName;
        string imagePath = Path.Combine(importDirectory, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);
        string markdownPath = Path.Combine(importDirectory, "plan.md");
        await File.WriteAllTextAsync(markdownPath, "# 计划\n\n![图](diagram.png)\n");
        var service = new QuickCaptureService(new QuickCaptureStore(Path.Combine(_root, "data")));

        QuickCaptureItem? imported = await service.ImportMarkdownFileAsync(markdownPath);

        Assert.NotNull(imported);
        TodoAttachment attachment = Assert.Single(imported!.Attachments);
        Assert.True(attachment.IsManagedCopy);
        Assert.True(File.Exists(attachment.FilePath));
        Assert.Contains($"deskbox-attachment://{attachment.Id}", imported.Body);

        Assert.NotNull(await service.DeleteItemAsync(imported.Id));
        Assert.True(await service.DeletePermanentlyAsync(imported.Id));
        Assert.False(File.Exists(attachment.FilePath));
        Assert.Empty(await service.GetTrashAsync());
    }

    [Fact]
    public async Task Service_DeletesManagedAttachmentOnlyAfterLastNoteReferenceIsRemoved()
    {
        Directory.CreateDirectory(_root);
        string dataRoot = Directory.CreateDirectory(Path.Combine(_root, "shared-reference")).FullName;
        string sharedPath = Path.Combine(dataRoot, "shared.bin");
        await File.WriteAllBytesAsync(sharedPath, [1, 2, 3, 4]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuickCaptureItem CreateItem(string id) => new()
        {
            Id = id,
            Body = id,
            ContentFormat = QuickCaptureContentFormat.Markdown,
            CreatedAt = now,
            UpdatedAt = now,
            Attachments =
            [
                new TodoAttachment
                {
                    Id = $"{id}-attachment",
                    FilePath = sharedPath,
                    DisplayName = "shared.bin",
                    StorageMode = TodoAttachment.ManagedStorageMode,
                    Type = "file"
                }
            ]
        };
        var store = new QuickCaptureStore(dataRoot);
        var repository = new SqliteQuickCaptureRepository(store);
        await repository.SaveAsync(new QuickCaptureStoreData
        {
            Items = [CreateItem("first"), CreateItem("second")]
        });
        var service = new QuickCaptureService(store, repository);

        Assert.NotNull(await service.DeleteItemAsync("first"));
        Assert.True(await service.DeletePermanentlyAsync("first"));
        Assert.True(File.Exists(sharedPath));

        Assert.NotNull(await service.DeleteItemAsync("second"));
        Assert.True(await service.DeletePermanentlyAsync("second"));
        Assert.False(File.Exists(sharedPath));
    }

    [Fact]
    public async Task Repository_SearchFindsTargetAmongTenThousandNotes()
    {
        Directory.CreateDirectory(_root);
        var store = new QuickCaptureStore(Path.Combine(_root, "search-scale"));
        var repository = new SqliteQuickCaptureRepository(store);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<QuickCaptureItem> notes = Enumerable.Range(0, 10_000)
            .Select(index => new QuickCaptureItem
            {
                Id = $"note-{index:D5}",
                Title = $"Note {index}",
                Body = index == 8_765
                    ? "release sentinel-tangerine acceptance target"
                    : $"ordinary searchable content {index}",
                ContentFormat = QuickCaptureContentFormat.PlainText,
                SortOrder = index,
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToList();
        await repository.SaveAsync(new QuickCaptureStoreData { Items = notes });

        IReadOnlyList<QuickCaptureSearchHit> hits = await repository.SearchAsync(
            "sentinel-tangerine",
            limit: 10);

        QuickCaptureSearchHit hit = Assert.Single(hits);
        Assert.Equal("note-08765", hit.Item.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
