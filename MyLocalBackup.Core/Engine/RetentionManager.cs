using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Data;

namespace MyLocalBackup.Core.Engine
{
    public class RetentionManager
    {
        private readonly DatabaseManager _db;
        private readonly BackupConfig _config;

        public RetentionManager(DatabaseManager db, BackupConfig config)
        {
            _db = db;
            _config = config;
        }

        public void Prune(string destinationRoot)
        {
            try
            {
                // Use central database filtered by destination
                var allSnapshots = _db.GetRestorePoints()
                    .Where(rp => rp.TargetDestination == destinationRoot && rp.Status == BackupStatus.Completed && !rp.IsPinned)
                    .OrderBy(rp => rp.Timestamp)
                    .ToList();

                if (allSnapshots.Count <= 5) return; // Keep at least newest 5 regardless

                // 1. Storage Pressure Check
                CheckStoragePressure(destinationRoot, allSnapshots);

                // 2. Snapshot Thinning (Thinning oldest first)
                var snapshotsToKeep = GetSnapshotsToKeep(allSnapshots);
                var snapshotsToDelete = allSnapshots
                    .Where(s => !snapshotsToKeep.Contains(s.Id) && !s.IsPinned)
                    .ToList();

                foreach (var snapshot in snapshotsToDelete)
                {
                    Logger.Log($"Retention: Removing old snapshot {Path.GetFileName(snapshot.Path)}...");
                    DeleteSnapshot(snapshot);
                }
            }
            catch (Exception ex)
            {
                // Don't let retention failures crash the backup
                Logger.Log($"Retention pruning failed: {ex}");
            }
        }

        private void CheckStoragePressure(string destinationRoot, List<RestorePoint> snapshots)
        {
            try
            {
                var pathRoot = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
                if (string.IsNullOrEmpty(pathRoot)) return;

                var drive = new DriveInfo(pathRoot);
                const long lowSpaceThreshold = 20L * 1024 * 1024 * 1024; // 20 GB
                
                if (drive.AvailableFreeSpace >= lowSpaceThreshold) return;

                if (!_config.AutoDeleteOldBackups)
                {
                    Logger.Log("Low disk space detected, but auto-deletion is disabled. Please free up space manually.");
                    return;
                }
                const int maxDeletionsPerRun = 10; // Prevent infinite loop
                int deletionCount = 0;

                while (drive.AvailableFreeSpace < lowSpaceThreshold && snapshots.Count > 5 && deletionCount < maxDeletionsPerRun)
                {
                    var oldest = snapshots[0];
                    snapshots.RemoveAt(0); // Remove from candidate list first to guarantee loop termination

                    // Re-check pin status in case user pinned it since we read the list
                    if (oldest.IsPinned) continue;

                    if (TryDeleteSnapshot(oldest))
                    {
                        deletionCount++;
                        // Refresh drive info to get updated free space
                        drive = new DriveInfo(pathRoot);
                    }
                }

                if (deletionCount >= maxDeletionsPerRun && drive.AvailableFreeSpace < lowSpaceThreshold)
                {
                    Logger.Log($"Warning: Storage pressure cleanup hit limit ({maxDeletionsPerRun} snapshots). Still low on space.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Storage pressure check failed: {ex}");
            }
        }

        private bool TryDeleteSnapshot(RestorePoint snapshot)
        {
            try
            {
                DeleteSnapshot(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Could not delete snapshot during storage pressure cleanup: {ex}");
                return false;
            }
        }

        private HashSet<int> GetSnapshotsToKeep(List<RestorePoint> snapshots)
        {
            var keep = new HashSet<int>();
            if (!snapshots.Any()) return keep;

            // Always keep the newest 5 snapshots regardless of thinning rules
            var newest5 = snapshots.OrderByDescending(s => s.Timestamp).Take(5).Select(s => s.Id);
            foreach (var id in newest5) keep.Add(id);

            // Simplified Balanced Logic:
            // - Last 24 hours: keep all
            // - Last 30 days: keep one per day
            // - Last 12 months: keep one per week

            // Use UTC for all internal time comparisons
            var now = DateTime.UtcNow;
            var hourlyLimit = now.AddHours(-24);
            var dailyLimit = now.AddDays(-30);

            // Grouping by criteria
            var hourly = snapshots.Where(s => s.Timestamp >= hourlyLimit).Select(s => s.Id);
            foreach (var id in hourly) keep.Add(id);

            var daily = snapshots
                .Where(s => s.Timestamp < hourlyLimit && s.Timestamp >= dailyLimit)
                .GroupBy(s => s.Timestamp.Date) // UTC date — consistent with UTC limit comparisons above
                .Select(g => g.Last().Id);
            foreach (var id in daily) keep.Add(id);

            var weekly = snapshots
                .Where(s => s.Timestamp < dailyLimit)
                .GroupBy(s => GetYearWeekKey(s.Timestamp)) // UTC — consistent with UTC limits
                .Select(g => g.Last().Id);
            foreach (var id in weekly) keep.Add(id);

            return keep;
        }

        public void DeleteSnapshot(RestorePoint snapshot)
        {
            // Two-phase deletion: mark as Deleting first, then clean filesystem, then remove DB record
            _db.UpdateRestorePointStatus(snapshot.Id, BackupStatus.Deleting);
            try
            {
                if (Directory.Exists(snapshot.Path))
                {
                    ClearReadOnlyAndDelete(snapshot.Path);
                }

                // Only remove DB record after confirming filesystem is clean
                if (!Directory.Exists(snapshot.Path))
                {
                    _db.DeleteRestorePoint(snapshot.Id);
                }
                else
                {
                    // Revert so the next retention run can retry rather than orphaning the record
                    _db.UpdateRestorePointStatus(snapshot.Id, BackupStatus.Completed);
                    Logger.Log($"Warning: Directory still exists after deletion attempt, reverting status for retry: {snapshot.Path}");
                }
            }
            catch (Exception ex)
            {
                // Revert so this snapshot isn't permanently stuck as Deleting
                try { _db.UpdateRestorePointStatus(snapshot.Id, BackupStatus.Completed); } catch { }
                Logger.Log($"Failed to delete snapshot {snapshot.Path}: {ex}");
            }
        }

        private static void ClearReadOnlyAndDelete(string directoryPath)
        {
            try
            {
                Directory.Delete(directoryPath, true);
            }
            catch (UnauthorizedAccessException)
            {
                // Retry after clearing readonly attributes
                foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    var attrs = File.GetAttributes(file);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                Directory.Delete(directoryPath, true);
            }
        }

        private static (int year, int week) GetYearWeekKey(DateTime time)
        {
            var day = System.Globalization.CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(time);
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday)
            {
                time = time.AddDays(3);
            }
            int week = System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(time, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            return (time.Year, week);
        }
    }
}
