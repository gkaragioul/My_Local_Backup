using MyLocalBackup.Core.Configuration;
using MyLocalBackup.Core;
using MyLocalBackup.Core.Models;
using System.Windows;
using System.IO;
using System.Diagnostics;
using System.Windows.Input;

namespace MyLocalBackup.UI.Views
{
    public partial class SettingsView : System.Windows.Controls.UserControl
    {
        private readonly ConfigManager _configManager;
        private bool _isInitializing = true;
        private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _failedFilesHandler;

        public SettingsView()
        {
            InitializeComponent();
            _configManager = App.ConfigManager;

            ChkAutoBackup.IsChecked = _configManager.Config.Schedule == ScheduleType.Automatic;
            SldInterval.Value = _configManager.Config.IntervalHours;
            TxtInterval.Text = SldInterval.Value == 1 ? "Every hour" : $"Every {SldInterval.Value} hours";

            // Initialize schedule mode radio buttons
            if (_configManager.Config.ScheduleMode == ScheduleMode.DailyFixedTime)
            {
                RbDailyTime.IsChecked = true;
                PanelInterval.Visibility = Visibility.Collapsed;
                PanelDailyTime.Visibility = Visibility.Visible;
            }
            else
            {
                RbInterval.IsChecked = true;
                PanelInterval.Visibility = Visibility.Visible;
                PanelDailyTime.Visibility = Visibility.Collapsed;
            }

            // Populate hour/minute combo boxes
            for (int h = 0; h < 24; h++)
                CmbHour.Items.Add(h.ToString("D2"));
            for (int m = 0; m < 60; m += 5)
                CmbMinute.Items.Add(m.ToString("D2"));

            // Set saved daily time
            if (TimeSpan.TryParse(_configManager.Config.DailyBackupTime, out var savedTime))
            {
                CmbHour.SelectedItem = savedTime.Hours.ToString("D2");
                // Snap to nearest 5-min increment
                int nearestMin = (savedTime.Minutes / 5) * 5;
                CmbMinute.SelectedItem = nearestMin.ToString("D2");
            }
            else
            {
                CmbHour.SelectedItem = "02";
                CmbMinute.SelectedItem = "00";
            }
            UpdateDailyTimeLabel();

            ChkLaunchStartup.IsChecked = _configManager.Config.LaunchAtStartup;
            ChkMinimizeTray.IsChecked = _configManager.Config.MinimizeToTray;
            ChkAutoDelete.IsChecked = _configManager.Config.AutoDeleteOldBackups;
            ChkShowNotifications.IsChecked = _configManager.Config.ShowBackupNotifications;

            // Subscribe while visible, unsubscribe when hidden.
            // Using Loaded/Unloaded pair keeps the badge current and prevents a
            // permanent subscription leak: the constructor-only pattern removed the
            // handler on first Unloaded but never re-added it on subsequent Loaded events.
            _failedFilesHandler = (s, e) => UpdateFailedFilesBadge();
            Loaded += (s, e) => {
                UpdateFailedFilesBadge(); // Catch any changes that happened while hidden
                UILogger.FailedFiles.CollectionChanged -= _failedFilesHandler; // Prevent duplicate on re-Loaded
                UILogger.FailedFiles.CollectionChanged += _failedFilesHandler;
            };
            Unloaded += (s, e) => UILogger.FailedFiles.CollectionChanged -= _failedFilesHandler;
            UpdateFailedFilesBadge(); // Initial state for first display

            _isInitializing = false;
        }

        private void UpdateFailedFilesBadge()
        {
            int count = UILogger.FailedFiles.Count;
            if (count > 0)
            {
                BadgeFailedCount.Visibility = Visibility.Visible;
                TxtFailedCount.Text = count.ToString();
                PanelFailedEmpty.Visibility = Visibility.Collapsed;
                LstFailedFiles.Visibility = Visibility.Visible;
            }
            else
            {
                BadgeFailedCount.Visibility = Visibility.Collapsed;
                PanelFailedEmpty.Visibility = Visibility.Visible;
                LstFailedFiles.Visibility = Visibility.Collapsed;
            }
        }

        public void SwitchToFailedFilesTab()
        {
            ShowTab("failed");
        }

        public void RefreshScheduleState()
        {
            _isInitializing = true;
            ChkAutoBackup.IsChecked = _configManager.Config.Schedule == ScheduleType.Automatic;
            _isInitializing = false;
        }

        private void ShowTab(string tab)
        {
            if (tab == "failed")
            {
                PanelActivityLog.Visibility = Visibility.Collapsed;
                PanelFailedFiles.Visibility = Visibility.Visible;
                PanelLogActions.Visibility = Visibility.Collapsed;

                BtnTabLogs.Foreground = TryFindResource("TextMutedBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
                BtnTabLogs.FontWeight = FontWeights.Normal;
                BtnTabFailed.Foreground = TryFindResource("PrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Teal;
                TxtTabFailedLabel.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                PanelActivityLog.Visibility = Visibility.Visible;
                PanelFailedFiles.Visibility = Visibility.Collapsed;
                PanelLogActions.Visibility = Visibility.Visible;

                BtnTabLogs.Foreground = TryFindResource("PrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Teal;
                BtnTabLogs.FontWeight = FontWeights.SemiBold;
                BtnTabFailed.Foreground = TryFindResource("TextMutedBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
                TxtTabFailedLabel.FontWeight = FontWeights.Normal;
            }

            UpdateFailedFilesBadge();
        }

        private void BtnTabLogs_Click(object sender, RoutedEventArgs e) => ShowTab("logs");
        private void BtnTabFailed_Click(object sender, RoutedEventArgs e) => ShowTab("failed");

        private void BtnShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.DataContext is string filePath)
            {
                OpenInExplorer(filePath);
            }
        }

        private void LstFailedFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstFailedFiles.SelectedItem is string filePath)
            {
                OpenInExplorer(filePath);
            }
        }

        private void OpenInExplorer(string filePath)
        {
            try
            {
                var explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (File.Exists(filePath))
                {
                    // Select the file in Explorer
                    Process.Start(explorerPath, $"/select,\"{filePath}\"")?.Dispose();
                }
                else
                {
                    // File doesn't exist anymore, try to open its parent folder
                    string? dir = Path.GetDirectoryName(filePath);
                    if (dir != null && Directory.Exists(dir))
                    {
                        Process.Start(explorerPath, $"\"{dir}\"")?.Dispose();
                    }
                    else
                    {
                        System.Windows.MessageBox.Show(
                            $"The file and its folder no longer exist:\n{filePath}",
                            "File Not Found",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.Log($"Error opening file in Explorer: {ex}");
            }
        }

        private void ChkAutoBackup_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool enableAuto = ChkAutoBackup.IsChecked ?? false;
            _configManager.Config.Schedule = enableAuto ? ScheduleType.Automatic : ScheduleType.Manual;
            _configManager.SaveConfig();

            if (enableAuto)
            {
                App.Scheduler?.Start();
                Logger.Log("Automatic backups enabled.");
            }
            else
            {
                App.Scheduler?.Stop();
                Logger.Log("Automatic backups disabled.");
            }
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            _configManager.Config.LaunchAtStartup = ChkLaunchStartup.IsChecked ?? false;
            _configManager.Config.MinimizeToTray = ChkMinimizeTray.IsChecked ?? false;
            _configManager.Config.AutoDeleteOldBackups = ChkAutoDelete.IsChecked ?? false;
            _configManager.Config.ShowBackupNotifications = ChkShowNotifications.IsChecked ?? false;

            _configManager.SaveConfig();

            // Update Windows startup registry
            SetLaunchAtStartup(_configManager.Config.LaunchAtStartup);
        }

        internal static void SetLaunchAtStartup(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;

                if (enable)
                {
                    // Use the actual running exe path so this works for both installed and portable builds
                    var exePath = Environment.ProcessPath ?? System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "MyLocalBackup", "MyLocalBackup.UI.exe");
                    key.SetValue("MyLocalBackup", $"\"{exePath}\" --minimized");
                }
                else
                {
                    key.DeleteValue("MyLocalBackup", throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update startup registry: {ex.Message}");
            }
        }

        private void SldInterval_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtInterval == null) return;

            int hours = (int)e.NewValue;
            TxtInterval.Text = hours == 1 ? "Every hour" : $"Every {hours} hours";

            if (!_isInitializing)
            {
                _configManager.Config.IntervalHours = hours;
                _configManager.SaveConfig();
                App.Scheduler?.Restart();
                Logger.Log($"Backup interval changed to {hours}h.");
            }
        }

        private void ScheduleMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool isDaily = RbDailyTime.IsChecked ?? false;
            _configManager.Config.ScheduleMode = isDaily ? ScheduleMode.DailyFixedTime : ScheduleMode.Interval;

            PanelInterval.Visibility = isDaily ? Visibility.Collapsed : Visibility.Visible;
            PanelDailyTime.Visibility = isDaily ? Visibility.Visible : Visibility.Collapsed;

            _configManager.SaveConfig();
            App.Scheduler?.Restart();
            Logger.Log($"Schedule mode changed to {(isDaily ? "Daily fixed time" : "Interval")}.");
        }

        private void DailyTime_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializing || CmbHour.SelectedItem == null || CmbMinute.SelectedItem == null) return;

            string hour = CmbHour.SelectedItem.ToString()!;
            string minute = CmbMinute.SelectedItem.ToString()!;
            _configManager.Config.DailyBackupTime = $"{hour}:{minute}";
            UpdateDailyTimeLabel();

            _configManager.SaveConfig();
            App.Scheduler?.Restart();
            Logger.Log($"Daily backup time changed to {hour}:{minute}.");
        }

        private void UpdateDailyTimeLabel()
        {
            if (TxtDailyTime == null || CmbHour?.SelectedItem == null || CmbMinute?.SelectedItem == null) return;
            TxtDailyTime.Text = $"Daily at {CmbHour.SelectedItem}:{CmbMinute.SelectedItem}";
        }

        private void BtnClearLogs_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            UILogger.Logs.Clear();
            Logger.Log("Logs cleared by user.");
        }

        private void BtnCopyLogs_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var allLogs = string.Join(System.Environment.NewLine, UILogger.Logs);
            if (string.IsNullOrEmpty(allLogs))
            {
                System.Windows.MessageBox.Show("No logs to copy.", "Info", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(allLogs);
                System.Windows.MessageBox.Show("Logs copied to clipboard.", "Success", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                System.Windows.MessageBox.Show("Clipboard is in use by another application. Please try again.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
    }
}
