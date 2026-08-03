using DeskBox.Services;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Tests;

public sealed class UsnJournalChangeReducerTests
{
    [Fact]
    public void Snapshot_PreservesMultipleLinksForOneFrn()
    {
        var reducer = new UsnJournalChangeReducer("C:");
        reducer.ReplaceSnapshot(
        [
            Record(10, 5, "Users", isDirectory: true),
            Record(11, 5, "Archive", isDirectory: true),
            Record(20, 10, "first.txt"),
            Record(20, 11, "second.txt")
        ]);

        Assert.Equal(
            new[] { @"C:\Archive\second.txt", @"C:\Users\first.txt" },
            reducer.ResolvePaths(20).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void ReplaceHardLinks_AddsAndRemovesIndividualPathsAtomically()
    {
        UsnJournalChangeReducer reducer = CreateReducer();
        UsnJournalRecord anchor = Record(20, 10, "before.txt");

        UsnJournalChangeImpact added = reducer.Apply(
        [
            new UsnJournalChange(
                UsnJournalChangeKind.ReplaceHardLinks,
                anchor,
                [Record(20, 10, "one.txt"), Record(20, 11, "two.txt")])
        ]);

        Assert.Contains(@"C:\Users\before.txt", added.RemovedPaths);
        Assert.Equal(2, reducer.ResolvePaths(20).Count());

        UsnJournalChangeImpact removed = reducer.Apply(
        [
            new UsnJournalChange(
                UsnJournalChangeKind.ReplaceHardLinks,
                anchor,
                [Record(20, 11, "two.txt")])
        ]);

        Assert.Contains(@"C:\Users\one.txt", removed.RemovedPaths);
        Assert.Equal(new[] { @"C:\Archive\two.txt" }, reducer.ResolvePaths(20).ToArray());
    }

    [Fact]
    public void CheckpointRestore_RollsBackCapacityRejectedHardLinkPlan()
    {
        UsnJournalChangeReducer reducer = CreateReducer();
        UsnJournalChange[] changes =
        [
            new UsnJournalChange(
                UsnJournalChangeKind.ReplaceHardLinks,
                Record(20, 10, "before.txt"),
                [Record(20, 10, "one.txt"), Record(20, 11, "two.txt")])
        ];
        UsnJournalChangeReducer.Checkpoint checkpoint = reducer.CreateCheckpoint(changes);

        reducer.Apply(changes);
        reducer.Restore(checkpoint);

        Assert.Equal(new[] { @"C:\Users\before.txt" }, reducer.ResolvePaths(20).ToArray());
    }

    [Fact]
    public void MovingDirectory_UpdatesOnlyTheHardLinkUnderThatDirectory()
    {
        UsnJournalChangeReducer reducer = CreateReducer();
        reducer.Apply(
        [
            new UsnJournalChange(
                UsnJournalChangeKind.ReplaceHardLinks,
                Record(20, 10, "before.txt"),
                [Record(20, 30, "inside.txt"), Record(20, 11, "outside.txt")])
        ]);

        reducer.Apply([Change(UsnJournalChangeKind.RenameOld, 30, 10, "Folder", isDirectory: true)]);
        reducer.Apply([Change(UsnJournalChangeKind.RenameNew, 30, 11, "Moved", isDirectory: true)]);

        Assert.Equal(
            new[] { @"C:\Archive\Moved\inside.txt", @"C:\Archive\outside.txt" },
            reducer.ResolvePaths(20).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void DeletingDirectory_PreservesHardLinkOutsideDeletedSubtree()
    {
        UsnJournalChangeReducer reducer = CreateReducer();
        reducer.Apply(
        [
            new UsnJournalChange(
                UsnJournalChangeKind.ReplaceHardLinks,
                Record(20, 10, "before.txt"),
                [Record(20, 30, "inside.txt"), Record(20, 11, "outside.txt")])
        ]);

        reducer.Apply([Change(UsnJournalChangeKind.Delete, 30, 10, "Folder", isDirectory: true)]);

        Assert.Equal(new[] { @"C:\Archive\outside.txt" }, reducer.ResolvePaths(20).ToArray());
    }

    [Theory]
    [InlineData(500_000, 1, 1, 500_000, true)]
    [InlineData(500_000, 0, 1, 500_000, false)]
    [InlineData(10, 11, 0, 500_000, false)]
    public void ProjectedCapacity_IsCheckedBeforeMutation(
        int current,
        int removed,
        int added,
        int maximum,
        bool expected)
    {
        Assert.Equal(
            expected,
            UsnJournalIndexService.IsProjectedEntryCountWithinCapacity(current, removed, added, maximum));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void EnumerationBatchPermission_RequiresOpenPauseGateAndCurrentSession(
        bool pauseGateSet,
        bool cancelled,
        bool expected)
    {
        Assert.Equal(
            expected,
            UsnJournalIndexService.CanProcessEnumerationBatch(4, 4, true, cancelled, pauseGateSet));
    }

    [Theory]
    [InlineData(38, 0u, 512u, true, 0)]
    [InlineData(234, 1024u, 512u, true, 1)]
    [InlineData(234, 512u, 512u, true, 2)]
    [InlineData(5, 0u, 512u, true, 2)]
    public void HardLinkEnumerationPolicy_HandlesEofGrowthAndFailure(
        int error,
        uint required,
        uint capacity,
        bool allowComplete,
        int expected)
    {
        Assert.Equal(
            expected,
            (int)UsnJournalIndexService.GetHardLinkEnumerationAction(error, required, capacity, allowComplete));
    }

    [Theory]
    [InlineData(4L, 4L, true, false)]
    [InlineData(4L, 5L, true, false)]
    [InlineData(4L, 4L, false, true)]
    [InlineData(4L, 5L, false, false)]
    public void SessionCurrent_RequiresEpochEnabledAndUncancelled(
        long expected,
        long current,
        bool enabled,
        bool cancelled)
    {
        Assert.Equal(
            expected == current && enabled && !cancelled,
            UsnJournalIndexService.IsSessionCurrent(expected, current, enabled, cancelled));
    }

    [Fact]
    public void MalformedJournalRecord_DoesNotAdvanceCursor()
    {
        IntPtr buffer = CreateJournalBuffer(nextUsn: 200, majorVersion: 3);
        try
        {
            bool parsed = UsnJournalIndexService.TryParseJournalBatch(
                buffer,
                8 + 80,
                currentCursor: 100,
                out long nextCursor,
                out IReadOnlyList<UsnJournalChange> changes);

            Assert.False(parsed);
            Assert.Equal(100, nextCursor);
            Assert.Empty(changes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void TruncatedJournalName_DoesNotAdvanceCursor()
    {
        IntPtr buffer = CreateJournalBuffer(nextUsn: 200, majorVersion: 2, recordLength: 64, declaredNameBytes: 12);
        try
        {
            Assert.False(UsnJournalIndexService.TryParseJournalBatch(
                buffer,
                8 + 64,
                currentCursor: 100,
                out long nextCursor,
                out _));
            Assert.Equal(100, nextCursor);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ValidJournalRecord_ParsesAndAdvancesCursor()
    {
        IntPtr buffer = CreateJournalBuffer(nextUsn: 200, majorVersion: 2);
        try
        {
            Assert.True(UsnJournalIndexService.TryParseJournalBatch(
                buffer,
                8 + 80,
                currentCursor: 100,
                out long nextCursor,
                out IReadOnlyList<UsnJournalChange> changes));
            Assert.Equal(200, nextCursor);
            Assert.Single(changes);
            Assert.Equal("file.txt", changes[0].Record.Name);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void HardLinkReason_RequestsFullLinkReplacement()
    {
        IntPtr buffer = CreateJournalBuffer(nextUsn: 200, majorVersion: 2, reason: 0x00010000);
        try
        {
            Assert.True(UsnJournalIndexService.TryParseJournalBatch(
                buffer,
                8 + 80,
                currentCursor: 100,
                out _,
                out IReadOnlyList<UsnJournalChange> changes));
            Assert.Equal(UsnJournalChangeKind.ReplaceHardLinks, Assert.Single(changes).Kind);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(7ul, 120L, 7ul, 100L, 150L, true)]
    [InlineData(7ul, 120L, 8ul, 100L, 150L, false)]
    [InlineData(7ul, 99L, 7ul, 100L, 150L, false)]
    [InlineData(7ul, 151L, 7ul, 100L, 150L, false)]
    public void JournalCursorValidity_DetectsReplacementAndTruncation(
        ulong expectedId,
        long cursor,
        ulong actualId,
        long lowestValid,
        long nextUsn,
        bool expected)
    {
        Assert.Equal(
            expected,
            UsnJournalIndexService.IsJournalCursorValid(
                expectedId,
                cursor,
                actualId,
                lowestValid,
                nextUsn));
    }

    [Fact]
    public void RenameOldAndNewAcrossBatches_ReplacesTheOldPath()
    {
        var reducer = CreateReducer();

        UsnJournalChangeImpact oldImpact = reducer.Apply(
        [
            Change(UsnJournalChangeKind.RenameOld, 20, 10, "before.txt")
        ]);
        UsnJournalChangeImpact newImpact = reducer.Apply(
        [
            Change(UsnJournalChangeKind.RenameNew, 20, 10, "after.txt")
        ]);

        Assert.False(oldImpact.Changed);
        Assert.Contains(@"C:\Users\before.txt", newImpact.RemovedPaths);
        Assert.Contains((ulong)20, newImpact.UpsertFileReferenceNumbers);
        Assert.Equal(@"C:\Users\after.txt", reducer.ResolvePath(20));
        Assert.Equal(0, reducer.PendingRenameCount);
    }

    [Fact]
    public void MovingDirectory_RebuildsItsWholeSubtree()
    {
        var reducer = CreateReducer();

        reducer.Apply([Change(UsnJournalChangeKind.RenameOld, 30, 10, "Folder", isDirectory: true)]);
        UsnJournalChangeImpact impact = reducer.Apply(
        [
            Change(UsnJournalChangeKind.RenameNew, 30, 11, "Moved", isDirectory: true)
        ]);

        Assert.Contains(@"C:\Users\Folder", impact.RemovedPaths);
        Assert.Contains((ulong)30, impact.RebuildDirectoryReferenceNumbers);
        Assert.Equal(@"C:\Archive\Moved", reducer.ResolvePath(30));
        Assert.Equal(@"C:\Archive\Moved\child.txt", reducer.ResolvePath(31));
        Assert.Equal(new ulong[] { 30, 31 }, reducer.EnumerateSubtreeFrns(30).Order().ToArray());
    }

    [Fact]
    public void DeletingDirectory_RemovesOldPathAndAllDescendantRecords()
    {
        var reducer = CreateReducer();

        UsnJournalChangeImpact impact = reducer.Apply(
        [
            Change(UsnJournalChangeKind.Delete, 30, 10, "Folder", isDirectory: true)
        ]);

        Assert.Contains(@"C:\Users\Folder", impact.RemovedPaths);
        Assert.False(reducer.Records.ContainsKey(30));
        Assert.False(reducer.Records.ContainsKey(31));
        Assert.Null(reducer.ResolvePath(31));
    }

    [Fact]
    public void LaterEventForSameFrn_DiscardsUnpairedRenameOld()
    {
        var reducer = CreateReducer();
        reducer.Apply([Change(UsnJournalChangeKind.RenameOld, 20, 10, "before.txt")]);

        reducer.Apply([Change(UsnJournalChangeKind.Upsert, 20, 10, "before.txt")]);

        Assert.Equal(0, reducer.PendingRenameCount);
        Assert.Equal(@"C:\Users\before.txt", reducer.ResolvePath(20));
    }

    [Fact]
    public void UnpairedRenameCache_IsBounded()
    {
        var reducer = CreateReducer();
        for (ulong frn = 1000; frn < 6000; frn++)
        {
            reducer.Apply([Change(UsnJournalChangeKind.RenameOld, frn, 10, $"{frn}.tmp")]);
        }

        Assert.InRange(reducer.PendingRenameCount, 1, 4096);
    }

    private static UsnJournalChangeReducer CreateReducer()
    {
        var reducer = new UsnJournalChangeReducer("C:");
        reducer.ReplaceSnapshot(
        [
            Record(10, 5, "Users", isDirectory: true),
            Record(11, 5, "Archive", isDirectory: true),
            Record(20, 10, "before.txt"),
            Record(30, 10, "Folder", isDirectory: true),
            Record(31, 30, "child.txt")
        ]);
        return reducer;
    }

    private static UsnJournalChange Change(
        UsnJournalChangeKind kind,
        ulong frn,
        ulong parentFrn,
        string name,
        bool isDirectory = false)
    {
        return new UsnJournalChange(kind, Record(frn, parentFrn, name, isDirectory));
    }

    private static UsnJournalRecord Record(
        ulong frn,
        ulong parentFrn,
        string name,
        bool isDirectory = false)
    {
        return new UsnJournalRecord(frn, parentFrn, name, isDirectory, 0);
    }

    private static IntPtr CreateJournalBuffer(
        long nextUsn,
        ushort majorVersion,
        int recordLength = 80,
        ushort declaredNameBytes = 16,
        int reason = 0x100)
    {
        const int cursorSize = 8;
        IntPtr buffer = Marshal.AllocHGlobal(cursorSize + recordLength);
        for (int i = 0; i < cursorSize + recordLength; i++)
        {
            Marshal.WriteByte(buffer, i, 0);
        }

        Marshal.WriteInt64(buffer, 0, nextUsn);
        int offset = cursorSize;
        Marshal.WriteInt32(buffer, offset, recordLength);
        Marshal.WriteInt16(buffer, offset + 4, unchecked((short)majorVersion));
        Marshal.WriteInt64(buffer, offset + 8, 20);
        Marshal.WriteInt64(buffer, offset + 16, 10);
        Marshal.WriteInt32(buffer, offset + 40, reason);
        Marshal.WriteInt16(buffer, offset + 56, unchecked((short)declaredNameBytes));
        Marshal.WriteInt16(buffer, offset + 58, 60);
        byte[] name = Encoding.Unicode.GetBytes("file.txt");
        int copyLength = Math.Min(name.Length, Math.Max(0, recordLength - 60));
        Marshal.Copy(name, 0, IntPtr.Add(buffer, offset + 60), copyLength);
        return buffer;
    }
}
