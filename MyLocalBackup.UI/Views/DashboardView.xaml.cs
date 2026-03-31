using System.Windows;
using System.Windows.Controls;
using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;
using MyLocalBackup.Core.Configuration;
using MyLocalBackup.Core;
using System.IO;
using System.Windows.Media.Animation;

namespace MyLocalBackup.UI.Views
{
    public partial class DashboardView : System.Windows.Controls.UserControl
    {
        private readonly DatabaseManager _db;
        private readonly BackupScheduler _scheduler;
        private readonly ConfigManager _configManager;
        private System.Windows.Threading.DispatcherTimer? _countdownTimer;
        private bool _isUnloaded;

        // Cached theme brushes to avoid repeated TryFindResource lookups
        private System.Windows.Media.Brush? _successBrush;
        private System.Windows.Media.Brush? _warningBrush;
        private System.Windows.Media.Brush? _errorBrush;
        private System.Windows.Media.Brush? _textMutedBrush;
        private System.Windows.Media.Brush? _primaryBrush;

        public DashboardView()
        {
            InitializeComponent();
            _db = App.DatabaseManager;
            _scheduler = App.Scheduler;
            _configManager = App.ConfigManager;

            // Subscribe/unsubscribe on Loaded/Unloaded to handle view reuse from cache
            this.Loaded += DashboardView_Loaded;
            this.Unloaded += DashboardView_Unloaded;
        }

        private void CacheBrushes()
        {
            _successBrush = TryFindResource("SuccessBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Green;
            _warningBrush = TryFindResource("WarningBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Orange;
            _errorBrush = TryFindResource("ErrorBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Red;
            _textMutedBrush = TryFindResource("TextMutedBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            _primaryBrush = TryFindResource("PrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Teal;
        }

        private void DashboardView_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;
            CacheBrushes();

            // Unsubscribe first to prevent duplicate handlers if Loaded fires multiple times
            _scheduler.BackupStarted -= OnBackupStarted;
            _scheduler.BackupCompleted -= OnBackupCompletedHandler;
            _scheduler.ProgressUpdated -= OnProgressUpdated;
            _scheduler.NextRunScheduled -= OnNextRunScheduled;
            // Subscribe to scheduler events (handles both initial load and navigation back)
            _scheduler.BackupStarted += OnBackupStarted;
            _scheduler.BackupCompleted += OnBackupCompletedHandler;
            _scheduler.ProgressUpdated += OnProgressUpdated;
            _scheduler.NextRunScheduled += OnNextRunScheduled;

            InitializeData();
            UpdateScheduleDisplay();
            StartCountdown();
        }

        private void DashboardView_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;

            // Unsubscribe from events to prevent memory leak and duplicate handlers
            _scheduler.BackupStarted -= OnBackupStarted;
            _scheduler.BackupCompleted -= OnBackupCompletedHandler;
            _scheduler.ProgressUpdated -= OnProgressUpdated;
            _scheduler.NextRunScheduled -= OnNextRunScheduled;

            // Unsubscribe Tick handlers before disposing timers to prevent leaks
            if (_countdownTimer != null)
            {
                _countdownTimer.Tick -= OnCountdownTimerTick;
            }
            StopAndClearTimer(ref _countdownTimer);

            if (_errorDismissTimer != null)
            {
                _errorDismissTimer.Tick -= OnErrorDismissTimerTick;
            }
            StopAndClearTimer(ref _errorDismissTimer);

            if (_hideTimer != null)
            {
                _hideTimer.Tick -= OnHideTimerTick;
            }
            StopAndClearTimer(ref _hideTimer);
        }

        private void OnBackupCompletedHandler(object? sender, (bool success, string? error, System.Collections.Generic.IReadOnlyList<string>? failedFiles) data)
        {
            // Store failed files for the Settings > Failed Files tab
            UILogger.SetFailedFiles(data.failedFiles);
            OnBackupCompleted(sender, (data.success, data.error));
        }

        private void InitializeData()
        {
            var rps = _db.GetRestorePoints();
            // Show the most recent finished backup (any terminal status)
            var lastRp = rps.Where(r => r.Status == Core.Models.BackupStatus.Completed
                                     || r.Status == Core.Models.BackupStatus.CompletedWithErrors
                                     || r.Status == Core.Models.BackupStatus.Failed
                                     || r.Status == Core.Models.BackupStatus.Interrupted)
                            .OrderByDescending(r => r.Timestamp)
                            .FirstOrDefault();

            if (lastRp != null)
            {
                TxtLastBackup.Text = $"Last backup: {lastRp.LocalTimestamp:g}";

                // Default: hide the logs link
                BtnViewLogs.Visibility = Visibility.Collapsed;
                TxtLogSeparator.Visibility = Visibility.Collapsed;

                switch (lastRp.Status)
                {
                    case Core.Models.BackupStatus.Completed:
                        TxtStatus.Text = "System Protected";
                        IconStatus.Text = "\uE73E"; // Checkmark
                        IconStatus.Foreground = _successBrush;
                        break;
                    case Core.Models.BackupStatus.CompletedWithErrors:
                        TxtStatus.Text = "Backup Completed with Errors";
                        IconStatus.Text = "\uE7BA"; // Warning
                        IconStatus.Foreground = _warningBrush;
                        // Show link to view logs for failed files
                        BtnViewLogs.Visibility = Visibility.Visible;
                        TxtLogSeparator.Visibility = Visibility.Visible;
                        // Load persisted failed files from DB so they survive app restarts
                        try
                        {
                            var failedFiles = _db.GetFailedFiles(lastRp.Id);
                            if (failedFiles.Count > 0)
                                UILogger.SetFailedFiles(failedFiles);
                        }
                        catch (Exception ex)
                        {
                            Core.Logger.Log($"Warning: Could not load failed files: {ex.Message}");
                        }
                        break;
                    case Core.Models.BackupStatus.Failed:
                        TxtStatus.Text = "Last Backup Failed";
                        IconStatus.Text = "\uE783"; // Error X
                        IconStatus.Foreground = _errorBrush;
                        // Show link to view logs
                        BtnViewLogs.Visibility = Visibility.Visible;
                        TxtLogSeparator.Visibility = Visibility.Visible;
                        break;
                    case Core.Models.BackupStatus.Interrupted:
                        TxtStatus.Text = "Last Backup Was Cancelled";
                        IconStatus.Text = "\uE711"; // Cancel
                        IconStatus.Foreground = _textMutedBrush;
                        break;
                }
            }
            else
            {
                TxtLastBackup.Text = "No backups yet.";
                TxtStatus.Text = "Ready to Backup";
                IconStatus.Text = "\uE73E";
                IconStatus.Foreground = _textMutedBrush;
            }

            // Sync with current running state in case view was recreated/loaded during backup
            if (_scheduler.IsRunning)
            {
                // Override icon to show active/progress state instead of last backup's status
                IconStatus.Text = "\uE895"; // Sync icon
                IconStatus.Foreground = _primaryBrush;

                ShowRunningActions();
                ProgressSection.Visibility = Visibility.Visible;
                ProgressSection.Opacity = 1;

                if (_scheduler.IsCancelling)
                {
                    TxtStatus.Text = "Cancelling...";
                    BtnSkipBackup.IsEnabled = false;
                    BtnPause.Visibility = Visibility.Collapsed;
                }
                else if (_scheduler.IsPaused)
                {
                    TxtStatus.Text = "Backup Paused";
                    BtnPause.Content = "Resume";
                }
                else
                {
                    TxtStatus.Text = "Backup in Progress";
                    BtnPause.Content = "Pause";
                }
            }
            else
            {
                // Ensure UI is fully reset when no backup is running
                ShowIdleActions();
                ProgressSection.Visibility = Visibility.Collapsed;
                BackupProgressBar.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgressTask.Text = "";
            }

            RefreshLists();
        }

        private void RefreshLists()
        {
            LstFolders.ItemsSource = null;
            LstFolders.ItemsSource = _configManager.Config.SourceFolders.ToList();

            if (_configManager.Config.Destination != null)
            {
                PanelDestination.Visibility = Visibility.Visible;
                TxtNoDest.Visibility = Visibility.Collapsed;
                TxtDestName.Text = _configManager.Config.Destination.Name;
                TxtDestPath.Text = _configManager.Config.Destination.RootPath;
                BtnChangeDestination.Content = "Change";
            }
            else
            {
                PanelDestination.Visibility = Visibility.Collapsed;
                TxtNoDest.Visibility = Visibility.Visible;
                BtnChangeDestination.Content = "Add";
            }
        }

        private System.Windows.Threading.DispatcherTimer? _hideTimer;
        private System.Windows.Threading.DispatcherTimer? _errorDismissTimer;
        private void OnBackupStarted(object? sender, string msg)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _hideTimer?.Stop(); // Prevent delayed hide from previous run

                // Reset progress bar to 0 for fresh start
                BackupProgressBar.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgressTask.Text = "Starting...";

                TxtStatus.Text = "Backup in Progress";

                // Reset status icon to a neutral sync/progress indicator
                IconStatus.Text = "\uE895"; // Sync icon
                IconStatus.Foreground = _primaryBrush;

                ProgressSection.Visibility = Visibility.Visible;
                ProgressSection.Opacity = 1; // Force opacity
                TxtDriveWarning.Visibility = Visibility.Visible;

                ShowRunningActions();
                BtnPause.Content = "Pause";

                // Opacity animation for progress
                var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400));
                ProgressSection.BeginAnimation(OpacityProperty, anim);

                // Hide next backup countdown while running
                TxtNextBackup.Visibility = Visibility.Collapsed;
                _countdownTimer?.Stop();
            });
        }

        private long _lastUiUpdateTicks;
        private void OnProgressUpdated(object? sender, (double percentage, string task) data)
        {
            // Always let the final 100% update through so the progress bar completes
            if (data.percentage < 100)
            {
                // Throttle intermediate UI updates to 4 FPS (250ms) to reduce Dispatcher overhead on low-end machines
                long nowTicks = DateTime.Now.Ticks;
                long lastTicks = Interlocked.Read(ref _lastUiUpdateTicks);
                if ((nowTicks - lastTicks) < TimeSpan.TicksPerMillisecond * 250) return;
                Interlocked.Exchange(ref _lastUiUpdateTicks, nowTicks);
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                BackupProgressBar.Value = data.percentage;
                TxtProgressPercent.Text = $"{(int)data.percentage}%";
                TxtStatus.Text = data.percentage >= 100 ? $"Backing up: Completed"
                    : data.percentage > 0 ? $"Backing up: {(int)data.percentage}%" : "Preparing backup...";

                // Safety check: ensure progress is visible if we're getting updates
                if (ProgressSection.Visibility != Visibility.Visible)
                {
                    ProgressSection.Visibility = Visibility.Visible;
                    ProgressSection.Opacity = 1;
                }

                // If the message is a full path (started with drive or network), let's make it look nicer
                string displayTask = data.task;
                if (displayTask.Contains("\\") && displayTask.Length > 60)
                {
                    // Shorten long paths for UI
                    displayTask = "Processing: ..." + displayTask[^55..];
                }

                TxtProgressTask.Text = displayTask;
            });
        }

        private void OnBackupCompleted(object? sender, (bool success, string? error) data)
        {
            Dispatcher.BeginInvoke(() =>
            {
                InitializeData();
                UpdateScheduleDisplay();
                StartCountdown();
                ShowIdleActions();

                if (!data.success && !string.IsNullOrEmpty(data.error))
                {
                    BackupErrorBanner.Visibility = Visibility.Visible;
                    TxtBackupError.Text = data.error;

                    // Auto-dismiss error banner after 5 seconds (reuse existing timer)
                    if (_errorDismissTimer == null)
                    {
                        _errorDismissTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                        _errorDismissTimer.Tick += OnErrorDismissTimerTick;
                    }
                    _errorDismissTimer.Stop();
                    _errorDismissTimer.Start();
                }

                // Hide progress with delay (reuse existing timer)
                if (_hideTimer == null)
                {
                    _hideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    _hideTimer.Tick += OnHideTimerTick;
                }
                _hideTimer.Stop();
                _hideTimer.Start();
            });
        }

        private void BtnViewLogs_Click(object sender, RoutedEventArgs e)
        {
            // Navigate to Settings and switch to the Failed Files tab
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.NavigateTo("Settings");
                // Get the SettingsView and switch to the Failed Files tab
                if (mainWindow.GetView("Settings") is SettingsView settingsView)
                {
                    settingsView.SwitchToFailedFilesTab();
                }
            }
        }

        private void StopAndClearTimer(ref System.Windows.Threading.DispatcherTimer? timer)
        {
            if (timer != null)
            {
                timer.Stop();
                timer = null;
            }
        }

        private void OnErrorDismissTimerTick(object? sender, EventArgs e)
        {
            _errorDismissTimer?.Stop();
            if (_isUnloaded) return;
            BackupErrorBanner.Visibility = Visibility.Collapsed;
        }

        private void OnHideTimerTick(object? sender, EventArgs e)
        {
            _hideTimer?.Stop();
            if (_isUnloaded) return;
            ProgressSection.Visibility = Visibility.Collapsed;
            TxtDriveWarning.Visibility = Visibility.Collapsed;
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler.IsPaused)
            {
                _scheduler.Resume();
                BtnPause.Content = "Pause";
                TxtStatus.Text = "Backup in Progress";
            }
            else
            {
                _scheduler.Pause();
                BtnPause.Content = "Resume";
                TxtStatus.Text = "Backup Paused";
            }
        }

        private void BtnCloseError_Click(object sender, RoutedEventArgs e)
        {
            BackupErrorBanner.Visibility = Visibility.Collapsed;
            _errorDismissTimer?.Stop();
        }

        private void BtnBackupNow_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler.IsRunning)
            {
                _scheduler.CancelBackup();
                TxtStatus.Text = "Cancelling...";
                BtnSkipBackup.IsEnabled = false;
                BtnPause.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Disable immediately to prevent rapid double-click launching duplicate backups
                BtnBackupNow.IsEnabled = false;
                Logger.Log("Manual backup requested from Dashboard.");

                _ = _scheduler.RunBackup().ContinueWith(t =>
                {
                    try
                    {
                        if (t.IsFaulted && t.Exception != null)
                            Logger.Log($"Backup failed unexpectedly: {t.Exception}");
                    }
                    finally
                    {
                        // Re-enable on UI thread after backup completes (success or failure)
                        // In finally so the button is always re-enabled even if logging throws
                        Dispatcher.BeginInvoke(() => BtnBackupNow.IsEnabled = true);
                    }
                }, TaskScheduler.Default);
            }
        }

        // Folder Management Logic
        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (!_configManager.Config.SourceFolders.Any(f => string.Equals(f, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    // Warn if user selected a root drive (e.g. H:\)
                    if (Path.GetPathRoot(dialog.SelectedPath)?.TrimEnd('\\').Equals(dialog.SelectedPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var result = System.Windows.MessageBox.Show(
                            "You selected an entire drive. This may include development folders (node_modules, bin, obj) and system files, which can make backups extremely slow.\n\nConsider adding specific subfolders instead.\n\nContinue anyway?",
                            "Large Backup Warning",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning);
                        if (result != System.Windows.MessageBoxResult.Yes)
                            return;
                    }

                    _configManager.Config.SourceFolders.Add(dialog.SelectedPath);
                    _configManager.SaveConfig();
                    RefreshLists();
                    UpdateScheduleDisplay();
                }
            }
        }

        private void BtnRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.DataContext is string folder)
            {
                _configManager.Config.SourceFolders.Remove(folder);
                _configManager.SaveConfig();
                RefreshLists();
                UpdateScheduleDisplay();
            }
        }

        // Destination Management Logic
        private void BtnAddDestination_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Backup Destination",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var path = dialog.SelectedPath;
                string name;
                try
                {
                    string driveRoot = Path.GetPathRoot(path) ?? path;
                    var drive = new DriveInfo(driveRoot);
                    string label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Disk" : drive.VolumeLabel;
                    name = $"{label} ({drive.Name.TrimEnd('\\')})";
                }
                catch
                {
                    // DriveInfo fails on UNC paths - use folder name instead
                    name = new DirectoryInfo(path).Name;
                }

                _configManager.Config.Destination = new Core.Models.BackupDestination
                {
                    Name = name,
                    RootPath = path
                };

                _configManager.SaveConfig();
                RefreshLists();
                UpdateScheduleDisplay();
            }
        }

        private void OnNextRunScheduled(object? sender, DateTime nextRun)
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateScheduleDisplay();
                StartCountdown();
            });
        }

        private void StartCountdown()
        {
            if (_countdownTimer == null)
            {
                _countdownTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _countdownTimer.Tick += OnCountdownTimerTick;
            }

            if (_scheduler.NextRunTime.HasValue && !_countdownTimer.IsEnabled)
            {
                _countdownTimer.Start();
                UpdateCountdown(); // Immediate update
            }
        }

        private void OnCountdownTimerTick(object? sender, EventArgs e)
        {
            if (_isUnloaded) { _countdownTimer?.Stop(); return; }
            UpdateCountdown();
        }

        private void UpdateCountdown()
        {
            if (!_scheduler.NextRunTime.HasValue || _scheduler.IsRunning)
            {
                _countdownTimer?.Stop();
                TxtNextBackup.Visibility = Visibility.Collapsed;
                return;
            }

            var remaining = _scheduler.NextRunTime.Value - DateTime.Now;
            if (remaining.TotalSeconds <= 0)
            {
                TxtNextBackup.Text = "Next backup: starting soon...";
            }
            else
            {
                string timeStr = remaining.TotalHours >= 1
                    ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
                    : $"{remaining.Minutes}m {remaining.Seconds}s";

                TxtNextBackup.Text = $"Next backup: in {timeStr}";
            }
            TxtNextBackup.Visibility = Visibility.Visible;
        }

        private void UpdateScheduleDisplay()
        {
            var config = _configManager.Config;
            if (config.Schedule != MyLocalBackup.Core.Models.ScheduleType.Manual)
            {
                TxtScheduleRate.Text = config.ScheduleMode == Core.Models.ScheduleMode.DailyFixedTime
                    ? $"Auto: Daily at {config.DailyBackupTime}"
                    : $"Auto: Every {config.IntervalHours}h";
                BadgeSchedule.Visibility = Visibility.Visible;
                BtnStopAutoBackup.Visibility = Visibility.Visible;

                // Ensure scheduler is running and has NextRunTime set
                if (!_scheduler.NextRunTime.HasValue && !_scheduler.IsRunning)
                {
                    // Scheduler needs to be (re)started to set NextRunTime
                    _scheduler.Start();
                }

                // Show countdown if we have a scheduled time and backup isn't running
                if (_scheduler.NextRunTime.HasValue && !_scheduler.IsRunning)
                {
                    TxtNextBackup.Visibility = Visibility.Visible;
                    StartCountdown();
                }
                else if (_scheduler.IsRunning)
                {
                    // Hide countdown while backup is in progress
                    TxtNextBackup.Visibility = Visibility.Collapsed;
                    _countdownTimer?.Stop();
                }
                else
                {
                    TxtNextBackup.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                BadgeSchedule.Visibility = Visibility.Collapsed;
                TxtNextBackup.Visibility = Visibility.Collapsed;
                BtnStopAutoBackup.Visibility = Visibility.Collapsed;
                _countdownTimer?.Stop();
            }
        }

        private void ShowIdleActions()
        {
            PanelIdleActions.Visibility = Visibility.Visible;
            PanelRunningActions.Visibility = Visibility.Collapsed;
            BtnSkipBackup.IsEnabled = true;
            StopIconSpin();
        }

        private void ShowRunningActions()
        {
            PanelIdleActions.Visibility = Visibility.Collapsed;
            PanelRunningActions.Visibility = Visibility.Visible;
            BtnSkipBackup.IsEnabled = true;
            BtnPause.Visibility = Visibility.Visible;
            StartIconSpin();
        }

        private void StartIconSpin()
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.5))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            IconRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
        }

        private void StopIconSpin()
        {
            IconRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            IconRotation.Angle = 0;
        }

        private void BtnStopAutoBackup_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "This will disable automatic backups. You will need to manually run backups or re-enable scheduling in Settings.\n\nAre you sure?",
                "Stop Automatic Backups",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                _configManager.Config.Schedule = Core.Models.ScheduleType.Manual;
                _configManager.SaveConfig();
                _scheduler.Stop();

                Logger.Log("Automatic backups disabled by user.");
                UpdateScheduleDisplay();
                InitializeData();

                // Sync the Settings view checkbox
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow?.GetView("Settings") is SettingsView settingsView)
                {
                    settingsView.RefreshScheduleState();
                }
            }
        }
    }
}
