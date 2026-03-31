using System.Windows;
using MyLocalBackup.UI.Views;
using MyLocalBackup.Core;

namespace MyLocalBackup.UI
{
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, UIElement> _views = new();
        private bool _allowShutdown;
        private bool _isUpdating;
        private readonly MyLocalBackup.Core.Services.UpdateService _updateService;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;
        private System.Threading.CancellationTokenSource _updateCts = new();

        // Store event handler references for proper unsubscription
        private readonly EventHandler<string> _backupStartedHandler;
        private readonly EventHandler<(bool success, string? error, System.Collections.Generic.IReadOnlyList<string>? failedFiles)> _backupCompletedHandler;
        private readonly EventHandler? _updateTimerTickHandler;

        public MainWindow()
        {
            InitializeComponent();

            _updateService = new MyLocalBackup.Core.Services.UpdateService();

            // Initialize Views
            _views["Dashboard"] = new DashboardView();
            var historyView = new Views.HistoryView();
            if (App.DatabaseManager != null && App.ConfigManager?.Config != null)
                historyView.Initialize(App.DatabaseManager, App.ConfigManager.Config);
            _views["History"] = historyView;
            _views["Settings"] = new SettingsView();

            // Set Initial View
            ContentArea.Content = _views["Dashboard"];

            // Subscribe to Scheduler with stored handlers for proper cleanup
            _backupStartedHandler = (s, m) =>
            {
                UpdateTrayState(true);
                if (App.ConfigManager?.Config != null && App.ConfigManager.Config.ShowBackupNotifications)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (this.WindowState == WindowState.Minimized || !this.IsVisible)
                            BackupNotificationWindow.ShowNotification();
                    });
                }
            };
            _backupCompletedHandler = (s, data) => UpdateTrayState(false);

            if (App.Scheduler != null)
            {
                App.Scheduler.BackupStarted += _backupStartedHandler;
                App.Scheduler.BackupCompleted += _backupCompletedHandler;
            }
            
            // Sync initial state
            if (App.Scheduler != null)
                UpdateTrayState(App.Scheduler.IsRunning);

            // Version Display
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            TxtVersion.Text = $"Version {version?.ToString(3) ?? "0.7.0"}";

            // Run update checks on UI thread to avoid cross-thread UI crashes.
            _updateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromHours(12)
            };
            _updateTimerTickHandler = async (s, e) =>
            {
                try
                {
                    _updateTimer?.Stop(); // Prevent overlapping ticks while async check runs
                    await CheckForUpdates(isAuto: true, _updateCts.Token);
                }
                catch (OperationCanceledException) { } // Expected on shutdown
                catch (Exception ex)
                {
                    Core.Logger.Log($"Auto update check failed: {ex}");
                }
                finally
                {
                    _updateTimer?.Start();
                }
            };
            _updateTimer.Tick += _updateTimerTickHandler;
            _updateTimer.Start();

            // Tray Double Click
            MyNotifyIcon.TrayMouseDoubleClick += TrayIcon_DoubleClick;

            // Check if a previous update failed (MSI didn't install, or app crashed after update)
            if (_updateService.CheckPendingUpdateFailed(out var expectedVer))
            {
                // Dismiss this version so the initial auto-check below doesn't
                // immediately show a second "new version available" dialog.
                if (expectedVer != null)
                    _updateService.DismissVersion(expectedVer);

                var prevInfo = MyLocalBackup.Core.Services.UpdateService.GetPreviousVersionInfo();
                if (prevInfo.HasValue)
                {
                    var result = System.Windows.MessageBox.Show(
                        $"The previous update to {expectedVer} did not install successfully.\n\nWould you like to download the previous working version (v{prevInfo.Value.Version})?",
                        "Update Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(prevInfo.Value.DownloadUrl) { UseShellExecute = true }); }
                        catch (Exception ex) { Core.Logger.Log($"Failed to open previous version download URL: {ex.Message}"); }
                    }
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        $"The previous update to {expectedVer} did not install successfully.\n\nPlease try updating again, or download the installer manually from GitHub.",
                        "Update Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // Initial update check — fire-and-forget with error handling
            _ = CheckForUpdates(isAuto: true, _updateCts.Token).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Core.Logger.Log($"Initial update check failed: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private async System.Threading.Tasks.Task CheckForUpdates(bool isAuto, System.Threading.CancellationToken ct = default)
        {
            try
            {
                var updateInfo = await _updateService.CheckForUpdatesAsync(isAuto);
                ct.ThrowIfCancellationRequested();
                if (updateInfo != null)
                {
                    if (System.Windows.MessageBox.Show($"A new version ({updateInfo.Version}) is available.\n\nRelease Notes:\n{updateInfo.ReleaseNotes}\n\nDo you want to update now?",
                        "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        await PerformUpdate(updateInfo);
                    }
                    else
                    {
                        // Remember this dismissal so auto-checks don't re-prompt
                        _updateService.DismissVersion(updateInfo.Version);
                    }
                }
                else if (!isAuto)
                {
                    System.Windows.MessageBox.Show("You are up to date!", "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (!isAuto)
                    System.Windows.MessageBox.Show($"Check failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async System.Threading.Tasks.Task PerformUpdate(MyLocalBackup.Core.Services.UpdateInfo info)
        {
            var prevTitle = this.Title;
            var prevVersionText = TxtVersion.Text;
            _isUpdating = true;
            try
            {
                // Save current version info for rollback before starting the update
                _updateService.SavePreviousVersionInfo();

                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mlb_{Guid.NewGuid():N}_{info.FileName}");

                this.Title = "Downloading Update...";
                TxtVersion.Text = "Downloading Update...";

                var progress = new Progress<double>(p =>
                {
                    TxtVersion.Text = $"Downloading: {p:F0}%";
                });

                await _updateService.DownloadInstallerAsync(info.DownloadUrl, tempPath, progress, info.ExpectedSha256, _updateCts.Token);

                this.Title = prevTitle;
                TxtVersion.Text = "Installing update...";

                // Write marker so next launch can detect if the MSI failed silently
                _updateService.WritePendingUpdateMarker(info.Version);

                // Launch installer, then fully exit the process — Close() alone leaves
                // the tray icon's native window alive, keeping file locks open.
                _updateService.RunInstaller(tempPath);
                _allowShutdown = true;
                CleanupResources();
                System.Windows.Application.Current.Shutdown();
                return;
            }
            catch (OperationCanceledException)
            {
                // User cancelled the download — restore UI silently
                this.Title = prevTitle;
                TxtVersion.Text = prevVersionText;
                _isUpdating = false;
                // Replace the cancelled token so future checks still work
                _updateCts.Dispose();
                _updateCts = new System.Threading.CancellationTokenSource();
                _updateTimer?.Start();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Title = prevTitle;
                TxtVersion.Text = prevVersionText;
                _isUpdating = false;
                // Restart auto-check timer so future updates are still detected
                _updateTimer?.Start();
            }
        }

        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdates.IsEnabled = false;
            try
            {
                await CheckForUpdates(isAuto: false);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Update check failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnCheckUpdates.IsEnabled = true;
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isUpdating && !_allowShutdown)
            {
                // Ask user if they want to cancel the in-progress download
                var result = System.Windows.MessageBox.Show(
                    "An update is being downloaded. Cancel the download?",
                    "Download In Progress", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    try { _updateCts.Cancel(); } catch { }
                }
                e.Cancel = true;
                return;
            }
            if (!_allowShutdown && App.ConfigManager?.Config != null && App.ConfigManager.Config.MinimizeToTray)
            {
                // Minimize to tray instead of closing
                e.Cancel = true;
                this.ShowInTaskbar = false;
                this.Hide();
            }
            else
            {
                // Actual shutdown - clean up resources
                CleanupResources();
            }
        }

        private void CleanupResources()
        {
            // Cancel any in-flight update checks before tearing down
            try { _updateCts.Cancel(); } catch { }

            // Unsubscribe from scheduler events to prevent memory leaks
            if (App.Scheduler != null)
            {
                App.Scheduler.BackupStarted -= _backupStartedHandler;
                App.Scheduler.BackupCompleted -= _backupCompletedHandler;
            }

            // Unsubscribe tick handler and dispose timer to prevent firing after shutdown
            if (_updateTimer != null && _updateTimerTickHandler != null)
                _updateTimer.Tick -= _updateTimerTickHandler;
            _updateTimer?.Stop();
            _updateTimer = null;

            // Dispose update service (HttpClient) and cancellation token
            _updateService?.Dispose();
            _updateCts.Dispose();
        }

        public void NavigateTo(string viewName)
        {
            if (_views.TryGetValue(viewName, out var view))
            {
                if (view is Views.HistoryView hv) hv.Refresh();
                ContentArea.Content = view;
                if (viewName == "Dashboard") NavDashboard.IsChecked = true;
                else if (viewName == "History") NavHistory.IsChecked = true;
                else if (viewName == "Settings") NavSettings.IsChecked = true;
            }
        }

        public UIElement? GetView(string viewName)
        {
            _views.TryGetValue(viewName, out var view);
            return view;
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb && rb.Content?.ToString() is string viewName)
            {
                if (_views.TryGetValue(viewName, out var view))
                {
                    if (view is Views.HistoryView hv) hv.Refresh();
                    ContentArea.Content = view;
                }
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdating) return; // Don't allow close during update download

            if (App.ConfigManager?.Config != null && App.ConfigManager.Config.MinimizeToTray)
            {
                this.ShowInTaskbar = false;
                this.Hide();
            }
            else
            {
                _allowShutdown = true;
                CleanupResources();
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void Header_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                try { this.DragMove(); }
                catch (InvalidOperationException) { } // Mouse button released between event and DragMove
            }
        }

        // About Actions
        private void BtnShowAbout_Click(object sender, RoutedEventArgs e) => AboutOverlay.Visibility = Visibility.Visible;
        private void BtnCloseAbout_Click(object sender, RoutedEventArgs e) => AboutOverlay.Visibility = Visibility.Collapsed;

        // Tray Actions
        private void UpdateTrayState(bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                // Safety check: ContextMenu items might be null if accessed before context menu is initialized 
                // or if the app is shutting down/starting up in an unstable state.
                if (TrayRunItem == null || TrayCancelItem == null || TrayStopAutoItem == null || TrayPauseItem == null) return;

                TrayRunItem.Visibility = isRunning ? Visibility.Collapsed : Visibility.Visible;
                TrayCancelItem.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;

                // Show/hide schedule-related items based on schedule mode
                bool isAutoBackupEnabled = App.ConfigManager?.Config != null && App.ConfigManager.Config.Schedule != Core.Models.ScheduleType.Manual;
                TrayStopAutoItem.Visibility = isAutoBackupEnabled ? Visibility.Visible : Visibility.Collapsed;
                TrayPauseItem.Visibility = isAutoBackupEnabled ? Visibility.Visible : Visibility.Collapsed;

                MyNotifyIcon.ToolTipText = "MyLocalBackup";
            });
        }

        private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
        {
            try { BtnShow_Click(this, new RoutedEventArgs()); }
            catch (Exception ex) { Core.Logger.Log($"Error restoring window from tray: {ex}"); }
        }

        private void BtnShow_Click(object sender, RoutedEventArgs e)
        {
            this.ShowInTaskbar = true;
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }
        private void BtnExit_Click(object sender, RoutedEventArgs e) { _allowShutdown = true; CleanupResources(); System.Windows.Application.Current.Shutdown(); }
        private void BtnBackupNowTray_Click(object sender, RoutedEventArgs e)
        {
            if (App.Scheduler == null) return;
            _ = App.Scheduler.RunBackup().ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Core.Logger.Log($"Backup failed unexpectedly: {t.Exception}");
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        private void BtnCancelTray_Click(object sender, RoutedEventArgs e) { App.Scheduler?.CancelBackup(); }
        private void TrayPause_Click(object sender, RoutedEventArgs e)
        {
            if (App.Scheduler == null) return;
            if (App.Scheduler.IsPaused) App.Scheduler.Resume();
            else App.Scheduler.Pause();
            TrayPauseItem.IsChecked = App.Scheduler.IsPaused;
        }

        private void TrayStopAuto_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "This will disable automatic backups. You will need to manually run backups or re-enable scheduling in Settings.\n\nAre you sure?",
                "Stop Automatic Backups",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (App.ConfigManager?.Config != null)
                {
                    App.ConfigManager.Config.Schedule = Core.Models.ScheduleType.Manual;
                    App.ConfigManager.SaveConfig();
                    App.Scheduler?.Stop();
                    
                    Logger.Log("Automatic backups disabled by user (from tray).");
                }
                
                // Update tray menu visibility
                TrayStopAutoItem.Visibility = Visibility.Collapsed;
                TrayPauseItem.Visibility = Visibility.Collapsed;
            }
        }

    }
}
