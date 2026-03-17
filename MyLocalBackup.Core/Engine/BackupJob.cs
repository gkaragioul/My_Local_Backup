using System.Diagnostics;
using System.Runtime.InteropServices;
using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Storage;
using MyLocalBackup.Core.Data;
using Microsoft.Data.Sqlite;

namespace MyLocalBackup.Core.Engine
{
    public class BackupJob
    {
        private readonly BackupConfig _config;
        private readonly string _destinationRoot;
        private readonly DatabaseManager _centralDb;
        private CancellationToken _cancellationToken;

        // Configuration for large file handling
        private const long LargeFileThreshold = 50 * 1024 * 1024; // 50 MB - files larger than this get chunked progress
        private const int ChunkSize = 1 * 1024 * 1024; // 1 MB chunks - avoids Large Object Heap pressure
        private const int FileOperationTimeoutSeconds = 300; // 5 minutes max per file operation attempt

        // Parallel processing configuration
        private const int BatchInsertSize = 500; // Number of file entries to batch before DB insert

        // Shared state for parallel operations - List+lock is more memory-efficient than ConcurrentBag
        private List<FileEntry> _pendingEntries = new(BatchInsertSize + 1);
        private readonly object _flushLock = new();
        private int _flushFailureCount; // Tracks consecutive flush failures to prevent unbounded re-queuing
        private const int MaxFlushRetries = 3; // Drop entries after this many consecutive failures
        private Dictionary<(long size, string lwt), (int rpId, string relativePath)>? _dedupIndex;
        private Dictionary<int, RestorePoint>? _restorePointCache;

        // Progress tracking - file-count based for accurate progress
        private long _totalFileCount;
        private long _processedFileCount;
        private volatile bool _hasErrors;
        private long _lastProgressReportTicks; // For throttling per-file progress callbacks
        private readonly List<string> _failedFiles = new();
        private readonly object _failedFilesLock = new();

        // Public error state for callers (e.g., BackupScheduler)
        public bool HasErrors => _hasErrors;
        public int ErrorCount { get { lock (_failedFilesLock) return _failedFiles.Count; } }
        public IReadOnlyList<string> FailedFiles { get { lock (_failedFilesLock) return _failedFiles.ToArray(); } }

        // System folders to always exclude (case-insensitive match on directory name)
        private static readonly HashSet<string> SystemExclusions = new(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin",
            "System Volume Information",
            "$WINDOWS.~BT",
            "$WinREAgent",
            "RECYCLER",
            "$SysReset"
        };

        public BackupJob(BackupConfig config, DatabaseManager centralDb, string destinationRoot)
        {
            _config = config;
            _centralDb = centralDb;
            _destinationRoot = destinationRoot;
        }

        private void EnsureDirectoryWithRetry(string path, Action<double, string>? onProgress, int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(path);
                    return; // Success
                }
                catch (DirectoryNotFoundException) when (attempt < maxRetries)
                {
                    // Drive may not be ready yet - wait and retry
                    Logger.Log($"Directory not accessible (attempt {attempt}/{maxRetries}): {path}. Retrying...");
                    onProgress?.Invoke(0, $"Waiting for drive... (attempt {attempt}/{maxRetries})");
                    // Cancellation-aware delay instead of Thread.Sleep
                    _cancellationToken.WaitHandle.WaitOne(1000 * attempt);
                    _cancellationToken.ThrowIfCancellationRequested();
                }
                catch (IOException ex) when (attempt < maxRetries && IsDriveNotReady(ex))
                {
                    // Drive not ready
                    Logger.Log($"Drive not ready (attempt {attempt}/{maxRetries}): {path}. Retrying...");
                    onProgress?.Invoke(0, $"Waiting for drive... (attempt {attempt}/{maxRetries})");
                    _cancellationToken.WaitHandle.WaitOne(1000 * attempt);
                    _cancellationToken.ThrowIfCancellationRequested();
                }
                // On the final attempt, the catch guards above are false so the exception propagates to the caller.
            }
        }

        /// <summary>
        /// Copies a file with progress updates for large files. Supports cancellation and handles locked files.
        /// </summary>
        private void CopyFileWithProgress(string sourcePath, string targetPath, long fileSize,
            Action<double, string>? onProgress, string fileName)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            // For small files, use simple copy (faster)
            if (fileSize < LargeFileThreshold)
            {
                CopyFileSimple(sourcePath, targetPath);
                return;
            }

            // For large files, use chunked copy with progress
            Logger.Log($"Copying large file ({fileSize / (1024 * 1024)}MB): {fileName}");

            var stopwatch = Stopwatch.StartNew();
            var chunkStopwatch = Stopwatch.StartNew(); // Track time per chunk for hang detection
            long totalBytesCopied = 0;

            try
            {
                using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 65536, FileOptions.SequentialScan);
                using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);

                var buffer = new byte[ChunkSize];
                int bytesRead;
                DateTime lastProgressUpdate = DateTime.Now;

                while (true)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    // Measure the blocking Read so hung drives are detected
                    chunkStopwatch.Restart();
                    bytesRead = sourceStream.Read(buffer, 0, buffer.Length);

                    if (chunkStopwatch.Elapsed.TotalSeconds > FileOperationTimeoutSeconds)
                        throw new TimeoutException($"File read stalled for {FileOperationTimeoutSeconds}s: {fileName}");

                    if (bytesRead == 0) break;

                    targetStream.Write(buffer, 0, bytesRead);
                    totalBytesCopied += bytesRead;

                    // Update progress every 500ms to avoid UI flooding
                    if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 500)
                    {
                        double fileCopyProgress = (double)totalBytesCopied / fileSize;
                        double mbCopied = totalBytesCopied / (1024.0 * 1024.0);
                        double mbTotal = fileSize / (1024.0 * 1024.0);
                        double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                        double mbPerSec = elapsedSec > 0 ? mbCopied / elapsedSec : 0;

                        // Calculate overall progress based on global file count + partial large file progress
                        // Large files get sub-file progress reporting within their "slot"
                        long currentProcessed = Interlocked.Read(ref _processedFileCount);
                        long totalFiles = Interlocked.Read(ref _totalFileCount);
                        double baseFileProgress = totalFiles > 0
                            ? 5.0 + (90.0 * currentProcessed / totalFiles)
                            : 0;
                        double fileSlotSize = totalFiles > 0 ? 90.0 / totalFiles : 0;
                        double localProgress = Math.Min(baseFileProgress + (fileSlotSize * fileCopyProgress), 95.0);

                        string progressMsg = $"Copying {fileName}: {mbCopied:F1}/{mbTotal:F1} MB ({mbPerSec:F1} MB/s)";
                        onProgress?.Invoke(localProgress, progressMsg);
                        lastProgressUpdate = DateTime.Now;
                    }
                }
            }
            catch (IOException ex)
            {
                // File might be locked - already handled by FileShare.ReadWrite, but log for debugging
                Logger.Log($"IO error copying {fileName}: {ex}");
                throw;
            }

            var elapsed = stopwatch.Elapsed;
            Logger.Log($"Large file copied: {fileName} ({fileSize / (1024 * 1024)}MB in {elapsed.TotalSeconds:F1}s)");
        }

        /// <summary>
        /// Simple file copy for small files with locked file fallback.
        /// </summary>
        private void CopyFileSimple(string sourcePath, string targetPath)
        {
            try
            {
                File.Copy(sourcePath, targetPath, true);
            }
            catch (IOException)
            {
                // Fallback: Try reading with shared access (for locked files like Word docs)
                using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 65536, FileOptions.SequentialScan);
                using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
                sourceStream.CopyTo(targetStream, bufferSize: 65536);
            }
        }

        public void Execute(Action<double, string>? onProgress = null, CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
            var snapshotsPath = Path.Combine(_destinationRoot, "Backup Snapshots");

            onProgress?.Invoke(0, "Preparing environment...");
            _cancellationToken.ThrowIfCancellationRequested();

            // Ensure directory structure with retry logic (drive may not be ready immediately)
            EnsureDirectoryWithRetry(snapshotsPath, onProgress);
            _cancellationToken.ThrowIfCancellationRequested();

            // Pre-scan to count total files for accurate progress (1-5%)
            onProgress?.Invoke(1, "Scanning source folders...");
            Interlocked.Exchange(ref _totalFileCount, CountFilesInSources(onProgress));
            Interlocked.Exchange(ref _processedFileCount, 0);
            Logger.Log($"Found {Interlocked.Read(ref _totalFileCount)} files to process.");
            _cancellationToken.ThrowIfCancellationRequested();

            // Use a single master connection for the entire backup session
            using var masterCentralConn = _centralDb.GetConnection();

            RestorePoint? rpCentral = null;

            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke(2, "Initializing live backup...");
                var timestampUtc = DateTime.UtcNow;
                var snapshotName = timestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH.mm.ss");
                var finalSnapshotPath = Path.Combine(snapshotsPath, snapshotName);

                // Create the folder immediately for live feedback
                Directory.CreateDirectory(finalSnapshotPath);

                // Detect if destination was formatted: check if any previous snapshots still exist on disk
                _cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke(3, "Checking destination state...");
                CleanupStaleRestorePoints(masterCentralConn, snapshotsPath);

                onProgress?.Invoke(4, "Creating backup records...");
                rpCentral = new RestorePoint
                {
                    Timestamp = timestampUtc,
                    Path = finalSnapshotPath,
                    Status = BackupStatus.InProgress,
                    TargetDestination = _destinationRoot
                };

                _centralDb.AddRestorePoint(rpCentral, masterCentralConn);

                _cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke(5, "Analyzing previous snapshots...");

                // Identification of previous snapshot for dedup (from central DB, filtered by destination)
                var latestCompleteRp = _centralDb.GetRestorePoints(masterCentralConn)
                    .Where(x => x.Id != rpCentral.Id && (x.Status == BackupStatus.Completed || x.Status == BackupStatus.CompletedWithErrors) && x.TargetDestination == _destinationRoot)
                    .OrderByDescending(x => x.Timestamp)
                    .FirstOrDefault();

                // Pre-load dedup index for O(1) lookups instead of DB query per file
                _cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke(6, "Building deduplication index...");
                _dedupIndex = _centralDb.GetDedupIndex(_destinationRoot, masterCentralConn, _cancellationToken);
                _cancellationToken.ThrowIfCancellationRequested();
                _restorePointCache = _centralDb.GetRestorePoints(masterCentralConn)
                    .Where(r => r.TargetDestination == _destinationRoot && (r.Status == BackupStatus.Completed || r.Status == BackupStatus.CompletedWithErrors))
                    .ToDictionary(r => r.Id);
                Logger.Log($"Dedup index loaded: {_dedupIndex.Count} entries from {_restorePointCache.Count} snapshots");

                // Reset pending entries for this backup
                _pendingEntries = new List<FileEntry>(BatchInsertSize + 1);

                foreach (var sourceFolder in _config.SourceFolders)
                {
                    if (!Directory.Exists(sourceFolder))
                    {
                        Logger.Log($"Warning: Source folder not found: {sourceFolder}");
                        continue;
                    }

                    // Sanitize folder name (especially for drive roots like Z:\)
                    var sourceFolderName = new DirectoryInfo(sourceFolder).Name;
                    if (string.IsNullOrEmpty(sourceFolderName) || sourceFolderName.Contains(':'))
                    {
                        sourceFolderName = sourceFolder.Replace(":", "").Replace("\\", "_").Replace("/", "_").Trim('_');
                    }

                    var targetSourceRoot = Path.Combine(finalSnapshotPath, sourceFolderName);

                    // Calculate current progress based on files processed so far
                    long totalForProgress = Interlocked.Read(ref _totalFileCount);
                    double currentProgress = totalForProgress > 0
                        ? 5.0 + (90.0 * Interlocked.Read(ref _processedFileCount) / totalForProgress)
                        : 5.0;
                    currentProgress = Math.Min(currentProgress, 95.0);

                    Logger.Log($"Processing folder: {sourceFolder} -> {sourceFolderName}");
                    onProgress?.Invoke(currentProgress, $"Backing up: {sourceFolderName}...");

                    ProcessDirectory(masterCentralConn, sourceFolderName, sourceFolder, sourceFolder, targetSourceRoot, latestCompleteRp, rpCentral.Id, onProgress);
                }

                // Flush any remaining pending file entries
                onProgress?.Invoke(95, "Saving file index...");
                FlushPendingEntries(masterCentralConn);

                // Validate that files were actually backed up
                long successCount = Interlocked.Read(ref _processedFileCount);
                int errorCount;
                lock (_failedFilesLock) { errorCount = _failedFiles.Count; }

                if (successCount == 0 && errorCount == 0)
                {
                    Logger.Log("Warning: Backup completed but NO files were processed. Check source folders.");
                }
                else
                {
                    Logger.Log($"Backup processed {successCount} files.");
                }

                // Log error summary if there were failures
                if (errorCount > 0)
                {
                    Logger.Log($"--- {errorCount} file(s) failed during backup ---");
                    string[] failedList;
                    lock (_failedFilesLock) { failedList = _failedFiles.ToArray(); }
                    // Log up to 50 failed files to avoid flooding the log
                    int logLimit = Math.Min(failedList.Length, 50);
                    for (int i = 0; i < logLimit; i++)
                    {
                        Logger.Log($"  FAILED: {failedList[i]}");
                    }
                    if (failedList.Length > 50)
                    {
                        Logger.Log($"  ... and {failedList.Length - 50} more.");
                    }
                    Logger.Log("--- End of error summary ---");
                }

                // Finalize the snapshot
                onProgress?.Invoke(98, "Finalizing backup...");
                Logger.Log($"Backup complete at {finalSnapshotPath}");

                var finalStatus = _hasErrors ? BackupStatus.CompletedWithErrors : BackupStatus.Completed;
                _centralDb.UpdateRestorePointStatus(rpCentral.Id, finalStatus, masterCentralConn);

                string completionMsg = _hasErrors
                    ? $"Backup completed with {errorCount} error(s). {successCount} files OK."
                    : "Backup successful.";
                onProgress?.Invoke(100, completionMsg);
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Backup canceled by user.");
                // Flush any pending entries so DB stays consistent with files on disk
                try { FlushPendingEntries(masterCentralConn); } catch (Exception flushEx) { Logger.Log($"Warning: Could not flush pending entries on cancel: {flushEx.Message}"); }
                try
                {
                    if (rpCentral != null)
                        _centralDb.UpdateRestorePointStatus(rpCentral.Id, BackupStatus.Interrupted, masterCentralConn);
                }
                catch (Exception statusEx)
                {
                    Logger.Log($"Warning: Could not update backup status to Interrupted: {statusEx}");
                }
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"Backup failed: {ex}");
                // Flush any pending entries so DB stays consistent with files on disk
                try { FlushPendingEntries(masterCentralConn); } catch (Exception flushEx) { Logger.Log($"Warning: Could not flush pending entries on failure: {flushEx.Message}"); }
                try
                {
                    if (rpCentral != null)
                        _centralDb.UpdateRestorePointStatus(rpCentral.Id, BackupStatus.Failed, masterCentralConn);
                }
                catch (Exception statusEx)
                {
                    Logger.Log($"Warning: Could not update backup status to Failed: {statusEx}");
                }
                throw;
            }
        }

        /// <summary>
        /// Detects if the destination was formatted by checking if previous snapshots still exist.
        /// If snapshots are missing (drive was formatted), removes stale records from central DB.
        /// </summary>
        /// <summary>
        /// Recursively deletes a directory in the background, respecting cancellation.
        /// Logs progress but does not block the caller.
        /// </summary>
        private static void DeleteDirectoryInBackground(string path, string displayName, CancellationToken ct)
        {
            Task.Run(() =>
            {
                try
                {
                    long deleted = 0;
                    var lastLog = DateTime.MinValue;

                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            Logger.Log($"Background cleanup of '{displayName}' paused by cancellation at {deleted:N0} files. Will resume next run.");
                            return;
                        }

                        try { File.Delete(file); }
                        catch { /* skip locked files */ }
                        deleted++;

                        if ((DateTime.Now - lastLog).TotalSeconds > 30)
                        {
                            Logger.Log($"Background cleanup '{displayName}': {deleted:N0} files deleted...");
                            lastLog = DateTime.Now;
                        }
                    }

                    // Remove the now-empty directory tree
                    try { Directory.Delete(path, true); }
                    catch { /* will be retried next run */ }

                    Logger.Log($"Background cleanup of '{displayName}' complete: {deleted:N0} files deleted.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Warning: Background cleanup of '{displayName}' failed: {ex.Message}");
                }
            }, CancellationToken.None); // Don't tie to backup token — let it keep running
        }

        private void CleanupStaleRestorePoints(SqliteConnection centralConn, string snapshotsPath)
        {
            try
            {
                // Clean up any Interrupted/InProgress records left by previous cancellations or crashes
                var partialSnapshots = _centralDb.GetRestorePoints(centralConn)
                    .Where(x => x.TargetDestination == _destinationRoot &&
                                (x.Status == BackupStatus.InProgress || x.Status == BackupStatus.Interrupted))
                    .ToList();

                foreach (var partial in partialSnapshots)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var folderName = Path.GetFileName(partial.Path);

                        if (Directory.Exists(partial.Path))
                        {
                            // Mark as Deleting in DB so it won't be picked up as a valid snapshot
                            _centralDb.UpdateRestorePointStatus(partial.Id, BackupStatus.Deleting, centralConn);
                            Logger.Log($"Queued background cleanup of partial snapshot: {folderName}");
                            DeleteDirectoryInBackground(partial.Path, folderName, _cancellationToken);
                        }
                        else
                        {
                            // Directory already gone — just remove the DB record
                            _centralDb.DeleteRestorePoint(partial.Id, centralConn);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Could not clean up partial snapshot {partial.Id}: {ex}");
                    }
                }

                // Also clean up any snapshots previously marked as Deleting (background delete from prior run)
                var deletingSnapshots = _centralDb.GetRestorePoints(centralConn)
                    .Where(x => x.TargetDestination == _destinationRoot && x.Status == BackupStatus.Deleting)
                    .ToList();

                foreach (var deleting in deletingSnapshots)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (Directory.Exists(deleting.Path))
                        {
                            var folderName = Path.GetFileName(deleting.Path);
                            Logger.Log($"Resuming background cleanup of: {folderName}");
                            DeleteDirectoryInBackground(deleting.Path, folderName, _cancellationToken);
                        }
                        else
                        {
                            // Already fully deleted — remove DB record
                            _centralDb.DeleteRestorePoint(deleting.Id, centralConn);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Could not resume cleanup of snapshot {deleting.Id}: {ex}");
                    }
                }

                _cancellationToken.ThrowIfCancellationRequested();

                var existingSnapshots = _centralDb.GetRestorePoints(centralConn)
                    .Where(x => x.TargetDestination == _destinationRoot && (x.Status == BackupStatus.Completed || x.Status == BackupStatus.CompletedWithErrors))
                    .ToList();

                if (existingSnapshots.Count == 0) return;

                // Check if any of the recorded snapshots actually exist on disk
                var staleSnapshots = existingSnapshots.Where(rp => !Directory.Exists(rp.Path)).ToList();

                if (staleSnapshots.Count > 0)
                {
                    Logger.Log($"Detected {staleSnapshots.Count} stale backup records (destination may have been formatted). Cleaning up...");
                    foreach (var stale in staleSnapshots)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            Logger.Log($"Removing stale record: {stale.Path}");
                            _centralDb.DeleteRestorePoint(stale.Id, centralConn);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Logger.Log($"Warning: Could not remove stale record {stale.Id}: {ex}");
                        }
                    }

                    if (existingSnapshots.Count == staleSnapshots.Count)
                    {
                        Logger.Log("No valid previous backups found on destination. Starting fresh backup.");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Error during stale record cleanup: {ex}");
            }
        }

        /// <summary>
        /// Pre-scans source folders to count total files for accurate progress reporting.
        /// </summary>
        private long CountFilesInSources(Action<double, string>? onProgress)
        {
            long count = 0;
            int folderIndex = 0;
            int totalFolders = _config.SourceFolders.Count;

            foreach (var sourceFolder in _config.SourceFolders)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(sourceFolder))
                    continue;

                var folderName = new DirectoryInfo(sourceFolder).Name;
                if (string.IsNullOrEmpty(folderName) || folderName.Contains(':'))
                    folderName = sourceFolder.Replace(":", "").Replace("\\", "_").Replace("/", "_").Trim('_');

                // Show scanning progress (0-5% range)
                double scanProgress = 1 + (4.0 * folderIndex / Math.Max(1, totalFolders));
                onProgress?.Invoke(scanProgress, $"Scanning: {folderName}...");

                count += CountFilesRecursive(sourceFolder);
                folderIndex++;
            }

            return count;
        }

        /// <summary>
        /// Recursively counts files in a directory, handling access errors gracefully.
        /// </summary>
        private long CountFilesRecursive(string directory)
        {
            long count = 0;

            try
            {
                // Count files in current directory
                count += Directory.EnumerateFiles(directory).Count();

                // Recurse into subdirectories
                foreach (var subDir in Directory.EnumerateDirectories(directory))
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    var dirName = Path.GetFileName(subDir);

                    // Skip system folders
                    if (SystemExclusions.Contains(dirName))
                        continue;

                    // Skip symlinks/junctions to avoid infinite loops
                    var dirInfo = new DirectoryInfo(subDir);
                    if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    count += CountFilesRecursive(subDir);
                }
            }
            catch (UnauthorizedAccessException ex) { Logger.Log($"Warning: Skipping inaccessible directory {directory}: {ex.Message}"); }
            catch (DirectoryNotFoundException) { /* Directory deleted between enumeration and access */ }
            catch (IOException ex) { Logger.Log($"Warning: I/O error scanning {directory}: {ex.Message}"); }

            return count;
        }

        /// <summary>
        /// Flushes pending file entries to the database in a batch.
        /// Swaps the list under lock so new entries added concurrently go into the next batch.
        /// </summary>
        private void FlushPendingEntries(SqliteConnection conn)
        {
            lock (_flushLock)
            {
                if (_pendingEntries.Count == 0) return;

                var entries = _pendingEntries;
                _pendingEntries = new List<FileEntry>(BatchInsertSize + 1);

                try
                {
                    _centralDb.AddFileEntriesBatch(entries, conn);
                    _flushFailureCount = 0; // Reset on success
                    Logger.Log($"Flushed {entries.Count} file entries to database");
                }
                catch (Exception ex)
                {
                    _flushFailureCount++;
                    if (_flushFailureCount <= MaxFlushRetries)
                    {
                        // Re-queue entries so they aren't silently lost
                        Logger.Log($"Warning: Batch insert failed ({entries.Count} entries), re-queuing (attempt {_flushFailureCount}/{MaxFlushRetries}): {ex}");
                        _pendingEntries.InsertRange(0, entries);
                    }
                    else
                    {
                        // Drop entries to prevent unbounded memory growth — log which files were lost
                        _hasErrors = true;
                        Logger.Log($"ERROR: Batch insert failed {_flushFailureCount} times, dropping {entries.Count} entries to prevent memory exhaustion: {ex}");
                        int logLimit = Math.Min(entries.Count, 20);
                        for (int i = 0; i < logLimit; i++)
                            Logger.Log($"  DROPPED: {entries[i].RelativePath}");
                        if (entries.Count > 20)
                            Logger.Log($"  ... and {entries.Count - 20} more entries dropped.");
                    }
                }
            }
        }

        /// <summary>
        /// Adds a file entry to the pending batch. Flushes to DB when batch is full.
        /// </summary>
        private void AddFileEntryBatched(FileEntry entry, SqliteConnection conn)
        {
            bool shouldFlush = false;
            lock (_flushLock)
            {
                _pendingEntries.Add(entry);
                shouldFlush = _pendingEntries.Count >= BatchInsertSize;
            }

            if (shouldFlush)
            {
                FlushPendingEntries(conn);
            }
        }

        /// <summary>
        /// Fast dedup lookup using pre-loaded index instead of DB query.
        /// </summary>
        private (int rpId, string relativePath)? FindInDedupIndex(long size, DateTime lwt)
        {
            if (_dedupIndex == null) return null;

            var key = (size, lwt.ToString("O"));
            if (_dedupIndex.TryGetValue(key, out var match))
            {
                return match;
            }
            return null;
        }

        private void ProcessDirectory(SqliteConnection centralConn, string sourceRootName, string sourceRoot, string currentDir, string stagingTargetDir, RestorePoint? prevRp, int rpIdCentral, Action<double, string>? onProgress)
        {
            // Check for cancellation at directory level for quick exit
            _cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Directory.CreateDirectory(stagingTargetDir);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                _hasErrors = true;
                Logger.Log($"FATAL: Disk full while creating directory: {ex}");
                throw;
            }
            catch (Exception ex)
            {
                _hasErrors = true;
                Logger.Log($"Error creating directory {stagingTargetDir}: {ex}");
                return;
            }

            // 1. Process Files
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Cannot access files in {currentDir}: {ex}");
                return;
            }

            foreach (var sourceFilePath in files)
            {
                // Check cancellation before each file
                _cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var fileName = Path.GetFileName(sourceFilePath);
                    var targetFilePath = Path.Combine(stagingTargetDir, fileName);

                    var fileInfo = new FileInfo(sourceFilePath);

                    // Handle symbolic links - try to preserve them
                    if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        var linkTarget = fileInfo.LinkTarget;
                        if (linkTarget != null)
                        {
                            try
                            {
                                // Preserve the symlink by creating one with the same target
                                File.CreateSymbolicLink(targetFilePath, linkTarget);

                                // Record symlink in database (batched for performance)
                                var symlinkEntry = new FileEntry
                                {
                                    RestorePointId = rpIdCentral,
                                    RelativePath = Path.Combine(sourceRootName, Path.GetRelativePath(sourceRoot, sourceFilePath)),
                                    IsDirectory = false,
                                    Size = 0,
                                    LastWriteTime = fileInfo.LastWriteTimeUtc,
                                    Attributes = (uint)fileInfo.Attributes
                                };
                                AddFileEntryBatched(symlinkEntry, centralConn);

                                Interlocked.Increment(ref _processedFileCount);
                                continue;
                            }
                            catch (UnauthorizedAccessException)
                            {
                                // Symlink creation requires admin or developer mode - log once
                                Logger.Log($"Warning: Cannot create symlink (requires admin/developer mode): {fileName} -> {linkTarget}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Warning: Failed to preserve symlink {fileName}: {ex}");
                            }
                        }
                        else
                        {
                            Logger.Log($"Warning: Could not resolve symlink target for file: {sourceFilePath}");
                        }

                        // Couldn't preserve symlink - record as failed so user sees it
                        Logger.Log($"Skipping unpreservable symlink: {sourceFilePath}");
                        lock (_failedFilesLock) { _failedFiles.Add(sourceFilePath); }
                        Interlocked.Increment(ref _processedFileCount);
                        continue;
                    }
                    var relativePath = Path.GetRelativePath(sourceRoot, sourceFilePath);
                    var dbRelativePath = Path.Combine(sourceRootName, relativePath);

                    bool linked = false;

                    // Priority 1: Check same path in latest snapshot
                    if (prevRp != null)
                    {
                        var prevSnapshotFilePath = Path.Combine(prevRp.Path, dbRelativePath);
                        if (File.Exists(prevSnapshotFilePath))
                        {
                            var prevInfo = new FileInfo(prevSnapshotFilePath);
                            if (fileInfo.Length == prevInfo.Length && fileInfo.LastWriteTimeUtc == prevInfo.LastWriteTimeUtc)
                            {
                                try
                                {
                                    linked = HardLinkManager.Create(targetFilePath, prevSnapshotFilePath);
                                }
                                catch (Exception hlEx)
                                {
                                    Logger.Log($"Warning: Hard link failed for {fileName}: {hlEx}");
                                }
                            }
                        }
                    }

                    // Priority 2: Rename detection (using pre-loaded dedup index for O(1) lookup)
                    if (!linked && _dedupIndex != null && _restorePointCache != null)
                    {
                        var match = FindInDedupIndex(fileInfo.Length, fileInfo.LastWriteTimeUtc);
                        if (match != null)
                        {
                            if (_restorePointCache.TryGetValue(match.Value.rpId, out var matchRp))
                            {
                                var matchPhysicalPath = Path.Combine(matchRp.Path, match.Value.relativePath);
                                try
                                {
                                    if (File.Exists(matchPhysicalPath))
                                        linked = HardLinkManager.Create(targetFilePath, matchPhysicalPath);
                                }
                                catch (Exception hlEx)
                                {
                                    // TOCTOU: source may have been deleted between Exists check and Create
                                    Logger.Log($"Warning: Hard link failed for {fileName}: {hlEx.Message}");
                                }
                            }
                        }
                    }

                    // Priority 3: Copy (with progress for large files and Read-Share fallback)
                    if (!linked)
                    {
                        try
                        {
                            CopyFileWithProgress(sourceFilePath, targetFilePath, fileInfo.Length, onProgress, fileName);
                        }
                        catch
                        {
                            // Clean up partial/truncated file to prevent dedup index poisoning
                            try { if (File.Exists(targetFilePath)) File.Delete(targetFilePath); }
                            catch (Exception cleanupEx) { Logger.Log($"Warning: Could not clean up partial file {targetFilePath}: {cleanupEx.Message}"); }
                            throw;
                        }
                        // Set timestamps separately - failure here shouldn't count as a copy error
                        try
                        {
                            File.SetLastWriteTimeUtc(targetFilePath, fileInfo.LastWriteTimeUtc);
                            File.SetCreationTimeUtc(targetFilePath, fileInfo.CreationTimeUtc);
                        }
                        catch (Exception tsEx)
                        {
                            Logger.Log($"Warning: Could not set timestamps on {fileName}: {tsEx}");
                        }
                    }

                    // Record in central DB (batched for performance)
                    var entry = new FileEntry
                    {
                        RestorePointId = rpIdCentral,
                        RelativePath = dbRelativePath,
                        IsDirectory = false,
                        Size = fileInfo.Length,
                        LastWriteTime = fileInfo.LastWriteTimeUtc,
                        Attributes = (uint)fileInfo.Attributes
                    };
                    AddFileEntryBatched(entry, centralConn);

                    Interlocked.Increment(ref _processedFileCount);

                    // Calculate progress based on global file count (5-95% range for file processing)
                    long totalForFileProgress = Interlocked.Read(ref _totalFileCount);
                    double fileProgress = totalForFileProgress > 0
                        ? 5.0 + (90.0 * Interlocked.Read(ref _processedFileCount) / totalForFileProgress)
                        : 5.0;
                    fileProgress = Math.Min(fileProgress, 95.0);

                    // Throttle per-file progress to at most once every 250ms to reduce GC pressure
                    var now = DateTime.Now.Ticks;
                    var lastTicks = Interlocked.Read(ref _lastProgressReportTicks);
                    if (now - lastTicks >= TimeSpan.TicksPerMillisecond * 250)
                    {
                        Interlocked.Exchange(ref _lastProgressReportTicks, now);
                        onProgress?.Invoke(fileProgress, sourceFilePath);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // Re-throw to stop processing immediately
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    _hasErrors = true;
                    Logger.Log($"FATAL: Disk full on destination: {ex}");
                    throw; // Abort the whole backup immediately if disk is full
                }
                catch (Exception ex)
                {
                    _hasErrors = true;
                    lock (_failedFilesLock) { _failedFiles.Add(sourceFilePath); }
                    // Only log if it's not a "file not found" for a symlink target
                    if (ex is not FileNotFoundException)
                    {
                        Logger.Log($"Error backing up file {sourceFilePath}: {ex}");
                    }
                }
            }

            // 2. Recurse into subdirectories
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Cannot access subdirectories in {currentDir}: {ex}");
                return;
            }

            foreach (var subDir in subDirs)
            {
                // Check for cancellation before processing each subdirectory
                _cancellationToken.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(subDir);

                // Skip system folders (Recycle Bin, System Volume Information, etc.)
                if (SystemExclusions.Contains(dirName))
                    continue;

                var targetSubDir = Path.Combine(stagingTargetDir, dirName);

                // Check if this directory is a symlink/junction
                var dirInfo = new DirectoryInfo(subDir);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var linkTarget = dirInfo.LinkTarget;
                    if (linkTarget != null)
                    {
                        try
                        {
                            // Preserve directory symlink
                            Directory.CreateSymbolicLink(targetSubDir, linkTarget);
                            continue;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            Logger.Log($"Warning: Cannot create directory symlink (requires admin/developer mode): {dirName} -> {linkTarget}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Warning: Failed to preserve directory symlink {dirName}: {ex}");
                        }
                    }
                    else
                    {
                        Logger.Log($"Warning: Could not resolve symlink target for directory: {subDir}");
                    }

                    // Couldn't preserve directory symlink - record as failed so user sees it
                    Logger.Log($"Skipping unpreservable directory symlink: {subDir}");
                    lock (_failedFilesLock) { _failedFiles.Add(subDir); }
                    continue;
                }

                ProcessDirectory(centralConn, sourceRootName, sourceRoot, subDir, targetSubDir, prevRp, rpIdCentral, onProgress);
            }
        }

        private static bool IsDiskFull(Exception ex)
        {
            const int ERROR_DISK_FULL = 112;
            const int ERROR_HANDLE_DISK_FULL = 39;
            int errorCode = Marshal.GetHRForException(ex) & 0xFFFF;
            return errorCode == ERROR_DISK_FULL || errorCode == ERROR_HANDLE_DISK_FULL;
        }

        private static bool IsDriveNotReady(Exception ex)
        {
            const int ERROR_NOT_READY = 21;
            int errorCode = Marshal.GetHRForException(ex) & 0xFFFF;
            return errorCode == ERROR_NOT_READY;
        }
    }
}
