using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Renci.SshNet;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using SftpExplorerWinUI.Helpers;
using SftpExplorerWinUI.Services;
using Windows.ApplicationModel.Resources;
using Microsoft.UI.Input.DragDrop;
using Microsoft.UI.Content;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Renci.SshNet.Common;

namespace SftpExplorerWinUI;

public sealed partial class SftpTabContent : UserControl
{
    private SftpClient? _sftpClient;
    private readonly SshConnectionSession _session;
    private readonly SshClientFactory _sshClientFactory;
    private readonly HostKeyConfirmationAsync _confirmHostKeyAsync;
    private string _connectionId = "";
    private string _currentRemotePath = "/";
    private List<string> _navigationHistory = new();
    private int _navigationIndex = -1;
    private List<FileItem> _clipboard = new();
    private bool _clipboardIsCut = false;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OpenFileInfo> _openFiles = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileSystemWatcher> _fileWatchers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _uploadLocks = new();

    private List<FileItem>? _dragFiles;
    private Dictionary<int, string> _uidToNameCache = new();
    private Dictionary<int, string> _gidToNameCache = new();
    private bool _nameResolutionSupported = true;
    private bool _isRightClickInProgress = false;
    private bool _isDownloadInProgress = false;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _pathSuggestionCts;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _backgroundTasksSync = new();
    private readonly HashSet<Task> _backgroundTasks = new();
    private readonly object _activeOperationsSync = new();
    private readonly HashSet<CancellationTokenSource> _activeOperations = new();
    private TaskCompletionSource<object?> _activeOperationsDrained = CreateCompletedTaskSource();
    private readonly Dictionary<string, AddressSuggestionCacheEntry> _addressSuggestionCache =
        new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, FileSystemStats> _fileSystemStatsCache =
        new Dictionary<string, FileSystemStats>(StringComparer.Ordinal);
    private DateTimeOffset _fileSystemStatsCacheCreatedAt = DateTimeOffset.MinValue;
    private Task<IReadOnlyDictionary<string, FileSystemStats>>? _fileSystemStatsLoadTask;
    private int _remoteRefreshVersion;
    private bool _isFreeSpaceColumnVisible;

    private static readonly GridLength FreeSpaceColumnVisibleWidth = new(240);
    private static readonly GridLength FreeSpaceColumnHiddenWidth = new(0);

    private const double TerminalDefaultHeight = 280;
    private const double TerminalMinHeight = 140;
    private const int TerminalOutputHistoryMaxLines = 9_001;
    private const int TerminalOutputHistoryMaxCharacters = 2_000_000;
    private const int TerminalMaxPendingCharacters = 1_000_000;
    private const int TerminalOutputDrainChunkCharacters = 64 * 1024;
    private const int TerminalOutputHistoryTrimSlackLines = 1_000;
    private const int TerminalOutputHistoryTrimSlackCharacters = 1_000_000;
    private const int TerminalCommandHistoryMaxEntries = 2_000;
    private const int TerminalCommandHistoryMaxCharacters = 1_000_000;
    private const int TerminalCommandMaxCharacters = 4_096;
    private const string UploadPickerSettingsIdentifier = "SftpExplorer.UploadFiles";
    private const string DownloadPickerSettingsIdentifier = "SftpExplorer.DownloadFolder";

    private SshClient? _terminalClient;
    private ShellStream? _terminalStream;
    private TaskCompletionSource<bool>? _terminalClosedSignal;
    private CancellationTokenSource? _terminalCts;
    private Task? _terminalReadTask;
    private readonly SemaphoreSlim _terminalWriteLock = new(1, 1);
    private readonly SshTerminalConnection _nativeTerminalConnection;
    private readonly object _terminalOutputQueueSync = new();
    private readonly Queue<string> _queuedTerminalOutput = new();
    private int _queuedTerminalOutputCharacters;
    private int _queuedTerminalOutputHeadOffset;
    private readonly StringBuilder _pendingTerminalOutput = new();
    private readonly StringBuilder _terminalOutputHistory = new();
    private int _terminalOutputHistoryLineCount;
    private int _terminalSessionVersion;
    private uint _terminalColumns = 80;
    private uint _terminalRows = 24;
    private bool _isTerminalConnecting;
    private bool _isTerminalMaximized;
    private bool _terminalRendererReady;
    private Task? _terminalRendererBindingTask;
    private double _terminalPreviousHeight = TerminalDefaultHeight;
    private readonly List<string> _terminalCommandHistory = new();
    private readonly StringBuilder _terminalCommandBuffer = new();
    private string _terminalRecentPlainOutput = "";
    private string _terminalPredictionSuffix = "";
    private bool _terminalAtPrompt;
    private bool _terminalTrackingCommand;
    private bool _terminalPredictionVisible;
    private int _terminalPredictionVersion;
    private int _terminalOutputDrainScheduled;
    private int _terminalResizeRevision;
    private int _terminalResizeAppliedRevision;
    private int _terminalResizeWorkerRunning;

    private static readonly Regex TerminalAnsiSequenceRegex = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\)|[@-_])",
        RegexOptions.Compiled);
    private static readonly Regex TerminalPromptRegex = new(
        "[#$>%]\\s*$",
        RegexOptions.Compiled);
    private static readonly Regex BashHistoryTimestampRegex = new(
        "^#\\d{10,}$",
        RegexOptions.Compiled);

    // Microsoft.UI.Input.DragDrop support (disabled)
    // DragDropManager and SftpDropTarget removed to avoid conflicts with external drags

    // Кеш для drag-drop
    private IReadOnlyList<IStorageItem>? _cachedDragItems;
    private List<FileItem>? _cachedDragSource;
    private Task<IReadOnlyList<IStorageItem>>? _dragPrepareTask;
    private bool _isDragDataReady = false;
    private bool _isDragPreparing = false; // Флаг активной подготовки данных
    private readonly List<DragTransferIssue> _dragTransferIssues = new();
    private bool _skipAllDragPermissionErrors;
    private readonly HashSet<Type> _skipAllDragErrorTypes = new();
    private bool _dragPreparationCanceled;
    private static readonly TimeSpan ImmediateFolderDragBudget = TimeSpan.FromSeconds(5);
    private const int DirectFolderFileLimit = 50;
    private readonly object _dragPreparationSync = new();
    private readonly Dictionary<string, bool> _folderRequiresPreparationCache = new(StringComparer.Ordinal);
    private DateTimeOffset _dragPreparationStartedAt;
    private int _dragPreparationCompletionHandled;
    private int _activeDragPreparationCount;
    private long _statusRevision;
    private bool _isDisposed;
    private bool _largeFolderDragPending;
    private readonly HashSet<string> _sudoBrowseRoots = new(StringComparer.Ordinal);
    private readonly object _autoSyncSync = new();
    private readonly HashSet<string> _autoSyncScheduled = new(StringComparer.Ordinal);

    // Drag progress tracking
    private int _dragTotalFiles = 0;
    private int _dragCompletedFiles = 0;

    // Download progress tracking (for regular downloads)
    private long _downloadTotalBytes = 0;
    private long _downloadedBytes = 0;

    // Active download tasks tracking
    private readonly HashSet<Task> _activeDownloadTasks = new();
    private readonly HashSet<string> _activeRemoteUploadStagingPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteUploadBackupTransaction> _activeRemoteUploadBackupTransactions =
        new(StringComparer.Ordinal);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SftpClient, SemaphoreSlim> ClientOperationGates = new();

    private enum DragTransferAction
    {
        Retry,
        Skip,
        SkipAll,
        TrySudo,
        Cancel
    }

    private sealed record DragTransferIssue(string Path, string Message, bool IsPermissionError);
    private sealed record AddressSuggestion(string FullPath, bool IsDirectory)
    {
        public override string ToString() => FullPath;
    }
    private sealed record AddressSuggestionCacheEntry(
        DateTimeOffset CreatedAt,
        IReadOnlyList<AddressSuggestion> Items);
    private sealed record FileSystemStats(long TotalBytes, long UsedBytes, long AvailableBytes);
    private readonly record struct TransferSummary(int Succeeded, int Failed)
    {
        public static TransferSummary operator +(TransferSummary left, TransferSummary right) =>
            new(left.Succeeded + right.Succeeded, left.Failed + right.Failed);
    }

    private static readonly TimeSpan AddressSuggestionCacheLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FileSystemStatsCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RemoteStagingCleanupTimeout = TimeSpan.FromSeconds(30);
    private const int MaxAddressSuggestions = 50;
    private sealed record RemoteUploadBackupTransaction(string BackupPath, string DestinationPath);

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }


    public ObservableCollection<FileItem> RemoteFiles { get; } = new();
    public event Action<string>? CurrentFolderChanged;
    public SftpClient Client => _sftpClient ?? throw new ObjectDisposedException(nameof(SftpTabContent));
    public SshConnectionSession Session => _session;
    public string CurrentFolderName => GetFolderDisplayName(_currentRemotePath);
    public string CurrentPath => _currentRemotePath;
    public Task CloseCleanupTask { get; private set; } = Task.CompletedTask;

    private static TaskCompletionSource<object?> CreateCompletedTaskSource()
    {
        var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(null);
        return source;
    }

    private static string CreateLocalTransferSessionDirectory(string purpose)
    {
        var sessionRoot = Path.Combine(Path.GetTempPath(), "SftpExplorer", purpose, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionRoot);
        return Path.GetFullPath(sessionRoot);
    }

    private static string CreatePartialFilePath(string finalPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath))
            ?? throw new IOException("The destination file has no parent directory.");
        return Path.Combine(directory, $".sftpexplorer-{Guid.NewGuid():N}.partial");
    }

    private static void EnsureDestinationDoesNotExist(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"Destination already exists: {path}");
        }
    }

    private async Task DownloadFileToLocalAtomicAsync(
        SftpClient client,
        string remotePath,
        string finalPath,
        Action<ulong>? progress,
        CancellationToken cancellationToken)
    {
        var fullFinalPath = Path.GetFullPath(finalPath);
        var parent = Path.GetDirectoryName(fullFinalPath)
            ?? throw new IOException("The destination file has no parent directory.");
        Directory.CreateDirectory(parent);
        EnsureDestinationDoesNotExist(fullFinalPath);
        var partialPath = CreatePartialFilePath(fullFinalPath);
        long lastProgressTimestamp = 0;

        try
        {
            await RunClientTaskAsync(client, async token =>
            {
                token.ThrowIfCancellationRequested();
                using var fileStream = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.SequentialScan);

                if (progress == null)
                {
                    await client.DownloadFileAsync(remotePath, fileStream, token).ConfigureAwait(false);
                }
                else
                {
                    var downloadProgress = new InlineProgress<DownloadFileProgressReport>(report =>
                    {
                        if (ShouldPublishProgress(ref lastProgressTimestamp))
                        {
                            progress(report.TotalBytesDownloaded);
                        }
                    });
                    await client.DownloadFileAsync(remotePath, fileStream, downloadProgress, token).ConfigureAwait(false);
                }

                fileStream.Flush(flushToDisk: true);
                if (progress != null)
                {
                    progress((ulong)fileStream.Length);
                }
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, fullFinalPath, overwrite: false);
        }
        catch
        {
            try
            {
                LocalPathSafety.EnsureStrictDescendant(parent, partialPath);
                if (File.Exists(partialPath) && !Directory.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
            catch (Exception cleanupException)
            {
                Log.Warning($"Failed to remove incomplete local file '{partialPath}': {cleanupException.Message}");
            }
            throw;
        }
    }

    private static bool IsContainedPath(string localRoot, string candidate, bool allowRoot = false)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (allowRoot && string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteOwnedStagingDirectory(
        string sessionRoot,
        string candidate,
        ISet<string> directoriesCreatedBySession)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        if (!IsContainedPath(sessionRoot, fullCandidate, allowRoot: true) ||
            !directoriesCreatedBySession.Contains(fullCandidate) ||
            !Directory.Exists(fullCandidate))
        {
            return;
        }

        Directory.Delete(fullCandidate, recursive: true);
        directoriesCreatedBySession.Remove(fullCandidate);
    }

    private void TrackBackgroundTask(Task task, bool isDownload = false)
    {
        lock (_backgroundTasksSync)
        {
            _backgroundTasks.Add(task);
            if (isDownload)
            {
                _activeDownloadTasks.Add(task);
            }
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_backgroundTasksSync)
                {
                    _backgroundTasks.Remove(completedTask);
                    _activeDownloadTasks.Remove(completedTask);
                }

                if (completedTask.IsFaulted && completedTask.Exception != null)
                {
                    Log.Error("A background tab operation failed.", completedTask.Exception.Flatten());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task RunClientActionAsync(
        SftpClient client,
        Action<CancellationToken> action,
        CancellationToken cancellationToken)
    {
        var operation = RunClientActionCoreAsync(client, action, cancellationToken);
        TrackBackgroundTask(operation);
        return operation;
    }

    private static async Task RunClientActionCoreAsync(
        SftpClient client,
        Action<CancellationToken> action,
        CancellationToken cancellationToken)
    {
        var gate = ClientOperationGates.GetValue(client, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => action(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task RunClientTaskAsync(
        SftpClient client,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var operation = RunClientTaskCoreAsync(client, action, cancellationToken);
        TrackBackgroundTask(operation);
        return operation;
    }

    private static async Task RunClientTaskCoreAsync(
        SftpClient client,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var gate = ClientOperationGates.GetValue(client, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep synchronous preflight/commit calls around the async transfer off the UI thread.
            await Task.Run(() => action(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool ShouldPublishProgress(ref long lastTimestamp, bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        var minimumInterval = Math.Max(1, Stopwatch.Frequency / 10); // 100 ms
        while (true)
        {
            var previous = Volatile.Read(ref lastTimestamp);
            if (!force && previous != 0 && now - previous < minimumInterval)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref lastTimestamp, now, previous) == previous)
            {
                return true;
            }
        }
    }

    private Task<TResult> RunClientResultAsync<TResult>(
        SftpClient client,
        Func<CancellationToken, TResult> action,
        CancellationToken cancellationToken)
    {
        var operation = RunClientResultCoreAsync(client, action, cancellationToken);
        TrackBackgroundTask(operation);
        return operation;
    }

    private static async Task<TResult> RunClientResultCoreAsync<TResult>(
        SftpClient client,
        Func<CancellationToken, TResult> action,
        CancellationToken cancellationToken)
    {
        var gate = ClientOperationGates.GetValue(client, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => action(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<SftpClient> ConnectAuxiliarySftpAsync(CancellationToken cancellationToken) =>
        _sshClientFactory.ConnectSftpAsync(_session, _confirmHostKeyAsync, cancellationToken);

    private Task<SshClient> ConnectAuxiliarySshAsync(CancellationToken cancellationToken) =>
        _sshClientFactory.ConnectSshAsync(_session, _confirmHostKeyAsync, cancellationToken);

    private SftpClient ConnectAuxiliarySftp(CancellationToken cancellationToken = default) =>
        ConnectAuxiliarySftpAsync(cancellationToken).GetAwaiter().GetResult();

    private SshClient ConnectAuxiliarySsh(CancellationToken cancellationToken = default) =>
        ConnectAuxiliarySshAsync(cancellationToken).GetAwaiter().GetResult();

    private CancellationTokenSource BeginCancelableOperation()
    {
        var replacement = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        lock (_activeOperationsSync)
        {
            if (_activeOperations.Count == 0)
            {
                _activeOperationsDrained = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _activeOperations.Add(replacement);
        }

        var previous = Interlocked.Exchange(ref _operationCts, replacement);
        if (previous != null)
        {
            // The previous operation owns and disposes its CTS in its own finally.
            // Disposing it here can race with registrations still being installed.
            previous.Cancel();
        }

        return replacement;
    }

    private void CompleteCancelableOperation(CancellationTokenSource operation)
    {
        Interlocked.CompareExchange(ref _operationCts, null, operation);
        lock (_activeOperationsSync)
        {
            _activeOperations.Remove(operation);
            if (_activeOperations.Count == 0)
            {
                _activeOperationsDrained.TrySetResult(null);
            }
        }
        operation.Dispose();
    }

    private Task GetActiveOperationsDrainTask()
    {
        lock (_activeOperationsSync)
        {
            return _activeOperationsDrained.Task;
        }
    }

    private async Task DrainCloseTasksAsync(IReadOnlyCollection<Task> tasks)
    {
        try
        {
            var allTasks = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(10)));
            if (ReferenceEquals(completed, allTasks))
            {
                try
                {
                    await allTasks;
                }
                catch (Exception ex)
                {
                    Log.Warning($"One or more tab cleanup tasks failed: {ex.Message}");
                }
                _lifetimeCts.Dispose();
            }
            else
            {
                Log.Warning($"Timed out waiting for {tasks.Count} tab background operation(s) to stop.");
                _ = allTasks.ContinueWith(
                    completedTasks =>
                    {
                        _ = completedTasks.Exception;
                        _lifetimeCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Tab cleanup coordination failed: {ex.Message}");
        }
    }

    private static async Task AwaitTerminalReadAndDisposeAsync(
        Task? terminalReadTask,
        CancellationTokenSource? terminalCts)
    {
        try
        {
            if (terminalReadTask != null)
            {
                await terminalReadTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning($"Terminal reader cleanup failed: {ex.Message}");
        }
        finally
        {
            terminalCts?.Dispose();
        }
    }

    private void HideProgressBars()
    {
        TransferProgressBar.Visibility = Visibility.Collapsed;
        TransferProgressBar.Value = 0;
        OverallProgressBar.Visibility = Visibility.Collapsed;
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = 0;
        OverallProgressText.Visibility = Visibility.Collapsed;
        ProgressPercent.Visibility = Visibility.Collapsed;
        ProgressSpeed.Visibility = Visibility.Collapsed;
        ProgressETA.Visibility = Visibility.Collapsed;
        CancelOperationButton.Visibility = Visibility.Collapsed;
        PreparationProgressRing.IsActive = false;
        PreparationProgressRing.Visibility = Visibility.Collapsed;
    }

    private void ShowProgressBar(int percent)
    {
        TransferProgressBar.Visibility = Visibility.Visible;
        TransferProgressBar.Value = percent;
        ProgressPercent.Visibility = Visibility.Visible;
        ProgressSpeed.Visibility = Visibility.Visible;
        ProgressETA.Visibility = Visibility.Visible;
    }

    private void ShowOverallProgress(int currentFile, int totalFiles)
    {
        OverallProgressBar.Visibility = Visibility.Visible;
        OverallProgressBar.Value = totalFiles > 0 ? (currentFile * 100) / totalFiles : 0;
        OverallProgressText.Visibility = Visibility.Visible;
        OverallProgressText.Text = string.Format(LocalizationHelper.GetString("FileProgress"), currentFile, totalFiles);
    }

    private void ShowOverallProgress(int currentFile, int totalFiles, long downloadedBytes, long totalBytes)
    {
        try
        {
            OverallProgressBar.Visibility = Visibility.Visible;
            OverallProgressBar.IsIndeterminate = false;

            // Рассчитываем процент по байтам (более точно)
            OverallProgressBar.Value = totalBytes > 0 ? (int)((downloadedBytes * 100) / totalBytes) : 0;

            OverallProgressText.Visibility = Visibility.Visible;

            // Безопасное форматирование с проверкой значений
            var fileProgressText = LocalizationHelper.GetString("FileProgress");
            var formattedProgress = string.IsNullOrEmpty(fileProgressText)
                ? $"File {currentFile} of {totalFiles}"
                : string.Format(fileProgressText, currentFile, totalFiles);

            var downloadedStr = downloadedBytes >= 0 ? FormatFileSize(downloadedBytes) : "0 B";
            var totalStr = totalBytes > 0 ? FormatFileSize(totalBytes) : "0 B";

            OverallProgressText.Text = $"{formattedProgress} - {downloadedStr}/{totalStr}";
        }
        catch (Exception ex)
        {
            Log.Error($"Error in ShowOverallProgress: {ex.Message}", ex);
            // Fallback to simple display
            OverallProgressText.Text = $"File {currentFile} of {totalFiles}";
        }
    }

    private void ShowOverallProgressIndeterminate(int currentFile)
    {
        OverallProgressBar.Visibility = Visibility.Visible;
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressText.Visibility = Visibility.Visible;
        OverallProgressText.Text = string.Format(LocalizationHelper.GetString("FilesDownloadedCount"), currentFile);
    }

    private void UpdateItemCount()
    {
        var itemCount = RemoteFiles.Count(item => !item.IsVirtualRoot);
        var selectedCount = RemoteFilesListView.SelectedItems.Cast<FileItem>().Count(item => !item.IsVirtualRoot);

        if (selectedCount > 0)
        {
            ItemCountText.Text = string.Format(LocalizationHelper.GetString("ItemCountSelected"), selectedCount, itemCount);
        }
        else
        {
            ItemCountText.Text = string.Format(LocalizationHelper.GetString("ItemCount"), itemCount);
        }
    }

    public SftpTabContent(
        SftpClient sftpClient,
        SshConnectionSession session,
        SshClientFactory factory,
        Func<HostKeyPrompt, CancellationToken, Task<bool>> confirmHostKeyAsync)
    {
        ArgumentNullException.ThrowIfNull(sftpClient);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(confirmHostKeyAsync);

        InitializeComponent();

        _nativeTerminalConnection = new SshTerminalConnection(
            data => _ = HandleTerminalInputAsync(data),
            (columns, rows) =>
            {
                UpdateTerminalDimensions(columns, rows);
                QueueRemoteTerminalResize();
            });
        NativeTerminal.Loaded += NativeTerminal_Loaded;
        _sftpClient = sftpClient;
        _session = session;
        _sshClientFactory = factory;
        _confirmHostKeyAsync = (prompt, cancellationToken) =>
            confirmHostKeyAsync(prompt, cancellationToken);
        _dragFiles = new List<FileItem>();

        // Создаём уникальный идентификатор соединения и отображаем его в статусе
        if (sftpClient?.ConnectionInfo != null)
        {
            _connectionId = $"{sftpClient.ConnectionInfo.Host}:{sftpClient.ConnectionInfo.Port}:{sftpClient.ConnectionInfo.Username}";
            ConnectionText.Text = $"📡 {sftpClient.ConnectionInfo.Username}@{sftpClient.ConnectionInfo.Host}:{sftpClient.ConnectionInfo.Port}";
        }
        RemoteFilesListView.ItemsSource = RemoteFiles;
        RemoteFilesListView.SelectionChanged += RemoteFilesListView_SelectionChanged;
        RemoteFilesListView.ContainerContentChanging += RemoteFilesListView_ContainerContentChanging;

        // Перехватываем правый клик до drag-and-drop системы
        RemoteFilesListView.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(RemoteFilesListView_PointerPressed), true);

        // Регистрируем обработчик закрытия
        this.Unloaded += OnUnloaded;

        // Инициализируем DragDropManager после загрузки UI
        this.Loaded += OnLoaded;

        // Initialize remote path
        if (_sftpClient?.IsConnected == true)
        {
            _currentRemotePath = _sftpClient.WorkingDirectory;

            // Инициализируем историю навигации
            _navigationHistory.Add(_currentRemotePath);
            _navigationIndex = 0;
            UpdateNavigationButtons();
            UpdateBreadcrumb();

            RefreshRemoteFiles();
            ConnectOverlay.Visibility = Visibility.Collapsed;
            HideProgressBars();
            StatusText.Text = $"Connected - {RemoteFiles.Count} items";
            NotifyCurrentFolderChanged();
        }
        else
        {
            StatusText.Text = "SFTP client not connected!";
            NotifyCurrentFolderChanged();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Debug("SFTP tab content loaded");
        // Using XAML-based `DragOver`/`Drop` handlers for external drag from Explorer.
        // DragDropManager initialization disabled to avoid intercepting external drags.
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unloaded is transient when switching or reparenting tabs. It is not a
        // disposal signal and must not cancel a folder being prepared.
        Log.Debug("SFTP tab content temporarily unloaded");
    }

    private async void TerminalToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalPanel.Visibility == Visibility.Visible)
        {
            await CloseTerminalAsync();
        }
        else
        {
            await OpenTerminalAsync();
        }
    }

    private async void TerminalCloseButton_Click(object sender, RoutedEventArgs e)
    {
        await CloseTerminalAsync();
    }

    private void TerminalClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearTerminalSurface();
    }

    private async void TerminalSaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(windowId)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"terminal-{DateTime.Now:yyyyMMdd-HHmmss}",
                Title = LocalizationHelper.GetString("TerminalOutputSavePickerTitle") ?? "Save terminal output"
            };
            picker.FileTypeChoices.Add("Text files", new List<string> { ".txt" });

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, _terminalOutputHistory.ToString(), new UTF8Encoding(false));
            StatusText.Text = LocalizationHelper.GetString("TerminalOutputSaved") ?? "Terminal output saved";
        }
        catch (Exception ex)
        {
            Log.Error($"Saving terminal output failed: {ex.Message}", ex);
            StatusText.Text = string.Format(
                LocalizationHelper.GetString("TerminalOutputSaveFailed") ?? "Unable to save terminal output: {0}",
                ex.Message);
        }
    }

    private void TerminalMaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        SetTerminalMaximized(!_isTerminalMaximized);
    }

    private void SetTerminalMaximized(bool maximized)
    {
        if (_isTerminalMaximized == maximized)
        {
            return;
        }

        if (!maximized)
        {
            NavigationBar.Visibility = Visibility.Visible;
            ActionToolbar.Visibility = Visibility.Visible;
            FilesRow.Height = new GridLength(1, GridUnitType.Star);
            TerminalSplitterRow.Height = new GridLength(6);
            TerminalRow.Height = new GridLength(_terminalPreviousHeight);
            TerminalMaximizeIcon.Glyph = "\uE740";
        }
        else
        {
            if (TerminalRow.ActualHeight >= TerminalMinHeight)
            {
                _terminalPreviousHeight = TerminalRow.ActualHeight;
            }

            NavigationBar.Visibility = Visibility.Collapsed;
            ActionToolbar.Visibility = Visibility.Collapsed;
            FilesRow.Height = new GridLength(0);
            TerminalSplitterRow.Height = new GridLength(0);
            TerminalRow.Height = new GridLength(1, GridUnitType.Star);
            TerminalMaximizeIcon.Glyph = "\uE73F";
        }

        _isTerminalMaximized = maximized;
    }

    private void TerminalSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isTerminalMaximized || TerminalPanel.Parent is not Grid contentGrid)
        {
            return;
        }

        var maximumHeight = Math.Max(TerminalMinHeight, contentGrid.ActualHeight - 200);
        var requestedHeight = TerminalRow.ActualHeight - e.VerticalChange;
        var newHeight = Math.Clamp(requestedHeight, TerminalMinHeight, maximumHeight);
        TerminalRow.Height = new GridLength(newHeight);
        _terminalPreviousHeight = newHeight;
    }

    private async void NativeTerminal_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureTerminalRendererBoundAsync();
        if (_isDisposed)
        {
            return;
        }

        SetTerminalInputEnabled(
            _terminalClient?.IsConnected == true &&
            _terminalStream != null &&
            _terminalClosedSignal?.Task.IsCompleted != true);
        FocusTerminal();
    }

    private Task EnsureTerminalRendererBoundAsync()
    {
        if (_terminalRendererReady || _isDisposed)
        {
            return Task.CompletedTask;
        }

        if (_terminalRendererBindingTask is { IsCompleted: false })
        {
            return _terminalRendererBindingTask;
        }

        _terminalRendererBindingTask = BindTerminalRendererAfterLayoutAsync();
        return _terminalRendererBindingTask;
    }

    private async Task BindTerminalRendererAfterLayoutAsync()
    {
        // Never bind the SSH backend from HwndHost.BuildWindowCore/Loaded's
        // synchronous layout stack. Native output can re-enter HWND creation and
        // overflow the UI thread when a second terminal control is present.
        await Task.Delay(1);

        for (var attempt = 0; attempt < 40 && !_isDisposed; attempt++)
        {
            if (NativeTerminal.IsRendererReady)
            {
                NativeTerminal.Connection = _nativeTerminalConnection;
                _terminalRendererReady = true;
                FlushPendingTerminalOutput();
                return;
            }

            await Task.Delay(50);
        }
    }

    private void UpdateTerminalDimensions(uint columns, uint rows)
    {
        _terminalColumns = Math.Clamp(columns, 2u, 500u);
        _terminalRows = Math.Clamp(rows, 1u, 300u);
    }

    private void QueueRemoteTerminalResize()
    {
        Interlocked.Increment(ref _terminalResizeRevision);
        StartRemoteTerminalResizeWorker();
    }

    private void StartRemoteTerminalResizeWorker()
    {
        var stream = _terminalStream;
        var cancellationToken = _terminalCts?.Token ?? CancellationToken.None;
        var sessionVersion = _terminalSessionVersion;
        if (stream == null ||
            cancellationToken == CancellationToken.None ||
            _terminalClient?.IsConnected != true ||
            Interlocked.CompareExchange(ref _terminalResizeWorkerRunning, 1, 0) != 0)
        {
            return;
        }

        _ = RunRemoteTerminalResizeWorkerAsync(stream, sessionVersion, cancellationToken);
    }

    private async Task RunRemoteTerminalResizeWorkerAsync(
        ShellStream stream,
        int sessionVersion,
        CancellationToken cancellationToken)
    {
        var attemptedRevision = Volatile.Read(ref _terminalResizeAppliedRevision);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   sessionVersion == _terminalSessionVersion &&
                   ReferenceEquals(stream, _terminalStream))
            {
                var revision = Volatile.Read(ref _terminalResizeRevision);
                attemptedRevision = revision;
                var columns = _terminalColumns;
                var rows = _terminalRows;

                await _terminalWriteLock.WaitAsync(cancellationToken);
                try
                {
                    if (sessionVersion != _terminalSessionVersion ||
                        !ReferenceEquals(stream, _terminalStream) ||
                        cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await Task.Run(() => stream.ChangeWindowSize(
                        columns,
                        rows,
                        columns * 9,
                        rows * 18), cancellationToken);
                }
                finally
                {
                    _terminalWriteLock.Release();
                }

                if (sessionVersion != _terminalSessionVersion ||
                    !ReferenceEquals(stream, _terminalStream))
                {
                    break;
                }

                Volatile.Write(ref _terminalResizeAppliedRevision, revision);
                if (revision == Volatile.Read(ref _terminalResizeRevision))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (sessionVersion == _terminalSessionVersion &&
                ReferenceEquals(stream, _terminalStream))
            {
                Volatile.Write(ref _terminalResizeAppliedRevision, attemptedRevision);
            }
            Log.Warning($"SSH terminal resize failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _terminalResizeWorkerRunning, 0);
            var currentSession = sessionVersion == _terminalSessionVersion &&
                                 ReferenceEquals(stream, _terminalStream);
            if ((!currentSession ||
                 Volatile.Read(ref _terminalResizeAppliedRevision) !=
                 Volatile.Read(ref _terminalResizeRevision)) &&
                _terminalStream != null)
            {
                StartRemoteTerminalResizeWorker();
            }
        }
    }

    public Task OpenTerminalMaximizedAsync()
    {
        return OpenTerminalAsync(maximize: true);
    }

    internal void SetTerminalLeftOverlayBoundary(double? boundaryInXamlRootDips)
    {
        NativeTerminal.SetLeftOverlayBoundary(boundaryInXamlRootDips);
    }

    internal bool SuspendNativeTerminalForXamlOverlay()
    {
        if (NativeTerminal.Visibility != Visibility.Visible)
        {
            return false;
        }

        // The terminal renderer owns a child HWND, which is always composed above
        // XAML overlays such as ContentDialog. Hide only that native surface while
        // the overlay is open; the XAML terminal panel remains in place underneath.
        NativeTerminal.Visibility = Visibility.Collapsed;
        return true;
    }

    internal void RestoreNativeTerminalAfterXamlOverlay(bool wasVisible)
    {
        if (wasVisible && IsLoaded && TerminalPanel.Visibility == Visibility.Visible)
        {
            NativeTerminal.Visibility = Visibility.Visible;
        }
    }

    internal void SuspendNativeTerminalForTabSwitch()
    {
        if (_isDisposed || NativeTerminal.Visibility != Visibility.Visible)
        {
            return;
        }

        // A child HWND is not clipped by the XAML visual tree. Hide it before
        // this tab is detached, otherwise it can remain above the next tab.
        NativeTerminal.Visibility = Visibility.Collapsed;
    }

    internal void RestoreNativeTerminalAfterTabSwitch()
    {
        if (!_isDisposed &&
            Parent != null &&
            TerminalPanel.Visibility == Visibility.Visible)
        {
            NativeTerminal.Visibility = Visibility.Visible;
        }
    }

    private async Task OpenTerminalAsync(bool maximize = false)
    {
        ShowTerminalPanel();
        // Let the newly visible panel complete one layout pass before applying
        // the native theme/viewport. Applying it while Collapsed leaves the
        // text buffer populated but the HWND surface visually black.
        await Task.Delay(50);
        await EnsureTerminalRendererBoundAsync();
        NativeTerminal.RefreshRenderer();
        if (maximize)
        {
            SetTerminalMaximized(true);
        }

        if (_terminalClient?.IsConnected == true &&
            _terminalStream != null &&
            _terminalClosedSignal?.Task.IsCompleted != true)
        {
            SetTerminalInputEnabled(true);
            FocusTerminal();
            return;
        }

        if (_terminalClient != null || _terminalStream != null)
        {
            await DisconnectTerminalAsync();
        }

        if (_isTerminalConnecting)
        {
            return;
        }

        var connectionInfo = _sftpClient?.ConnectionInfo;
        if (connectionInfo == null || _sftpClient?.IsConnected != true)
        {
            AppendTerminalOutput(LocalizationHelper.GetString("TerminalUnavailable") ??
                                 "SFTP connection is not available.\r\n");
            SetTerminalInputEnabled(false);
            return;
        }

        _isTerminalConnecting = true;
        SetTerminalInputEnabled(false);
        ResetTerminalCommandLine();
        _terminalCommandHistory.Clear();
        _terminalRecentPlainOutput = "";
        ClearTerminalSurface();
        AppendTerminalOutput(LocalizationHelper.GetString("TerminalConnecting") ??
                             "Connecting SSH terminal...\r\n");
        var terminalLabel = LocalizationHelper.GetString("TerminalTitleLabel");
        TerminalTitle.Text = $"{terminalLabel} · {connectionInfo.Username}@{connectionInfo.Host}:{connectionInfo.Port}";
        var sessionVersion = ++_terminalSessionVersion;
        SshClient? client = null;
        ShellStream? stream = null;
        var terminalClosedSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<EventArgs> shellClosedHandler = (_, _) =>
            terminalClosedSignal.TrySetResult(true);
        IReadOnlyList<string> bashHistory = Array.Empty<string>();

        try
        {
            client = await ConnectAuxiliarySshAsync(_lifetimeCts.Token);
            await Task.Run(() =>
            {
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                bashHistory = ReadRemoteBashHistory(client);
                stream = client.CreateShellStream(
                    "xterm-256color",
                    _terminalColumns,
                    _terminalRows,
                    _terminalColumns * 9,
                    _terminalRows * 18,
                    4096);
                stream.Closed += shellClosedHandler;
            });

            if (sessionVersion != _terminalSessionVersion ||
                stream == null ||
                terminalClosedSignal.Task.IsCompleted)
            {
                if (stream != null)
                {
                    stream.Closed -= shellClosedHandler;
                    stream.Dispose();
                }
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
                client.Dispose();
                return;
            }

            ReplaceTerminalCommandHistory(bashHistory);
            _terminalClient = client;
            _terminalStream = stream;
            _terminalClosedSignal = terminalClosedSignal;
            var terminalCts = new CancellationTokenSource();
            _terminalCts = terminalCts;
            _terminalReadTask = Task.Run(() => ReadTerminalOutputAsync(
                client,
                stream,
                sessionVersion,
                terminalClosedSignal,
                shellClosedHandler,
                terminalCts.Token));

            StartRemoteTerminalResizeWorker();

            var terminalReady = !terminalClosedSignal.Task.IsCompleted && client.IsConnected;
            SetTerminalInputEnabled(terminalReady);
            if (terminalReady)
            {
                FocusTerminal();
            }

            if (!string.IsNullOrEmpty(_currentRemotePath))
            {
                await SetTerminalWorkingDirectoryAsync(_currentRemotePath);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            if (stream != null)
            {
                stream.Closed -= shellClosedHandler;
            }
            stream?.Dispose();
            client?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"SSH terminal connection failed: {ex.Message}", ex);
            if (stream != null)
            {
                try
                {
                    stream.Closed -= shellClosedHandler;
                }
                catch (Exception handlerException)
                {
                    Log.Debug($"SSH terminal close handler removal failed: {handlerException.Message}");
                }
            }
            stream?.Dispose();
            if (client?.IsConnected == true)
            {
                client.Disconnect();
            }
            client?.Dispose();

            var errorPrefix = LocalizationHelper.GetString("TerminalConnectionFailed") ??
                              "SSH terminal connection failed";
            AppendTerminalOutput($"\r\n{errorPrefix}: {ex.Message}\r\n");
            SetTerminalInputEnabled(false);
        }
        finally
        {
            _isTerminalConnecting = false;
        }
    }

    private void ShowTerminalPanel()
    {
        TerminalPanel.Visibility = Visibility.Visible;
        NativeTerminal.Visibility = Visibility.Visible;
        TerminalSplitter.Visibility = Visibility.Visible;
        FilesRow.Height = new GridLength(1, GridUnitType.Star);
        TerminalSplitterRow.Height = new GridLength(6);
        TerminalRow.Height = new GridLength(_terminalPreviousHeight);
        _isTerminalMaximized = false;
        TerminalMaximizeIcon.Glyph = "\uE740";
    }

    private void HideTerminalPanel()
    {
        // The renderer owns a child HWND. Collapsing only its XAML parent does not
        // notify that HWND, so hide the control itself before collapsing the row.
        NativeTerminal.Visibility = Visibility.Collapsed;
        TerminalPanel.Visibility = Visibility.Collapsed;
        TerminalSplitter.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Visible;
        ActionToolbar.Visibility = Visibility.Visible;
        FilesRow.Height = new GridLength(1, GridUnitType.Star);
        TerminalSplitterRow.Height = new GridLength(0);
        TerminalRow.Height = new GridLength(0);
        _isTerminalMaximized = false;
        TerminalMaximizeIcon.Glyph = "\uE740";
    }

    private async Task WriteTerminalAsync(string text)
    {
        var stream = _terminalStream;
        if (stream == null || _terminalClient?.IsConnected != true)
        {
            return;
        }

        await _terminalWriteLock.WaitAsync();
        try
        {
            await Task.Run(() => stream.Write(text));
        }
        catch (Exception ex)
        {
            Log.Error($"SSH terminal write failed: {ex.Message}", ex);
            AppendTerminalOutput($"\r\n[write error: {ex.Message}]\r\n");
        }
        finally
        {
            _terminalWriteLock.Release();
        }
    }

    private async Task SetTerminalWorkingDirectoryAsync(string remotePath)
    {
        // The terminal is an independent SSH shell. It must establish access with
        // the connected user's rights; an SFTP listing obtained through sudo does
        // not grant those rights to this session.
        var command = $"cd -- {QuoteShellArgument(remotePath)} 2>/dev/null || cd -- \"$HOME\" 2>/dev/null\r";
        await WriteTerminalAsync(command);
    }

    private async Task HandleTerminalInputAsync(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        ClearTerminalPredictionSurface();

        if (TryAcceptTerminalPrediction(data, out var acceptedText))
        {
            await WriteTerminalAsync(acceptedText);
            RefreshTerminalPrediction();
            ScheduleTerminalPrediction();
            return;
        }

        var forwardedText = TrackTerminalInput(data);
        RefreshTerminalPrediction();
        if (forwardedText.Length != 0)
        {
            await WriteTerminalAsync(forwardedText);
        }

        ScheduleTerminalPrediction();
    }

    private bool TryAcceptTerminalPrediction(string data, out string acceptedText)
    {
        acceptedText = "";
        if (!_terminalTrackingCommand || string.IsNullOrEmpty(_terminalPredictionSuffix))
        {
            return false;
        }

        if (data is not ("\x1b[C" or "\x1bOC" or "\x1b[F" or "\x1bOF" or "\x1b[4~"))
        {
            return false;
        }

        acceptedText = _terminalPredictionSuffix;
        _terminalCommandBuffer.Append(acceptedText);
        _terminalPredictionSuffix = "";
        return true;
    }

    private string TrackTerminalInput(string data)
    {
        if (data.StartsWith('\x1b'))
        {
            // Cursor movement and remote history/completion can replace the line
            // without exposing its new value to the host application.
            _terminalTrackingCommand = false;
            _terminalCommandBuffer.Clear();
            _terminalPredictionSuffix = "";
            return data;
        }

        var forwarded = new StringBuilder(data.Length);
        var trackPastedLines = _terminalAtPrompt || _terminalTrackingCommand;
        for (var index = 0; index < data.Length; index++)
        {
            var character = data[index];
            if (character is '\r' or '\n')
            {
                if (character == '\n' && index > 0 && data[index - 1] == '\r')
                {
                    continue;
                }

                if (_terminalTrackingCommand)
                {
                    RememberTerminalCommand(_terminalCommandBuffer.ToString());
                }

                _terminalCommandBuffer.Clear();
                _terminalTrackingCommand = false;
                _terminalAtPrompt = false;
                _terminalPredictionSuffix = "";
                forwarded.Append('\r');

                if (trackPastedLines && index < data.Length - 1)
                {
                    _terminalTrackingCommand = true;
                }
                continue;
            }

            if (character is '\b' or '\x7f')
            {
                if (_terminalTrackingCommand && _terminalCommandBuffer.Length > 0)
                {
                    _terminalCommandBuffer.Length--;
                }
                forwarded.Append(character);
                continue;
            }

            if (character == '\x03')
            {
                ResetTerminalCommandLine();
                forwarded.Append(character);
                continue;
            }

            if (character == '\x15')
            {
                _terminalCommandBuffer.Clear();
                forwarded.Append(character);
                continue;
            }

            if (character == '\x17')
            {
                RemoveLastTerminalWord();
                forwarded.Append(character);
                continue;
            }

            if (character == '\t' || char.IsControl(character))
            {
                _terminalTrackingCommand = false;
                _terminalCommandBuffer.Clear();
                _terminalPredictionSuffix = "";
                forwarded.Append(character);
                continue;
            }

            if (!_terminalTrackingCommand && (_terminalAtPrompt || trackPastedLines))
            {
                _terminalTrackingCommand = true;
                _terminalCommandBuffer.Clear();
            }

            if (_terminalTrackingCommand && _terminalCommandBuffer.Length < TerminalCommandMaxCharacters)
            {
                _terminalCommandBuffer.Append(character);
            }
            _terminalAtPrompt = false;
            forwarded.Append(character);
        }

        return forwarded.ToString();
    }

    private void RemoveLastTerminalWord()
    {
        while (_terminalCommandBuffer.Length > 0 &&
               char.IsWhiteSpace(_terminalCommandBuffer[_terminalCommandBuffer.Length - 1]))
        {
            _terminalCommandBuffer.Length--;
        }
        while (_terminalCommandBuffer.Length > 0 &&
               !char.IsWhiteSpace(_terminalCommandBuffer[_terminalCommandBuffer.Length - 1]))
        {
            _terminalCommandBuffer.Length--;
        }
    }

    private void RememberTerminalCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0 ||
            command.Length > TerminalCommandMaxCharacters ||
            command.Any(char.IsControl))
        {
            return;
        }

        _terminalCommandHistory.RemoveAll(existing => string.Equals(
            existing,
            command,
            StringComparison.Ordinal));
        _terminalCommandHistory.Add(command);
        if (_terminalCommandHistory.Count > TerminalCommandHistoryMaxEntries)
        {
            _terminalCommandHistory.RemoveAt(0);
        }
    }

    private static IReadOnlyList<string> ReadRemoteBashHistory(SshClient client)
    {
        try
        {
            using var command = client.CreateCommand(
                $"if [ -n \"$HOME\" ] && [ -r \"$HOME/.bash_history\" ]; then " +
                $"tail -n {TerminalCommandHistoryMaxEntries} \"$HOME/.bash_history\" 2>/dev/null | " +
                $"tail -c {TerminalCommandHistoryMaxCharacters}; fi");
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            var output = command.Execute();
            if (string.IsNullOrEmpty(output))
            {
                return Array.Empty<string>();
            }

            var history = new List<string>();
            using var reader = new StringReader(output);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || BashHistoryTimestampRegex.IsMatch(line))
                {
                    continue;
                }

                history.Add(line);
            }

            return history;
        }
        catch (Exception ex)
        {
            Log.Debug($"Unable to read remote .bash_history: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private void ReplaceTerminalCommandHistory(IEnumerable<string> commands)
    {
        _terminalCommandHistory.Clear();
        foreach (var command in commands)
        {
            RememberTerminalCommand(command);
        }
    }

    private void RefreshTerminalPrediction()
    {
        _terminalPredictionSuffix = "";
        if (!_terminalTrackingCommand || _terminalCommandBuffer.Length == 0)
        {
            return;
        }

        var prefix = _terminalCommandBuffer.ToString();
        for (var index = _terminalCommandHistory.Count - 1; index >= 0; index--)
        {
            var command = _terminalCommandHistory[index];
            if (command.Length > prefix.Length &&
                command.StartsWith(prefix, StringComparison.Ordinal))
            {
                _terminalPredictionSuffix = command[prefix.Length..];
                return;
            }
        }
    }

    private void ScheduleTerminalPrediction()
    {
        var version = ++_terminalPredictionVersion;
        if (!_terminalRendererReady ||
            !_terminalTrackingCommand ||
            string.IsNullOrEmpty(_terminalPredictionSuffix))
        {
            return;
        }

        _ = RenderTerminalPredictionAsync(version);
    }

    private async Task RenderTerminalPredictionAsync(int version)
    {
        await Task.Delay(75);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _terminalPredictionVersion ||
                !_terminalRendererReady ||
                !_terminalTrackingCommand ||
                string.IsNullOrEmpty(_terminalPredictionSuffix))
            {
                return;
            }

            var suffix = _terminalPredictionSuffix;
            var cursorColumns = suffix.EnumerateRunes().Count();
            _nativeTerminalConnection.WriteOutput($"\x1b[90m{suffix}\x1b[39m\x1b[{cursorColumns}D");
            _terminalPredictionVisible = true;
        });
    }

    private void ClearTerminalPredictionSurface()
    {
        ++_terminalPredictionVersion;
        if (!_terminalPredictionVisible || !_terminalRendererReady)
        {
            return;
        }

        _nativeTerminalConnection.WriteOutput("\x1b[0K");
        _terminalPredictionVisible = false;
    }

    private void ObserveTerminalOutput(string output)
    {
        var plainOutput = TerminalAnsiSequenceRegex.Replace(output, "");
        if (plainOutput.Length == 0)
        {
            return;
        }

        _terminalRecentPlainOutput += plainOutput;
        if (_terminalRecentPlainOutput.Length > 1024)
        {
            _terminalRecentPlainOutput = _terminalRecentPlainOutput[^1024..];
        }

        var lastLineBreak = Math.Max(
            _terminalRecentPlainOutput.LastIndexOf('\r'),
            _terminalRecentPlainOutput.LastIndexOf('\n'));
        var lastLine = lastLineBreak >= 0
            ? _terminalRecentPlainOutput[(lastLineBreak + 1)..]
            : _terminalRecentPlainOutput;

        if (TerminalPromptRegex.IsMatch(lastLine))
        {
            _terminalAtPrompt = true;
            if (!_terminalTrackingCommand)
            {
                _terminalCommandBuffer.Clear();
            }
        }
        else if (!_terminalTrackingCommand)
        {
            _terminalAtPrompt = false;
        }
    }

    private void AddTerminalOutputToHistory(string output)
    {
        var plainOutput = TerminalAnsiSequenceRegex.Replace(output, "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\0", "", StringComparison.Ordinal);
        if (plainOutput.Length == 0)
        {
            return;
        }

        _terminalOutputHistory.Append(plainOutput);
        foreach (var character in plainOutput)
        {
            if (character == '\n')
            {
                _terminalOutputHistoryLineCount++;
            }
        }

        var excessLines = _terminalOutputHistoryLineCount > TerminalOutputHistoryMaxLines
            ? _terminalOutputHistoryLineCount -
              (TerminalOutputHistoryMaxLines - TerminalOutputHistoryTrimSlackLines)
            : 0;
        var excessCharacters = _terminalOutputHistory.Length > TerminalOutputHistoryMaxCharacters
            ? _terminalOutputHistory.Length -
              (TerminalOutputHistoryMaxCharacters - TerminalOutputHistoryTrimSlackCharacters)
            : 0;
        var trimEnd = 0;
        var removedLines = 0;
        while (trimEnd < _terminalOutputHistory.Length && removedLines < excessLines)
        {
            if (_terminalOutputHistory[trimEnd++] == '\n')
            {
                removedLines++;
            }
        }

        while (trimEnd < excessCharacters && trimEnd < _terminalOutputHistory.Length)
        {
            if (_terminalOutputHistory[trimEnd++] == '\n')
            {
                removedLines++;
            }
        }

        if (trimEnd > 0)
        {
            _terminalOutputHistory.Remove(0, trimEnd);
            _terminalOutputHistoryLineCount = Math.Max(0, _terminalOutputHistoryLineCount - removedLines);
        }
    }

    private void ResetTerminalCommandLine()
    {
        ClearTerminalPredictionSurface();
        _terminalCommandBuffer.Clear();
        _terminalPredictionSuffix = "";
        _terminalTrackingCommand = false;
        _terminalAtPrompt = false;
    }

    private async Task ReadTerminalOutputAsync(
        SshClient client,
        ShellStream stream,
        int sessionVersion,
        TaskCompletionSource<bool> shellClosedSignal,
        EventHandler<EventArgs> shellClosedHandler,
        CancellationToken cancellationToken)
    {
        var byteBuffer = new byte[TerminalOutputDrainChunkCharacters];
        var decoder = Encoding.UTF8.GetDecoder();
        var characterBuffer = new char[Encoding.UTF8.GetMaxCharCount(byteBuffer.Length)];
        try
        {
            var reachedEndOfStream = false;
            while (!cancellationToken.IsCancellationRequested && !reachedEndOfStream)
            {
                while (stream.DataAvailable)
                {
                    var bytesRead = stream.Read(byteBuffer, 0, byteBuffer.Length);
                    if (bytesRead == 0)
                    {
                        reachedEndOfStream = true;
                        break;
                    }

                    var charactersRead = decoder.GetChars(
                        byteBuffer,
                        0,
                        bytesRead,
                        characterBuffer,
                        0,
                        flush: false);
                    if (charactersRead > 0)
                    {
                        AppendTerminalOutput(new string(characterBuffer, 0, charactersRead));
                    }
                }

                if (reachedEndOfStream || shellClosedSignal.Task.IsCompleted)
                {
                    while (stream.DataAvailable)
                    {
                        var bytesRead = stream.Read(byteBuffer, 0, byteBuffer.Length);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        var charactersRead = decoder.GetChars(
                            byteBuffer,
                            0,
                            bytesRead,
                            characterBuffer,
                            0,
                            flush: false);
                        if (charactersRead > 0)
                        {
                            AppendTerminalOutput(new string(characterBuffer, 0, charactersRead));
                        }
                    }
                    break;
                }

                var delayTask = Task.Delay(40, cancellationToken);
                var completedTask = await Task.WhenAny(delayTask, shellClosedSignal.Task);
                if (ReferenceEquals(completedTask, delayTask))
                {
                    await delayTask;
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                var trailingCharacters = decoder.GetChars(
                    Array.Empty<byte>(),
                    0,
                    0,
                    characterBuffer,
                    0,
                    flush: true);
                if (trailingCharacters > 0)
                {
                    AppendTerminalOutput(new string(characterBuffer, 0, trailingCharacters));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested &&
                !shellClosedSignal.Task.IsCompleted)
            {
                Log.Error($"SSH terminal read failed: {ex.Message}", ex);
                AppendTerminalOutput($"\r\n[read error: {ex.Message}]\r\n");
            }
        }
        finally
        {
            try
            {
                stream.Closed -= shellClosedHandler;
            }
            catch (Exception ex)
            {
                Log.Debug($"SSH terminal close handler removal failed: {ex.Message}");
            }

            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    InvalidateClosedTerminalSession(
                        client,
                        stream,
                        sessionVersion,
                        shellClosedSignal);
                });
            }
            catch (Exception ex)
            {
                Log.Debug($"SSH terminal close invalidation was not queued: {ex.Message}");
            }
        }
    }

    private void InvalidateClosedTerminalSession(
        SshClient client,
        ShellStream stream,
        int sessionVersion,
        TaskCompletionSource<bool> shellClosedSignal)
    {
        if (sessionVersion != _terminalSessionVersion ||
            !ReferenceEquals(stream, _terminalStream) ||
            !ReferenceEquals(shellClosedSignal, _terminalClosedSignal))
        {
            return;
        }

        ++_terminalSessionVersion;
        var cts = _terminalCts;
        _terminalClient = null;
        _terminalStream = null;
        _terminalClosedSignal = null;
        _terminalCts = null;
        _terminalReadTask = null;
        ResetTerminalCommandLine();
        _terminalRecentPlainOutput = "";
        SetTerminalInputEnabled(false);

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            stream.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"SSH terminal stream cleanup failed: {ex.Message}");
        }

        cts?.Dispose();
        _ = Task.Run(() =>
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"SSH terminal client disconnect failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug($"SSH terminal client cleanup failed: {ex.Message}");
                }
            }
        });
    }

    private void AppendTerminalOutput(string output)
    {
        if (_isDisposed)
        {
            DropQueuedTerminalOutputAfterDispatcherShutdown();
            return;
        }

        if (output.Length == 0)
        {
            return;
        }

        if (output.Length > TerminalMaxPendingCharacters)
        {
            output = output[^TerminalMaxPendingCharacters..];
        }

        lock (_terminalOutputQueueSync)
        {
            _queuedTerminalOutput.Enqueue(output);
            _queuedTerminalOutputCharacters += output.Length;
            while (_queuedTerminalOutputCharacters > TerminalMaxPendingCharacters)
            {
                var excess = _queuedTerminalOutputCharacters - TerminalMaxPendingCharacters;
                var head = _queuedTerminalOutput.Peek();
                var available = head.Length - _queuedTerminalOutputHeadOffset;
                var charactersToDrop = Math.Min(excess, available);
                _queuedTerminalOutputHeadOffset += charactersToDrop;
                _queuedTerminalOutputCharacters -= charactersToDrop;
                if (_queuedTerminalOutputHeadOffset == head.Length)
                {
                    _queuedTerminalOutput.Dequeue();
                    _queuedTerminalOutputHeadOffset = 0;
                }
            }

            if (Interlocked.Exchange(ref _terminalOutputDrainScheduled, 1) != 0)
            {
                return;
            }
        }

        try
        {
            if (!DispatcherQueue.TryEnqueue(DrainTerminalOutput))
            {
                DropQueuedTerminalOutputAfterDispatcherShutdown();
            }
        }
        catch (Exception ex)
        {
            DropQueuedTerminalOutputAfterDispatcherShutdown();
            Log.Debug($"Terminal output drain was not queued: {ex.Message}");
        }
    }

    private void DrainTerminalOutput()
    {
        string output;
        bool hasMoreOutput;
        lock (_terminalOutputQueueSync)
        {
            var charactersToDrain = Math.Min(
                TerminalOutputDrainChunkCharacters,
                _queuedTerminalOutputCharacters);
            var outputBuilder = new StringBuilder(charactersToDrain);
            while (charactersToDrain > 0 && _queuedTerminalOutput.Count > 0)
            {
                var head = _queuedTerminalOutput.Peek();
                var available = head.Length - _queuedTerminalOutputHeadOffset;
                var charactersToTake = Math.Min(charactersToDrain, available);
                outputBuilder.Append(head, _queuedTerminalOutputHeadOffset, charactersToTake);
                _queuedTerminalOutputHeadOffset += charactersToTake;
                _queuedTerminalOutputCharacters -= charactersToTake;
                charactersToDrain -= charactersToTake;
                if (_queuedTerminalOutputHeadOffset == head.Length)
                {
                    _queuedTerminalOutput.Dequeue();
                    _queuedTerminalOutputHeadOffset = 0;
                }
            }

            output = outputBuilder.ToString();
            hasMoreOutput = _queuedTerminalOutputCharacters > 0;
            if (!hasMoreOutput)
            {
                Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
            }
        }

        if (output.Length == 0 || _isDisposed)
        {
            return;
        }

        AddTerminalOutputToHistory(output);
        if (_terminalRendererReady)
        {
            ClearTerminalPredictionSurface();
            _nativeTerminalConnection.WriteOutput(output);
            ObserveTerminalOutput(output);
            RefreshTerminalPrediction();
            ScheduleTerminalPrediction();
        }
        else
        {
            _pendingTerminalOutput.Append(output);
            if (_pendingTerminalOutput.Length > TerminalMaxPendingCharacters)
            {
                _pendingTerminalOutput.Remove(
                    0,
                    _pendingTerminalOutput.Length - TerminalMaxPendingCharacters);
            }
        }

        if (hasMoreOutput)
        {
            try
            {
                if (!DispatcherQueue.TryEnqueue(DrainTerminalOutput))
                {
                    DropQueuedTerminalOutputAfterDispatcherShutdown();
                }
            }
            catch (Exception ex)
            {
                DropQueuedTerminalOutputAfterDispatcherShutdown();
                Log.Debug($"Terminal output continuation was not queued: {ex.Message}");
            }
        }
    }

    private void DropQueuedTerminalOutputAfterDispatcherShutdown()
    {
        lock (_terminalOutputQueueSync)
        {
            _queuedTerminalOutput.Clear();
            _queuedTerminalOutputCharacters = 0;
            _queuedTerminalOutputHeadOffset = 0;
            Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
        }
    }

    private void FlushPendingTerminalOutput()
    {
        if (_pendingTerminalOutput.Length == 0 || !_terminalRendererReady)
        {
            return;
        }

        var output = _pendingTerminalOutput.ToString();
        _pendingTerminalOutput.Clear();
        _nativeTerminalConnection.WriteOutput(output);
        ObserveTerminalOutput(output);
    }

    private void ClearTerminalSurface()
    {
        ClearTerminalPredictionSurface();
        lock (_terminalOutputQueueSync)
        {
            _queuedTerminalOutput.Clear();
            _queuedTerminalOutputCharacters = 0;
            _queuedTerminalOutputHeadOffset = 0;
        }
        _pendingTerminalOutput.Clear();
        _terminalOutputHistory.Clear();
        _terminalOutputHistoryLineCount = 0;
        if (_terminalRendererReady)
        {
            _nativeTerminalConnection.WriteOutput("\x1b[H\x1b[2J\x1b[3J");
        }
    }

    private void SetTerminalInputEnabled(bool enabled)
    {
        _nativeTerminalConnection.SetInputEnabled(enabled);
        if (_terminalRendererReady)
        {
            _nativeTerminalConnection.WriteOutput(enabled ? "\x1b[?25h" : "\x1b[?25l");
        }
    }

    private void FocusTerminal()
    {
        if (!_terminalRendererReady || TerminalPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        NativeTerminal.Focus(FocusState.Programmatic);
    }

    private static string QuoteShellArgument(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsBashScript(FileItem item)
    {
        if (item.IsNavigableDirectory)
        {
            return false;
        }

        var extension = Path.GetExtension(item.Name);
        return extension.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bash", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunBashScriptAsync(FileItem item, bool useSudo)
    {
        if (!IsBashScript(item))
        {
            return;
        }

        await OpenTerminalAsync();
        if (_terminalClient?.IsConnected != true || _terminalStream == null)
        {
            return;
        }

        var lastSeparator = item.FullPath.LastIndexOf('/');
        var workingDirectory = lastSeparator <= 0 ? "/" : item.FullPath[..lastSeparator];
        var sudoPrefix = useSudo ? "sudo -- " : string.Empty;
        var command = $"(cd -- {QuoteShellArgument(workingDirectory)} && {sudoPrefix}bash -- {QuoteShellArgument(item.FullPath)})";

        RememberTerminalCommand(command);
        await WriteTerminalAsync(command + "\r");
    }

    private async Task CloseTerminalAsync()
    {
        HideTerminalPanel();
        await DisconnectTerminalAsync();
    }

    private async Task DisconnectTerminalAsync()
    {
        ++_terminalSessionVersion;
        var client = _terminalClient;
        var stream = _terminalStream;
        var cts = _terminalCts;
        var readTask = _terminalReadTask;

        _terminalClient = null;
        _terminalStream = null;
        _terminalClosedSignal = null;
        _terminalCts = null;
        _terminalReadTask = null;
        ResetTerminalCommandLine();
        _terminalRecentPlainOutput = "";
        SetTerminalInputEnabled(false);

        cts?.Cancel();
        stream?.Dispose();

        if (readTask != null)
        {
            try
            {
                await readTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (client != null)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (client.IsConnected)
                    {
                        client.Disconnect();
                    }
                }
                finally
                {
                    client.Dispose();
                }
            });
        }

        cts?.Dispose();
    }

    public void DisposeTerminal()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _dragPreparationCanceled = true;
        _lifetimeCts.Cancel();
        Interlocked.Exchange(ref _operationCts, null);
        CancellationTokenSource[] activeOperations;
        lock (_activeOperationsSync)
        {
            activeOperations = _activeOperations.ToArray();
        }
        foreach (var activeOperation in activeOperations)
        {
            try
            {
                activeOperation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        CancelPathSuggestions();

        // This method is called only when the tab/window is really closing.
        foreach (var cacheKey in _openFiles.Keys.ToList())
        {
            CleanupOpenFile(cacheKey);
        }

        ++_terminalSessionVersion;
        var client = _terminalClient;
        var stream = _terminalStream;
        var cts = _terminalCts;
        var terminalReadTask = _terminalReadTask;

        _terminalClient = null;
        _terminalStream = null;
        _terminalClosedSignal = null;
        _terminalCts = null;
        _terminalReadTask = null;
        lock (_terminalOutputQueueSync)
        {
            _queuedTerminalOutput.Clear();
            _queuedTerminalOutputCharacters = 0;
            _queuedTerminalOutputHeadOffset = 0;
            Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
        }
        _pendingTerminalOutput.Clear();

        try
        {
            NativeTerminal.Loaded -= NativeTerminal_Loaded;
            NativeTerminal.Connection = null;
            _nativeTerminalConnection.Close();
            NativeTerminal.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning($"Terminal renderer cleanup failed: {ex.Message}");
        }
        _terminalRendererReady = false;

        cts?.Cancel();
        stream?.Dispose();
        var terminalCleanupTask = AwaitTerminalReadAndDisposeAsync(terminalReadTask, cts);

        if (client != null)
        {
            var disconnectTask = Task.Run(() =>
            {
                try
                {
                    if (client.IsConnected)
                    {
                        client.Disconnect();
                    }
                }
                finally
                {
                    client.Dispose();
                }
            }, CancellationToken.None);
            TrackBackgroundTask(disconnectTask);
        }

        var tasksToDrain = new HashSet<Task>();
        lock (_backgroundTasksSync)
        {
            tasksToDrain.UnionWith(_backgroundTasks);
        }
        tasksToDrain.Add(GetActiveOperationsDrainTask());
        tasksToDrain.Add(terminalCleanupTask);
        if (_dragPrepareTask != null)
        {
            tasksToDrain.Add(_dragPrepareTask);
        }
        CloseCleanupTask = DrainCloseTasksAndCleanupRemoteUploadsAsync(tasksToDrain.ToArray());
    }

    private async Task DrainCloseTasksAndCleanupRemoteUploadsAsync(Task[] tasks)
    {
        await DrainCloseTasksAsync(tasks).ConfigureAwait(false);
        await CleanupRemoteUploadStagingFilesAfterCloseAsync().ConfigureAwait(false);
    }

    // Перехватываем правый клик и блокируем drag-and-drop
    private void RemoteFilesListView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        if (props.IsRightButtonPressed)
        {
            _isRightClickInProgress = true;
            e.Handled = true;

            // Показываем контекстное меню напрямую
            DispatcherQueue.TryEnqueue(() => ShowContextMenu(e));
        }
        else
        {
            _isRightClickInProgress = false;
        }
    }


    // Attach DragStarting handler to ListViewItem containers when they are realized
    private void RemoteFilesListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem lvi)
        {
            // Привязываем DragStarting к контейнеру
            lvi.DragStarting -= RemoteFiles_DragStarting;
            lvi.DragStarting += RemoteFiles_DragStarting;

            // Show yellow folder fill only for directories
            if (args.Item is FileItem fileItem)
            {
                var rootGrid = lvi.ContentTemplateRoot as Grid;
                if (rootGrid != null)
                {
                    ApplyFreeSpaceColumnWidth(rootGrid);

                    var iconGrid = rootGrid.Children[0] as Grid;
                    if (iconGrid != null && iconGrid.Children.Count > 0)
                    {
                        var folderFill = iconGrid.Children[0] as FontIcon;
                        if (folderFill != null)
                        {
                            folderFill.Visibility = fileItem.IsDirectory
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                        }
                    }
                }
            }
        }
    }

    private void UpdateFreeSpaceColumnVisibility()
    {
        _isFreeSpaceColumnVisible = RemoteFiles.Any(item => item.HasFileSystemStats);
        FileColumnsHeaderGrid.ColumnDefinitions[2].Width = _isFreeSpaceColumnVisible
            ? FreeSpaceColumnVisibleWidth
            : FreeSpaceColumnHiddenWidth;
        FreeSpaceColumnHeader.Visibility = _isFreeSpaceColumnVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var item in RemoteFiles)
        {
            if (RemoteFilesListView.ContainerFromItem(item) is ListViewItem listItem &&
                listItem.ContentTemplateRoot is Grid rowGrid)
            {
                ApplyFreeSpaceColumnWidth(rowGrid);
            }
        }
    }

    private void ApplyFreeSpaceColumnWidth(Grid rowGrid)
    {
        if (rowGrid.ColumnDefinitions.Count > 2)
        {
            rowGrid.ColumnDefinitions[2].Width = _isFreeSpaceColumnVisible
                ? FreeSpaceColumnVisibleWidth
                : FreeSpaceColumnHiddenWidth;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationIndex > 0)
        {
            _navigationIndex--;
            NavigateToPath(_navigationHistory[_navigationIndex], false);
            UpdateNavigationButtons();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationIndex < _navigationHistory.Count - 1)
        {
            _navigationIndex++;
            NavigateToPath(_navigationHistory[_navigationIndex], false);
            UpdateNavigationButtons();
        }
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sftpClient?.IsConnected != true) return;

        try
        {
            var currentPath = _currentRemotePath.TrimEnd('/');

            // Если уже в корне, не делаем ничего
            if (currentPath == "" || currentPath == "/")
            {
                StatusText.Text = "Already at root directory";
                return;
            }

            // Находим последний слеш и берём всё до него
            var lastSlashIndex = currentPath.LastIndexOf('/');
            string parentPath;

            if (lastSlashIndex <= 0)
            {
                // Родитель - это корень
                parentPath = "/";
            }
            else
            {
                // Берём всё до последнего слеша
                parentPath = currentPath.Substring(0, lastSlashIndex);
                if (string.IsNullOrEmpty(parentPath)) parentPath = "/";
            }

            NavigateToPath(parentPath, true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sftpClient?.IsConnected != true) return;
        NavigateToPath(_sftpClient.WorkingDirectory, true);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRemoteFiles(forceFileSystemRefresh: true);
    }

    private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true) return;

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("CreateFolderTitle"),
            PrimaryButtonText = LocalizationHelper.GetString("CreateButtonDialog"),
            CloseButtonText = LocalizationHelper.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var textBox = new TextBox
        {
            PlaceholderText = LocalizationHelper.GetString("FileNamePlaceholder"),
            Text = LocalizationHelper.GetString("NewFolder")
        };
        dialog.Content = textBox;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            try
            {
                ValidateRemoteEntryNameForPosix(textBox.Text);
                var newFolderPath = CombineRemotePath(_currentRemotePath, textBox.Text);
                await RunClientActionAsync(client, cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (client.Exists(newFolderPath))
                    {
                        throw new IOException($"Remote destination already exists: {newFolderPath}");
                    }

                    client.CreateDirectory(newFolderPath);
                }, _lifetimeCts.Token);
                RefreshRemoteFiles();
                StatusText.Text = string.Format(LocalizationHelper.GetString("FolderCreated"), textBox.Text);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorCreatingFolder"), ex.Message);
            }
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true) return;

        var targetPath = _currentRemotePath;
        bool canWrite;
        try
        {
            canWrite = await CheckWritePermissionAsync(client, targetPath, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        // Проверяем права на запись в текущую папку
        if (!canWrite)
        {
            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("PermissionDenied"),
                Content = string.Format(LocalizationHelper.GetString("NoWritePermission"), targetPath),
                CloseButtonText = LocalizationHelper.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);

        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(windowId)
        {
            SettingsIdentifier = UploadPickerSettingsIdentifier,
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            Title = LocalizationHelper.GetString("UploadPickerTitle") ?? "Select files to upload"
        };
        picker.FileTypeFilter.Add("*");

        var results = await picker.PickMultipleFilesAsync();
        if (results is null || results.Count == 0) return;

        int totalFiles = results.Count;
        int currentFileIndex = 0;
        int succeeded = 0;
        int failed = 0;

        // SDK 1.7+: Show badge with transfer count
        BadgeNotificationService.IncrementTransfer();

        try
        {
            foreach (var result in results)
            {
                try
                {
                    if (string.IsNullOrEmpty(result.Path)) continue;

                    var file = await StorageFile.GetFileFromPathAsync(result.Path);
                    currentFileIndex++;
                    var remotePath = targetPath.TrimEnd('/') + "/" + file.Name;
                    var fileSize = (long)(await file.GetBasicPropertiesAsync()).Size;

                    await UploadFileWithProgress(file, remotePath, fileSize, currentFileIndex, totalFiles, _lifetimeCts.Token);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorUploading"), result.Path, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isDisposed)
            {
                StatusText.Text = LocalizationHelper.GetString("OperationCanceled") ?? "Operation canceled";
            }
            return;
        }
        finally
        {
            // SDK 1.7+: Clear badge when done
            BadgeNotificationService.DecrementTransfer();
        }

        HideProgressBars();
        RefreshRemoteFiles();
        StatusText.Text = failed == 0
            ? string.Format(LocalizationHelper.GetString("FilesUploaded"), succeeded)
            : $"Uploaded {succeeded} of {succeeded + failed} file(s); {failed} failed.";
    }

    private async Task UploadFileWithProgress(
        StorageFile file,
        string remotePath,
        long fileSize,
        int currentIndex,
        int totalFiles,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        using var stream = await file.OpenStreamForReadAsync();
        var client = _sftpClient ?? throw new InvalidOperationException("SFTP client is unavailable.");
        var temporaryRemotePath = CreateRemotePartialPath(remotePath);
        RegisterRemoteUploadStagingPath(temporaryRemotePath);
        long lastProgressTimestamp = 0;

        await RunClientTaskAsync(client, async token =>
        {
            token.ThrowIfCancellationRequested();
            if (client.Exists(remotePath))
            {
                throw new IOException($"Remote destination already exists: {remotePath}");
            }

            try
            {
                var uploadProgress = new InlineProgress<UploadFileProgressReport>(report =>
                {
                    var uploaded = report.TotalBytesUploaded;
                    if (!ShouldPublishProgress(
                            ref lastProgressTimestamp,
                            force: fileSize >= 0 && uploaded >= (ulong)fileSize))
                    {
                        return;
                    }
                    var percent = fileSize > 0 ? (int)((uploaded * 100) / (ulong)fileSize) : 100;
                    var elapsed = (DateTime.Now - startTime).TotalSeconds;
                    var speed = elapsed > 0 ? uploaded / elapsed : 0;
                    var remaining = uploaded >= (ulong)Math.Max(0, fileSize)
                        ? 0
                        : (ulong)fileSize - uploaded;
                    var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isDisposed) return;
                        StatusText.Text = string.Format(LocalizationHelper.GetString("UploadingProgress"), currentIndex, totalFiles, file.Name);
                        ProgressPercent.Text = $"{percent}% ({FormatFileSize((long)uploaded)}/{FormatFileSize(fileSize)})";
                        ProgressSpeed.Text = string.Format(LocalizationHelper.GetString("SpeedPerSecond"), FormatFileSize((long)speed));
                        ProgressETA.Text = string.Format(LocalizationHelper.GetString("TimeRemaining"), FormatTimeSpan(eta));
                        ShowProgressBar(percent);
                    });
                });
                await client.UploadFileAsync(
                    stream,
                    temporaryRemotePath,
                    canOverride: false,
                    uploadProgress,
                    token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                if (client.Exists(remotePath))
                {
                    throw new IOException($"Remote destination appeared while uploading: {remotePath}");
                }

                client.RenameFile(temporaryRemotePath, remotePath);
                UnregisterRemoteUploadStagingPath(temporaryRemotePath);
            }
            catch
            {
                TryDeleteRemoteFile(client, temporaryRemotePath);
                throw;
            }
        }, cancellationToken);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        await DownloadSelectedFiles();
    }

    /// <summary>
    /// Загружает файлы из Windows Explorer (drag-and-drop)
    /// </summary>
    internal async Task UploadFilesFromSystemAsync(List<IStorageItem> items)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true) return;

        var targetPath = _currentRemotePath;
        bool canWrite;
        try
        {
            canWrite = await CheckWritePermissionAsync(client, targetPath, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        // Проверяем права на запись в текущую папку
        if (!canWrite)
        {
            await RunOnUiThreadAsync(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = LocalizationHelper.GetString("PermissionDenied"),
                    Content = string.Format(LocalizationHelper.GetString("NoWritePermission"), targetPath),
                    CloseButtonText = LocalizationHelper.GetString("OK"),
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            });
            return;
        }

        int totalFiles = items.Count;
        int currentFileIndex = 0;
        var summary = new TransferSummary();

        // SDK 1.7+: Show badge with transfer count
        BadgeNotificationService.IncrementTransfer();

        try
        {
            foreach (var item in items)
            {
                try
                {
                    if (item is StorageFile file)
                    {
                        currentFileIndex++;
                        var remotePath = targetPath.TrimEnd('/') + "/" + file.Name;
                        var fileSize = (long)(await file.GetBasicPropertiesAsync()).Size;

                        await UploadFileWithProgress(file, remotePath, fileSize, currentFileIndex, totalFiles, _lifetimeCts.Token);
                        summary += new TransferSummary(1, 0);
                    }
                    else if (item is StorageFolder folder)
                    {
                        // Загружаем папку рекурсивно
                        summary += await UploadFolderRecursiveAsync(folder, targetPath, _lifetimeCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    summary += new TransferSummary(0, 1);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorUploading"), item.Name, ex.Message);
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isDisposed)
            {
                DispatcherQueue.TryEnqueue(() =>
                    StatusText.Text = LocalizationHelper.GetString("OperationCanceled") ?? "Operation canceled");
            }
            return;
        }
        finally
        {
            // SDK 1.7+: Clear badge when done
            BadgeNotificationService.DecrementTransfer();
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            HideProgressBars();
            RefreshRemoteFiles();
            StatusText.Text = summary.Failed == 0
                ? string.Format(LocalizationHelper.GetString("FilesUploaded"), summary.Succeeded)
                : $"Uploaded {summary.Succeeded} item(s); {summary.Failed} failed.";
        });
    }

    private async Task<TransferSummary> UploadFolderRecursiveAsync(
        StorageFolder folder,
        string remoteBasePath,
        CancellationToken cancellationToken)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true) return new TransferSummary(0, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var finalRemotePath = CombineRemotePath(remoteBasePath, folder.Name);
        var stagingRemotePath = CombineRemotePath(remoteBasePath, $".sftpexplorer-{Guid.NewGuid():N}.partial");
        RegisterRemoteUploadStagingPath(stagingRemotePath);
        try
        {
            await RunClientActionAsync(client, token =>
            {
                token.ThrowIfCancellationRequested();
                if (client.Exists(finalRemotePath))
                {
                    throw new IOException($"Remote destination already exists: {finalRemotePath}");
                }
                client.CreateDirectory(stagingRemotePath);
            }, cancellationToken);

            var summary = await UploadFolderContentsAsync(folder, stagingRemotePath, cancellationToken);
            if (summary.Failed > 0)
            {
                await RunClientActionAsync(client, _ =>
                    TryDeleteOwnedRemoteStagingTree(client, remoteBasePath, stagingRemotePath), CancellationToken.None);
                return new TransferSummary(0, Math.Max(1, summary.Failed));
            }

            await RunClientActionAsync(client, token =>
            {
                token.ThrowIfCancellationRequested();
                if (client.Exists(finalRemotePath))
                {
                    throw new IOException($"Remote destination appeared while uploading: {finalRemotePath}");
                }
                client.RenameFile(stagingRemotePath, finalRemotePath);
                UnregisterRemoteUploadStagingPath(stagingRemotePath);
            }, cancellationToken);

            return summary + new TransferSummary(1, 0);
        }
        catch (OperationCanceledException)
        {
            await RunClientActionAsync(client, _ =>
                TryDeleteOwnedRemoteStagingTree(client, remoteBasePath, stagingRemotePath), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Error uploading folder '{finalRemotePath}': {ex.Message}", ex);
            await RunClientActionAsync(client, _ =>
                TryDeleteOwnedRemoteStagingTree(client, remoteBasePath, stagingRemotePath), CancellationToken.None);
            return new TransferSummary(0, 1);
        }
    }

    private async Task<TransferSummary> UploadFolderContentsAsync(
        StorageFolder folder,
        string remoteFolderPath,
        CancellationToken cancellationToken)
    {
        var client = _sftpClient ?? throw new InvalidOperationException("SFTP client is unavailable.");
        var summary = new TransferSummary();
        var files = await folder.GetFilesAsync();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remotePath = CombineRemotePath(remoteFolderPath, file.Name);
                var fileSize = (long)(await file.GetBasicPropertiesAsync()).Size;
                await UploadFileWithProgress(file, remotePath, fileSize, 1, 1, cancellationToken);
                summary += new TransferSummary(1, 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Error uploading file '{file.Name}': {ex.Message}", ex);
                summary += new TransferSummary(0, 1);
            }
        }

        var subfolders = await folder.GetFoldersAsync();
        foreach (var subfolder in subfolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteSubfolderPath = CombineRemotePath(remoteFolderPath, subfolder.Name);
            try
            {
                await RunClientActionAsync(client, token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (client.Exists(remoteSubfolderPath))
                    {
                        throw new IOException($"Duplicate remote destination: {remoteSubfolderPath}");
                    }
                    client.CreateDirectory(remoteSubfolderPath);
                }, cancellationToken);
                summary += new TransferSummary(1, 0);
                summary += await UploadFolderContentsAsync(subfolder, remoteSubfolderPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Error uploading subfolder '{subfolder.Name}': {ex.Message}", ex);
                summary += new TransferSummary(0, 1);
            }
        }

        return summary;
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sftpClient?.IsConnected != true) return;

        var selectedItems = GetSelectedRealItems();
        if (selectedItems.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("DeleteTitle"),
            Content = string.Format(LocalizationHelper.GetString("DeleteConfirmation"), selectedItems.Count),
            PrimaryButtonText = LocalizationHelper.GetString("DeleteButtonDialog"),
            CloseButtonText = LocalizationHelper.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // Prepare cancellation and UI
            var operation = BeginCancelableOperation();
            var token = operation.Token;

            // Show UI elements
            DispatcherQueue.TryEnqueue(() =>
            {
                CancelOperationButton.Visibility = Visibility.Visible;
                CancelOperationButton.IsEnabled = true;
                OverallProgressBar.Visibility = Visibility.Visible;
                ProgressPercent.Visibility = Visibility.Visible;
                ProgressSpeed.Visibility = Visibility.Visible;
                ProgressETA.Visibility = Visibility.Visible;
                StatusText.Text = LocalizationHelper.GetString("Deleting") ?? "Deleting...";
            });

            try
            {
                // Count total files to provide overall progress
                var deleteClient = _sftpClient!;
                var totalFiles = await RunClientResultAsync(
                    deleteClient,
                    cancellationToken => CountFilesForItems(selectedItems, cancellationToken),
                    token);

                // Use atomic counter for deleted files
                int deletedCounter = 0;
                var progress = new Progress<(int deleted, int total, int percent, string current)>((report) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ShowOverallProgress(report.deleted, report.total);
                        ProgressPercent.Text = $"{report.percent}%";
                        StatusText.Text = report.current;
                        ShowProgressBar(report.percent);
                    });
                });

                var failedDeletes = new System.Collections.Concurrent.ConcurrentBag<string>();

                await RunClientActionAsync(deleteClient, cancellationToken =>
                {
                    foreach (var item in selectedItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (item.IsDirectory)
                        {
                            // Recursive delete with progress reporting
                            DeleteDirectoryRecursiveWithProgress(item.FullPath, (name) =>
                            {
                                var current = Interlocked.Increment(ref deletedCounter);
                                var percent = totalFiles > 0 ? (current * 100) / totalFiles : 0;
                                ((IProgress<(int, int, int, string)>)progress).Report((current, totalFiles, percent, $"Deleting {name}"));
                                // Do not throw here; cancellation is handled via token checks inside the recursive method
                            }, cancellationToken, failedDeletes);
                        }
                        else
                        {
                            // Delete single file (handle permission and other errors per-file)
                            try
                            {
                                _sftpClient.DeleteFile(item.FullPath);
                            }
                            catch (Renci.SshNet.Common.SftpPermissionDeniedException pex)
                            {
                                Log.Warning($"Permission denied deleting file {item.FullPath}: {pex.Message}");
                                failedDeletes.Add(item.FullPath);
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Error deleting file {item.FullPath}: {ex.Message}", ex);
                                failedDeletes.Add(item.FullPath);
                            }
                            finally
                            {
                                // Always count the item as processed so progress advances
                                var current = Interlocked.Increment(ref deletedCounter);
                                var percent = totalFiles > 0 ? (current * 100) / totalFiles : 0;
                                ((IProgress<(int, int, int, string)>)progress).Report((current, totalFiles, percent, $"Deleting {item.Name}"));
                                // Do not throw here; cancellation is handled via token checks in the background loop
                            }
                        }
                    }
                }, token);

                // After background run, show dialog if any failed
                if (failedDeletes.Count > 0)
                {
                    var failedList = failedDeletes.ToArray();
                    var preview = string.Join("\n", failedList.Take(25));
                    var more = failedList.Length > 25 ? $"\n... and {failedList.Length - 25} more" : string.Empty;
                    var message = string.Format(LocalizationHelper.GetString("DeleteFailedMessage"), preview + more);

                    await RunOnUiThreadAsync(async () =>
                    {
                        var dlg = new ContentDialog
                        {
                            Title = LocalizationHelper.GetString("DeleteFailedTitle"),
                            Content = message,
                            CloseButtonText = LocalizationHelper.GetString("OK"),
                            XamlRoot = this.XamlRoot
                        };
                        await dlg.ShowAsync();
                    });
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    var succeeded = Math.Max(0, selectedItems.Count - failedDeletes.Count);
                    StatusText.Text = failedDeletes.IsEmpty
                        ? string.Format(LocalizationHelper.GetString("ItemsDeleted"), succeeded)
                        : $"Deleted {succeeded} of {selectedItems.Count} selected item(s); {failedDeletes.Count} failed.";
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() => StatusText.Text = LocalizationHelper.GetString("OperationCanceled") ?? "Operation canceled");
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorDeleting"), ex.Message));
            }
            finally
            {
                CompleteCancelableOperation(operation);
                DispatcherQueue.TryEnqueue(() =>
                {
                    CancelOperationButton.IsEnabled = true;
                    CancelOperationButton.Visibility = Visibility.Collapsed;
                    HideProgressBars();
                    RefreshRemoteFiles();
                });
            }
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_operationCts != null && !_operationCts.IsCancellationRequested)
            {
                _operationCts.Cancel();
                CancelOperationButton.IsEnabled = false;
                StatusText.Text = LocalizationHelper.GetString("Canceling") ?? "Canceling...";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error cancelling operation: {ex.Message}", ex);
        }
    }

    // Synchronous helper executed on a background thread to delete a directory tree with a per-file callback
    private void DeleteDirectoryRecursiveWithProgress(string path, Action<string>? onFileDeleted, CancellationToken token, System.Collections.Concurrent.ConcurrentBag<string>? failedPaths = null)
    {
        if (_sftpClient?.IsConnected != true) return;
        // We handle exceptions per-entry to avoid aborting the whole operation on permission errors
        try
        {
            var entries = _sftpClient.ListDirectory(path).ToList();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                if (entry.Name == "." || entry.Name == "..") continue;

                if (entry.IsDirectory)
                {
                    var childPath = entry.FullName;
                    try
                    {
                        DeleteDirectoryRecursiveWithProgress(childPath, onFileDeleted, token, failedPaths);
                    }
                    catch (Renci.SshNet.Common.SftpPermissionDeniedException pex)
                    {
                        Log.Warning($"Permission denied deleting directory {childPath}: {pex.Message}");
                        failedPaths?.Add(childPath);
                        // mark as processed so progress advances
                        onFileDeleted?.Invoke(Path.GetFileName(childPath));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error deleting directory {childPath}: {ex.Message}", ex);
                        failedPaths?.Add(childPath);
                        onFileDeleted?.Invoke(Path.GetFileName(childPath));
                    }
                }
                else
                {
                    try
                    {
                        // Delete file
                        _sftpClient.DeleteFile(entry.FullName);
                    }
                    catch (Renci.SshNet.Common.SftpPermissionDeniedException pex)
                    {
                        Log.Warning($"Permission denied deleting file {entry.FullName}: {pex.Message}");
                        failedPaths?.Add(entry.FullName);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error deleting file {entry.FullName}: {ex.Message}", ex);
                        failedPaths?.Add(entry.FullName);
                    }
                    finally
                    {
                        // Notify progress even if deletion failed so overall progress advances
                        onFileDeleted?.Invoke(entry.Name);
                    }
                }
            }

            // Now delete the directory itself
            try
            {
                _sftpClient.DeleteDirectory(path);
            }
            catch (Renci.SshNet.Common.SftpPermissionDeniedException pex)
            {
                Log.Warning($"Permission denied deleting directory {path}: {pex.Message}");
                failedPaths?.Add(path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Error deleting directory {path}: {ex.Message}", ex);
                failedPaths?.Add(path);
            }
            finally
            {
                onFileDeleted?.Invoke(Path.GetFileName(path));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // If we can't list the directory, log and treat it as processed so progress continues
            Log.Warning($"Error listing/deleting {path}: {ex.Message}");
            onFileDeleted?.Invoke(Path.GetFileName(path));
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        RemoteFilesListView.SelectAll();
    }

    private List<FileItem> GetSelectedRealItems() =>
        RemoteFilesListView.SelectedItems.Cast<FileItem>()
            .Where(item => !item.IsVirtualRoot)
            .ToList();

    private void RemoteFilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedCount = GetSelectedRealItems().Count;

        CutButton.IsEnabled = selectedCount > 0;
        CopyButton.IsEnabled = selectedCount > 0;
        RenameButton.IsEnabled = selectedCount == 1;
        DeleteButton.IsEnabled = selectedCount > 0;
        DownloadButton.IsEnabled = selectedCount > 0;
        PasteButton.IsEnabled = _clipboard.Count > 0;

        UpdateItemCount();
    }

    private async void NewFileButton_Click(object sender, RoutedEventArgs e)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true) return;

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("CreateFileTitle"),
            PrimaryButtonText = LocalizationHelper.GetString("CreateButtonDialog"),
            CloseButtonText = LocalizationHelper.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var textBox = new TextBox
        {
            PlaceholderText = LocalizationHelper.GetString("FileNamePlaceholder"),
            Text = LocalizationHelper.GetString("DefaultFileName")
        };
        dialog.Content = textBox;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            try
            {
                ValidateRemoteEntryNameForPosix(textBox.Text);
                var newFilePath = CombineRemotePath(_currentRemotePath, textBox.Text);
                await RunClientActionAsync(client, cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (client.Exists(newFilePath))
                    {
                        throw new IOException($"Remote destination already exists: {newFilePath}");
                    }

                    using var stream = new MemoryStream();
                    client.UploadFile(stream, newFilePath, false);
                }, _lifetimeCts.Token);
                RefreshRemoteFiles();
                StatusText.Text = string.Format(LocalizationHelper.GetString("FileCreated"), textBox.Text);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorCreatingFile"), ex.Message);
            }
        }
    }

    private void CutButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = GetSelectedRealItems();
        if (selectedItems.Count == 0) return;

        _clipboard = selectedItems;
        _clipboardIsCut = true;
        PasteButton.IsEnabled = true;
        StatusText.Text = string.Format(LocalizationHelper.GetString("ItemsCut"), _clipboard.Count);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = GetSelectedRealItems();
        if (selectedItems.Count == 0) return;

        _clipboard = selectedItems;
        _clipboardIsCut = false;
        PasteButton.IsEnabled = true;
        StatusText.Text = string.Format(LocalizationHelper.GetString("ItemsCopied"), _clipboard.Count);
    }

    private async void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true || _clipboard.Count == 0) return;

        var operation = BeginCancelableOperation();
        var token = operation.Token;
        var clipboardSnapshot = _clipboard.ToList();
        var wasCut = _clipboardIsCut;
        var succeeded = 0;
        var failures = new List<string>();
        var failedCutItems = new List<FileItem>();

        CancelOperationButton.Visibility = Visibility.Visible;
        CancelOperationButton.IsEnabled = true;

        try
        {
            foreach (var item in clipboardSnapshot)
            {
                token.ThrowIfCancellationRequested();
                var destPath = CombineRemotePath(_currentRemotePath, item.Name);

                try
                {
                    if (item.IsSymbolicLink)
                    {
                        throw new NotSupportedException($"Copying or moving symbolic links is not supported: {item.FullPath}");
                    }
                    ValidateRemoteCopyDestination(item.FullPath, destPath, item.IsDirectory);
                    if (wasCut)
                    {
                        await RunClientActionAsync(client, cancellationToken =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (client.Exists(destPath))
                            {
                                throw new IOException($"Remote destination already exists: {destPath}");
                            }

                            client.RenameFile(item.FullPath, destPath);
                        }, token);
                    }
                    else if (item.IsDirectory)
                    {
                        await CopyDirectoryRecursive(item.FullPath, destPath, token);
                    }
                    else
                    {
                        await RunClientActionAsync(client, cancellationToken =>
                            CopyRemoteFileBounded(client, item.FullPath, destPath, cancellationToken), token);
                    }

                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to paste '{item.FullPath}' to '{destPath}': {ex.Message}", ex);
                    failures.Add($"{item.Name}: {ex.Message}");
                    if (wasCut)
                    {
                        failedCutItems.Add(item);
                    }
                }
            }

            if (wasCut)
            {
                _clipboard = failedCutItems;
                PasteButton.IsEnabled = _clipboard.Count > 0;
            }

            RefreshRemoteFiles();
            StatusText.Text = failures.Count == 0
                ? (wasCut ? LocalizationHelper.GetString("ItemsMoved") : LocalizationHelper.GetString("ItemsPasted"))
                : $"Completed {succeeded} of {clipboardSnapshot.Count} item(s); {failures.Count} failed. {failures[0]}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = LocalizationHelper.GetString("OperationCanceled") ?? "Operation canceled";
        }
        finally
        {
            CompleteCancelableOperation(operation);
            CancelOperationButton.Visibility = Visibility.Collapsed;
            CancelOperationButton.IsEnabled = true;
            HideProgressBars();
        }
    }

    private async Task CopyDirectoryRecursive(
        string sourcePath,
        string destPath,
        CancellationToken cancellationToken)
    {
        var client = _sftpClient ?? throw new InvalidOperationException("SFTP client is unavailable.");
        ValidateRemoteCopyDestination(sourcePath, destPath, isDirectory: true);

        await RunClientActionAsync(client, token =>
        {
            token.ThrowIfCancellationRequested();
            if (client.Exists(destPath))
            {
                throw new IOException($"Remote destination already exists: {destPath}");
            }

            var parent = GetRemoteParentPath(destPath);
            var stagingPath = CombineRemotePath(parent, $".sftpexplorer-{Guid.NewGuid():N}.partial");
            try
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                CopyRemoteDirectoryInto(client, sourcePath, stagingPath, visited, token);
                token.ThrowIfCancellationRequested();
                if (client.Exists(destPath))
                {
                    throw new IOException($"Remote destination appeared while copying: {destPath}");
                }

                client.RenameFile(stagingPath, destPath);
            }
            catch
            {
                TryDeleteOwnedRemoteStagingTree(client, parent, stagingPath);
                throw;
            }
        }, cancellationToken);
    }

    private void CopyRemoteDirectoryInto(
        SftpClient client,
        string sourcePath,
        string destinationPath,
        ISet<string> visitedSourceDirectories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalSource = CanonicalizeRemotePath(sourcePath);
        if (!visitedSourceDirectories.Add(canonicalSource))
        {
            throw new IOException($"A directory cycle was detected at '{sourcePath}'.");
        }

        client.CreateDirectory(destinationPath);
        foreach (var entry in client.ListDirectory(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..") continue;
            ValidateRemoteEntryNameForPosix(entry.Name);

            if (entry.IsSymbolicLink)
            {
                throw new NotSupportedException($"Copying symbolic links is not supported: {entry.FullName}");
            }

            var sourceChild = CombineRemotePath(sourcePath, entry.Name);
            var destinationChild = CombineRemotePath(destinationPath, entry.Name);
            if (entry.IsDirectory)
            {
                CopyRemoteDirectoryInto(client, sourceChild, destinationChild, visitedSourceDirectories, cancellationToken);
            }
            else
            {
                CopyRemoteFileBounded(client, sourceChild, destinationChild, cancellationToken, entry.Length);
            }
        }
    }

    private void CopyRemoteFileBounded(
        SftpClient client,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        long? knownFileSize = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRemoteCopyDestination(sourcePath, destinationPath, isDirectory: false);
        if (client.Exists(destinationPath))
        {
            throw new IOException($"Remote destination already exists: {destinationPath}");
        }

        var temporaryPath = CreateRemotePartialPath(destinationPath);
        RegisterRemoteUploadStagingPath(temporaryPath);
        var fileSize = knownFileSize ?? client.Get(sourcePath).Length;
        var startTime = DateTime.UtcNow;
        long copied = 0;
        long lastProgressTimestamp = 0;

        try
        {
            using (var input = client.OpenRead(sourcePath))
            using (var output = client.OpenWrite(temporaryPath))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    copied += read;

                    if (!ShouldPublishProgress(
                            ref lastProgressTimestamp,
                            force: fileSize >= 0 && copied >= fileSize))
                    {
                        continue;
                    }

                    var copiedSnapshot = copied;
                    var elapsed = Math.Max(0.001, (DateTime.UtcNow - startTime).TotalSeconds);
                    var percent = fileSize > 0 ? (int)Math.Clamp(copiedSnapshot * 100L / fileSize, 0, 100) : 0;
                    var speed = copiedSnapshot / elapsed;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isDisposed) return;
                        StatusText.Text = LocalizationHelper.GetString("Copying");
                        ProgressPercent.Text = $"{percent}% ({FormatFileSize(copiedSnapshot)}/{FormatFileSize(fileSize)})";
                        ProgressSpeed.Text = string.Format(LocalizationHelper.GetString("SpeedPerSecond"), FormatFileSize((long)speed));
                        ShowProgressBar(percent);
                    });
                }

                output.Flush();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (client.Exists(destinationPath))
            {
                throw new IOException($"Remote destination appeared while copying: {destinationPath}");
            }
            client.RenameFile(temporaryPath, destinationPath);
            UnregisterRemoteUploadStagingPath(temporaryPath);
        }
        catch
        {
            TryDeleteRemoteFile(client, temporaryPath);
            throw;
        }
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true) return;

        var selectedItems = GetSelectedRealItems();
        if (selectedItems.Count != 1) return;

        var item = selectedItems[0];

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("RenameTitle"),
            PrimaryButtonText = LocalizationHelper.GetString("RenameButtonDialog"),
            CloseButtonText = LocalizationHelper.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var textBox = new TextBox
        {
            PlaceholderText = LocalizationHelper.GetString("NewNamePlaceholder"),
            Text = item.Name
        };
        dialog.Content = textBox;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            try
            {
                ValidateRemoteEntryNameForPosix(textBox.Text);
                var newPath = CombineRemotePath(_currentRemotePath, textBox.Text);
                if (!string.Equals(
                        CanonicalizeRemotePath(item.FullPath),
                        CanonicalizeRemotePath(newPath),
                        StringComparison.Ordinal))
                {
                    await RunClientActionAsync(client, cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (client.Exists(newPath))
                        {
                            throw new IOException($"Remote destination already exists: {newPath}");
                        }

                        client.RenameFile(item.FullPath, newPath);
                    }, _lifetimeCts.Token);
                }
                RefreshRemoteFiles();
                StatusText.Text = string.Format(LocalizationHelper.GetString("RenamedTo"), textBox.Text);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorRenaming"), ex.Message);
            }
        }
    }

    private void SortByNameAsc_Click(object sender, RoutedEventArgs e)
    {
        SortDisplayedItems(items => items.OrderBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Name));
        StatusText.Text = LocalizationHelper.GetString("SortedByNameAsc");
    }

    private void SortByNameDesc_Click(object sender, RoutedEventArgs e)
    {
        SortDisplayedItems(items => items.OrderBy(f => f.IsDirectory ? 0 : 1).ThenByDescending(f => f.Name));
        StatusText.Text = LocalizationHelper.GetString("SortedByNameDesc");
    }

    private void SortBySizeAsc_Click(object sender, RoutedEventArgs e)
    {
        SortDisplayedItems(items => items.OrderBy(f => f.IsDirectory ? 0 : 1)
            .ThenBy(f => f.IsDirectory ? 0 : ParseSize(f.Size)));
        StatusText.Text = LocalizationHelper.GetString("SortedBySizeAsc");
    }

    private void SortBySizeDesc_Click(object sender, RoutedEventArgs e)
    {
        SortDisplayedItems(items => items.OrderBy(f => f.IsDirectory ? 0 : 1)
            .ThenByDescending(f => f.IsDirectory ? 0 : ParseSize(f.Size)));
        StatusText.Text = LocalizationHelper.GetString("SortedBySizeDesc");
    }

    private void SortByDateAsc_Click(object sender, RoutedEventArgs e)
    {
        SortDisplayedItems(items => items.OrderBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Modified));
        StatusText.Text = LocalizationHelper.GetString("SortedByDateAsc");
    }

    private void SortByDateDesc_Click(object sender, RoutedEventArgs e)
    {
        SortDisplayedItems(items => items.OrderBy(f => f.IsDirectory ? 0 : 1).ThenByDescending(f => f.Modified));
        StatusText.Text = LocalizationHelper.GetString("SortedByDateDesc");
    }

    private void SortDisplayedItems(Func<IEnumerable<FileItem>, IEnumerable<FileItem>> sort)
    {
        var virtualRoot = RemoteFiles.FirstOrDefault(item => item.IsVirtualRoot);
        var sorted = sort(RemoteFiles.Where(item => !item.IsVirtualRoot)).ToList();

        RemoteFiles.Clear();
        if (virtualRoot != null)
        {
            RemoteFiles.Add(virtualRoot);
        }

        foreach (var item in sorted)
        {
            RemoteFiles.Add(item);
        }
    }

    private long ParseSize(string sizeStr)
    {
        if (sizeStr == "<DIR>") return 0;

        var parts = sizeStr.Split(' ');
        if (parts.Length != 2 || !double.TryParse(parts[0], out var value)) return 0;

        return parts[1] switch
        {
            "B" => (long)value,
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1024 * 1024 * 1024),
            "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
            _ => 0
        };
    }

    private async void PropertiesButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = RemoteFilesListView.SelectedItems.Cast<FileItem>().ToList();
        if (selectedItems.Count != 1) return;

        var item = selectedItems[0];
        if (item.IsVirtualRoot)
        {
            await ShowFileSystemPropertiesAsync(item);
            return;
        }

        var typeText = item.IsDirectory ? LocalizationHelper.GetString("PropertiesTypeFolder") : LocalizationHelper.GetString("PropertiesTypeFile");
        var info = string.Format(LocalizationHelper.GetString("PropertiesName"), item.Name) + "\n" +
                   string.Format(LocalizationHelper.GetString("PropertiesPath"), item.FullPath) + "\n" +
                   string.Format(LocalizationHelper.GetString("PropertiesSize"), item.Size) + "\n" +
                   string.Format(LocalizationHelper.GetString("PropertiesModified"), item.Modified) + "\n" +
                   string.Format(LocalizationHelper.GetString("PropertiesPermissions"), item.Permissions) + "\n" +
                   string.Format(LocalizationHelper.GetString("PropertiesType"), typeText);

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("PropertiesTitle"),
            Content = new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = LocalizationHelper.GetString("Close"),
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private async Task ShowFileSystemPropertiesAsync(FileItem item)
    {
        var lines = new List<string>
        {
            string.Format(LocalizationHelper.GetString("PropertiesName"), item.Name),
            string.Format(LocalizationHelper.GetString("PropertiesPath"), item.FullPath),
            string.Format(LocalizationHelper.GetString("PropertiesType"), LocalizationHelper.GetString("PropertiesTypeFileSystem"))
        };

        if (item.HasFileSystemStats)
        {
            lines.Add(string.Format(
                LocalizationHelper.GetString("PropertiesCapacity"),
                FormatDiskSpace(item.FileSystemTotalBytes)));
            lines.Add(string.Format(
                LocalizationHelper.GetString("PropertiesUsedSpace"),
                FormatDiskSpace(item.FileSystemUsedBytes)));
            lines.Add(string.Format(
                LocalizationHelper.GetString("PropertiesFreeSpace"),
                FormatDiskSpace(item.FileSystemAvailableBytes)));
        }

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("PropertiesTitle"),
            Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = LocalizationHelper.GetString("Close"),
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private async void PathBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        CancelPathSuggestions();
        var suggestionCts = new CancellationTokenSource();
        _pathSuggestionCts = suggestionCts;

        try
        {
            await Task.Delay(180, suggestionCts.Token);
            var (directory, namePrefix) = SplitAddressSuggestionInput(sender.Text);
            var suggestions = await GetAddressSuggestionsAsync(
                directory,
                namePrefix,
                suggestionCts.Token);

            suggestionCts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_pathSuggestionCts, suggestionCts))
            {
                return;
            }

            sender.ItemsSource = suggestions;
            sender.IsSuggestionListOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"Address suggestions unavailable: {ex.Message}");
            if (ReferenceEquals(_pathSuggestionCts, suggestionCts))
            {
                sender.ItemsSource = null;
                sender.IsSuggestionListOpen = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_pathSuggestionCts, suggestionCts))
            {
                _pathSuggestionCts = null;
            }
            suggestionCts.Dispose();
        }
    }

    private void PathBox_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is AddressSuggestion suggestion)
        {
            sender.Text = suggestion.FullPath;
        }
    }

    private async void PathBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var path = args.ChosenSuggestion is AddressSuggestion suggestion
            ? suggestion.FullPath
            : args.QueryText;
        ExitAddressEditMode();
        await NavigateOrOpenAddressPathAsync(path);
    }

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        ExitAddressEditMode();
    }

    private async void RemoteFiles_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && element.DataContext is FileItem item)
        {
            if (item.IsVirtualRoot)
            {
                RefreshRemoteFiles(forceFileSystemRefresh: true);
            }
            else if (item.IsSymbolicLink)
            {
                await OpenSymbolicLinkAsync(item);
            }
            else if (item.IsDirectory)
            {
                if (item.CanRead || IsSudoBrowsePath(item.FullPath))
                {
                    NavigateToPath(item.FullPath, true);
                }
                else
                {
                    await TryOpenDirectoryWithSudoAsync(item.FullPath);
                }
            }
            else
            {
                if (item.CanRead)
                {
                    // Открываем файл в редакторе по умолчанию
                    await OpenFileInDefaultEditor(item);
                }
                else
                {
                    await TryOpenFileWithSudoAsync(item);
                }
            }
        }
    }

    private async Task OpenSymbolicLinkAsync(FileItem item)
    {
        var connectionInfo = _sftpClient?.ConnectionInfo;
        if (connectionInfo == null)
        {
            return;
        }

        try
        {
            var useSudo = IsSudoBrowsePath(item.FullPath);
            var targetPath = await Task.Run(() =>
            {
                using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);

                var sudoPrefix = useSudo ? "sudo -n " : string.Empty;
                using var command = sshClient.CreateCommand(
                    $"{sudoPrefix}readlink -f -- {QuotePosixShellArgument(item.FullPath)}");
                command.CommandTimeout = TimeSpan.FromSeconds(15);
                var output = command.Execute().TrimEnd('\r', '\n');

                if (command.ExitStatus != 0 || string.IsNullOrWhiteSpace(output))
                {
                    var error = string.IsNullOrWhiteSpace(command.Error)
                        ? string.Format(LocalizationHelper.GetString("DragSudoExitCode"), command.ExitStatus)
                        : command.Error.Trim();
                    throw new IOException(error);
                }

                return NormalizeRemotePath(output);
            });

            var targetIsDirectory = item.SymbolicLinkTargetIsDirectory ||
                await Task.Run(() => _sftpClient!.GetAttributes(targetPath).IsDirectory);
            if (targetIsDirectory)
            {
                if (item.CanRead || IsSudoBrowsePath(targetPath))
                {
                    NavigateToPath(targetPath, true);
                }
                else
                {
                    await TryOpenDirectoryWithSudoAsync(targetPath);
                }
                return;
            }

            var targetItem = new FileItem
            {
                Name = item.Name,
                FullPath = targetPath,
                Size = item.Size,
                SizeBytes = item.SizeBytes,
                Modified = item.Modified,
                Permissions = item.Permissions,
                Owner = item.Owner,
                Group = item.Group,
                Icon = item.Icon,
                CanRead = item.CanRead
            };
            if (targetItem.CanRead)
            {
                await OpenFileInDefaultEditor(targetItem);
            }
            else
            {
                await TryOpenFileWithSudoAsync(targetItem);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open symbolic link '{item.FullPath}': {ex.Message}", ex);
            StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorOpeningFile"), ex.Message);

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("ErrorDialogTitle"),
                Content = string.Format(LocalizationHelper.GetString("ErrorOpeningFileDialog"), item.Name, ex.Message),
                CloseButtonText = LocalizationHelper.GetString("OK"),
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private async Task OpenFileInDefaultEditor(FileItem item)
    {
        var client = _sftpClient;
        if (client?.IsConnected != true) return;

        try
        {
            string remotePath = item.FullPath;
            string cacheKey = $"{_connectionId}:{remotePath}";
            string tempFilePath;

            // Проверяем, открыт ли уже этот файл
            if (_openFiles.TryGetValue(cacheKey, out var existingFileInfo))
            {
                // Файл уже открыт - используем существующий временный файл
                tempFilePath = existingFileInfo.LocalPath;
                StatusText.Text = string.Format(LocalizationHelper.GetString("FileAlreadyOpen"), item.Name);
            }
            else
            {
                // Первое открытие - скачиваем файл
                StatusText.Text = string.Format(LocalizationHelper.GetString("LoadingFile"), item.Name);

                // Создаём временную папку для SFTP файлов
                var tempSftpFolder = Path.Combine(Path.GetTempPath(), "SftpExplorer");
                Directory.CreateDirectory(tempSftpFolder);

                // Создаём уникальную подпапку на основе хеша пути, чтобы избежать конфликтов
                // при открытии файлов с одинаковыми именами из разных папок
                var uniqueId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{_connectionId}\0{item.FullPath}")));
                var hashedFolder = LocalPathSafety.CombineChild(tempSftpFolder, uniqueId);
                var uniqueFolder = LocalPathSafety.CombineChild(hashedFolder, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(uniqueFolder);

                // Используем оригинальное имя файла
                tempFilePath = LocalPathSafety.CombineChild(uniqueFolder, item.Name);

                // Скачиваем файл
                var fileSize = await RunClientResultAsync(client, cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return client.Get(item.FullPath).Length;
                }, _lifetimeCts.Token);
                var startTime = DateTime.Now;

                await DownloadFileToLocalAtomicAsync(
                    client,
                    item.FullPath,
                    tempFilePath,
                    downloaded =>
                {
                    var percent = fileSize > 0 ? (int)((downloaded * 100) / (ulong)fileSize) : 100;
                    var elapsed = (DateTime.Now - startTime).TotalSeconds;
                    var speed = elapsed > 0 ? downloaded / elapsed : 0;
                    var remaining = downloaded >= (ulong)Math.Max(0, fileSize)
                        ? 0
                        : (ulong)fileSize - downloaded;
                    var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isDisposed) return;
                        StatusText.Text = string.Format(LocalizationHelper.GetString("LoadingFile"), item.Name);
                        ProgressPercent.Text = $"{percent}% ({FormatFileSize((long)downloaded)}/{FormatFileSize(fileSize)})";
                        ProgressSpeed.Text = string.Format(LocalizationHelper.GetString("SpeedPerSecond"), FormatFileSize((long)speed));
                        ProgressETA.Text = string.Format(LocalizationHelper.GetString("TimeRemaining"), FormatTimeSpan(eta));
                        ShowProgressBar(percent);
                    });
                },
                _lifetimeCts.Token);

                // Сохраняем информацию об открытом файле
                var fileInfo = new OpenFileInfo
                {
                    RemotePath = remotePath,
                    LocalPath = tempFilePath,
                    LastWriteTime = File.GetLastWriteTimeUtc(tempFilePath),
                    LastUploadedLength = new FileInfo(tempFilePath).Length
                };
                _openFiles[cacheKey] = fileInfo;

                // Создаём семафор для синхронизации загрузок
                _uploadLocks[cacheKey] = new SemaphoreSlim(1, 1);

                // Настраиваем FileSystemWatcher для отслеживания изменений
                var watcher = new FileSystemWatcher
                {
                    Path = Path.GetDirectoryName(tempFilePath)!,
                    Filter = Path.GetFileName(tempFilePath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                watcher.Changed += (_, e) => QueueAutoSync(e.FullPath, cacheKey, item.FullPath);
                _fileWatchers[cacheKey] = watcher;

                HideProgressBars();
                StatusText.Text = string.Format(LocalizationHelper.GetString("FileLoadedAutoSync"), item.Name);
            }

            // Открываем в редакторе по умолчанию
            var processStartInfo = new ProcessStartInfo
            {
                FileName = tempFilePath,
                UseShellExecute = true,
                Verb = "open"
            };

            Process.Start(processStartInfo);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorOpeningFile"), ex.Message);

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("ErrorDialogTitle"),
                Content = string.Format(LocalizationHelper.GetString("ErrorOpeningFileDialog"), item.Name, ex.Message),
                CloseButtonText = LocalizationHelper.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private void QueueAutoSync(string localPath, string cacheKey, string remotePath)
    {
        lock (_autoSyncSync)
        {
            if (!_autoSyncScheduled.Add(cacheKey))
            {
                return;
            }
        }

        var task = RunAutoSyncWorkerAsync(localPath, cacheKey, remotePath);
        TrackBackgroundTask(task);
    }

    private async Task RunAutoSyncWorkerAsync(string localPath, string cacheKey, string remotePath)
    {
        try
        {
            if (!_openFiles.TryGetValue(cacheKey, out var fileInfo) ||
                !_uploadLocks.TryGetValue(cacheKey, out var uploadLock))
            {
                return;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token,
                fileInfo.SyncCancellation.Token);
            var token = linkedCts.Token;
            await Task.Delay(500, token);
            var retry = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(localPath) || !_openFiles.ContainsKey(cacheKey)) return;

                await uploadLock.WaitAsync(token);
                try
                {
                    var localInfo = new FileInfo(localPath);
                    localInfo.Refresh();
                    if (localInfo.LastWriteTimeUtc <= fileInfo.LastWriteTime &&
                        localInfo.Length == fileInfo.LastUploadedLength)
                    {
                        return;
                    }

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isDisposed) return;
                        StatusText.Text = string.Format(LocalizationHelper.GetString("FileSyncing"), Path.GetFileName(localPath));
                    });

                    var uploadedStableSnapshot = await UploadLocalSnapshotReplacingAsync(
                        localPath,
                        remotePath,
                        localInfo.LastWriteTimeUtc,
                        localInfo.Length,
                        token);
                    if (!uploadedStableSnapshot)
                    {
                        retry = 0;
                        await Task.Delay(500, token);
                        continue;
                    }

                    fileInfo.LastWriteTime = localInfo.LastWriteTimeUtc;
                    fileInfo.LastUploadedLength = localInfo.Length;
                    retry = 0;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isDisposed) return;
                        StatusText.Text = string.Format(LocalizationHelper.GetString("ChangesSynced"), Path.GetFileName(localPath));
                        RefreshRemoteFiles();
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    retry++;
                    Log.Warning($"Auto-sync attempt {retry} failed for '{remotePath}': {ex.Message}");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isDisposed) return;
                        StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorUploadingChanges"), ex.Message);
                    });
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(retry, 5))), token);
                }
                finally
                {
                    uploadLock.Release();
                }

                // Re-read metadata after a successful upload. If another watcher event
                // arrived while the semaphore was held, the worker stays alive and
                // synchronizes the newer dirty version instead of dropping it.
                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the file/tab is the expected shutdown path.
        }
        catch (Exception ex)
        {
            Log.Error($"Auto-sync worker failed for '{remotePath}': {ex.Message}", ex);
        }
        finally
        {
            var shouldRestart = false;
            lock (_autoSyncSync)
            {
                _autoSyncScheduled.Remove(cacheKey);
                if (!_isDisposed && _openFiles.TryGetValue(cacheKey, out var currentInfo) && File.Exists(localPath))
                {
                    var currentLocalInfo = new FileInfo(localPath);
                    currentLocalInfo.Refresh();
                    shouldRestart = currentLocalInfo.LastWriteTimeUtc > currentInfo.LastWriteTime ||
                                    currentLocalInfo.Length != currentInfo.LastUploadedLength;
                }
            }

            if (shouldRestart)
            {
                QueueAutoSync(localPath, cacheKey, remotePath);
            }
        }
    }

    private async Task<bool> UploadLocalSnapshotReplacingAsync(
        string localPath,
        string remotePath,
        DateTime expectedWriteTimeUtc,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var client = _sftpClient ?? throw new InvalidOperationException("SFTP client is unavailable.");
        var temporaryRemotePath = CreateRemotePartialPath(remotePath);
        RegisterRemoteUploadStagingPath(temporaryRemotePath);
        var stable = true;

        await RunClientActionAsync(client, token =>
        {
            try
            {
                using (var fileStream = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024,
                    FileOptions.SequentialScan))
                {
                    client.UploadFile(fileStream, temporaryRemotePath, false, _ => token.ThrowIfCancellationRequested());
                }

                token.ThrowIfCancellationRequested();
                var afterUpload = new FileInfo(localPath);
                afterUpload.Refresh();
                stable = afterUpload.Exists &&
                         afterUpload.LastWriteTimeUtc == expectedWriteTimeUtc &&
                         afterUpload.Length == expectedLength;
                if (!stable)
                {
                    TryDeleteRemoteFile(client, temporaryRemotePath);
                    return;
                }

                CommitRemoteReplacement(client, temporaryRemotePath, remotePath);
                UnregisterRemoteUploadStagingPath(temporaryRemotePath);
            }
            catch
            {
                TryDeleteRemoteFile(client, temporaryRemotePath);
                throw;
            }
        }, cancellationToken);

        return stable;
    }

    private void CleanupOpenFile(string cacheKey)
    {
        if (_openFiles.TryGetValue(cacheKey, out var fileInfo))
        {
            try
            {
                // Останавливаем watcher
                if (_fileWatchers.TryGetValue(cacheKey, out var watcher))
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    _fileWatchers.TryRemove(cacheKey, out _);
                }

                fileInfo.SyncCancellation.Cancel();

                // Удаляем только файл из собственного cache root; никогда не
                // рекурсивно удаляем каталог, полученный из remote input.
                var cacheRoot = Path.Combine(Path.GetTempPath(), "SftpExplorer");
                LocalPathSafety.EnsureStrictDescendant(cacheRoot, fileInfo.LocalPath);
                if (File.Exists(fileInfo.LocalPath) && !Directory.Exists(fileInfo.LocalPath))
                {
                    File.SetAttributes(fileInfo.LocalPath, System.IO.FileAttributes.Normal);
                    File.Delete(fileInfo.LocalPath);
                }

                // Освобождаем семафор
                if (_uploadLocks.TryGetValue(cacheKey, out var uploadLock))
                {
                    // A queued worker may still release this semaphore after the
                    // cancellation above. Removing the last strong reference is
                    // safe; disposing it here would race with that release.
                    _uploadLocks.TryRemove(cacheKey, out _);
                }

                _openFiles.TryRemove(cacheKey, out _);
            }
            catch (Exception ex)
            {
                Log.Error($"Error cleaning up file: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Navigate to a specific path (public wrapper for duplication)
    /// </summary>
    public void NavigateToPath(string path)
    {
        NavigateToPath(path, true);
    }

    private void NavigateToPath(string path, bool addToHistory)
    {
        if (_sftpClient?.IsConnected != true) return;

        try
        {
            Log.Debug($"NavigateToPath: {path}, addToHistory: {addToHistory}");
            _currentRemotePath = path;
            NotifyCurrentFolderChanged();

            if (addToHistory)
            {
                // Удаляем всё что после текущей позиции
                if (_navigationIndex < _navigationHistory.Count - 1)
                {
                    _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
                }

                // Добавляем новый путь
                _navigationHistory.Add(path);
                _navigationIndex = _navigationHistory.Count - 1;

                UpdateNavigationButtons();
            }

            RefreshRemoteFiles();
            Log.Debug($"Calling UpdateBreadcrumb for path: {_currentRemotePath}");
            UpdateBreadcrumb();
            Log.Debug($"UpdateBreadcrumb completed, panel children: {BreadcrumbPanel.Children.Count}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _navigationIndex > 0;
        ForwardButton.IsEnabled = _navigationIndex < _navigationHistory.Count - 1;
    }

    private void NotifyCurrentFolderChanged()
    {
        CurrentFolderChanged?.Invoke(CurrentFolderName);
    }

    private static string GetFolderDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return "/";
        }

        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    private void UpdateBreadcrumb()
    {
        BreadcrumbPanel.Children.Clear();

        // Home button
        var homeBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE80F", FontSize = 14 },
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        homeBtn.Click += (s, e) => HomeButton_Click(s, e);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(homeBtn, LocalizationHelper.GetString("HomeButton"));
        BreadcrumbPanel.Children.Add(homeBtn);

        if (_currentRemotePath == "/" || string.IsNullOrEmpty(_currentRemotePath))
        {
            return;
        }

        var parts = _currentRemotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "";

        for (int i = 0; i < parts.Length; i++)
        {
            currentPath += "/" + parts[i];
            var pathCopy = currentPath;

            // Separator
            var separator = new TextBlock
            {
                Text = ">",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                Opacity = 0.6,
                FontSize = 12
            };
            BreadcrumbPanel.Children.Add(separator);

            // Path segment button
            var segmentBtn = new Button
            {
                Content = parts[i],
                Height = 28,
                Padding = new Thickness(8, 4, 8, 4),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            segmentBtn.Click += (s, e) =>
            {
                NavigateToPath(pathCopy, true);
            };
            BreadcrumbPanel.Children.Add(segmentBtn);
        }
    }

    private void AddressBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PathBox.Visibility == Visibility.Visible)
        {
            return;
        }

        BreadcrumbScroll.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        PathBox.Text = _currentRemotePath;
        PathBox.ItemsSource = null;
        PathBox.Focus(FocusState.Programmatic);
        PathBox.ApplyTemplate();
        if (PathBox.FindName("TextBox") is TextBox pathTextBox)
        {
            pathTextBox.SelectAll();
        }
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // AutoSuggestBox briefly moves focus into its popup when a suggestion is clicked.
        // Defer the check so that SuggestionChosen/QuerySubmitted can finish first.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (PathBox.Visibility == Visibility.Visible && !PathBox.IsSuggestionListOpen)
            {
                ExitAddressEditMode();
            }
        });
    }

    private void ExitAddressEditMode()
    {
        CancelPathSuggestions();
        PathBox.IsSuggestionListOpen = false;
        PathBox.ItemsSource = null;
        PathBox.Visibility = Visibility.Collapsed;
        BreadcrumbScroll.Visibility = Visibility.Visible;
    }

    private void CancelPathSuggestions()
    {
        var suggestionCts = _pathSuggestionCts;
        _pathSuggestionCts = null;
        suggestionCts?.Cancel();
    }

    private (string Directory, string NamePrefix) SplitAddressSuggestionInput(string input)
    {
        var trimmedInput = input.Trim();
        var resolvedPath = ResolveAddressPath(trimmedInput);
        if (trimmedInput.EndsWith("/", StringComparison.Ordinal) || resolvedPath == "/")
        {
            return (resolvedPath, string.Empty);
        }

        var lastSlash = resolvedPath.LastIndexOf('/');
        var directory = lastSlash <= 0 ? "/" : resolvedPath[..lastSlash];
        var namePrefix = resolvedPath[(lastSlash + 1)..];
        return (directory, namePrefix);
    }

    private async Task<IReadOnlyList<AddressSuggestion>> GetAddressSuggestionsAsync(
        string directory,
        string namePrefix,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AddressSuggestion> directoryItems;
        if (string.Equals(directory, _currentRemotePath, StringComparison.Ordinal))
        {
            directoryItems = RemoteFiles
                .Where(item => !item.IsVirtualRoot)
                .Select(item => new AddressSuggestion(item.FullPath, item.IsNavigableDirectory))
                .ToList();
        }
        else if (_addressSuggestionCache.TryGetValue(directory, out var cachedEntry) &&
                 DateTimeOffset.UtcNow - cachedEntry.CreatedAt <= AddressSuggestionCacheLifetime)
        {
            directoryItems = cachedEntry.Items;
        }
        else
        {
            directoryItems = await LoadAddressSuggestionsAsync(directory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _addressSuggestionCache[directory] = new AddressSuggestionCacheEntry(
                DateTimeOffset.UtcNow,
                directoryItems);
        }

        return directoryItems
            .Where(item => GetRemoteFileName(item.FullPath).StartsWith(
                namePrefix,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => GetRemoteFileName(item.FullPath), StringComparer.OrdinalIgnoreCase)
            .Take(MaxAddressSuggestions)
            .ToList();
    }

    private async Task<IReadOnlyList<AddressSuggestion>> LoadAddressSuggestionsAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var connectionInfo = _sftpClient?.ConnectionInfo
            ?? throw new InvalidOperationException(LocalizationHelper.GetString("DragSudoNoConnection"));

        if (IsSudoBrowsePath(directory))
        {
            return await Task.Run(
                () => (IReadOnlyList<AddressSuggestion>)ListDirectoryWithSudo(directory)
                    .Select(item => new AddressSuggestion(item.FullPath, item.IsNavigableDirectory))
                    .ToList(),
                cancellationToken);
        }

        return await Task.Run(() =>
        {
            using var client = ConnectAuxiliarySftp(cancellationToken);
            return (IReadOnlyList<AddressSuggestion>)client.ListDirectory(directory)
                .Where(item => item.Name != "." && item.Name != "..")
                .Select(item => new AddressSuggestion(
                    item.FullName,
                    IsAddressSuggestionDirectory(client, item)))
                .ToList();
        }, cancellationToken);
    }

    private static bool IsAddressSuggestionDirectory(
        SftpClient client,
        Renci.SshNet.Sftp.ISftpFile item)
    {
        if (item.IsDirectory)
        {
            return true;
        }

        if (!item.IsSymbolicLink)
        {
            return false;
        }

        try
        {
            return client.GetAttributes(item.FullName).IsDirectory;
        }
        catch
        {
            return false;
        }
    }

    private async Task NavigateOrOpenAddressPathAsync(string input)
    {
        if (_sftpClient?.IsConnected != true || string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var path = ResolveAddressPath(input);
        try
        {
            var item = RemoteFiles.FirstOrDefault(candidate =>
                !candidate.IsVirtualRoot &&
                string.Equals(candidate.FullPath, path, StringComparison.Ordinal));
            item ??= await ResolveAddressTargetAsync(path);

            if (item.IsNavigableDirectory)
            {
                if (item.IsSymbolicLink)
                {
                    await OpenSymbolicLinkAsync(item);
                }
                else if (!item.CanRead && !IsSudoBrowsePath(path))
                {
                    await TryOpenDirectoryWithSudoAsync(path);
                }
                else
                {
                    NavigateToPath(path, true);
                }
                return;
            }

            if (item.IsSymbolicLink)
            {
                await OpenSymbolicLinkAsync(item);
            }
            else if (item.CanRead)
            {
                await OpenFileInDefaultEditor(item);
            }
            else
            {
                await TryOpenFileWithSudoAsync(item);
            }

            // Keep the file list on the directory that contains the opened file.
            // Do this after downloading/opening because SSH.NET clients are not used concurrently.
            NavigateToPath(GetRemoteParentPath(path), true);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to resolve address path '{path}': {ex.Message}");
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async Task<FileItem> ResolveAddressTargetAsync(string path)
    {
        var parentPath = GetRemoteParentPath(path);
        if (IsSudoBrowsePath(parentPath))
        {
            var sudoItem = await Task.Run(() => ListDirectoryWithSudo(parentPath)
                .FirstOrDefault(item => string.Equals(item.FullPath, path, StringComparison.Ordinal)));
            return sudoItem ?? throw new FileNotFoundException($"Remote path not found: {path}");
        }

        var connectionInfo = _sftpClient?.ConnectionInfo
            ?? throw new InvalidOperationException(LocalizationHelper.GetString("DragSudoNoConnection"));

        return await Task.Run(() =>
        {
            using var client = ConnectAuxiliarySftp(_lifetimeCts.Token);
            var file = client.Get(path);
            var targetIsDirectory = client.GetAttributes(path).IsDirectory;

            using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);
            var directAccess = GetDirectReadAccess(
                sshClient,
                new[] { (path, targetIsDirectory) });

            return new FileItem
            {
                Name = GetRemoteFileName(path),
                Size = targetIsDirectory ? "<DIR>" : FormatFileSize(file.Length),
                SizeBytes = targetIsDirectory ? 0 : file.Length,
                Modified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Permissions = GetUnixPermissions(file),
                Owner = file.UserId.ToString(CultureInfo.InvariantCulture),
                Group = file.GroupId.ToString(CultureInfo.InvariantCulture),
                IsDirectory = file.IsDirectory,
                IsSymbolicLink = file.IsSymbolicLink,
                SymbolicLinkTargetIsDirectory = file.IsSymbolicLink && targetIsDirectory,
                FullPath = path,
                Icon = GetFileIconGlyph(GetRemoteFileName(path), file.IsDirectory, file.IsSymbolicLink),
                CanRead = directAccess.TryGetValue(path, out var canRead) && canRead
            };
        });
    }

    private string ResolveAddressPath(string input)
    {
        var trimmedInput = input.Trim();
        var combinedPath = trimmedInput.StartsWith("/", StringComparison.Ordinal)
            ? trimmedInput
            : CombineRemotePath(_currentRemotePath, trimmedInput);
        var segments = new List<string>();
        foreach (var segment in combinedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static string GetRemoteParentPath(string path)
    {
        var normalizedPath = NormalizeRemotePath(path);
        var lastSlash = normalizedPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : normalizedPath[..lastSlash];
    }

    private static string GetRemoteFileName(string path)
    {
        var normalizedPath = NormalizeRemotePath(path);
        var lastSlash = normalizedPath.LastIndexOf('/');
        return lastSlash < 0 ? normalizedPath : normalizedPath[(lastSlash + 1)..];
    }



    private void ClearDragCache()
    {
        _cachedDragItems = null;
        _cachedDragSource = null;
        _dragPrepareTask = null;
        _isDragPreparing = false;
        _folderRequiresPreparationCache.Clear();
    }

    private void RefreshRemoteFiles(bool forceFileSystemRefresh = false)
    {
        if (_isDisposed) return;
        var refreshTask = RefreshRemoteFilesCoreAsync(forceFileSystemRefresh);
        TrackBackgroundTask(refreshTask);
    }

    private async Task RefreshRemoteFilesCoreAsync(bool forceFileSystemRefresh)
    {
        // Сбрасываем кеш drag-drop при обновлении файлов
        ClearDragCache();
        var refreshStatusRevision = Volatile.Read(ref _statusRevision);
        var refreshVersion = Interlocked.Increment(ref _remoteRefreshVersion);

        if (_sftpClient?.IsConnected != true)
        {
            StatusText.Text = "Cannot refresh: SFTP client not connected";
            return;
        }

        try
        {
            // Show loading overlay
            LoadingOverlay.Visibility = Visibility.Visible;

            // Выполняем операцию ListDirectory в фоновом потоке
            var currentPath = _currentRemotePath;
            var useSudo = IsSudoBrowsePath(currentPath);
            List<FileItem> fileItems;
            if (useSudo)
            {
                fileItems = await Task.Run(
                    () => ListDirectoryWithSudo(currentPath),
                    _lifetimeCts.Token);
            }
            else
            {
                var client = _sftpClient
                    ?? throw new InvalidOperationException("SFTP client is unavailable.");
                fileItems = await RunClientResultAsync(client, cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ListDirectoryWithSftp(currentPath);
                }, _lifetimeCts.Token);
            }
            _lifetimeCts.Token.ThrowIfCancellationRequested();

            if (refreshVersion != Volatile.Read(ref _remoteRefreshVersion) ||
                !string.Equals(currentPath, _currentRemotePath, StringComparison.Ordinal))
            {
                return;
            }

            // Do not create an auxiliary SSH connection until the directory is
            // visible. On high-latency or connection-limited servers its `df`
            // command can otherwise compete with the very first SFTP listing.
            var initialStats = _fileSystemStatsCache;
            ApplyFileSystemStats(fileItems, initialStats);

            RemoteFiles.Clear();
            UpdateFreeSpaceColumnVisibility();
            if (currentPath == "/")
            {
                var rootItem = CreateVirtualRootItem();
                ApplyFileSystemStats(rootItem, initialStats.GetValueOrDefault("/"));
                RemoteFiles.Add(rootItem);
            }

            // Обновляем UI в главном потоке
            foreach (var item in fileItems)
            {
                RemoteFiles.Add(item);
            }
            UpdateFreeSpaceColumnVisibility();
            _addressSuggestionCache[currentPath] = new AddressSuggestionCacheEntry(
                DateTimeOffset.UtcNow,
                fileItems.Select(item => new AddressSuggestion(item.FullPath, item.IsNavigableDirectory)).ToList());

            PathBox.Text = _currentRemotePath;

            // A directory refresh may finish after a folder drag preparation has started.
            // Do not overwrite the active transfer status or hide its progress indicators.
            if (!_isDownloadInProgress &&
                Volatile.Read(ref _activeDragPreparationCount) == 0 &&
                refreshStatusRevision == Volatile.Read(ref _statusRevision))
            {
                HideProgressBars();
                StatusText.Text = string.Format(LocalizationHelper.GetString("LoadedItems"), fileItems.Count);
            }
            else
            {
                Log.Debug($"Refresh status suppressed: revision={refreshStatusRevision}->{Volatile.Read(ref _statusRevision)}, activeDragPreparations={Volatile.Read(ref _activeDragPreparationCount)}");
            }

            UpdateItemCount();
            LoadingOverlay.Visibility = Visibility.Collapsed;

            try
            {
                var refreshedStats = await GetFileSystemStatsAsync(forceFileSystemRefresh);
                if (refreshVersion == Volatile.Read(ref _remoteRefreshVersion) &&
                    string.Equals(currentPath, _currentRemotePath, StringComparison.Ordinal))
                {
                    ApplyFileSystemStats(RemoteFiles, refreshedStats);
                    UpdateFreeSpaceColumnVisibility();
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception statsError)
            {
                // The file list is already usable; filesystem statistics are a
                // best-effort enhancement and must not replace it with an error.
                Log.Warning($"Filesystem statistics refresh failed: {statsError.Message}");
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _activeDragPreparationCount) == 0 &&
                refreshStatusRevision == Volatile.Read(ref _statusRevision))
            {
                StatusText.Text = $"Error loading remote files: {ex.Message}";
            }
            else
            {
                Log.Warning($"Remote file refresh failed while drag preparation was active: {ex.Message}");
            }
        }
        finally
        {
            // Hide loading overlay
            if (refreshVersion == Volatile.Read(ref _remoteRefreshVersion))
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, FileSystemStats>> GetFileSystemStatsAsync(bool forceRefresh)
    {
        if (!forceRefresh &&
            DateTimeOffset.UtcNow - _fileSystemStatsCacheCreatedAt <= FileSystemStatsCacheLifetime)
        {
            return _fileSystemStatsCache;
        }

        var loadTask = _fileSystemStatsLoadTask;
        if (loadTask == null || loadTask.IsCompleted)
        {
            loadTask = Task.Run(ReadRemoteFileSystemStats, _lifetimeCts.Token);
            _fileSystemStatsLoadTask = loadTask;
        }

        try
        {
            var stats = await loadTask;
            if (stats.Count > 0)
            {
                _fileSystemStatsCache = stats;
                _fileSystemStatsCacheCreatedAt = DateTimeOffset.UtcNow;
            }

            return stats.Count > 0 ? stats : _fileSystemStatsCache;
        }
        finally
        {
            if (ReferenceEquals(_fileSystemStatsLoadTask, loadTask))
            {
                _fileSystemStatsLoadTask = null;
            }
        }
    }

    private IReadOnlyDictionary<string, FileSystemStats> ReadRemoteFileSystemStats()
    {
        var connectionInfo = _sftpClient?.ConnectionInfo;
        if (connectionInfo == null)
        {
            return new Dictionary<string, FileSystemStats>(StringComparer.Ordinal);
        }

        try
        {
            using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);
            const string commandText =
                "if output=$(LC_ALL=C df -a -B1 --output=size,used,avail,target 2>/dev/null); " +
                "then printf 'B\\n%s\\n' \"$output\"; " +
                "else printf 'K\\n'; LC_ALL=C df -a -kP 2>/dev/null; fi";
            using var command = sshClient.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromSeconds(5);
            var output = command.Execute();

            if (string.IsNullOrWhiteSpace(output))
            {
                Log.Warning($"Could not read remote file-system statistics: exit code {command.ExitStatus}");
                return new Dictionary<string, FileSystemStats>(StringComparer.Ordinal);
            }

            var stats = ParseFileSystemStats(output);
            if (stats.Count == 0)
            {
                Log.Warning($"Remote df returned no usable file-system statistics: exit code {command.ExitStatus}");
            }

            return stats;
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not read remote file-system statistics: {ex.Message}");
            return new Dictionary<string, FileSystemStats>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, FileSystemStats> ParseFileSystemStats(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var stats = new Dictionary<string, FileSystemStats>(StringComparer.Ordinal);
        if (lines.Length < 2)
        {
            return stats;
        }

        var valuesAreBytes = lines[0].Trim() == "B";
        var valuesAreKilobytes = lines[0].Trim() == "K";
        if (!valuesAreBytes && !valuesAreKilobytes)
        {
            return stats;
        }

        for (var index = 2; index < lines.Length; index++)
        {
            var pattern = valuesAreBytes
                ? @"^\s*(\d+)\s+(\d+)\s+(\d+)\s+(.+?)\s*$"
                : @"^\S+\s+(\d+)\s+(\d+)\s+(\d+)\s+\S+\s+(.+?)\s*$";
            var match = Regex.Match(lines[index], pattern, RegexOptions.CultureInvariant);
            if (!match.Success ||
                !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var used) ||
                !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var available))
            {
                continue;
            }

            if (valuesAreKilobytes)
            {
                try
                {
                    total = checked(total * 1024);
                    used = checked(used * 1024);
                    available = checked(available * 1024);
                }
                catch (OverflowException)
                {
                    continue;
                }
            }

            var mountPoint = DecodeDfPath(match.Groups[4].Value);
            if (!mountPoint.StartsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            mountPoint = NormalizeRemotePath(mountPoint);
            stats[mountPoint] = new FileSystemStats(total, used, available);
        }

        return stats;
    }

    private static string DecodeDfPath(string path) =>
        Regex.Replace(
            path,
            @"\\([0-7]{3})",
            match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString(),
            RegexOptions.CultureInvariant);

    private FileItem CreateVirtualRootItem() => new()
    {
        Name = "Root filesystem (/)",
        FullPath = "/",
        Icon = "\uEDA2",
        IsVirtualRoot = true
    };

    private void ApplyFileSystemStats(
        IEnumerable<FileItem> items,
        IReadOnlyDictionary<string, FileSystemStats> stats)
    {
        foreach (var item in items)
        {
            var mountPoint = item.IsVirtualRoot ? "/" : NormalizeRemotePath(item.FullPath);
            ApplyFileSystemStats(
                item,
                !item.IsSymbolicLink && stats.TryGetValue(mountPoint, out var fileSystemStats)
                    ? fileSystemStats
                    : null);
        }
    }

    private void ApplyFileSystemStats(FileItem item, FileSystemStats? stats)
    {
        if (stats == null || stats.TotalBytes <= 0)
        {
            item.ClearFileSystemStats();
            return;
        }

        var freeSpaceText = string.Format(
            LocalizationHelper.GetString("FreeSpaceFormat"),
            FormatDiskSpace(stats.AvailableBytes),
            FormatDiskSpace(stats.TotalBytes));
        item.SetFileSystemStats(stats.TotalBytes, stats.UsedBytes, stats.AvailableBytes, freeSpaceText);
    }

    private static string FormatDiskSpace(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        var format = displayValue < 10 && unitIndex > 0 ? "0.0" : "0";
        return $"{displayValue.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }



    private void RemoteFiles_ItemClick(object sender, ItemClickEventArgs e)
    {
        // Одиночный клик просто выбирает элемент, не открывает папку
        // Папки открываются двойным кликом в RemoteFiles_DoubleTapped
    }

    private string GetFileIconGlyph(string fileName, bool isDirectory, bool isSymbolicLink = false)
    {
        if (isSymbolicLink)
            return "\uE7C3"; // Symbolic link

        if (isDirectory)
            return "\uE8B7"; // Folder

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            // Text/Documents - Page icon
            ".txt" or ".log" or ".md" or ".readme" => "\uF000",

            // Code files - Code icon
            ".py" => "\uE943", // Python
            ".rb" => "\uE943", // Ruby
            ".js" or ".ts" or ".jsx" or ".tsx" => "\uE943", // JavaScript/TypeScript
            ".java" => "\uE943", // Java
            ".cpp" or ".c" or ".h" or ".hpp" => "\uE943", // C/C++
            ".cs" => "\uE943", // C#
            ".go" => "\uE943", // Go
            ".rs" => "\uE943", // Rust
            ".php" => "\uE943", // PHP
            ".swift" or ".kt" => "\uE943", // Swift/Kotlin

            // Web files
            ".html" or ".htm" => "\uE12B", // Globe/Web
            ".css" or ".scss" or ".sass" or ".less" => "\uE8A5",

            // Data/Config files
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => "\uE943",
            ".csv" or ".tsv" or ".dat" => "\uE81E",
            ".conf" or ".config" or ".cfg" or ".ini" or ".env" => "\uF259", // Settings gear
            ".sql" or ".db" or ".sqlite" => "\uEE94", // Database

            // Archives - ZipFolder icon
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".tgz" => "\uF012",

            // Images - Photo icon
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" => "\uEB9F",

            // Videos - Video icon
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" => "\uE714",

            // Audio - MusicNote icon
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" => "\uE8D6",

            // Office documents
            ".pdf" => "\uEA90", // PDF icon
            ".doc" or ".docx" => "\uE8A1", // Word
            ".xls" or ".xlsx" => "\uE8A1", // Excel
            ".ppt" or ".pptx" => "\uE8A1", // PowerPoint

            // Executables/Applications
            ".exe" or ".msi" or ".app" or ".dmg" or ".deb" or ".rpm" => "\uEB3B", // App icon
            ".dll" or ".so" or ".dylib" => "\uE9F5", // Library

            // Scripts
            ".sh" or ".bash" or ".zsh" or ".fish" or ".bat" or ".cmd" or ".ps1" => "\uE756", // Terminal/Console

            // Security/Certificates
            ".cer" or ".crt" or ".pem" or ".key" or ".p12" or ".pfx" => "\uEB95", // Shield/Lock

            // Disk images
            ".iso" or ".img" or ".vhd" or ".vmdk" => "\uEDA2", // Disk/CD

            // Default
            _ => "\uE8A5" // Generic document
        };
    }

    private string GetOwnerName(int uid)
    {
        if (_uidToNameCache.TryGetValue(uid, out var cachedName))
            return cachedName;
        return uid.ToString(CultureInfo.InvariantCulture);
    }

    private string GetGroupName(int gid)
    {
        if (_gidToNameCache.TryGetValue(gid, out var cachedName))
            return cachedName;
        return gid.ToString(CultureInfo.InvariantCulture);
    }

    private void PopulateUnixIdentityCaches(
        SshClient sshClient,
        IReadOnlyCollection<Renci.SshNet.Sftp.ISftpFile> files)
    {
        if (!_nameResolutionSupported || files.Count == 0)
        {
            return;
        }

        var userIds = files.Select(file => file.UserId)
            .Distinct()
            .Where(id => !_uidToNameCache.ContainsKey(id))
            .ToArray();
        var groupIds = files.Select(file => file.GroupId)
            .Distinct()
            .Where(id => !_gidToNameCache.ContainsKey(id))
            .ToArray();

        try
        {
            ResolveUnixIdentityBatch(sshClient, "passwd", userIds, _uidToNameCache);
            ResolveUnixIdentityBatch(sshClient, "group", groupIds, _gidToNameCache);
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not resolve remote user/group names: {ex.Message}");
            _nameResolutionSupported = false;
        }
    }

    private static void ResolveUnixIdentityBatch(
        SshClient sshClient,
        string database,
        IReadOnlyCollection<int> ids,
        Dictionary<int, string> destination)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var arguments = string.Join(
            ' ',
            ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        using var command = sshClient.CreateCommand($"getent {database} {arguments}");
        command.CommandTimeout = TimeSpan.FromSeconds(5);
        var output = command.Execute();

        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(':');
            if (fields.Length >= 3 &&
                int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                ids.Contains(id) &&
                !string.IsNullOrWhiteSpace(fields[0]))
            {
                destination[id] = fields[0];
            }
        }
    }

    private string GetUnixPermissions(Renci.SshNet.Sftp.ISftpFile file)
    {
        try
        {
            var perms = "";
            perms += file.OwnerCanRead ? "r" : "-";
            perms += file.OwnerCanWrite ? "w" : "-";
            perms += file.OwnerCanExecute ? "x" : "-";
            perms += file.GroupCanRead ? "r" : "-";
            perms += file.GroupCanWrite ? "w" : "-";
            perms += file.GroupCanExecute ? "x" : "-";
            perms += file.OthersCanRead ? "r" : "-";
            perms += file.OthersCanWrite ? "w" : "-";
            perms += file.OthersCanExecute ? "x" : "-";
            return perms;
        }
        catch
        {
            // Не всегда можно получить права, возвращаем заглушку
            return "---------";
        }
    }

    /// <summary>
    /// Проверяет, есть ли права на чтение файла/папки
    /// </summary>
    private bool CanRead(Renci.SshNet.Sftp.ISftpFile file)
    {
        try
        {
            // Проверяем права для владельца, группы и остальных
            // Обычно текущий пользователь - владелец или член группы
            return file.OwnerCanRead || file.GroupCanRead || file.OthersCanRead;
        }
        catch
        {
            // Не удалось проверить права - считаем, что нет доступа
            return false;
        }
    }

    /// <summary>
    /// Проверяет, есть ли права на запись в папку
    /// </summary>
    private bool CanWrite(Renci.SshNet.Sftp.ISftpFile file)
    {
        try
        {
            return file.OwnerCanWrite || file.GroupCanWrite || file.OthersCanWrite;
        }
        catch
        {
            // Не удалось проверить права - считаем, что нет доступа
            return false;
        }
    }

    /// <summary>
    /// Проверяет права на чтение для файла по пути
    /// </summary>
    private bool CheckReadPermission(SftpClient client, string path)
    {
        try
        {
            var file = client.Get(path);
            return CanRead(file);
        }
        catch
        {
            // Файл не найден или нет прав - возвращаем false
            return false;
        }
    }

    /// <summary>
    /// Проверяет права на запись в папку по пути
    /// </summary>
    private bool CheckWritePermission(SftpClient client, string path)
    {
        try
        {
            var file = client.Get(path);
            return file.IsDirectory && CanWrite(file);
        }
        catch
        {
            // Папка не найдена или нет прав - возвращаем false
            return false;
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        // Фиксированный формат: всегда 2 знака после запятой, выравнивание по правому краю на 6 символов
        return $"{len,6:0.00} {sizes[order]}";
    }

    private string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan.TotalSeconds < 1) return "   0с";
        if (timeSpan.TotalMinutes < 1) return $"{(int)timeSpan.TotalSeconds,3}с";
        if (timeSpan.TotalHours < 1) return $"{(int)timeSpan.TotalMinutes,2}м {timeSpan.Seconds,2}с";
        return $"{(int)timeSpan.TotalHours,2}ч {timeSpan.Minutes,2}м";
    }

    // Drag and Drop для Remote Files
    private void RemoteFiles_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Блокируем drag при правом клике
        if (_isRightClickInProgress)
        {
            e.Cancel = true;
            return;
        }

        var items = e.Items.Cast<FileItem>().ToList();

        if (items.Count == 0 || items.Any(item => item.IsVirtualRoot) || _sftpClient?.IsConnected != true)
        {
            e.Cancel = true;
            return;
        }

        // Сбрасываем флаг готовности
        _isDragDataReady = false;

        // Проверяем - есть ли папки? Для папок нужен другой подход
        bool hasFolders = items.Any(i => i.IsDirectory);

        if (hasFolders)
        {
            // Для папок используем старый подход с кешем (streamed files не поддерживают папки)
            StartFolderDrag(e, items);
        }
        else
        {
            // Для файлов используем streamed files - мгновенный drag!
            _isDragDataReady = true; // Для файлов данные готовы сразу
            StartStreamedFileDrag(e, items);
        }
    }

    private void StartStreamedFileDrag(DragItemsStartingEventArgs e, List<FileItem> items)
    {
        _dragFiles = items;

        // Устанавливаем операцию Copy
        e.Data.RequestedOperation = DataPackageOperation.Copy;

        // Создаём streamed files - drag начинается мгновенно, скачивание при drop
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var def = request.GetDeferral();
            try
            {
                var streamedFiles = new List<IStorageItem>();

                // Инициализируем прогресс
                _dragTotalBytes = items.Sum(i => i.SizeBytes);
                _dragDownloadedBytes = 0;
                _dragStartTime = DateTime.Now;
                int totalFiles = items.Count;
                int filesCompleted = 0;

                foreach (var item in items)
                {
                    var remotePath = item.FullPath;
                    var fileSize = item.SizeBytes;
                    var fileName = item.Name;
                    LocalPathSafety.ValidateSingleName(fileName);
                    var currentIndex = filesCompleted + 1;
                    var totalFilesCapture = totalFiles;

                    // Создаём streamed file - содержимое скачивается лениво при чтении
                    var streamedFile = await StorageFile.CreateStreamedFileAsync(
                        fileName,
                        async stream =>
                        {
                            Log.Debug($"[StartStreamedFileDrag] 🔄 Delegate started for {fileName}");
                            try
                            {
                                var outputStream = stream.AsStreamForWrite();

                                var client = _sftpClient ?? throw new InvalidOperationException("SFTP client is unavailable.");
                                await RunClientTaskAsync(client, async cancellationToken =>
                                {
                                    long previousDownloaded = 0;
                                    long lastProgressTimestamp = 0;

                                    var downloadProgress = new InlineProgress<DownloadFileProgressReport>(report =>
                                    {
                                        var downloaded = report.TotalBytesDownloaded;
                                        var delta = (long)downloaded - previousDownloaded;
                                        previousDownloaded = (long)downloaded;
                                        Interlocked.Add(ref _dragDownloadedBytes, delta);

                                        if (!ShouldPublishProgress(
                                                ref lastProgressTimestamp,
                                                force: fileSize >= 0 && downloaded >= (ulong)fileSize))
                                        {
                                            return;
                                        }

                                        var elapsed = (DateTime.Now - _dragStartTime).TotalSeconds;
                                        var speed = elapsed > 0 ? _dragDownloadedBytes / elapsed : 0;
                                        var remaining = _dragTotalBytes - _dragDownloadedBytes;
                                        var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;
                                        var overallPercent = _dragTotalBytes > 0 ? (int)((_dragDownloadedBytes * 100) / _dragTotalBytes) : 0;

                                        DispatcherQueue.TryEnqueue(() =>
                                        {
                                            StatusText.Text = string.Format(LocalizationHelper.GetString("DownloadingProgress"), currentIndex, totalFilesCapture, fileName);
                                            ProgressPercent.Text = $"{overallPercent}% ({FormatFileSize(_dragDownloadedBytes)}/{FormatFileSize(_dragTotalBytes)})";
                                            ProgressSpeed.Text = string.Format(LocalizationHelper.GetString("SpeedPerSecond"), FormatFileSize((long)speed));
                                            ProgressETA.Text = string.Format(LocalizationHelper.GetString("TimeRemaining"), FormatTimeSpan(eta));
                                            ShowProgressBar(overallPercent);
                                        });
                                    });
                                    await client.DownloadFileAsync(
                                        remotePath,
                                        outputStream,
                                        downloadProgress,
                                        cancellationToken).ConfigureAwait(false);
                                }, _lifetimeCts.Token);

                                Log.Debug($"[StartStreamedFileDrag] ✅ Download complete for {fileName}");

                                try
                                {
                                    // Попытка гарантировать запись данных в WinRT-поток перед возвратом
                                    outputStream.Flush();
                                }
                                catch (Exception fx)
                                {
                                    Log.Warning($"[StartStreamedFileDrag] outputStream.Flush() failed: {fx.Message}");
                                }

                                try
                                {
                                    await stream.FlushAsync().AsTask().ConfigureAwait(false);
                                }
                                catch (Exception fx)
                                {
                                    Log.Warning($"[StartStreamedFileDrag] stream.FlushAsync() failed: {fx.Message}");
                                }

                                try
                                {
                                    // Закрываем .NET-обёртку
                                    outputStream.Dispose();
                                }
                                catch (Exception dx)
                                {
                                    Log.Warning($"[StartStreamedFileDrag] outputStream.Dispose() failed: {dx.Message}");
                                }

                                try
                                {
                                    // Закрываем WinRT-поток — это сигнал системе, что данных больше нет
                                    stream.Dispose();
                                }
                                catch (Exception dx)
                                {
                                    Log.Warning($"[StartStreamedFileDrag] stream.Dispose() failed: {dx.Message}");
                                }

                                // Файл завершён - увеличиваем счётчик и проверяем
                                var completed = Interlocked.Increment(ref filesCompleted);
                                if (completed == totalFilesCapture)
                                {
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        HideProgressBars();
                                        StatusText.Text = LocalizationHelper.GetString("DragDropComplete");
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"❌ Streamed download failed for {fileName}: {ex.Message}", ex);
                                stream.FailAndClose(StreamedFileFailureMode.Failed);
                            }
                        },
                        null);

                    streamedFiles.Add(streamedFile);
                }

                request.SetData(streamedFiles);
                Log.Debug($"✔️ DataProvider: providing {streamedFiles.Count} streamed file(s)");
            }
            catch (Exception ex)
            {
                Log.Error("❌ DataProvider error: " + ex, ex);
            }
            finally
            {
                def.Complete();
            }
        });

        Log.Debug($"➡️ StartStreamedFileDrag: {items.Count} file(s)");
    }

    private void StartFolderDrag(DragItemsStartingEventArgs e, List<FileItem> items)
    {
        _dragFiles = items;

        var cacheValid = _cachedDragItems is { Count: > 0 } &&
                         _cachedDragSource != null &&
                         _cachedDragSource.Count == items.Count &&
                         _cachedDragSource.All(cached => items.Any(item => item.FullPath == cached.FullPath));

        if (cacheValid)
        {
            _largeFolderDragPending = false;
            _isDragDataReady = true;
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            e.Data.SetStorageItems(_cachedDragItems!);
            StatusText.Text = LocalizationHelper.GetString("DragPreparedDataProvided");
            Log.Info($"Folder drag: synchronously providing {_cachedDragItems!.Count} prepared item(s)");
            return;
        }

        if (_dragPrepareTask is { IsCompleted: false })
        {
            e.Cancel = true;
            Log.Debug("Folder drag: preparation is already running; preserving the current progress display");
            return;
        }

        var requiresPreparation = FolderSelectionRequiresPreparation(items);
        ResetFolderDragPreparation(items);
        _largeFolderDragPending = requiresPreparation;

        if (requiresPreparation)
        {
            // Keep the first gesture visible, but use it only to start preparation. Explorer
            // receives a real StorageFolder on the next drag, after local staging is complete.
            DispatcherQueue.TryEnqueue(ShowLargeFolderPreparationHint);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            e.Data.SetText(LocalizationHelper.GetString("DragLargeFolderPreparingMessage"));
            _ = PrepareFolderDragAsync(items, request: null, forceSecondDrag: true);
            Log.Info($"Folder drag: large selection detected; first gesture starts preparation for {items.Count} item(s)");
            return;
        }

        // Try the normal one-gesture path first. Small/fast folders are staged before
        // Windows' provider deadline and are copied immediately. If the deadline expires,
        // preparation continues into the cache and the next drag is synchronous.
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                await PrepareFolderDragAsync(items, request);
            }
            finally
            {
                deferral.Complete();
            }
        });

        Log.Info($"Folder drag: adaptive preparation registered for {items.Count} selected item(s)");
    }

    private static string GetUnixPermissions(string octalMode)
    {
        try
        {
            var mode = Convert.ToInt32(octalMode, 8);
            var permissions = new char[9];
            var masks = new[] { 0x100, 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };
            var glyphs = new[] { 'r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x' };
            for (var index = 0; index < masks.Length; index++)
            {
                permissions[index] = (mode & masks[index]) != 0 ? glyphs[index] : '-';
            }

            return new string(permissions);
        }
        catch
        {
            return "---------";
        }
    }

    private List<FileItem> ListDirectoryWithSftp(string path)
    {
        var files = _sftpClient!.ListDirectory(path)
            .Where(file => file.Name != "." && file.Name != "..")
            .ToList();
        var symbolicLinkDirectoryTargets = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.IsSymbolicLink))
        {
            try
            {
                symbolicLinkDirectoryTargets[file.FullName] = _sftpClient.GetAttributes(file.FullName).IsDirectory;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to inspect symbolic link target '{file.FullName}': {ex.Message}");
                symbolicLinkDirectoryTargets[file.FullName] = false;
            }
        }

        var directAccess = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (files.Count > 0)
        {
            try
            {
                using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);
                directAccess = GetDirectReadAccess(
                    sshClient,
                    files.Select(file => (
                        file.FullName,
                        file.IsDirectory || symbolicLinkDirectoryTargets.GetValueOrDefault(file.FullName))).ToList());
                PopulateUnixIdentityCaches(sshClient, files);
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not load remote access and identity metadata: {ex.Message}");
            }
        }
        var items = new List<FileItem>(files.Count);

        foreach (var file in files)
        {
            var canRead = directAccess.TryGetValue(file.FullName, out var hasAccess)
                ? hasAccess
                : CanRead(file);

            items.Add(new FileItem
            {
                Name = file.Name,
                Size = file.IsDirectory ? "<DIR>" : FormatFileSize(file.Length),
                SizeBytes = file.IsDirectory ? 0 : file.Length,
                Modified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Permissions = GetUnixPermissions(file),
                Owner = GetOwnerName(file.UserId),
                Group = GetGroupName(file.GroupId),
                IsDirectory = file.IsDirectory,
                IsSymbolicLink = file.IsSymbolicLink,
                SymbolicLinkTargetIsDirectory = symbolicLinkDirectoryTargets.GetValueOrDefault(file.FullName),
                FullPath = file.FullName,
                Icon = GetFileIconGlyph(file.Name, file.IsDirectory, file.IsSymbolicLink),
                CanRead = canRead
            });
        }

        return SortFileItems(items);
    }

    private List<FileItem> ListDirectoryWithSudo(string path)
    {
        var connectionInfo = _sftpClient?.ConnectionInfo
            ?? throw new InvalidOperationException(LocalizationHelper.GetString("DragSudoNoConnection"));

        using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);

        const string findFormat = "%f\\0%y\\0%m\\0%U\\0%G\\0%u\\0%g\\0%s\\0%T@\\0%Y\\0";
        var commandText = $"sudo -n find -- {QuotePosixShellArgument(path)} -mindepth 1 -maxdepth 1 -printf {QuotePosixShellArgument(findFormat)}";
        using var command = sshClient.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(30);
        var output = command.Execute();

        if (command.ExitStatus != 0)
        {
            var error = string.IsNullOrWhiteSpace(command.Error)
                ? string.Format(LocalizationHelper.GetString("DragSudoExitCode"), command.ExitStatus)
                : command.Error.Trim();
            throw new UnauthorizedAccessException(error);
        }

        var fields = output.Split('\0');
        var fieldCount = fields.Length > 0 && fields[^1].Length == 0 ? fields.Length - 1 : fields.Length;
        if (fieldCount % 10 != 0)
        {
            throw new InvalidDataException(LocalizationHelper.GetString("SudoFolderInvalidResponse"));
        }

        var items = new List<FileItem>(fieldCount / 10);
        for (var index = 0; index < fieldCount; index += 10)
        {
            var name = fields[index];
            var isDirectory = fields[index + 1] == "d";
            var isSymbolicLink = fields[index + 1] == "l";
            _ = long.TryParse(fields[index + 7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            _ = double.TryParse(fields[index + 8], NumberStyles.Float, CultureInfo.InvariantCulture, out var unixTime);
            var fullPath = CombineRemotePath(path, name);

            items.Add(new FileItem
            {
                Name = name,
                Size = isDirectory ? "<DIR>" : FormatFileSize(size),
                SizeBytes = isDirectory ? 0 : size,
                Modified = DateTime.UnixEpoch.AddSeconds(unixTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Permissions = GetUnixPermissions(fields[index + 2]),
                Owner = fields[index + 5],
                Group = fields[index + 6],
                IsDirectory = isDirectory,
                IsSymbolicLink = isSymbolicLink,
                SymbolicLinkTargetIsDirectory = isSymbolicLink && fields[index + 9] == "d",
                FullPath = fullPath
            });
        }

        var directAccess = GetDirectReadAccess(
            sshClient,
            items.Select(item => (item.FullPath, item.IsNavigableDirectory)).ToList());
        foreach (var item in items)
        {
            item.CanRead = directAccess.TryGetValue(item.FullPath, out var hasAccess) && hasAccess;
            item.Icon = GetFileIconGlyph(item.Name, item.IsDirectory, item.IsSymbolicLink);
        }

        return SortFileItems(items);
    }

    private Task<bool> CheckWritePermissionAsync(
        SftpClient client,
        string path,
        CancellationToken cancellationToken) =>
        RunClientResultAsync(client, token =>
        {
            token.ThrowIfCancellationRequested();
            return CheckWritePermission(client, path);
        }, cancellationToken);

    private static Dictionary<string, bool> GetDirectReadAccess(
        SshClient sshClient,
        IReadOnlyList<(string Path, bool IsDirectory)> items)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var chunk in items.Chunk(100))
        {
            var checks = chunk.Select(item =>
            {
                var quotedPath = QuotePosixShellArgument(item.Path);
                var condition = item.IsDirectory
                    ? $"test -r {quotedPath} && test -x {quotedPath}"
                    : $"test -r {quotedPath}";
                return $"if {condition}; then printf 1; else printf 0; fi";
            });

            using var command = sshClient.CreateCommand(string.Join("; ", checks));
            command.CommandTimeout = TimeSpan.FromSeconds(15);
            var output = command.Execute();
            if (command.ExitStatus != 0 || output.Length != chunk.Length)
            {
                continue;
            }

            for (var index = 0; index < chunk.Length; index++)
            {
                result[chunk[index].Path] = output[index] == '1';
            }
        }

        return result;
    }

    private static List<FileItem> SortFileItems(IEnumerable<FileItem> items) =>
        items.OrderBy(item => item.IsDirectory ? 0 : 1)
            .ThenBy(item => item.Name)
            .ToList();

    private static string CombineRemotePath(string directory, string name) =>
        directory == "/" ? $"/{name}" : $"{directory.TrimEnd('/')}/{name}";

    private static string CreateRemotePartialPath(string finalPath) =>
        CombineRemotePath(GetRemoteParentPath(finalPath), $".sftpexplorer-{Guid.NewGuid():N}.partial");

    private static void ValidateRemoteEntryNameForPosix(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw new IOException($"Invalid remote directory entry name: '{name}'.");
        }
    }

    private static string CanonicalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var absolute = path.StartsWith("/", StringComparison.Ordinal);
        var components = new List<string>();
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == "..")
            {
                if (components.Count > 0) components.RemoveAt(components.Count - 1);
                continue;
            }
            components.Add(component);
        }

        var canonical = string.Join('/', components);
        return absolute ? "/" + canonical : canonical;
    }

    private static bool IsSameOrRemoteDescendant(string candidatePath, string ancestorPath)
    {
        var candidate = CanonicalizeRemotePath(candidatePath);
        var ancestor = CanonicalizeRemotePath(ancestorPath);
        if (string.Equals(candidate, ancestor, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = ancestor == "/" ? "/" : ancestor.TrimEnd('/') + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static void ValidateRemoteCopyDestination(string sourcePath, string destinationPath, bool isDirectory)
    {
        var source = CanonicalizeRemotePath(sourcePath);
        var destination = CanonicalizeRemotePath(destinationPath);
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            throw new IOException("Source and destination are the same remote path.");
        }

        if (isDirectory && IsSameOrRemoteDescendant(destination, source))
        {
            throw new IOException("A directory cannot be copied or moved into itself or one of its descendants.");
        }
    }

    private static void TryDeleteRemoteFile(SftpClient client, string remotePath)
    {
        try
        {
            if (client.Exists(remotePath))
            {
                client.DeleteFile(remotePath);
            }
        }
        catch (Exception cleanupException)
        {
            Log.Warning($"Failed to remove incomplete remote file '{remotePath}': {cleanupException.Message}");
        }
    }

    private void RegisterRemoteUploadStagingPath(string path)
    {
        lock (_activeRemoteUploadStagingPaths) _activeRemoteUploadStagingPaths.Add(path);
    }

    private void UnregisterRemoteUploadStagingPath(string path)
    {
        lock (_activeRemoteUploadStagingPaths) _activeRemoteUploadStagingPaths.Remove(path);
    }

    private void RegisterRemoteUploadBackupTransaction(RemoteUploadBackupTransaction transaction)
    {
        lock (_activeRemoteUploadBackupTransactions)
            _activeRemoteUploadBackupTransactions[transaction.BackupPath] = transaction;
    }

    private void UnregisterRemoteUploadBackupTransaction(RemoteUploadBackupTransaction transaction)
    {
        lock (_activeRemoteUploadBackupTransactions)
            _activeRemoteUploadBackupTransactions.Remove(transaction.BackupPath);
    }

    private async Task CleanupRemoteUploadStagingFilesAfterCloseAsync()
    {
        RemoteUploadBackupTransaction[] backups;
        string[] stagingPaths;
        lock (_activeRemoteUploadBackupTransactions)
            backups = _activeRemoteUploadBackupTransactions.Values.ToArray();
        lock (_activeRemoteUploadStagingPaths)
            stagingPaths = _activeRemoteUploadStagingPaths.ToArray();
        if (backups.Length == 0 && stagingPaths.Length == 0) return;

        using var cleanupCts = new CancellationTokenSource(RemoteStagingCleanupTimeout);
        try
        {
            using var cleanupClient = await ConnectAuxiliarySftpAsync(cleanupCts.Token).ConfigureAwait(false);
            foreach (var transaction in backups)
            {
                cleanupCts.Token.ThrowIfCancellationRequested();
                if (!await cleanupClient.ExistsAsync(transaction.BackupPath, cleanupCts.Token))
                {
                    UnregisterRemoteUploadBackupTransaction(transaction);
                    continue;
                }
                if (!await cleanupClient.ExistsAsync(transaction.DestinationPath, cleanupCts.Token))
                    await cleanupClient.RenameFileAsync(transaction.BackupPath, transaction.DestinationPath, cleanupCts.Token);
                else
                    await cleanupClient.DeleteFileAsync(transaction.BackupPath, cleanupCts.Token);
                UnregisterRemoteUploadBackupTransaction(transaction);
            }
            foreach (var path in stagingPaths)
            {
                cleanupCts.Token.ThrowIfCancellationRequested();
                if (await cleanupClient.ExistsAsync(path, cleanupCts.Token))
                {
                    var attributes = await cleanupClient.GetAttributesAsync(path, cleanupCts.Token);
                    if (attributes.IsDirectory)
                        DeleteOwnedRemoteTree(cleanupClient, path);
                    else
                        await cleanupClient.DeleteFileAsync(path, cleanupCts.Token);
                }
                UnregisterRemoteUploadStagingPath(path);
            }
            if (cleanupClient.IsConnected) cleanupClient.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not finish remote upload staging cleanup: {ex.Message}");
        }
    }

    private void CommitRemoteReplacement(SftpClient client, string temporaryPath, string finalPath)
    {
        try
        {
            client.RenameFile(temporaryPath, finalPath, isPosix: true);
            return;
        }
        catch (Exception posixRenameException)
        {
            // Some SFTP v3 servers do not expose the POSIX rename extension. Fall
            // back to a recoverable two-rename transaction: the original is moved
            // to a unique backup before the complete temporary file is committed.
            Log.Debug($"POSIX rename is unavailable for '{finalPath}': {posixRenameException.Message}");
        }

        if (!client.Exists(finalPath))
        {
            client.RenameFile(temporaryPath, finalPath);
            return;
        }

        var parent = GetRemoteParentPath(finalPath);
        var backupPath = CombineRemotePath(parent, $".sftpexplorer-{Guid.NewGuid():N}.backup");
        var backupTransaction = new RemoteUploadBackupTransaction(backupPath, finalPath);
        RegisterRemoteUploadBackupTransaction(backupTransaction);
        client.RenameFile(finalPath, backupPath);
        try
        {
            client.RenameFile(temporaryPath, finalPath);
            TryDeleteRemoteFile(client, backupPath);
            if (!client.Exists(backupPath))
            {
                UnregisterRemoteUploadBackupTransaction(backupTransaction);
            }
        }
        catch
        {
            try
            {
                if (!client.Exists(finalPath) && client.Exists(backupPath))
                {
                    client.RenameFile(backupPath, finalPath);
                }
                if (!client.Exists(backupPath))
                {
                    UnregisterRemoteUploadBackupTransaction(backupTransaction);
                }
            }
            catch (Exception restoreException)
            {
                Log.Error(
                    $"Failed to restore remote backup '{backupPath}' after replacing '{finalPath}'.",
                    restoreException);
            }
            throw;
        }
    }

    private static void TryDeleteOwnedRemoteStagingTree(
        SftpClient client,
        string expectedParent,
        string stagingPath)
    {
        var canonicalParent = CanonicalizeRemotePath(expectedParent);
        var canonicalStaging = CanonicalizeRemotePath(stagingPath);
        var stagingName = GetRemoteFileName(canonicalStaging);
        if (!string.Equals(GetRemoteParentPath(canonicalStaging), canonicalParent, StringComparison.Ordinal) ||
            !stagingName.StartsWith(".sftpexplorer-", StringComparison.Ordinal) ||
            !stagingName.EndsWith(".partial", StringComparison.Ordinal))
        {
            Log.Warning($"Refusing to clean an unowned remote staging path: {stagingPath}");
            return;
        }

        try
        {
            DeleteOwnedRemoteTree(client, canonicalStaging);
        }
        catch (Exception cleanupException)
        {
            Log.Warning($"Failed to remove remote staging directory '{stagingPath}': {cleanupException.Message}");
        }
    }

    private static void DeleteOwnedRemoteTree(SftpClient client, string directoryPath)
    {
        if (!client.Exists(directoryPath)) return;
        foreach (var entry in client.ListDirectory(directoryPath))
        {
            if (entry.Name is "." or "..") continue;
            ValidateRemoteEntryNameForPosix(entry.Name);
            var childPath = CombineRemotePath(directoryPath, entry.Name);
            if (entry.IsDirectory && !entry.IsSymbolicLink)
            {
                DeleteOwnedRemoteTree(client, childPath);
            }
            else
            {
                client.DeleteFile(childPath);
            }
        }
        client.DeleteDirectory(directoryPath);
    }

    private bool FolderSelectionRequiresPreparation(List<FileItem> items)
    {
        var cacheKey = string.Join(
            "\n",
            items.OrderBy(item => item.FullPath, StringComparer.Ordinal)
                .Select(item => $"{(item.IsDirectory ? 'D' : 'F')}:{item.FullPath}"));

        if (_folderRequiresPreparationCache.TryGetValue(cacheKey, out var cachedResult))
        {
            return cachedResult;
        }

        // DragItemsStarting is a UI event. Never connect or run remote `find`
        // synchronously here: a slow server would freeze the pointer/drag loop.
        // Directory selections conservatively use the prepared two-phase path;
        // file-only selections keep the immediate streamed-file path.
        var requiresPreparation = items.Any(item => item.IsDirectory) ||
                                  items.Count > DirectFolderFileLimit;
        _folderRequiresPreparationCache[cacheKey] = requiresPreparation;
        return requiresPreparation;
    }

    private void ShowLargeFolderPreparationHint()
    {
        LargeFolderPreparationTip.Title = LocalizationHelper.GetString("DragLargeFolderPreparingTitle");
        LargeFolderPreparationTip.Subtitle = LocalizationHelper.GetString("DragLargeFolderPreparingMessage");
        LargeFolderPreparationTip.IsOpen = true;
    }

    private void HideLargeFolderPreparationHint()
    {
        LargeFolderPreparationTip.IsOpen = false;
        _largeFolderDragPending = false;
    }

    private void ResetFolderDragPreparation(List<FileItem> items)
    {
        Interlocked.Increment(ref _statusRevision);
        _cachedDragItems = null;
        _cachedDragSource = items.ToList();
        lock (_dragPreparationSync)
        {
            _dragPrepareTask = null;
            _dragPreparationStartedAt = default;
        }
        Interlocked.Exchange(ref _dragPreparationCompletionHandled, 0);
        _isDragDataReady = false;
        _isDragPreparing = true;
        _skipAllDragPermissionErrors = false;
        _skipAllDragErrorTypes.Clear();
        _dragPreparationCanceled = false;
        _dragTransferIssues.Clear();
    }

    private async Task PrepareFolderDragAsync(
        List<FileItem> items,
        DataProviderRequest? request,
        bool forceSecondDrag = false)
    {
        Task<IReadOnlyList<IStorageItem>> preparationTask;
        DateTimeOffset preparationStartedAt;
        bool startedHere;
        var client = _sftpClient;

        lock (_dragPreparationSync)
        {
            startedHere = _dragPrepareTask == null;
            if (startedHere)
            {
                _dragPreparationStartedAt = DateTimeOffset.Now;
                _dragPrepareTask = client?.IsConnected == true
                    ? RunClientResultAsync(client, cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return PrepareDragStorageItemsInBackground(items);
                    }, _lifetimeCts.Token)
                    : Task.FromResult<IReadOnlyList<IStorageItem>>(Array.Empty<IStorageItem>());
                TrackBackgroundTask(_dragPrepareTask);
            }

            preparationTask = _dragPrepareTask!;
            preparationStartedAt = _dragPreparationStartedAt;
        }

        try
        {
            if (startedHere)
            {
                Interlocked.Increment(ref _activeDragPreparationCount);
                BadgeNotificationService.IncrementTransfer();
                await RunOnUiThreadAsync(() =>
                {
                    var preparingText = LocalizationHelper.GetString("PreparingDragDrop");
                    StatusText.Text = preparingText;
                    PreparationProgressRing.Visibility = Visibility.Visible;
                    PreparationProgressRing.IsActive = true;
                    OverallProgressBar.Visibility = Visibility.Visible;
                    OverallProgressBar.IsIndeterminate = true;
                    OverallProgressText.Visibility = Visibility.Visible;
                    OverallProgressText.Text = preparingText;
                    Log.Info("Folder drag UI: preparation indicators shown");
                });
                var deadlineText = request == null ? "none" : request.Deadline.ToString("O");
                Log.Info($"Folder drag: preparing {items.Count} selected item(s), deadline={deadlineText}, directBudget={ImmediateFolderDragBudget.TotalSeconds:0.#}s, forceSecondDrag={forceSecondDrag}");
            }
            else
            {
                Log.Debug("Folder drag: reusing the in-flight preparation for an additional data request");
            }

            var result = await preparationTask;

            if (_dragPreparationCanceled)
            {
                _cachedDragItems = null;
                _cachedDragSource = null;
                if (TryClaimDragPreparationCompletion())
                {
                    await RunOnUiThreadAsync(() =>
                        StatusText.Text = LocalizationHelper.GetString("DragTransferCanceled"));
                }
                return;
            }

            if (result.Count == 0)
            {
                _cachedDragItems = null;
                _cachedDragSource = null;
                if (TryClaimDragPreparationCompletion())
                {
                    await RunOnUiThreadAsync(async () =>
                    {
                        StatusText.Text = LocalizationHelper.GetString("DragTransferFailed");
                        await ShowFolderDragPreparationFailedAsync();
                    });
                }
                return;
            }

            // Keep user-approved partial results too: the next drag must copy exactly what was prepared.
            _cachedDragItems = result;
            _isDragDataReady = true;
            await RunOnUiThreadAsync(HideProgressBars);

            var deliveredToCurrentDrop = false;
            var preparationElapsed = DateTimeOffset.Now - preparationStartedAt;
            if (!forceSecondDrag &&
                request != null &&
                preparationElapsed <= ImmediateFolderDragBudget &&
                DateTimeOffset.Now < request.Deadline)
            {
                try
                {
                    request.SetData(result);
                    deliveredToCurrentDrop = true;
                }
                catch (Exception ex)
                {
                    // Windows may close the request just before the published deadline.
                    // The prepared cache is still valid for an immediate second drag.
                    Log.Warning($"Folder drag: current drop no longer accepts data: {ex.Message}");
                }
            }

            if (!TryClaimDragPreparationCompletion())
            {
                return;
            }

            if (deliveredToCurrentDrop)
            {
                await RunOnUiThreadAsync(() =>
                {
                    HideLargeFolderPreparationHint();
                    StatusText.Text = LocalizationHelper.GetString("DragDropComplete");
                });
                Log.Info($"Folder drag: delivered {result.Count} prepared item(s) in the initial drop after {preparationElapsed.TotalSeconds:0.0}s, skipped={_dragTransferIssues.Count}");
                return;
            }

            Log.Info($"Folder drag: direct budget exceeded after {preparationElapsed.TotalSeconds:0.0}s; cache ready, items={result.Count}, skipped={_dragTransferIssues.Count}");
            await RunOnUiThreadAsync(async () =>
            {
                HideLargeFolderPreparationHint();
                StatusText.Text = _dragTransferIssues.Count == 0
                    ? LocalizationHelper.GetString("DragPreparationReady")
                    : string.Format(LocalizationHelper.GetString("DragPreparationReadyWithSkipped"), _dragTransferIssues.Count);
                await ShowFolderDragPreparationReadyAsync();
            });
        }
        catch (OperationCanceledException)
        {
            _dragPreparationCanceled = true;
            _cachedDragItems = null;
            _cachedDragSource = null;
            if (TryClaimDragPreparationCompletion())
            {
                await RunOnUiThreadAsync(() =>
                {
                    HideLargeFolderPreparationHint();
                    HideProgressBars();
                    StatusText.Text = LocalizationHelper.GetString("DragTransferCanceled");
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Folder drag preparation failed: {ex.Message}", ex);
            _cachedDragItems = null;
            _cachedDragSource = null;
            if (TryClaimDragPreparationCompletion())
            {
                await RunOnUiThreadAsync(async () =>
                {
                    HideLargeFolderPreparationHint();
                    HideProgressBars();
                    StatusText.Text = string.Format(
                        LocalizationHelper.GetString("DragTransferFailedWithError") ?? "Transfer failed: {0}",
                        ex.Message);
                    await ShowFolderDragPreparationFailedAsync(ex.Message);
                });
            }
        }
        finally
        {
            if (startedHere)
            {
                _isDragPreparing = false;
                Interlocked.Decrement(ref _activeDragPreparationCount);
                BadgeNotificationService.DecrementTransfer();
            }
        }
    }

    private bool TryClaimDragPreparationCompletion()
    {
        return Interlocked.CompareExchange(ref _dragPreparationCompletionHandled, 1, 0) == 0;
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (_isDisposed)
        {
            Log.Debug("Folder drag UI action skipped because the tab content is disposed");
            return Task.CompletedTask;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!_isDisposed)
                    {
                        action();
                    }
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            Log.Warning("Folder drag UI action could not be enqueued because the dispatcher is shutting down");
            completion.SetResult();
        }

        return completion.Task;
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (_isDisposed)
        {
            Log.Debug("Folder drag UI async action skipped because the tab content is disposed");
            return Task.CompletedTask;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (!_isDisposed)
                    {
                        await action();
                    }
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            Log.Warning("Folder drag UI async action could not be enqueued because the dispatcher is shutting down");
            completion.SetResult();
        }

        return completion.Task;
    }



    // DragStarting for individual ListViewItem containers — sets DragUI so Explorer accepts drag
    private void RemoteFiles_DragStarting(object sender, DragStartingEventArgs e)
    {
        // Pointer interaction in the file list takes focus back from the embedded terminal.
        RemoteFilesListView.Focus(FocusState.Pointer);

        // Блокируем drag при правом клике
        if (_isRightClickInProgress)
        {
            e.Cancel = true;
            return;
        }

        var selectedItems = RemoteFilesListView.SelectedItems.Cast<FileItem>().ToList();

        if (selectedItems.Count == 0 || selectedItems.Any(item => item.IsVirtualRoot))
        {
            e.Cancel = true;
            return;
        }

        // Устанавливаем только Copy операцию
        e.AllowedOperations = DataPackageOperation.Copy;

        // Enhanced visual feedback using DragUIOverride
        try
        {
            if (selectedItems.Any())
            {
                var dragUI = e.DragUI;
                // The folder provider is idempotent and shares one preparation task, so the
                // drag preview and Explorer's actual drop request cannot start duplicate walks.
                dragUI.SetContentFromDataPackage();

                // Set custom caption
                if (selectedItems.Count == 1)
                {
                    var item = selectedItems[0];
                    if (item.IsDirectory)
                    {
                        var caption = $"📁 {item.Name}";
                        Log.Debug($"Drag caption: {caption}");
                    }
                    else
                    {
                        Log.Debug($"Drag caption: 📄 {item.Name} ({item.Size})");
                    }
                }
                else
                {
                    var fileCount = selectedItems.Count(f => !f.IsDirectory);
                    var folderCount = selectedItems.Count(f => f.IsDirectory);

                    var caption = _isDragDataReady
                        ? (fileCount > 0 && folderCount > 0
                            ? $"📦 {fileCount} files, {folderCount} folders"
                            : fileCount > 0
                            ? $"📄 {fileCount} files"
                            : $"📁 {folderCount} folders")
                        : $"⏳ Preparing {fileCount + folderCount} items...";

                    Log.Debug($"Drag caption: {caption}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error customizing drag UI: {ex.Message}", ex);
        }
    }


    private DateTime _dragStartTime;
    private long _dragTotalBytes;
    private long _dragDownloadedBytes;

    // Синхронная версия для фонового потока (папки)
    private IReadOnlyList<IStorageItem> PrepareDragStorageItemsInBackground(List<FileItem> items)
    {
        var preparedItems = new List<IStorageItem>();

        if (_sftpClient?.IsConnected != true)
        {
            return preparedItems;
        }

        // Инициализируем счётчики
        _dragDownloadedBytes = 0;
        _dragTotalBytes = 0;
        _dragTotalFiles = 0;
        _dragCompletedFiles = 0;
        _dragStartTime = DateTime.Now;

        int currentFileIndex = 0;

        // Create a unique, caller-owned staging root. Only paths recorded in
        // createdDirectories are ever eligible for recursive cleanup.
        var sessionFolder = CreateLocalTransferSessionDirectory("Drag");
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sessionFolder };
        var reservedTopLevelNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory)
                {
                    // Скачиваем папку рекурсивно
                    var localFolderPath = LocalPathSafety.ReserveChild(sessionFolder, item.Name, reservedTopLevelNames);
                    EnsureDestinationDoesNotExist(localFolderPath);
                    Directory.CreateDirectory(localFolderPath);
                    createdDirectories.Add(localFolderPath);
                    currentFileIndex = DownloadDirectoryForDragSync(
                        item.FullPath,
                        localFolderPath,
                        currentFileIndex,
                        out var directoryAvailable,
                        sessionFolder,
                        createdDirectories);

                    if (!directoryAvailable)
                    {
                        DeleteOwnedStagingDirectory(sessionFolder, localFolderPath, createdDirectories);
                        continue;
                    }

                    var storageFolder = StorageFolder.GetFolderFromPathAsync(localFolderPath).AsTask().Result;
                    preparedItems.Add(storageFolder);
                }
                else
                {
                    // Скачиваем файл с прогрессом
                    currentFileIndex++;
                    var tempPath = LocalPathSafety.ReserveChild(sessionFolder, item.Name, reservedTopLevelNames);
                    var fileSize = item.SizeBytes;

                    DownloadFileForDragWithRecovery(
                        item.FullPath,
                        tempPath,
                        fileSize,
                        currentFileIndex,
                        item.Name,
                        sessionFolder);

                    if (!File.Exists(tempPath))
                    {
                        continue;
                    }

                    var storageFile = StorageFile.GetFileFromPathAsync(tempPath).AsTask().Result;
                    preparedItems.Add(storageFile);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to stage '{item.Name}' for drag: {ex.Message}", ex);
                var action = ResolveDragTransferError(item.FullPath, ex, item.IsDirectory);
                if (action == DragTransferAction.Retry)
                {
                    // The granular SFTP operations already retry. A failure here is local staging,
                    // so report it as skipped rather than restarting a partially staged tree.
                    AddDragTransferIssue(item.FullPath, ex, ex is SftpPermissionDeniedException);
                }
            }
        }

        return preparedItems;
    }

    private void DownloadFileForDragSync(string remotePath, string localPath, long fileSize, int currentFileIndex, string fileName)
    {
        long previousDownloaded = 0;
        long lastProgressTimestamp = 0;

        _lifetimeCts.Token.ThrowIfCancellationRequested();
        using var fs = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        _sftpClient!.DownloadFile(remotePath, fs, downloaded =>
        {
            _lifetimeCts.Token.ThrowIfCancellationRequested();
            if (_dragPreparationCanceled) throw new OperationCanceledException();
            var delta = (long)downloaded - previousDownloaded;
            previousDownloaded = (long)downloaded;
            Interlocked.Add(ref _dragDownloadedBytes, delta);

            if (!ShouldPublishProgress(
                    ref lastProgressTimestamp,
                    force: fileSize >= 0 && downloaded >= (ulong)fileSize))
            {
                return;
            }

            var elapsed = (DateTime.Now - _dragStartTime).TotalSeconds;
            var speed = elapsed > 0 ? _dragDownloadedBytes / elapsed : 0;
            var remaining = _dragTotalBytes - _dragDownloadedBytes;
            var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;

            // Показываем прогресс по текущему файлу
            var filePercent = fileSize > 0 ? (int)((downloaded * 100) / (ulong)fileSize) : 0;

            if (_isDisposed)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isDisposed)
                {
                    return;
                }

                StatusText.Text = string.Format(LocalizationHelper.GetString("DownloadingFileNum"), currentFileIndex, fileName);
                ProgressPercent.Text = $"{filePercent}% ({FormatFileSize((long)downloaded)}/{FormatFileSize(fileSize)})";
                ProgressSpeed.Text = string.Format(LocalizationHelper.GetString("SpeedPerSecond"), FormatFileSize((long)speed));
                ProgressETA.Text = string.Format(LocalizationHelper.GetString("TimeRemaining"), FormatTimeSpan(eta));
                ShowProgressBar(filePercent);

                // Общий прогресс с объёмом данных
                if (_dragTotalFiles > 0 && _dragTotalBytes > 0)
                {
                    ShowOverallProgress(_dragCompletedFiles, _dragTotalFiles, _dragDownloadedBytes, _dragTotalBytes);
                }
                else
                {
                    ShowOverallProgressIndeterminate(currentFileIndex);
                }
            });
        });

        // Увеличиваем счётчик завершённых файлов
        Interlocked.Increment(ref _dragCompletedFiles);
    }

    private int DownloadDirectoryForDragSync(
        string remotePath,
        string localPath,
        int currentFileIndex,
        out bool directoryAvailable,
        string sessionRoot,
        ISet<string> directoriesCreatedBySession)
    {
        if (_sftpClient?.IsConnected != true)
        {
            directoryAvailable = false;
            return currentFileIndex;
        }

        while (true)
        {
            try
            {
                var entries = _sftpClient.ListDirectory(remotePath).ToList();
                var reservedNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var entry in entries)
                {
                    if (entry.Name == "." || entry.Name == "..") continue;

                    if (_dragPreparationCanceled)
                    {
                        throw new OperationCanceledException();
                    }

                    ValidateRemoteEntryNameForPosix(entry.Name);
                    var remoteItemPath = CombineRemotePath(remotePath, entry.Name);
                    var localItemPath = LocalPathSafety.ReserveChild(localPath, entry.Name, reservedNames);

                    if (entry.IsDirectory)
                    {
                        if (entry.IsSymbolicLink)
                        {
                            throw new NotSupportedException($"Dragging symbolic-link directories is not supported: {remoteItemPath}");
                        }

                        EnsureDestinationDoesNotExist(localItemPath);
                        Directory.CreateDirectory(localItemPath);
                        directoriesCreatedBySession.Add(localItemPath);
                        currentFileIndex = DownloadDirectoryForDragSync(
                            remoteItemPath,
                            localItemPath,
                            currentFileIndex,
                            out var childDirectoryAvailable,
                            sessionRoot,
                            directoriesCreatedBySession);

                        if (!childDirectoryAvailable)
                        {
                            DeleteOwnedStagingDirectory(sessionRoot, localItemPath, directoriesCreatedBySession);
                        }
                    }
                    else
                    {
                        currentFileIndex++;
                        DownloadFileForDragWithRecovery(
                            remoteItemPath,
                            localItemPath,
                            entry.Length,
                            currentFileIndex,
                            entry.Name,
                            sessionRoot);
                    }
                }

                directoryAvailable = true;
                return currentFileIndex;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var action = ResolveDragTransferError(remotePath, ex, isDirectory: true);
                if (action == DragTransferAction.Retry)
                {
                    continue;
                }

                directoryAvailable = false;
                return currentFileIndex;
            }
        }
    }

    private void DownloadFileForDragWithRecovery(
        string remotePath,
        string localPath,
        long fileSize,
        int currentFileIndex,
        string fileName,
        string sessionRoot)
    {
        while (true)
        {
            try
            {
                DownloadFileForDragSync(remotePath, localPath, fileSize, currentFileIndex, fileName);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    LocalPathSafety.EnsureStrictDescendant(sessionRoot, localPath);
                    if (File.Exists(localPath) && !Directory.Exists(localPath))
                    {
                        File.Delete(localPath);
                    }
                }
                catch (Exception cleanupException)
                {
                    Log.Warning($"Failed to remove incomplete drag file '{localPath}': {cleanupException.Message}");
                }

                var action = ResolveDragTransferError(remotePath, ex, isDirectory: false);
                if (action == DragTransferAction.Retry)
                {
                    continue;
                }

                return;
            }
        }
    }

    private DragTransferAction ResolveDragTransferError(string remotePath, Exception exception, bool isDirectory)
    {
        var isPermissionError = exception is SftpPermissionDeniedException;

        if ((isPermissionError && _skipAllDragPermissionErrors) ||
            (!isPermissionError && _skipAllDragErrorTypes.Contains(exception.GetType())))
        {
            AddDragTransferIssue(remotePath, exception, isPermissionError);
            return DragTransferAction.Skip;
        }

        var displayedException = exception;
        var canTrySudo = isPermissionError;

        while (true)
        {
            var action = RequestDragTransferAction(
                remotePath,
                displayedException,
                isPermissionError,
                canTrySudo);

            switch (action)
            {
                case DragTransferAction.Retry:
                    return DragTransferAction.Retry;

                case DragTransferAction.Skip:
                    AddDragTransferIssue(remotePath, exception, isPermissionError);
                    return DragTransferAction.Skip;

                case DragTransferAction.SkipAll:
                    if (isPermissionError)
                    {
                        _skipAllDragPermissionErrors = true;
                    }
                    else
                    {
                        _skipAllDragErrorTypes.Add(exception.GetType());
                    }

                    AddDragTransferIssue(remotePath, exception, isPermissionError);
                    return DragTransferAction.Skip;

                case DragTransferAction.TrySudo:
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusText.Text = string.Format(
                            LocalizationHelper.GetString("DragSudoFixInProgress"),
                            remotePath);
                    });

                    if (TryGrantReadAccessWithSudo(remotePath, isDirectory, out var sudoError))
                    {
                        Log.Info($"Granted read access with sudo ACL for '{remotePath}'");
                        return DragTransferAction.Retry;
                    }

                    Log.Warning($"Could not grant read access with sudo for '{remotePath}': {sudoError}");
                    displayedException = new InvalidOperationException(
                        string.Format(
                            LocalizationHelper.GetString("DragSudoFixFailed"),
                            sudoError),
                        exception);
                    canTrySudo = false;
                    break;

                case DragTransferAction.Cancel:
                default:
                    _dragPreparationCanceled = true;
                    throw new OperationCanceledException();
            }
        }
    }

    private DragTransferAction RequestDragTransferAction(
        string remotePath,
        Exception exception,
        bool isPermissionError,
        bool canTrySudo)
    {
        var completion = new TaskCompletionSource<DragTransferAction>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialogRoot = XamlRoot;
                if (_isDisposed || dialogRoot == null)
                {
                    Log.Warning("Folder drag error dialog cannot be shown because its tab is not attached");
                    completion.TrySetResult(DragTransferAction.Cancel);
                    return;
                }

                var options = new StackPanel { Spacing = 8 };
                options.Children.Add(new TextBlock
                {
                    Text = isPermissionError
                        ? LocalizationHelper.GetString("DragPermissionErrorDescription")
                        : LocalizationHelper.GetString("DragTransferErrorDescription"),
                    TextWrapping = TextWrapping.Wrap
                });
                options.Children.Add(new TextBlock
                {
                    Text = remotePath,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });
                options.Children.Add(new TextBlock
                {
                    Text = exception.Message,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });

                var actions = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };

                void AddOption(string text, DragTransferAction action, bool isChecked = false)
                {
                    actions.Children.Add(new RadioButton
                    {
                        Content = text,
                        Tag = action,
                        IsChecked = isChecked,
                        GroupName = "DragTransferErrorAction"
                    });
                }

                if (isPermissionError)
                {
                    AddOption(LocalizationHelper.GetString("DragOptionSkipItem"), DragTransferAction.Skip, isChecked: true);
                    AddOption(LocalizationHelper.GetString("DragOptionSkipAllPermissionErrors"), DragTransferAction.SkipAll);
                    if (canTrySudo)
                    {
                        AddOption(LocalizationHelper.GetString("DragOptionTrySudo"), DragTransferAction.TrySudo);
                    }
                }
                else
                {
                    AddOption(LocalizationHelper.GetString("DragOptionRetry"), DragTransferAction.Retry, isChecked: true);
                    AddOption(LocalizationHelper.GetString("DragOptionSkipItem"), DragTransferAction.Skip);
                    AddOption(LocalizationHelper.GetString("DragOptionSkipAllSimilarErrors"), DragTransferAction.SkipAll);
                }

                AddOption(LocalizationHelper.GetString("DragOptionCancelTransfer"), DragTransferAction.Cancel);
                options.Children.Add(actions);

                var dialog = new ContentDialog
                {
                    Title = isPermissionError
                        ? LocalizationHelper.GetString("PermissionDenied")
                        : LocalizationHelper.GetString("DragTransferErrorTitle"),
                    Content = options,
                    PrimaryButtonText = LocalizationHelper.GetString("ContinueButton"),
                    CloseButtonText = LocalizationHelper.GetString("CancelButton"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = dialogRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    completion.TrySetResult(DragTransferAction.Cancel);
                    return;
                }

                var selectedAction = actions.Children
                    .OfType<RadioButton>()
                    .FirstOrDefault(option => option.IsChecked == true)?.Tag;

                completion.TrySetResult(
                    selectedAction is DragTransferAction action
                        ? action
                        : DragTransferAction.Cancel);
            }
            catch (Exception dialogException)
            {
                Log.Error($"Failed to show drag transfer error dialog: {dialogException.Message}", dialogException);
                completion.TrySetResult(DragTransferAction.Cancel);
            }
        }))
        {
            return DragTransferAction.Cancel;
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    private bool TryGrantReadAccessWithSudo(string remotePath, bool isDirectory, out string error)
    {
        error = string.Empty;
        var connectionInfo = _sftpClient?.ConnectionInfo;
        if (connectionInfo == null)
        {
            error = LocalizationHelper.GetString("DragSudoNoConnection");
            return false;
        }

        var username = connectionInfo.Username;
        if (string.IsNullOrWhiteSpace(username) ||
            username.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')))
        {
            error = LocalizationHelper.GetString("DragSudoInvalidUsername");
            return false;
        }

        try
        {
            using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);

            var recursiveOption = isDirectory ? "-R " : string.Empty;
            var acl = $"u:{username}:r{(isDirectory ? "X" : string.Empty)}";
            var commandText = $"sudo -n setfacl {recursiveOption}-m {QuotePosixShellArgument(acl)} -- {QuotePosixShellArgument(remotePath)}";
            using var command = sshClient.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromMinutes(2);
            command.Execute();

            if (command.ExitStatus == 0)
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(command.Error)
                ? string.Format(LocalizationHelper.GetString("DragSudoExitCode"), command.ExitStatus)
                : command.Error.Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string QuotePosixShellArgument(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private bool IsSudoBrowsePath(string path)
    {
        var normalizedPath = NormalizeRemotePath(path);
        return _sudoBrowseRoots.Any(root =>
            normalizedPath == root || normalizedPath.StartsWith(root + "/", StringComparison.Ordinal));
    }

    private async Task TryOpenDirectoryWithSudoAsync(string path)
    {
        var (available, error) = await CheckSudoBrowseAvailableAsync(path);
        if (!available)
        {
            Log.Warning($"Non-interactive sudo browse is unavailable for '{path}': {error}");
            await ShowPermissionDeniedAsync(path);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("InsufficientPermissions"),
            Content = string.Format(LocalizationHelper.GetString("OpenFolderWithSudoPrompt"), path),
            PrimaryButtonText = LocalizationHelper.GetString("OpenWithSudoButton"),
            CloseButtonText = LocalizationHelper.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var normalizedPath = NormalizeRemotePath(path);
        _sudoBrowseRoots.RemoveWhere(root =>
            normalizedPath == root || normalizedPath.StartsWith(root + "/", StringComparison.Ordinal));
        _sudoBrowseRoots.Add(normalizedPath);
        NavigateToPath(path, true);
    }

    private async Task<(bool Available, string Error)> CheckSudoBrowseAvailableAsync(string path)
    {
        var connectionInfo = _sftpClient?.ConnectionInfo;
        if (connectionInfo == null)
        {
            return (false, LocalizationHelper.GetString("DragSudoNoConnection"));
        }

        return await Task.Run(() =>
        {
            try
            {
                using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);
                var commandText = $"sudo -n find -- {QuotePosixShellArgument(path)} -mindepth 1 -maxdepth 1 -quit >/dev/null";
                using var command = sshClient.CreateCommand(commandText);
                command.CommandTimeout = TimeSpan.FromSeconds(15);
                command.Execute();

                if (command.ExitStatus == 0)
                {
                    return (true, string.Empty);
                }

                var commandError = string.IsNullOrWhiteSpace(command.Error)
                    ? string.Format(LocalizationHelper.GetString("DragSudoExitCode"), command.ExitStatus)
                    : command.Error.Trim();
                return (false, commandError);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    private async Task ShowPermissionDeniedAsync(string path)
    {
        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("InsufficientPermissions"),
            Content = string.Format(LocalizationHelper.GetString("NoReadPermission"), path),
            CloseButtonText = LocalizationHelper.GetString("OK"),
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task TryOpenFileWithSudoAsync(FileItem item, bool showOpenWithDialog = false)
    {
        var (available, error) = await CheckSudoFileReadAvailableAsync(item.FullPath);
        if (!available)
        {
            Log.Warning($"Non-interactive sudo file access is unavailable for '{item.FullPath}': {error}");
            await ShowPermissionDeniedAsync(item.FullPath);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("InsufficientPermissions"),
            Content = string.Format(LocalizationHelper.GetString("OpenFileWithSudoPrompt"), item.FullPath),
            PrimaryButtonText = LocalizationHelper.GetString("OpenWithSudoButton"),
            CloseButtonText = LocalizationHelper.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            StatusText.Text = string.Format(LocalizationHelper.GetString("LoadingFile"), item.Name);
            var tempFilePath = GetSudoFileCachePath(item);
            await Task.Run(() => DownloadFileWithSudo(item.FullPath, tempFilePath, _lifetimeCts.Token), _lifetimeCts.Token);
            File.SetAttributes(tempFilePath, File.GetAttributes(tempFilePath) | System.IO.FileAttributes.ReadOnly);

            if (showOpenWithDialog)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32.dll,OpenAs_RunDLL \"{tempFilePath}\"",
                    UseShellExecute = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = tempFilePath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }

            StatusText.Text = string.Format(LocalizationHelper.GetString("OpenedFileWithSudoReadOnly"), item.Name);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open file with sudo '{item.FullPath}': {ex.Message}", ex);
            StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorOpeningFile"), ex.Message);
            var errorDialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("ErrorDialogTitle"),
                Content = string.Format(LocalizationHelper.GetString("ErrorOpeningFileDialog"), item.Name, ex.Message),
                CloseButtonText = LocalizationHelper.GetString("OK"),
                XamlRoot = XamlRoot
            };
            await errorDialog.ShowAsync();
        }
    }

    private async Task<(bool Available, string Error)> CheckSudoFileReadAvailableAsync(string path)
    {
        var connectionInfo = _sftpClient?.ConnectionInfo;
        if (connectionInfo == null)
        {
            return (false, LocalizationHelper.GetString("DragSudoNoConnection"));
        }

        return await Task.Run(() =>
        {
            try
            {
                using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);
                var commandText = $"sudo -n head -c 0 -- {QuotePosixShellArgument(path)} >/dev/null";
                using var command = sshClient.CreateCommand(commandText);
                command.CommandTimeout = TimeSpan.FromSeconds(15);
                command.Execute();

                if (command.ExitStatus == 0)
                {
                    return (true, string.Empty);
                }

                var commandError = string.IsNullOrWhiteSpace(command.Error)
                    ? string.Format(LocalizationHelper.GetString("DragSudoExitCode"), command.ExitStatus)
                    : command.Error.Trim();
                return (false, commandError);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    private void DownloadFileWithSudo(string remotePath, string localPath, CancellationToken cancellationToken)
    {
        var connectionInfo = _sftpClient?.ConnectionInfo
            ?? throw new InvalidOperationException(LocalizationHelper.GetString("DragSudoNoConnection"));

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        EnsureDestinationDoesNotExist(localPath);
        var partialPath = CreatePartialFilePath(localPath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sshClient = ConnectAuxiliarySsh(_lifetimeCts.Token);
            using var command = sshClient.CreateCommand($"sudo -n cat -- {QuotePosixShellArgument(remotePath)}");
            command.CommandTimeout = TimeSpan.FromMinutes(10);
            using var fileStream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var asyncResult = command.BeginExecute();
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = command.OutputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileStream.Write(buffer, 0, read);
            }
            command.EndExecute(asyncResult);

            if (command.ExitStatus != 0)
            {
                var commandError = string.IsNullOrWhiteSpace(command.Error)
                    ? string.Format(LocalizationHelper.GetString("DragSudoExitCode"), command.ExitStatus)
                    : command.Error.Trim();
                throw new UnauthorizedAccessException(commandError);
            }

            fileStream.Flush(flushToDisk: true);
            fileStream.Close();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, localPath, overwrite: false);
        }
        catch
        {
            var parent = Path.GetDirectoryName(localPath)!;
            LocalPathSafety.EnsureStrictDescendant(parent, partialPath);
            if (File.Exists(partialPath) && !Directory.Exists(partialPath))
            {
                File.SetAttributes(partialPath, System.IO.FileAttributes.Normal);
                File.Delete(partialPath);
            }
            throw;
        }
    }

    private static string GetSudoFileCachePath(FileItem item)
    {
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.FullPath)));
        var sudoRoot = Path.Combine(Path.GetTempPath(), "SftpExplorer", "Sudo");
        var hashRoot = LocalPathSafety.CombineChild(sudoRoot, pathHash);
        var sessionRoot = LocalPathSafety.CombineChild(hashRoot, Guid.NewGuid().ToString("N"));
        return LocalPathSafety.CombineChild(sessionRoot, item.Name);
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        return path.TrimEnd('/');
    }

    private void AddDragTransferIssue(string remotePath, Exception exception, bool isPermissionError)
    {
        if (_dragTransferIssues.Any(issue =>
                string.Equals(issue.Path, remotePath, StringComparison.Ordinal) &&
                string.Equals(issue.Message, exception.Message, StringComparison.Ordinal)))
        {
            return;
        }

        _dragTransferIssues.Add(new DragTransferIssue(remotePath, exception.Message, isPermissionError));
    }

    private async Task ShowFolderDragPreparationReadyAsync()
    {
        var dialogRoot = XamlRoot;
        if (dialogRoot == null)
        {
            Log.Info("Folder drag is ready while its tab is detached; keeping the ready status for the next activation");
            return;
        }

        string message;
        if (_dragTransferIssues.Count == 0)
        {
            message = LocalizationHelper.GetString("DragPreparationReadyMessage");
        }
        else
        {
            var preview = BuildDragTransferIssuePreview();
            message = string.Format(
                LocalizationHelper.GetString("DragPreparationReadyWithSkippedMessage"),
                _dragTransferIssues.Count,
                preview);
        }

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("DragPreparationReadyTitle"),
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            CloseButtonText = LocalizationHelper.GetString("OK"),
            XamlRoot = dialogRoot
        };

        await dialog.ShowAsync();
    }

    private async Task ShowFolderDragPreparationFailedAsync(string? details = null)
    {
        var dialogRoot = XamlRoot;
        if (dialogRoot == null)
        {
            Log.Warning("Folder drag preparation failed while its tab is detached; keeping the failure status for the next activation");
            return;
        }

        var message = string.IsNullOrWhiteSpace(details)
            ? LocalizationHelper.GetString("DragPreparationFailedMessage")
            : string.Format(LocalizationHelper.GetString("DragPreparationFailedWithErrorMessage"), details);

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("DragPreparationFailedTitle"),
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            CloseButtonText = LocalizationHelper.GetString("OK"),
            XamlRoot = dialogRoot
        };

        await dialog.ShowAsync();
    }

    private string BuildDragTransferIssuePreview()
    {
        var preview = string.Join(
            Environment.NewLine,
            _dragTransferIssues.Take(15).Select(issue => $"• {issue.Path}: {issue.Message}"));
        var remaining = _dragTransferIssues.Count - 15;
        if (remaining > 0)
        {
            preview += Environment.NewLine + string.Format(
                LocalizationHelper.GetString("DragSkippedMoreItems"),
                remaining);
        }

        return preview;
    }

    // Фоновый подсчёт файлов и байтов для drag-drop
    private void CountFilesAndBytesInBackground(List<FileItem> items)
    {
        try
        {
            if (_sftpClient?.IsConnected != true) return;

            long totalBytes = 0;
            int totalFiles = 0;

            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    CountFilesAndBytesRecursive(item.FullPath, ref totalFiles, ref totalBytes);
                }
                else
                {
                    totalFiles++;
                    totalBytes += item.SizeBytes;
                }
            }

            // Обновляем общие счётчики
            Interlocked.Exchange(ref _dragTotalFiles, totalFiles);
            Interlocked.Exchange(ref _dragTotalBytes, totalBytes);

            Log.Debug($"📊 Counted {totalFiles} files, {FormatFileSize(totalBytes)} total");
        }
        catch (Exception ex)
        {
            Log.Error($"❌ Error counting files: {ex.Message}", ex);
        }
    }

    private void CountFilesAndBytesRecursive(string remotePath, ref int fileCount, ref long byteCount)
    {
        if (_sftpClient?.IsConnected != true) return;

        try
        {
            var entries = _sftpClient.ListDirectory(remotePath).ToList();

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..") continue;

                var remoteItemPath = $"{remotePath.TrimEnd('/')}/{entry.Name}";

                if (entry.IsDirectory)
                {
                    CountFilesAndBytesRecursive(remoteItemPath, ref fileCount, ref byteCount);
                }
                else
                {
                    fileCount++;
                    byteCount += entry.Length;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error counting in {remotePath}: {ex.Message}", ex);
        }
    }

    // Counts files under a list of selected items (files + directories)
    private int CountFilesForItems(List<FileItem> items, CancellationToken cancellationToken)
    {
        if (_sftpClient?.IsConnected != true) return 0;

        int total = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (item.IsDirectory)
                {
                    total += CountFilesRecursive(item.FullPath, cancellationToken);
                }
                else
                {
                    total++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning($"Error counting files for {item.FullPath}: {ex.Message}");
            }
        }
        return Math.Max(1, total); // avoid division by zero
    }

    private int CountFilesRecursive(string remotePath, CancellationToken cancellationToken)
    {
        if (_sftpClient?.IsConnected != true) return 0;

        int count = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = _sftpClient.ListDirectory(remotePath).ToList();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name == "." || entry.Name == "..") continue;
                if (entry.IsDirectory)
                {
                    if (entry.IsSymbolicLink) continue;
                    count += CountFilesRecursive(
                        CombineRemotePath(remotePath, entry.Name),
                        cancellationToken);
                }
                else
                {
                    count++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning($"Error counting in {remotePath}: {ex.Message}");
        }

        return count;
    }

    private async void RemoteFiles_DragOver(object sender, DragEventArgs e)
    {
        // Accept files from local file system with enhanced visual feedback
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // Проверяем - идёт ли подготовка данных для drag
            if (_isDragPreparing)
            {
                // Показываем курсор "ожидание" вместо prohibition
                e.AcceptedOperation = DataPackageOperation.Copy;

                try
                {
                    var dragUI = e.DragUIOverride;
                    dragUI.IsCaptionVisible = true;
                    dragUI.IsContentVisible = true;
                    dragUI.IsGlyphVisible = true;
                    dragUI.Caption = "⏳ Preparing files... Please wait";
                }
                catch (Exception ex)
                {
                    Log.Error($"Error customizing drag UI during preparation: {ex.Message}", ex);
                }
            }
            else
            {
                // Данные готовы - нормальный drag
                e.AcceptedOperation = DataPackageOperation.Copy;

                // Enhanced visual feedback using DragUIOverride
                try
                {
                    var dragUI = e.DragUIOverride;
                    dragUI.IsCaptionVisible = true;
                    dragUI.IsContentVisible = true;
                    dragUI.IsGlyphVisible = true;

                    // Check for keyboard modifiers using Windows.System.VirtualKey
                    // Note: In WinUI 3, modifier detection in DragOver is limited
                    // We provide appropriate feedback for the copy operation
                    dragUI.Caption = "Upload to SFTP";

                    // Set appropriate glyph
                    // dragUI.SetContentFromBitmapImage(...); // Could set custom image if needed
                }
                catch (Exception ex)
                {
                    Log.Error($"Error customizing drag UI: {ex.Message}", ex);
                }
            }
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;

            try
            {
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = "⚠️ Cannot drop here";
            }
            catch
            {
                // Не удалось обновить UI драг-дропа - не критично
            }
        }
    }

    private async void RemoteFiles_Drop(object sender, DragEventArgs e)
    {
        // Handle files dropped from local file system
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await UploadFilesFromSystemAsync(items.ToList());
        }
    }

    // Context Menu для Remote Files
    private void ShowContextMenu(PointerRoutedEventArgs e)
    {
        // Определяем элемент под курсором
        FileItem? clickedItem = null;
        if (e.OriginalSource is FrameworkElement element)
        {
            clickedItem = element.DataContext as FileItem;

            // Если кликнули на элемент, который не выбран - выбираем только его
            if (clickedItem != null &&
                (clickedItem.IsVirtualRoot || !RemoteFilesListView.SelectedItems.Contains(clickedItem)))
            {
                RemoteFilesListView.SelectedItem = clickedItem;
            }
        }

        var menu = new MenuFlyout();
        if (clickedItem?.IsVirtualRoot == true)
        {
            var refreshItem = new MenuFlyoutItem
            {
                Text = LocalizationHelper.GetString("RefreshContextMenuItem"),
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            refreshItem.Click += (s, args) => RefreshRemoteFiles(forceFileSystemRefresh: true);
            menu.Items.Add(refreshItem);

            var propertiesItem = new MenuFlyoutItem
            {
                Text = LocalizationHelper.GetString("PropertiesTitle"),
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            propertiesItem.Click += async (s, args) => await ShowFileSystemPropertiesAsync(clickedItem);
            menu.Items.Add(propertiesItem);

            menu.Closed += (s, args) => _isRightClickInProgress = false;
            var rootItemPosition = e.GetCurrentPoint(RemoteFilesListView).Position;
            menu.ShowAt(RemoteFilesListView, rootItemPosition);
            return;
        }

        var selectedItems = GetSelectedRealItems();

        if (selectedItems.Count == 0)
        {
            _isRightClickInProgress = false;
            return;
        }

        // Если выбран один элемент - показываем полное меню
        if (selectedItems.Count == 1)
        {
            var item = selectedItems[0];
            var isSingleFile = !item.IsNavigableDirectory;

            if (isSingleFile)
            {
                var openItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("OpenMenuItem"), Icon = new SymbolIcon(Symbol.OpenFile) };
                openItem.Click += async (s, args) => await OpenRemoteFile();
                menu.Items.Add(openItem);

                var openWithItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("OpenWithMenuItem") };
                openWithItem.Icon = new FontIcon { Glyph = "\uE8A7" };
                openWithItem.Click += async (s, args) => await OpenFileWith();
                menu.Items.Add(openWithItem);

                if (IsBashScript(item))
                {
                    var runItem = new MenuFlyoutItem
                    {
                        Text = LocalizationHelper.GetString("RunMenuItem"),
                        Icon = new FontIcon
                        {
                            Glyph = "\uF5B0",
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
                        }
                    };
                    runItem.Click += async (s, args) =>
                    {
                        menu.Hide();
                        await Task.Delay(50);
                        await RunBashScriptAsync(item, useSudo: false);
                    };
                    menu.Items.Add(runItem);

                    var runWithSudoItem = new MenuFlyoutItem
                    {
                        Text = LocalizationHelper.GetString("RunWithSudoMenuItem"),
                        Icon = new FontIcon
                        {
                            Glyph = "\uE7EF",
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
                        }
                    };
                    runWithSudoItem.Click += async (s, args) =>
                    {
                        menu.Hide();
                        await Task.Delay(50);
                        await RunBashScriptAsync(item, useSudo: true);
                    };
                    menu.Items.Add(runWithSudoItem);
                }

                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var downloadItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("Download"), Icon = new SymbolIcon(Symbol.Download) };
            downloadItem.Click += async (s, args) =>
            {
                menu.Hide();
                await Task.Delay(50);
                await DownloadSelectedFiles();
            };
            menu.Items.Add(downloadItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var cutItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("Cut") };
            cutItem.Icon = new FontIcon { Glyph = "\uE8C6" };
            cutItem.Click += (s, args) => CutButton_Click(s, new RoutedEventArgs());
            menu.Items.Add(cutItem);

            var copyItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("Copy") };
            copyItem.Icon = new FontIcon { Glyph = "\uE8C8" };
            copyItem.Click += (s, args) => CopyButton_Click(s, new RoutedEventArgs());
            menu.Items.Add(copyItem);

            var pasteItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("Paste") };
            pasteItem.Icon = new FontIcon { Glyph = "\uE77F" };
            pasteItem.Click += async (s, args) => PasteButton_Click(s, new RoutedEventArgs());
            pasteItem.IsEnabled = _clipboard.Count > 0;
            menu.Items.Add(pasteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var renameItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("RenameMenuItem") };
            renameItem.Icon = new FontIcon { Glyph = "\uE8AC" };
            renameItem.Click += async (s, args) => RenameButton_Click(s, new RoutedEventArgs());
            menu.Items.Add(renameItem);

            var deleteItem = new MenuFlyoutItem { Text = LocalizationHelper.GetString("DeleteButtonDialog"), Icon = new SymbolIcon(Symbol.Delete) };
            deleteItem.Click += async (s, args) => await DeleteRemoteFiles();
            menu.Items.Add(deleteItem);
        }
        else
        {
            // Если выбрано несколько - показываем только скачать
            var downloadItem = new MenuFlyoutItem { Text = string.Format(LocalizationHelper.GetString("DownloadMultiple"), selectedItems.Count), Icon = new SymbolIcon(Symbol.Download) };
            downloadItem.Click += async (s, args) =>
            {
                menu.Hide();
                await Task.Delay(50);
                await DownloadSelectedFiles();
            };
            menu.Items.Add(downloadItem);
        }

        menu.Closed += (s, args) => _isRightClickInProgress = false;

        var position = e.GetCurrentPoint(RemoteFilesListView).Position;
        menu.ShowAt(RemoteFilesListView, position);
    }

    private async Task OpenFileWith()
    {
        var selectedItems = GetSelectedRealItems();
        if (selectedItems.Count != 1 || selectedItems[0].IsDirectory) return;

        var item = selectedItems[0];

        if (!item.CanRead)
        {
            await TryOpenFileWithSudoAsync(item, showOpenWithDialog: true);
            return;
        }

        if (_sftpClient?.IsConnected != true) return;

        try
        {
            StatusText.Text = string.Format(LocalizationHelper.GetString("DownloadingFile"), item.Name);

            var sessionFolder = CreateLocalTransferSessionDirectory("OpenWith");
            var tempFilePath = LocalPathSafety.CombineChild(sessionFolder, item.Name);
            await DownloadFileToLocalAtomicAsync(
                _sftpClient,
                item.FullPath,
                tempFilePath,
                progress: null,
                cancellationToken: _lifetimeCts.Token);

            // Открываем диалог "Открыть с помощью"
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL {tempFilePath}",
                UseShellExecute = false
            };

            Process.Start(processStartInfo);

            StatusText.Text = string.Format(LocalizationHelper.GetString("OpenedDialogFor"), item.Name);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(LocalizationHelper.GetString("Error"), ex.Message);

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("ErrorDialogTitle"),
                Content = string.Format(LocalizationHelper.GetString("ErrorOpeningDialogSelection"), ex.Message),
                CloseButtonText = LocalizationHelper.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    // File Operations
    private async Task DownloadSelectedFiles()
    {
        Log.Debug($"[DownloadSelectedFiles] Called. _isDownloadInProgress={_isDownloadInProgress}");

        if (_isDownloadInProgress)
        {
            Log.Debug("[DownloadSelectedFiles] Already in progress, returning");
            StatusText.Text = "Download already in progress..."; // Показываем пользователю
            return;
        }

        var selected = GetSelectedRealItems();
        Log.Debug($"[DownloadSelectedFiles] Selected items: {selected.Count}, _sftpClient connected: {_sftpClient?.IsConnected}");

        if (!selected.Any() || _sftpClient?.IsConnected != true)
        {
            Log.Debug("[DownloadSelectedFiles] No items selected or not connected, returning");
            if (!selected.Any())
                StatusText.Text = "No items selected";
            else
                StatusText.Text = "SFTP not connected";
            return;
        }

        // Проверяем права на чтение для выбранных файлов (используем уже загруженные данные)
        var unreadableItems = selected.Where(f => !f.CanRead).ToList();

        if (unreadableItems.Count > 0)
        {
            // Показываем сообщение о недоступных файлах
            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("PermissionDenied"),
                Content = string.Format(LocalizationHelper.GetString("SomeFilesNoReadPermission"), unreadableItems.Count),
                CloseButtonText = LocalizationHelper.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        _isDownloadInProgress = true;
        Log.Debug("[DownloadSelectedFiles] Set _isDownloadInProgress = true");
        try
        {
            await ShowPickersAndDownload(selected);
            // НЕ сбрасываем _isDownloadInProgress здесь - это делается в фоновой задаче
        }
        catch (Exception ex)
        {
            HideProgressBars();
            StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorDownloadingMultiple"), ex.Message);
            _isDownloadInProgress = false; // Только при ошибке сбрасываем здесь
        }
        // Убрали finally блок - флаг сбрасывается в фоновой задаче
    }

    private async Task ShowPickersAndDownload(List<FileItem> selected)
    {
        try
        {
            if (selected == null || selected.Count == 0)
            {
                Log.Warning("[ShowPickersAndDownload] No items selected");
                _isDownloadInProgress = false;
                return;
            }

            // Важная задержка: даем UI время на обновление и закрытие контекстных меню
            await Task.Delay(150);

            if (App.MainWindow != null) App.MainWindow.Activate();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);

            // Если выбран один файл (не папка) — показываем "Сохранить как"
            if (selected.Count == 1 && !selected[0].IsDirectory)
            {
                var item = selected[0];
                if (item == null)
                {
                    Log.Warning("[ShowPickersAndDownload] Selected item is null");
                    _isDownloadInProgress = false;
                    return;
                }

                var singleFolderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
                {
                    SettingsIdentifier = DownloadPickerSettingsIdentifier,
                    SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
                    Title = LocalizationHelper.GetString("DownloadFolderPickerTitle") ?? "Select a download folder"
                };

                var singleFolderResult = await singleFolderPicker.PickSingleFolderAsync();
                if (singleFolderResult is null)
                {
                    Log.Warning("[ShowPickersAndDownload] User cancelled folder picker for single-file save");
                    _isDownloadInProgress = false;
                    return;
                }

                var folderPathChosen = singleFolderResult.Path;
                if (string.IsNullOrEmpty(folderPathChosen))
                {
                    folderPathChosen = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    Log.Warning($"[ShowPickersAndDownload] singleFolderResult.Path empty — falling back to Desktop: {folderPathChosen}");
                }

                var resultPath = LocalPathSafety.CombineChild(folderPathChosen, item.Name);
                Log.Debug($"[ShowPickersAndDownload] Single file download to: {resultPath}");

                BadgeNotificationService.IncrementTransfer();

                // Копируем нужные данные
                var itemFullPath = item.FullPath;
                var singleOperation = BeginCancelableOperation();
                var singleOperationToken = singleOperation.Token;
                CancelOperationButton.Visibility = Visibility.Visible;
                CancelOperationButton.IsEnabled = true;

                // Запускаем скачивание в фоновом потоке (fire-and-forget)
                var singleDownloadTask = Task.Run(async () =>
                {
                    SftpClient? backgroundClient = null;
                    try
                    {
                        singleOperationToken.ThrowIfCancellationRequested();
                        backgroundClient = await ConnectAuxiliarySftpAsync(singleOperationToken);
                        singleOperationToken.ThrowIfCancellationRequested();

                        // Инициализируем счётчики для одиночного файла (получаем реальный размер)
                        try
                        {
                            var fileSize = backgroundClient.Get(itemFullPath).Length;
                            _downloadTotalBytes = fileSize;
                            _downloadedBytes = 0;
                        }
                        catch
                        {
                            _downloadTotalBytes = 0;
                            _downloadedBytes = 0;
                        }

                        await DownloadSingleFileToPath(backgroundClient, item, resultPath, 1, 1, singleOperationToken);

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            HideProgressBars();
                            StatusText.Text = string.Format(LocalizationHelper.GetString("FilesDownloaded"), 1);
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                            StatusText.Text = LocalizationHelper.GetString("OperationCanceled") ?? "Operation canceled");
                    }
                    catch (Exception ex)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorDownloading"), ex.Message);
                        });
                    }
                    finally
                    {
                        Log.Debug($"[Single File Download] Finally block - cleaning up");
                        if (backgroundClient != null)
                        {
                            if (backgroundClient.IsConnected) backgroundClient.Disconnect();
                            backgroundClient.Dispose();
                        }
                        BadgeNotificationService.DecrementTransfer();
                        _isDownloadInProgress = false; // Сбрасываем флаг когда фоновая задача завершена
                        CompleteCancelableOperation(singleOperation);
                        Log.Debug($"[Single File Download] Set _isDownloadInProgress = false");
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            CancelOperationButton.Visibility = Visibility.Collapsed;
                            CancelOperationButton.IsEnabled = true;
                        });
                    }
                });

                TrackBackgroundTask(singleDownloadTask, isDownload: true);
                return;
            }

            // Несколько файлов или папки — показываем выбор папки
            var folderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                SettingsIdentifier = DownloadPickerSettingsIdentifier,
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
                Title = LocalizationHelper.GetString("DownloadFolderPickerTitle") ?? "Select a download folder"
            };

            var folderResult = await folderPicker.PickSingleFolderAsync();
            if (folderResult is null)
            {
                _isDownloadInProgress = false; // Сбрасываем если пользователь отменил
                return;
            }

            BadgeNotificationService.IncrementTransfer();

            // Копируем список выбранных файлов
            var selectedCopy = selected.ToList();
            var targetPath = folderResult.Path;
            var operation = BeginCancelableOperation();
            var operationToken = operation.Token;
            CancelOperationButton.Visibility = Visibility.Visible;
            CancelOperationButton.IsEnabled = true;

            // Запускаем скачивание в фоновом потоке (fire-and-forget), чтобы не блокировать UI
            var downloadTask = Task.Run(async () =>
            {
                SftpClient? multiBackgroundClient = null;

                try
                {
                    Log.Info($"[Background Download] Starting connection...");
                    operationToken.ThrowIfCancellationRequested();
                    multiBackgroundClient = await ConnectAuxiliarySftpAsync(operationToken);
                    operationToken.ThrowIfCancellationRequested();
                    Log.Info($"[Background Download] Connected successfully");

                    // Показываем неопределенный прогресс-бар (анимация точек)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        OverallProgressBar.Visibility = Visibility.Visible;
                        OverallProgressBar.IsIndeterminate = true;
                        StatusText.Text = LocalizationHelper.GetString("CalculatingFiles");
                    });

                    int totalFiles = 0;
                    long totalBytes = 0;
                    int currentFileIndex = 0;

                    Log.Info($"[Background Download] Starting file calculation for {selectedCopy.Count} items");
                    // Подсчет файлов
                    foreach (var item in selectedCopy)
                    {
                        operationToken.ThrowIfCancellationRequested();
                        if (item.IsDirectory)
                        {
                            var (files, bytes) = await CountFilesAndBytesInDirectoryRecursive(
                                multiBackgroundClient,
                                item.FullPath,
                                operationToken);
                            totalFiles += files;
                            totalBytes += bytes;
                        }
                        else
                        {
                            totalFiles++;
                            try
                            {
                                var attr = multiBackgroundClient.Get(item.FullPath);
                                totalBytes += attr.Length;
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Failed to get size for item: {item.FullPath}", ex);
                                totalBytes += item.SizeBytes;
                            }
                        }
                    }

                    Log.Info($"[Background Download] Calculation complete: {totalFiles} files, {totalBytes} bytes");

                    // Save totals
                    _downloadTotalBytes = totalBytes;
                    _downloadedBytes = 0;

                    // Возвращаем обычный режим прогресс-бара
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        OverallProgressBar.IsIndeterminate = false;
                    });

                    Log.Info($"[Background Download] Starting download...");
                    var failures = new List<string>();
                    var succeededFiles = 0;
                    var reservedTopLevelNames = new HashSet<string>(StringComparer.Ordinal);
                    // Скачиваем файлы
                    foreach (var item in selectedCopy)
                    {
                        operationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (item.IsDirectory)
                            {
                                var result = await DownloadDirectoryRecursive(
                                    multiBackgroundClient,
                                    item.FullPath,
                                    targetPath,
                                    item.Name,
                                    currentFileIndex,
                                    totalFiles,
                                    operationToken,
                                    failures,
                                    reservedTopLevelNames);
                                currentFileIndex = result.CurrentIndex;
                                succeededFiles += result.Succeeded;
                            }
                            else
                            {
                                currentFileIndex++;
                                await DownloadSingleFile(
                                    multiBackgroundClient,
                                    item,
                                    targetPath,
                                    currentFileIndex,
                                    totalFiles,
                                    operationToken,
                                    reservedTopLevelNames);
                                succeededFiles++;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Failed to download '{item.FullPath}': {ex.Message}", ex);
                            failures.Add($"{item.FullPath}: {ex.Message}");
                        }
                    }

                    Log.Info($"[Background Download] Download complete!");

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HideProgressBars();
                        StatusText.Text = failures.Count == 0
                            ? string.Format(LocalizationHelper.GetString("FilesDownloaded"), succeededFiles)
                            : $"Downloaded {succeededFiles} file(s); {failures.Count} item(s) failed. {failures[0]}";
                    });
                }
                catch (OperationCanceledException)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HideProgressBars();
                        StatusText.Text = LocalizationHelper.GetString("OperationCanceled") ?? "Operation canceled";
                    });
                }
                catch (Exception ex)
                {
                    Log.Error($"[Background Download] ERROR: {ex.Message}", ex);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HideProgressBars();
                        StatusText.Text = $"Download ERROR: {ex.Message}"; // Показываем ошибку напрямую
                    });
                }
                finally
                {
                    Log.Debug($"[Background Download] Finally block - cleaning up");
                    if (multiBackgroundClient != null)
                    {
                        if (multiBackgroundClient.IsConnected) multiBackgroundClient.Disconnect();
                        multiBackgroundClient.Dispose();
                    }
                    BadgeNotificationService.DecrementTransfer();
                    _isDownloadInProgress = false; // Сбрасываем флаг когда фоновая задача завершена
                    CompleteCancelableOperation(operation);
                    Log.Debug($"[Background Download] Set _isDownloadInProgress = false");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        CancelOperationButton.Visibility = Visibility.Collapsed;
                        CancelOperationButton.IsEnabled = true;
                    });
                }
            });

            TrackBackgroundTask(downloadTask, isDownload: true);
        }
        catch (Exception ex)
        {
            HideProgressBars();
            StatusText.Text = string.Format(LocalizationHelper.GetString("ErrorDownloadingMultiple"), ex.Message);
            _isDownloadInProgress = false; // Сбрасываем при ошибке
        }
    }

    private async Task DownloadSingleFileToPath(
        SftpClient client,
        FileItem item,
        string targetPath,
        int currentIndex,
        int totalFiles,
        CancellationToken cancellationToken)
    {
        var fileSize = await RunClientResultAsync(
            client,
            token =>
            {
                token.ThrowIfCancellationRequested();
                return client.Get(item.FullPath).Length;
            },
            cancellationToken);
        var startTime = DateTime.Now;
        long previousDownloaded = 0;

        await DownloadFileToLocalAtomicAsync(
            client,
            item.FullPath,
            targetPath,
            downloaded =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delta = (long)downloaded - previousDownloaded;
                previousDownloaded = (long)downloaded;
                Interlocked.Add(ref _downloadedBytes, delta);

                var percent = fileSize > 0 ? (int)((downloaded * 100) / (ulong)fileSize) : 100;
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                var speed = elapsed > 0 ? downloaded / elapsed : 0;
                var remaining = downloaded >= (ulong)Math.Max(0, fileSize)
                    ? 0
                    : (ulong)fileSize - downloaded;
                var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isDisposed) return;
                    StatusText.Text = string.Format(LocalizationHelper.GetString("DownloadingProgress"), currentIndex, totalFiles, item.Name);
                    ProgressPercent.Text = $"{percent}% ({FormatFileSize((long)downloaded)}/{FormatFileSize(fileSize)})";
                    ProgressSpeed.Text = string.Format(LocalizationHelper.GetString("SpeedPerSecond"), FormatFileSize((long)speed));
                    ProgressETA.Text = string.Format(LocalizationHelper.GetString("TimeRemaining"), FormatTimeSpan(eta));
                    ShowProgressBar(percent);
                    if (totalFiles > 0 && _downloadTotalBytes > 0)
                    {
                        ShowOverallProgress(
                            Math.Max(0, currentIndex - 1),
                            totalFiles,
                            Interlocked.Read(ref _downloadedBytes),
                            _downloadTotalBytes);
                    }
                    else if (totalFiles > 0)
                    {
                        ShowOverallProgress(currentIndex, totalFiles);
                    }
                });
            },
            cancellationToken);
    }

    private async Task<int> CountFilesInDirectory(string remotePath)
    {
        int count = 0;
        try
        {
            var files = _sftpClient?.ListDirectory(remotePath);
            if (files == null) return 0;

            foreach (var file in files.Where(f => f.Name != "." && f.Name != ".."))
            {
                if (file.IsDirectory)
                {
                    count += await CountFilesInDirectory(file.FullName);
                }
                else
                {
                    count++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to count files in directory: {remotePath}", ex);
        }
        return count;
    }

    private async Task<(int fileCount, long byteCount)> CountFilesAndBytesInDirectoryRecursive(
        SftpClient client,
        string remotePath,
        CancellationToken cancellationToken)
    {
        int fileCount = 0;
        long byteCount = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = client.ListDirectory(remotePath);
            if (files == null) return (0, 0);

            foreach (var file in files.Where(f => f.Name != "." && f.Name != ".."))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.IsDirectory)
                {
                    if (file.IsSymbolicLink) continue;
                    var (subFiles, subBytes) = await CountFilesAndBytesInDirectoryRecursive(
                        client,
                        CombineRemotePath(remotePath, file.Name),
                        cancellationToken);
                    fileCount += subFiles;
                    byteCount += subBytes;
                }
                else
                {
                    fileCount++;
                    byteCount += file.Length;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to count files/bytes in directory: {remotePath}", ex);
        }

        return (fileCount, byteCount);
    }

    private async Task DownloadSingleFile(
        SftpClient client,
        FileItem item,
        string targetFolder,
        int currentIndex,
        int totalFiles,
        CancellationToken cancellationToken,
        ISet<string>? reservedNames = null)
    {
        var localPath = reservedNames == null
            ? LocalPathSafety.CombineChild(targetFolder, item.Name)
            : LocalPathSafety.ReserveChild(targetFolder, item.Name, reservedNames);
        await DownloadSingleFileToPath(client, item, localPath, currentIndex, totalFiles, cancellationToken);
    }

    private async Task<(int CurrentIndex, int Succeeded)> DownloadDirectoryRecursive(
        SftpClient client,
        string remotePath,
        string localBasePath,
        string folderName,
        int currentIndex,
        int totalFiles,
        CancellationToken cancellationToken,
        ICollection<string> failures,
        ISet<string>? parentReservedNames = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localPath = parentReservedNames == null
            ? LocalPathSafety.CombineChild(localBasePath, folderName)
            : LocalPathSafety.ReserveChild(localBasePath, folderName, parentReservedNames);
        EnsureDestinationDoesNotExist(localPath);
        Directory.CreateDirectory(localPath);

        var files = await RunClientResultAsync(client, token =>
        {
            token.ThrowIfCancellationRequested();
            return client.ListDirectory(remotePath).ToList();
        }, cancellationToken);
        var reservedNames = new HashSet<string>(StringComparer.Ordinal);
        var succeeded = 0;

        foreach (var file in files.Where(f => f.Name != "." && f.Name != ".."))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LocalPathSafety.ValidateSingleName(file.Name);
                if (file.IsSymbolicLink)
                {
                    throw new NotSupportedException($"Downloading symbolic links recursively is not supported: {file.FullName}");
                }

                if (file.IsDirectory)
                {
                    var childResult = await DownloadDirectoryRecursive(
                        client,
                        CombineRemotePath(remotePath, file.Name),
                        localPath,
                        file.Name,
                        currentIndex,
                        totalFiles,
                        cancellationToken,
                        failures,
                        reservedNames);
                    currentIndex = childResult.CurrentIndex;
                    succeeded += childResult.Succeeded;
                }
                else
                {
                    currentIndex++;
                    var item = new FileItem
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        SizeBytes = file.Length,
                        IsDirectory = false
                    };
                    await DownloadSingleFile(client, item, localPath, currentIndex, totalFiles, cancellationToken, reservedNames);
                    succeeded++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Error downloading '{file.FullName}': {ex.Message}", ex);
                failures.Add($"{file.FullName}: {ex.Message}");
            }
        }

        return (currentIndex, succeeded);
    }

    private async Task OpenRemoteFile()
    {
        if (RemoteFilesListView.SelectedItem is not FileItem item)
        {
            return;
        }

        if (item.IsVirtualRoot)
        {
            return;
        }

        if (item.IsSymbolicLink)
        {
            await OpenSymbolicLinkAsync(item);
            return;
        }

        if (!item.IsDirectory)
        {
            if (!item.CanRead)
            {
                await TryOpenFileWithSudoAsync(item);
                return;
            }

            try
            {
                var sessionFolder = CreateLocalTransferSessionDirectory("Open");
                var tempPath = LocalPathSafety.CombineChild(sessionFolder, item.Name);
                await DownloadFileToLocalAtomicAsync(
                    _sftpClient!,
                    item.FullPath,
                    tempPath,
                    progress: null,
                    cancellationToken: _lifetimeCts.Token);

                Process.Start(new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error opening file: {ex.Message}";
            }
        }
    }



    private Task DeleteRemoteFiles()
    {
        // Context-menu delete uses the same recursive, cancellable and
        // per-item failure-reporting workflow as the toolbar button.
        DeleteButton_Click(this, new RoutedEventArgs());
        return Task.CompletedTask;
    }


    // DragDropManager + custom drop target removed; using XAML `DragOver`/`Drop` handlers instead

    public class FileItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Size { get; set; } = "";
        public long SizeBytes { get; set; } = 0;
        public string Modified { get; set; } = "";
        public string Permissions { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Group { get; set; } = "";
        public string Icon { get; set; } = "\uE8A5";
        public bool IsDirectory { get; set; }
        public bool IsSymbolicLink { get; set; }
        public bool SymbolicLinkTargetIsDirectory { get; set; }
        public bool IsNavigableDirectory => IsDirectory || (IsSymbolicLink && SymbolicLinkTargetIsDirectory);
        public string FullPath { get; set; } = "";
        public bool CanRead { get; set; } = true;
        public bool IsVirtualRoot { get; set; }
        public bool HasFileSystemStats { get; private set; }
        public long FileSystemTotalBytes { get; private set; }
        public long FileSystemUsedBytes { get; private set; }
        public long FileSystemAvailableBytes { get; private set; }
        public double OccupiedPercentage { get; private set; }
        public string FreeSpaceText { get; private set; } = "";
        public Visibility FreeSpaceVisibility => HasFileSystemStats ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ExecutableOverlayVisibility
        {
            get
            {
                if (IsNavigableDirectory || !Permissions.Contains('x'))
                {
                    return Visibility.Collapsed;
                }

                var extension = Path.GetExtension(Name);
                var isBashFile = extension.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
                                 extension.Equals(".bash", StringComparison.OrdinalIgnoreCase);
                return isBashFile || string.IsNullOrEmpty(extension)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
        public Visibility SymbolicLinkOverlayVisibility => IsSymbolicLink ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RestrictedOverlayVisibility => CanRead ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetFileSystemStats(long totalBytes, long usedBytes, long availableBytes, string freeSpaceText)
        {
            HasFileSystemStats = true;
            FileSystemTotalBytes = Math.Max(0, totalBytes);
            FileSystemUsedBytes = Math.Max(0, usedBytes);
            FileSystemAvailableBytes = Math.Clamp(availableBytes, 0, FileSystemTotalBytes);
            OccupiedPercentage = FileSystemTotalBytes == 0
                ? 0
                : Math.Clamp(
                    (FileSystemTotalBytes - FileSystemAvailableBytes) * 100d / FileSystemTotalBytes,
                    0,
                    100);
            FreeSpaceText = freeSpaceText;
            NotifyFileSystemStatsChanged();
        }

        public void ClearFileSystemStats()
        {
            if (!HasFileSystemStats)
            {
                return;
            }

            HasFileSystemStats = false;
            FileSystemTotalBytes = 0;
            FileSystemUsedBytes = 0;
            FileSystemAvailableBytes = 0;
            OccupiedPercentage = 0;
            FreeSpaceText = "";
            NotifyFileSystemStatsChanged();
        }

        private void NotifyFileSystemStatsChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFileSystemStats)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileSystemTotalBytes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileSystemUsedBytes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileSystemAvailableBytes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OccupiedPercentage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreeSpaceText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreeSpaceVisibility)));
        }
    }

    public class OpenFileInfo
    {
        public string RemotePath { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public DateTime LastWriteTime { get; set; }
        public long LastUploadedLength { get; set; }
        public CancellationTokenSource SyncCancellation { get; } = new();
    }
}
