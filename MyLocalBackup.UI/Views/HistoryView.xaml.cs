using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.UI.Views
{
    public partial class HistoryView : System.Windows.Controls.UserControl
    {
        private DatabaseManager? _db;
        private BackupConfig? _config;
        private RetentionManager? _retention;

        public HistoryView()
        {
            InitializeComponent();
        }

        public void Initialize(DatabaseManager db, BackupConfig config)
        {
            _db = db;
            _config = config;
            _retention = new RetentionManager(db, config);
        }

        public void Refresh()
        {
            if (_db == null) return;
            var history = _db.GetRestorePoints()
                .OrderByDescending(r => r.Timestamp)
                .ToList();
            GridHistory.ItemsSource = history;
        }

        private void MenuExplore_Click(object sender, RoutedEventArgs e)
        {
            if (GridHistory.SelectedItem is RestorePoint rp && System.IO.Directory.Exists(rp.Path))
            {
                var explorerPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                var proc = Process.Start(explorerPath, $"\"{rp.Path}\"");
                proc?.Dispose();
            }
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (GridHistory.SelectedItem is RestorePoint rp)
            {
                try { System.Windows.Clipboard.SetText(rp.Path); }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_retention == null || GridHistory.SelectedItem is not RestorePoint rp) return;

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to delete this backup from {rp.LocalTimestamp}?\n\nThis will permanently delete the files on disk.",
                "Confirm Delete", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                try
                {
                    _retention.DeleteSnapshot(rp);
                    Refresh();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error deleting backup: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
        }
    }
}
