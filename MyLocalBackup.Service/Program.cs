using MyLocalBackup.Core.Configuration;
using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Service
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyLocalBackup");
                Directory.CreateDirectory(appData);

                Core.Logger.Log("Initializing database...");
                var dbPath = Path.Combine(appData, "metadata.db");
                var db = new DatabaseManager(dbPath);

                // Load user's saved config
                Core.Logger.Log("Loading configuration...");
                var configManager = new ConfigManager();
                var config = configManager.Config;
                Core.Logger.Log($"Config loaded: schedule={config.Schedule}, mode={config.ScheduleMode}, interval={config.IntervalHours}h, destination={config.Destination?.Name ?? "(none)"}");

                var scheduler = new BackupScheduler(config, db);

                Core.Logger.Log("Performing startup self-check...");
                PerformSelfCheck(db);

                scheduler.Start();

                Core.Logger.Log("MyLocalBackup Service Running...");

                // Graceful shutdown handler
                using var shutdownEvent = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Core.Logger.Log("Shutdown signal received. Stopping scheduler...");
                    shutdownEvent.Set();
                };

                shutdownEvent.Wait(); // Block until shutdown signal
                scheduler.Stop();
                Core.Logger.Log("Service stopped.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: Service crashed during startup or operation: {ex}");
                Core.Logger.Log($"FATAL: Service crashed: {ex}");
            }
            finally
            {
                Core.Logger.Shutdown();
            }
        }

        static void PerformSelfCheck(DatabaseManager db)
        {
            var rps = db.GetRestorePoints();

            // Safety check: if ALL restore points are Deleting/InProgress, this is likely
            // database corruption (e.g. a crash during bulk deletion). Skip cleanup to be safe.
            var totalCount = rps.Count;
            var orphanedCount = rps.Count(rp => rp.Status == BackupStatus.Deleting || rp.Status == BackupStatus.InProgress);
            if (totalCount > 2 && orphanedCount == totalCount)
            {
                Core.Logger.Log("WARNING: PerformSelfCheck skipped — all restore points have Deleting/InProgress status. Possible corruption.");
                return;
            }

            foreach (var rp in rps)
            {
                if (rp.Status == BackupStatus.Deleting || rp.Status == BackupStatus.InProgress)
                {
                    Core.Logger.Log($"Found orphaned restore point {rp.Id} ({rp.Status}). Cleaning up...");
                    try
                    {
                        if (Directory.Exists(rp.Path))
                        {
                            Directory.Delete(rp.Path, true);
                        }
                        db.DeleteRestorePoint(rp.Id);
                        Core.Logger.Log($"Cleaned up orphaned restore point {rp.Id}.");
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Log($"Failed to clean up orphaned restore point {rp.Id}: {ex}");
                    }
                }
            }
        }
    }
}
