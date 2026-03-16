using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.UI
{
    /// <summary>Converts UTC DateTime to local time for display without mutating the source object.</summary>
    public class UtcToLocalConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dt)
                return dt.ToLocalTime();
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public partial class BackupHistoryWindow : Window
    {
        private readonly DatabaseManager _db;
        private readonly BackupConfig _config;
        private readonly RetentionManager _retention;

        public BackupHistoryWindow(DatabaseManager db, BackupConfig config)
        {
            InitializeComponent();
            _db = db;
            _config = config;
            _retention = new RetentionManager(db, config);
            LoadHistory();
        }

        private void LoadHistory()
        {
            var history = _db.GetRestorePoints()
                .OrderByDescending(r => r.Timestamp)
                .ToList();
            GridHistory.ItemsSource = history;
        }

        private void MenuExplore_Click(object sender, RoutedEventArgs e)
        {
            if (GridHistory.SelectedItem is RestorePoint rp && System.IO.Directory.Exists(rp.Path))
            {
                // Use full path to explorer.exe for security
                var explorerPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                var proc = Process.Start(explorerPath, $"\"{rp.Path}\"");
                proc?.Dispose();
            }
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (GridHistory.SelectedItem is RestorePoint rp)
            {
                try
                {
                    System.Windows.Clipboard.SetText(rp.Path);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (GridHistory.SelectedItem is RestorePoint rp)
            {
                var result = System.Windows.MessageBox.Show($"Are you sure you want to delete this backup from {rp.LocalTimestamp}?\n\nThis will permanently delete the files on disk.",
                    "Confirm Delete", MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        _retention.DeleteSnapshot(rp);
                        LoadHistory();
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"Error deleting backup: {ex.Message}", "Error", MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    }
                }
            }
        }
    }
}
