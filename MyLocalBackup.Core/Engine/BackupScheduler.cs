using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Data;

namespace MyLocalBackup.Core.Engine
{
    public class BackupScheduler
    {
        private Timer? _timer;
        private readonly object _timerLock = new();
        private readonly BackupConfig _config;
        private readonly DatabaseManager _db;
        private readonly SemaphoreSlim _backupSemaphore = new(1, 1);
        private readonly ManualResetEventSlim _pauseEvent = new(true); // Initially signaled (not paused)
        private CancellationTokenSource? _backupCts;
        private readonly object _ctsLock = new();
        private SynchronizationContext? _syncContext;
        private Action<Action>? _uiDispatcher;

        public event EventHandler<string>? BackupStarted;
        public event EventHandler<(bool success, string? error, IReadOnlyList<string>? failedFiles)>? BackupCompleted;
        public event EventHandler<(double percentage, string task)>? ProgressUpdated;
        public event EventHandler<DateTime>? NextRunScheduled;

        public DateTime? NextRunTime { get; private set; }
        public bool IsRunning => _backupSemaphore.CurrentCount == 0;
        public bool IsPaused => !_pauseEvent.IsSet;
        public bool IsCancelling { get { lock (_ctsLock) { return _backupCts?.IsCancellationRequested ?? false; } } }

        public void CancelBackup()
        {
            lock (_ctsLock)
            {
                // Cancel under lock to prevent racing with Dispose in finally
                _backupCts?.Cancel();
            }
            // Wake up if paused so cancellation can proceed.
            // Try-catch guards against ObjectDisposedException if Dispose() runs concurrently.
            try { _pauseEvent.Set(); }
            catch (ObjectDisposedException) { Logger.Log("Warning: Pause event already disposed during CancelBackup."); }
        }

        public BackupScheduler(BackupConfig config, DatabaseManager db)
        {
            _config = config;
            _db = db;
            // Capture UI thread context for event marshaling
            _syncContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Sets a dispatcher function to marshal events to the UI thread.
        /// Call this from the UI layer after the message loop is running.
        /// </summary>
        public void SetUiDispatcher(Action<Action> dispatcher)
        {
            _uiDispatcher = dispatcher;
        }

        // Helper to invoke events on the UI thread if available
        private void InvokeOnSyncContext(Action action)
        {
            // Prefer explicit UI dispatcher if set
            if (_uiDispatcher != null)
            {
                _uiDispatcher(action);
            }
            else if (_syncContext != null)
            {
                _syncContext.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }

        public void Start()
        {
            if (_config.Schedule == ScheduleType.Manual) return;
            if (!HasValidBackupConfig())
            {
                Logger.Log("Scheduler not started: source folders or destination are not configured.");
                Stop();
                return;
            }

            if (_config.ScheduleMode == ScheduleMode.DailyFixedTime)
            {
                StartDailySchedule();
                return;
            }

            RestorePoint? lastBackup;
            try { lastBackup = _db.GetLastSuccessfulRestorePoint(); }
            catch (Exception ex)
            {
                Logger.Log($"Could not query last backup, falling back to full interval: {ex.Message}");
                lastBackup = null;
            }

            TimeSpan delay;

            if (lastBackup != null)
            {
                // Calculate when the next run SHOULD be (Timestamp is already UTC)
                var nextRun = lastBackup.Timestamp.AddHours(_config.IntervalHours);
                delay = nextRun - DateTime.UtcNow;

                if (delay.TotalMilliseconds <= 0)
                {
                    // We missed the schedule while offline. Run immediately (short delay for stabilization).
                    Logger.Log("Missed scheduled backup while offline. Catching up in 5 seconds...");
                    delay = TimeSpan.FromSeconds(5);
                }
                else
                {
                    Logger.Log($"Resuming schedule. Next backup in {delay.TotalMinutes:F1} minutes.");
                }
            }
            else
            {
                // First run ever (or query failed). Wait for the interval.
                delay = TimeSpan.FromHours(_config.IntervalHours);
            }

            ScheduleNextRun(delay);
        }

        private void StartDailySchedule()
        {
            var delay = GetDelayUntilNextDailyRun();

            // Check if a backup already ran today at/after the target time.
            // This prevents duplicate daily backups when the app is restarted
            // before the target time on the same day the backup already completed.
            RestorePoint? lastBackup;
            try { lastBackup = _db.GetLastSuccessfulRestorePoint(); }
            catch (Exception ex)
            {
                Logger.Log($"Could not query last backup for daily schedule check: {ex.Message}");
                lastBackup = null;
            }
            if (lastBackup != null)
            {
                if (!TimeSpan.TryParse(_config.DailyBackupTime, out var targetTime))
                {
                    targetTime = new TimeSpan(2, 0, 0);
                    Logger.Log($"WARNING: Invalid DailyBackupTime '{_config.DailyBackupTime}', using default 02:00.");
                }

                var lastBackupLocal = lastBackup.Timestamp.ToLocalTime();
                var todayTarget = DateTime.Today.Add(targetTime);

                // If last backup was today and was at/after the target time, skip to tomorrow
                if (lastBackupLocal.Date == DateTime.Today && lastBackupLocal.TimeOfDay >= targetTime)
                {
                    var tomorrowTarget = todayTarget.AddDays(1);
                    delay = tomorrowTarget - DateTime.Now;
                    Logger.Log($"Daily backup already completed today at {lastBackupLocal:HH:mm}. Next run tomorrow at {_config.DailyBackupTime}.");
                }
                else
                {
                    Logger.Log($"Daily backup scheduled for {_config.DailyBackupTime}. Next run in {delay.TotalHours:F1} hours.");
                }
            }
            else
            {
                Logger.Log($"Daily backup scheduled for {_config.DailyBackupTime}. Next run in {delay.TotalHours:F1} hours.");
            }

            ScheduleNextRun(delay);
        }

        private TimeSpan GetDelayUntilNextDailyRun()
        {
            if (!TimeSpan.TryParse(_config.DailyBackupTime, out var targetTime))
            {
                targetTime = new TimeSpan(2, 0, 0);
                Logger.Log($"WARNING: Invalid DailyBackupTime '{_config.DailyBackupTime}', using default 02:00.");
            }

            var now = DateTime.Now;
            var todayRun = now.Date.Add(targetTime);

            // If target time already passed today, schedule for tomorrow
            if (todayRun < now)
                todayRun = todayRun.AddDays(1);

            return todayRun - now;
        }

        private void ScheduleNextRun(TimeSpan delay)
        {
            lock (_timerLock)
            {
                _timer?.Dispose();
                NextRunTime = DateTime.Now.Add(delay);
                var nextRun = NextRunTime.Value;
                InvokeOnSyncContext(() => NextRunScheduled?.Invoke(this, nextRun));
                // Use TimeSpan overload to avoid int overflow for intervals > 24.8 days
                var safeDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
                _timer = new Timer(_ => RunScheduledBackupSafe(), null, safeDelay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Timer callback wrapper — ensures the async Task is properly awaited
        /// so exceptions are never silently lost on the ThreadPool.
        /// </summary>
        private async void RunScheduledBackupSafe()
        {
            try { await RunScheduledBackup(); }
            catch (Exception ex) { Logger.Log($"Unhandled error in scheduled backup: {ex}"); }
        }

        private async Task RunScheduledBackup()
        {
            try
            {
                Logger.Log("Starting scheduled backup...");
                await RunBackup();
            }
            catch (Exception ex)
            {
                Logger.Log($"Scheduled backup failed unexpectedly: {ex}");
            }
            finally
            {
                // Hold _timerLock for the entire re-schedule decision to prevent a race where
                // Stop() runs concurrently and sets _timer=null after our null-check but before
                // ScheduleNextRun creates a new timer (which would restart a stopped scheduler).
                // Safe: C# Monitor is reentrant, so the nested ScheduleNextRun/Stop calls that
                // also acquire _timerLock will succeed immediately on the same thread.
                lock (_timerLock)
                {
                    if (_timer != null && _config.Schedule != ScheduleType.Manual && HasValidBackupConfig())
                    {
                        try
                        {
                            var nextDelay = _config.ScheduleMode == ScheduleMode.DailyFixedTime
                                ? GetDelayUntilNextDailyRun()
                                : TimeSpan.FromHours(_config.IntervalHours);
                            ScheduleNextRun(nextDelay);
                            Logger.Log($"Next backup scheduled for {NextRunTime:g}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to re-schedule next backup: {ex}");
                        }
                    }
                    else
                    {
                        Logger.Log($"Not rescheduling: schedule={_config.Schedule}, validConfig={HasValidBackupConfig()}");
                        if (!HasValidBackupConfig())
                        {
                            Stop();
                        }
                    }
                }
            }
        }

        public void Restart()
        {
            Stop();
            Start();
        }

        public void Stop()
        {
            lock (_timerLock)
            {
                _timer?.Dispose();
                _timer = null;
                NextRunTime = null;
            }
        }

        /// <summary>
        /// Releases all resources. Call once during app shutdown after Stop().
        /// </summary>
        public void Dispose()
        {
            Stop();
            _pauseEvent.Set(); // Release any threads waiting on pause before disposing
            _pauseEvent.Dispose();
            _backupSemaphore.Dispose();
        }

        public void Pause()
        {
            _pauseEvent.Reset();
        }

        public void Resume()
        {
            _pauseEvent.Set();
        }

        public async Task RunBackup()
        {
            // We allow manual backups even if paused (scheduler skip only applies to timer runs)
            if (!_backupSemaphore.Wait(0))
            {
                Logger.Log("Backup already in progress. Manual request ignored.");
                return;
            }

            CancellationToken token;
            lock (_ctsLock)
            {
                _backupCts = new CancellationTokenSource();
                token = _backupCts.Token;
            }

            InvokeOnSyncContext(() => BackupStarted?.Invoke(this, "Starting Backup..."));
            bool allSuccess = true;
            string? lastError = null;
            IReadOnlyList<string>? failedFiles = null;

            try
            {
                if (_config.Destination == null)
                {
                    lastError = "No backup destination configured.";
                    Logger.Log(lastError);
                    allSuccess = false;
                    return;
                }

                if (_config.SourceFolders.Count == 0)
                {
                    lastError = "No source folders configured. Please add folders to back up.";
                    Logger.Log(lastError);
                    allSuccess = false;
                    return;
                }

                if (token.IsCancellationRequested)
                {
                    allSuccess = false;
                    return;
                }

                try
                {
                    var dest = _config.Destination;
                    var job = new BackupJob(_config, _db, dest.RootPath);
                    await Task.Run(() => job.Execute((p, t) =>
                    {
                        // Note: Cancellation is now also handled inside BackupJob for responsive file operations
                        if (token.IsCancellationRequested) throw new OperationCanceledException();

                        // Live Pause: Wait here if scheduler is paused (event is the authoritative state)
                        if (!_pauseEvent.IsSet)
                        {
                            InvokeOnSyncContext(() => ProgressUpdated?.Invoke(this, (p, "Paused...")));
                            while (!_pauseEvent.Wait(500))
                            {
                                if (token.IsCancellationRequested) throw new OperationCanceledException();
                            }
                            // Re-check cancellation after waking from pause
                            if (token.IsCancellationRequested) throw new OperationCanceledException();
                        }

                        InvokeOnSyncContext(() => ProgressUpdated?.Invoke(this, (p, t)));
                    }, token), token);

                    // Report partial errors from the job
                    if (job.HasErrors)
                    {
                        allSuccess = false;
                        lastError = $"Backup completed with {job.ErrorCount} file error(s).";
                        failedFiles = job.FailedFiles;
                        Logger.Log(lastError);
                    }

                    var retention = new RetentionManager(_db, _config);
                    retention.Prune(dest.RootPath);
                }
                catch (OperationCanceledException)
                {
                    lastError = "Backup canceled by user.";
                    Logger.Log(lastError);
                    allSuccess = false;
                }
                catch (Exception ex)
                {
                    lastError = $"Backup to {_config.Destination.Name} failed: {ex.Message}";
                    Logger.Log($"Backup to {_config.Destination.Name} failed: {ex}");
                    allSuccess = false;
                }
            }
            finally
            {
                lock (_ctsLock)
                {
                    _backupCts?.Dispose();
                    _backupCts = null;
                }
                _backupSemaphore.Release();
                Logger.Flush(); // Ensure all log entries are written to disk after backup
                InvokeOnSyncContext(() => BackupCompleted?.Invoke(this, (allSuccess, lastError, failedFiles)));
            }
        }

        private bool HasValidBackupConfig()
        {
            return _config.Destination != null
                   && _config.SourceFolders.Count > 0;
        }
    }
}
