using System.Windows;
using MyLocalBackup.UI.Views;
using MyLocalBackup.Core;

namespace MyLocalBackup.UI
{
    public partial class MainWindow : Window
    {
        private const string ReleasesUrl = "https://github.com/gkaragioul/My_Local_Backup/releases/latest";

        private readonly Dictionary<string, UIElement> _views = new();
        private bool _allowShutdown;

        // Store event handler references for proper unsubscription
        private readonly EventHandler<string> _backupStartedHandler;
        private readonly EventHandler<(bool success, string? error, System.Collections.Generic.IReadOnlyList<string>? failedFiles)> _backupCompletedHandler;

        public MainWindow()
        {
            InitializeComponent();

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

            // Tray Double Click
            MyNotifyIcon.TrayMouseDoubleClick += TrayIcon_DoubleClick;
        }

        private void BtnOpenReleases_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReleasesUrl)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Core.Logger.Log($"Failed to open GitHub releases URL: {ex}");
                System.Windows.MessageBox.Show(
                    $"Could not open GitHub Releases automatically.\n\nPlease visit:\n{ReleasesUrl}",
                    "GitHub Releases", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
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
            // Unsubscribe from scheduler events to prevent memory leaks
            if (App.Scheduler != null)
            {
                App.Scheduler.BackupStarted -= _backupStartedHandler;
                App.Scheduler.BackupCompleted -= _backupCompletedHandler;
            }
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
