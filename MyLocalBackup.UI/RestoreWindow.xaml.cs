using System.Windows;
using System.Collections.ObjectModel;
using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MyLocalBackup.UI
{
    public partial class RestoreWindow : Window
    {
        private readonly DatabaseManager _db;
        public ObservableCollection<RestorePoint> Snapshots { get; set; } = new();
        private RestorePoint? _selectedSnapshot;

        public RestoreWindow(DatabaseManager db)
        {
            InitializeComponent();
            _db = db;
            DataContext = this;
            LoadHistory();
        }

        private void LoadHistory()
        {
            Snapshots.Clear();
            // Don't mutate RestorePoint.Timestamp here — the XAML binding uses
            // UtcToLocalConverter for display, so mutating would double-convert.
            foreach (var rp in _db.GetRestorePoints().OrderByDescending(r => r.Timestamp))
                Snapshots.Add(rp);
        }

        private void OnSnapshotClicked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton btn && btn.Tag is int id)
            {
                _selectedSnapshot = Snapshots.FirstOrDefault(s => s.Id == id);
                if (_selectedSnapshot != null)
                {
                    LoadFiles(_selectedSnapshot.Id);
                }
            }
        }

        private void LoadFiles(int rpId)
        {
            try
            {
                var files = _db.GetFilesForSnapshot(rpId);
                FileListBox.ItemsSource = files;
            }
            catch (Exception ex)
            {
                Core.Logger.Log($"Error loading files for snapshot {rpId}: {ex.Message}");
                System.Windows.MessageBox.Show($"Error loading files: {ex.Message}");
            }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileListBox.SelectedItem is FileEntry entry)
            {
                TxtSelectedFile.Text = entry.RelativePath;
                BtnRestore.IsEnabled = true;
            }
            else
            {
                TxtSelectedFile.Text = "No file selected";
                BtnRestore.IsEnabled = false;
            }
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSnapshot == null || FileListBox.SelectedItem is not FileEntry entry) return;

            // Validate snapshot still exists on disk before attempting restore
            if (!Directory.Exists(_selectedSnapshot.Path))
            {
                System.Windows.MessageBox.Show("This snapshot no longer exists on disk. It may have been deleted.", "Snapshot Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using var dialog = new System.Windows.Forms.FolderBrowserDialog();
            dialog.Description = "Select Folder to Restore To";
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    // Validate RelativePath is actually relative to prevent path injection
                    if (Path.IsPathRooted(entry.RelativePath) || entry.RelativePath.Contains(".."))
                    {
                        System.Windows.MessageBox.Show("Invalid file path detected. This file cannot be restored.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    var sourcePath = Path.GetFullPath(Path.Combine(_selectedSnapshot.Path, entry.RelativePath));
                    // Ensure resolved path is still within the snapshot directory (catches Unicode normalization tricks)
                    if (!sourcePath.StartsWith(Path.GetFullPath(_selectedSnapshot.Path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        System.Windows.MessageBox.Show("Invalid file path detected. This file cannot be restored.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    var destPath = Path.Combine(dialog.SelectedPath, Path.GetFileName(entry.RelativePath));

                    if (entry.IsDirectory)
                    {
                        var staging = RestorationManager.RestoreToStaging(sourcePath, true);
                        RestorationManager.FinalizeRestore(staging, destPath);
                    }
                    else
                    {
                        RestorationManager.RestoreFile(sourcePath, destPath, overwrite: true);
                    }

                    System.Windows.MessageBox.Show("Restore Completed Successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Restore Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
