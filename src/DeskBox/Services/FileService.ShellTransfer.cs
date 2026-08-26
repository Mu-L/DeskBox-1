#if !DESKBOX_NATIVE_AOT
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

public sealed partial class FileService
{
    private const uint ShellFileOperationNoConfirmMakeDirectory = 0x0200;
    private const uint ClsContextInProcessServer = 0x1;
    private const uint CoInitApartmentThreaded = 0x2;
    private const uint ShellDisplayNameFileSystemPath = 0x80058000;
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
    private static readonly Guid s_fileOperationClassId =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid s_fileOperationInterfaceId =
        new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid s_shellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private async Task<IReadOnlyList<FileTransferResult>>
        ExecuteModernShellTransferPlanAsync(
            IReadOnlyList<TransferOperation> operations,
            bool move,
            IntPtr ownerWindowHandle,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return [];
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectoryTransfers(operations);
            foreach (TransferOperation operation in operations)
            {
                string? destinationDirectory = Path.GetDirectoryName(
                    operation.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }
            }
        }, cancellationToken);

        progress?.Report(CreateShellProgress(
            FileTransferPhase.DelegatedToShell,
            operations.Count,
            completedItems: 0));

        var stopwatch = Stopwatch.StartNew();
        App.Log(
            $"[FileTransfer] Windows shell transfer start " +
            $"count={operations.Count} move={move} " +
            $"owner=0x{ownerWindowHandle.ToInt64():X}");

        ShellTransferOutcome outcome;
        try
        {
            outcome = await RunShellTransferOnStaThreadAsync(
                operations,
                move,
                ownerWindowHandle,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Canceled,
                operations.Count,
                completedItems: 0));
            throw;
        }
        catch
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Failed,
                operations.Count,
                completedItems: 0));
            throw;
        }

        bool shellReportedFailure =
            outcome.PerformHResult < 0 ||
            outcome.FinishHResult < 0 ||
            outcome.FailedItemCount > 0;
        bool shellCanceled =
            outcome.Aborted ||
            outcome.PerformHResult == ErrorCancelledHResult ||
            outcome.FinishHResult == ErrorCancelledHResult ||
            cancellationToken.IsCancellationRequested;
        IReadOnlyList<FileTransferResult> completedResults =
            ReconcileShellTransferResults(
                operations,
                outcome.CompletedResults,
                move,
                allowSuccessfulBatchFallback:
                    !shellCanceled && !shellReportedFailure);

        App.Log(
            $"[FileTransfer] Windows shell transfer returned " +
            $"count={operations.Count} completed={completedResults.Count} " +
            $"move={move} aborted={outcome.Aborted} " +
            $"failedItems={outcome.FailedItemCount} " +
            $"performHr=0x{outcome.PerformHResult:X8} " +
            $"finishHr=0x{outcome.FinishHResult:X8} " +
            $"elapsedMs={stopwatch.ElapsedMilliseconds}");

        if (shellCanceled)
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Canceled,
                operations.Count,
                completedResults.Count));
            throw new FileTransferCanceledException(
                completedResults,
                cancellationToken);
        }

        if (shellReportedFailure || completedResults.Count != operations.Count)
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Failed,
                operations.Count,
                completedResults.Count));
            Exception? innerException = GetShellFailureException(outcome);
            throw new FileTransferPartialFailureException(
                completedResults,
                innerException);
        }

        progress?.Report(CreateShellProgress(
            FileTransferPhase.Completed,
            operations.Count,
            completedResults.Count));
        return completedResults;
    }

    private static FileTransferProgress CreateShellProgress(
        FileTransferPhase phase,
        int totalItems,
        int completedItems)
    {
        return new FileTransferProgress(
            phase,
            CurrentItemName: null,
            completedItems,
            totalItems,
            BytesTransferred: 0,
            TotalBytes: null,
            BytesPerSecond: null,
            EstimatedRemaining: null);
    }

    private static Exception? GetShellFailureException(
        ShellTransferOutcome outcome)
    {
        int hresult = outcome.PerformHResult < 0
            ? outcome.PerformHResult
            : outcome.FinishHResult < 0
                ? outcome.FinishHResult
                : outcome.FirstFailedItemHResult;
        return hresult < 0 && hresult != ErrorCancelledHResult
            ? Marshal.GetExceptionForHR(hresult)
            : null;
    }

    private static IReadOnlyList<FileTransferResult>
        ReconcileShellTransferResults(
            IReadOnlyList<TransferOperation> operations,
            IReadOnlyList<FileTransferResult> reportedResults,
            bool move,
            bool allowSuccessfulBatchFallback)
    {
        var results = new Dictionary<string, FileTransferResult>(
            StringComparer.OrdinalIgnoreCase);
        foreach (FileTransferResult result in reportedResults)
        {
            results[result.SourcePath] = result;
        }

        foreach (TransferOperation operation in operations)
        {
            if (results.ContainsKey(operation.SourcePath))
            {
                continue;
            }

            bool completed = move
                ? IsCompletedShellMove(
                    operation.SourcePath,
                    operation.DestinationPath)
                : allowSuccessfulBatchFallback &&
                  IsCompletedShellCopy(
                      operation.SourcePath,
                      operation.DestinationPath);
            if (completed)
            {
                results[operation.SourcePath] = new FileTransferResult(
                    operation.SourcePath,
                    operation.DestinationPath);
            }
        }

        return operations
            .Where(operation => results.ContainsKey(operation.SourcePath))
            .Select(operation => results[operation.SourcePath])
            .ToArray();
    }

    internal static bool IsCompletedShellCopy(
        string sourcePath,
        string destinationPath)
    {
        if (File.Exists(sourcePath) && File.Exists(destinationPath))
        {
            try
            {
                return new FileInfo(sourcePath).Length ==
                       new FileInfo(destinationPath).Length;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return Directory.Exists(sourcePath) &&
               Directory.Exists(destinationPath);
    }

    private static Task<ShellTransferOutcome> RunShellTransferOnStaThreadAsync(
        IReadOnlyList<TransferOperation> operations,
        bool move,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ShellTransferOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(ExecuteShellTransferOnCurrentThread(
                    operations,
                    move,
                    ownerWindowHandle,
                    cancellationToken));
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "DeskBox Windows File Operation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ShellTransferOutcome ExecuteShellTransferOnCurrentThread(
        IReadOnlyList<TransferOperation> operations,
        bool move,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int initializeResult = CoInitializeEx(
            IntPtr.Zero,
            CoInitApartmentThreaded);
        ThrowForShellHResult(initializeResult, cancellationToken);
        bool uninitialize = initializeResult >= 0;
        IFileOperationNative? fileOperation = null;
        var retainedShellItems = new List<object>(operations.Count * 2);
        uint adviseCookie = 0;
        var sink = new ShellFileOperationProgressSink(
            operations,
            cancellationToken);
        try
        {
            Guid classId = s_fileOperationClassId;
            Guid interfaceId = s_fileOperationInterfaceId;
            ThrowForShellHResult(
                CoCreateInstance(
                    ref classId,
                    IntPtr.Zero,
                    ClsContextInProcessServer,
                    ref interfaceId,
                    out fileOperation),
                cancellationToken);

            ThrowForShellHResult(
                fileOperation.Advise(sink, out adviseCookie),
                cancellationToken);
            ThrowForShellHResult(
                fileOperation.SetOperationFlags(
                    ShellFileOperationNoConfirmMakeDirectory),
                cancellationToken);
            if (ownerWindowHandle != IntPtr.Zero)
            {
                ThrowForShellHResult(
                    fileOperation.SetOwnerWindow(ownerWindowHandle),
                    cancellationToken);
            }

            foreach (TransferOperation operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IShellItemNative sourceItem = CreateShellItem(
                    operation.SourcePath,
                    cancellationToken);
                IShellItemNative destinationFolderItem = CreateShellItem(
                    Path.GetDirectoryName(operation.DestinationPath)!,
                    cancellationToken);
                retainedShellItems.Add(sourceItem);
                retainedShellItems.Add(destinationFolderItem);
                string destinationName = Path.GetFileName(
                    operation.DestinationPath);
                int queueResult = move
                    ? fileOperation.MoveItem(
                        sourceItem,
                        destinationFolderItem,
                        destinationName,
                        IntPtr.Zero)
                    : fileOperation.CopyItem(
                        sourceItem,
                        destinationFolderItem,
                        destinationName,
                        IntPtr.Zero);
                ThrowForShellHResult(queueResult, cancellationToken);
            }

            int performResult = fileOperation.PerformOperations();
            int abortedResult = fileOperation.GetAnyOperationsAborted(
                out bool aborted);
            if (abortedResult < 0 && performResult >= 0)
            {
                performResult = abortedResult;
            }

            return new ShellTransferOutcome(
                sink.GetCompletedResults(),
                aborted,
                performResult,
                sink.FinishHResult,
                sink.FailedItemCount,
                sink.FirstFailedItemHResult);
        }
        finally
        {
            if (fileOperation is not null && adviseCookie != 0)
            {
                try
                {
                    _ = fileOperation.Unadvise(adviseCookie);
                }
                catch
                {
                }
            }

            foreach (object shellItem in retainedShellItems)
            {
                ReleaseComObject(shellItem);
            }

            if (fileOperation is not null)
            {
                ReleaseComObject(fileOperation);
            }

            GC.KeepAlive(sink);
            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static IShellItemNative CreateShellItem(
        string path,
        CancellationToken cancellationToken)
    {
        Guid interfaceId = s_shellItemInterfaceId;
        ThrowForShellHResult(
            SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ref interfaceId,
                out IShellItemNative shellItem),
            cancellationToken);
        return shellItem;
    }

    private static void ThrowForShellHResult(
        int hresult,
        CancellationToken cancellationToken)
    {
        if (hresult >= 0)
        {
            return;
        }

        if (hresult == ErrorCancelledHResult ||
            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        Marshal.ThrowExceptionForHR(hresult);
    }

    private static void ReleaseComObject(object value)
    {
        try
        {
            if (Marshal.IsComObject(value))
            {
                _ = Marshal.ReleaseComObject(value);
            }
        }
        catch
        {
        }
    }

    private static string? TryGetShellItemPath(IShellItemNative? item)
    {
        if (item is null)
        {
            return null;
        }

        IntPtr pathPointer = IntPtr.Zero;
        try
        {
            int result = item.GetDisplayName(
                ShellDisplayNameFileSystemPath,
                out pathPointer);
            return result >= 0 && pathPointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(pathPointer)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
            {
                CoTaskMemFree(pathPointer);
            }
        }
    }

    private sealed record ShellTransferOutcome(
        IReadOnlyList<FileTransferResult> CompletedResults,
        bool Aborted,
        int PerformHResult,
        int FinishHResult,
        int FailedItemCount,
        int FirstFailedItemHResult);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellFileOperationProgressSink :
        IFileOperationProgressSinkNative
    {
        private const int SuccessHResult = 0;
        private readonly IReadOnlyList<TransferOperation> _operations;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, FileTransferResult> _completed =
            new(StringComparer.OrdinalIgnoreCase);

        internal ShellFileOperationProgressSink(
            IReadOnlyList<TransferOperation> operations,
            CancellationToken cancellationToken)
        {
            _operations = operations;
            _cancellationToken = cancellationToken;
        }

        internal int FinishHResult { get; private set; }

        internal int FailedItemCount { get; private set; }

        internal int FirstFailedItemHResult { get; private set; }

        internal IReadOnlyList<FileTransferResult> GetCompletedResults()
        {
            return _operations
                .Where(operation => _completed.ContainsKey(
                    operation.SourcePath))
                .Select(operation => _completed[operation.SourcePath])
                .ToArray();
        }

        public int StartOperations() => CancellationResult();

        public int FinishOperations(int result)
        {
            FinishHResult = result;
            return SuccessHResult;
        }

        public int PreRenameItem(
            uint flags,
            IShellItemNative item,
            string? newName) => CancellationResult();

        public int PostRenameItem(
            uint flags,
            IShellItemNative item,
            string? newName,
            int renameResult,
            IShellItemNative? newlyCreatedItem) => SuccessHResult;

        public int PreMoveItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            string? newName) => CancellationResult();

        public int PostMoveItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            string? newName,
            int moveResult,
            IShellItemNative? newlyCreatedItem)
        {
            RecordTransferResult(item, newlyCreatedItem, moveResult);
            return CancellationResult();
        }

        public int PreCopyItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            string? newName) => CancellationResult();

        public int PostCopyItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            string? newName,
            int copyResult,
            IShellItemNative? newlyCreatedItem)
        {
            RecordTransferResult(item, newlyCreatedItem, copyResult);
            return CancellationResult();
        }

        public int PreDeleteItem(uint flags, IShellItemNative item) =>
            CancellationResult();

        public int PostDeleteItem(
            uint flags,
            IShellItemNative item,
            int deleteResult,
            IShellItemNative? newlyCreatedItem) => SuccessHResult;

        public int PreNewItem(
            uint flags,
            IShellItemNative destinationFolder,
            string? newName) => CancellationResult();

        public int PostNewItem(
            uint flags,
            IShellItemNative destinationFolder,
            string? newName,
            string? templateName,
            uint fileAttributes,
            int newItemResult,
            IShellItemNative? newItem) => SuccessHResult;

        public int UpdateProgress(uint totalWork, uint completedWork) =>
            CancellationResult();

        public int ResetTimer() => SuccessHResult;

        public int PauseTimer() => SuccessHResult;

        public int ResumeTimer() => SuccessHResult;

        private int CancellationResult()
        {
            return _cancellationToken.IsCancellationRequested
                ? ErrorCancelledHResult
                : SuccessHResult;
        }

        private void RecordTransferResult(
            IShellItemNative sourceItem,
            IShellItemNative? newlyCreatedItem,
            int operationResult)
        {
            if (operationResult < 0)
            {
                FailedItemCount++;
                if (FirstFailedItemHResult == 0)
                {
                    FirstFailedItemHResult = operationResult;
                }

                return;
            }

            string? sourcePath = TryGetShellItemPath(sourceItem);
            string? destinationPath = TryGetShellItemPath(newlyCreatedItem);
            TransferOperation? operation = sourcePath is null
                ? null
                : _operations.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.SourcePath,
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase));
            if (operation is null && destinationPath is not null)
            {
                operation = _operations.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.DestinationPath,
                        destinationPath,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (operation is null)
            {
                return;
            }

            _completed[operation.SourcePath] = new FileTransferResult(
                operation.SourcePath,
                destinationPath ?? operation.DestinationPath);
        }
    }

    [ComVisible(true)]
    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSinkNative
    {
        [PreserveSig]
        int StartOperations();

        [PreserveSig]
        int FinishOperations(int result);

        [PreserveSig]
        int PreRenameItem(
            uint flags,
            IShellItemNative item,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostRenameItem(
            uint flags,
            IShellItemNative item,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int renameResult,
            IShellItemNative? newlyCreatedItem);

        [PreserveSig]
        int PreMoveItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostMoveItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int moveResult,
            IShellItemNative? newlyCreatedItem);

        [PreserveSig]
        int PreCopyItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostCopyItem(
            uint flags,
            IShellItemNative item,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int copyResult,
            IShellItemNative? newlyCreatedItem);

        [PreserveSig]
        int PreDeleteItem(uint flags, IShellItemNative item);

        [PreserveSig]
        int PostDeleteItem(
            uint flags,
            IShellItemNative item,
            int deleteResult,
            IShellItemNative? newlyCreatedItem);

        [PreserveSig]
        int PreNewItem(
            uint flags,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostNewItem(
            uint flags,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
            uint fileAttributes,
            int newItemResult,
            IShellItemNative? newItem);

        [PreserveSig]
        int UpdateProgress(uint totalWork, uint completedWork);

        [PreserveSig]
        int ResetTimer();

        [PreserveSig]
        int PauseTimer();

        [PreserveSig]
        int ResumeTimer();
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationNative
    {
        [PreserveSig]
        int Advise(IFileOperationProgressSinkNative progressSink, out uint cookie);

        [PreserveSig]
        int Unadvise(uint cookie);

        [PreserveSig]
        int SetOperationFlags(uint operationFlags);

        [PreserveSig]
        int SetProgressMessage(
            [MarshalAs(UnmanagedType.LPWStr)] string message);

        [PreserveSig]
        int SetProgressDialog(IntPtr progressDialog);

        [PreserveSig]
        int SetProperties(IntPtr propertyChangeArray);

        [PreserveSig]
        int SetOwnerWindow(IntPtr ownerWindowHandle);

        [PreserveSig]
        int ApplyPropertiesToItem(IShellItemNative item);

        [PreserveSig]
        int ApplyPropertiesToItems(IntPtr items);

        [PreserveSig]
        int RenameItem(
            IShellItemNative item,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);

        [PreserveSig]
        int RenameItems(
            IntPtr items,
            [MarshalAs(UnmanagedType.LPWStr)] string newName);

        [PreserveSig]
        int MoveItem(
            IShellItemNative item,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            IntPtr progressSink);

        [PreserveSig]
        int MoveItems(IntPtr items, IShellItemNative destinationFolder);

        [PreserveSig]
        int CopyItem(
            IShellItemNative item,
            IShellItemNative destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? copyName,
            IntPtr progressSink);

        [PreserveSig]
        int CopyItems(IntPtr items, IShellItemNative destinationFolder);

        [PreserveSig]
        int DeleteItem(IShellItemNative item, IntPtr progressSink);

        [PreserveSig]
        int DeleteItems(IntPtr items);

        [PreserveSig]
        int NewItem(
            IShellItemNative destinationFolder,
            uint fileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
            IntPtr progressSink);

        [PreserveSig]
        int PerformOperations();

        [PreserveSig]
        int GetAnyOperationsAborted(
            [MarshalAs(UnmanagedType.Bool)] out bool anyOperationsAborted);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemNative
    {
        [PreserveSig]
        int BindToHandler(
            IntPtr bindContext,
            ref Guid handlerId,
            ref Guid interfaceId,
            out IntPtr result);

        [PreserveSig]
        int GetParent(out IShellItemNative parent);

        [PreserveSig]
        int GetDisplayName(uint displayNameType, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(IShellItemNative other, uint hint, out int order);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(
        IntPtr reserved,
        uint coInitialize);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outerUnknown,
        uint classContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IFileOperationNative fileOperation);

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemNative shellItem);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);
}
#endif
