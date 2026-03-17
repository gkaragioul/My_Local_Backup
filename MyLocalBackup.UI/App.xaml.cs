using System.Windows;
using System.Runtime.InteropServices;
using MyLocalBackup.Core;
using MyLocalBackup.Core.Configuration;
using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;

namespace MyLocalBackup.UI
{
    public partial class App : System.Windows.Application
    {
        public static ConfigManager ConfigManager { get; private set; } = null!;
        public static DatabaseManager DatabaseManager { get; private set; } = null!;
        public static BackupScheduler Scheduler { get; private set; } = null!;

        private static Mutex? _singleInstanceMutex;
        private const string MutexName = "MyLocalBackup_SingleInstance_Mutex";

        // P/Invoke for bringing existing window to foreground
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Global Exception Handling
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                LogCriticalError((Exception)args.ExceptionObject, "AppDomain.UnhandledException");

            DispatcherUnhandledException += (s, args) =>
            {
                LogCriticalError(args.Exception, "DispatcherUnhandledException");
                args.Handled = true; // Mark handled so WPF doesn't show its default crash dialog
                Shutdown();
            };

            // Detect code injection via startup hooks (profiling env vars excluded —
            // enterprise AV/EDR sets COR_ENABLE_PROFILING system-wide, causing false positives)
            if (Environment.GetEnvironmentVariable("DOTNET_StartupHooks") != null)
            {
                System.Windows.MessageBox.Show(
                    "A .NET startup hook has been detected.\n\nPlease remove the DOTNET_StartupHooks environment variable and restart.",
                    "Startup Hook Detected", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // Single instance check
            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running - try to bring it to foreground
                try
                {
                    IntPtr existingWindow = FindWindow(null, "MyLocalBackup");
                    if (existingWindow != IntPtr.Zero)
                    {
                        ShowWindow(existingWindow, SW_RESTORE);
                        SetForegroundWindow(existingWindow);
                    }
                }
                catch { /* Ignore errors when activating existing window */ }

                // We don't own the mutex, so don't release it in OnExit
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;

                // Exit this instance
                Shutdown();
                return;
            }

            // Single instance check passed. Proceed with initialization.
            // We do NOT call base.OnStartup(e) here yet because it triggers MainWindow creation via StartupUri.
            // We want our services initialized FIRST.

            try
            {
                // 0. Initialize Logger Bridge
                UILogger.Initialize();

                // Clean up old crash logs (keep only last 5)
                try
                {
                    var crashDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "MyLocalBackup");
                    var crashFiles = System.IO.Directory.GetFiles(crashDir, "crash_*.txt");
                    if (crashFiles.Length > 5)
                    {
                        Array.Sort(crashFiles);
                        for (int i = 0; i < crashFiles.Length - 5; i++)
                            try { System.IO.File.Delete(crashFiles[i]); } catch { }
                    }
                }
                catch { }

                // 1. Initialize Configuration
                Logger.Log("Initializing configuration...");
                ConfigManager = new ConfigManager();

                if (ConfigManager.ConfigWasCorrupted)
                {
                    System.Windows.MessageBox.Show(
                        "Your configuration file was corrupted and could not be read.\n\nAll settings have been reset to defaults. Your previous configuration has been saved to:\nconfig.json.bak\n\nYou can find it in: %LocalAppData%\\MyLocalBackup\\",
                        "Configuration Corrupted",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                // 2. Initialize Database
                var dbPath = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "MyLocalBackup",
                    "metadata.db"
                );
                Logger.Log($"Connecting to database: {dbPath}");
                DatabaseManager = new DatabaseManager(dbPath);

                // 2.5. Self-check: clean up orphaned restore points from crashed backups
                Logger.Log("Performing startup self-check...");
                PerformSelfCheck(DatabaseManager);

                // 3. Initialize Scheduler
                Logger.Log("Initializing scheduler...");
                Scheduler = new BackupScheduler(ConfigManager.Config, DatabaseManager);

                // Hook up global logs
                Scheduler.BackupStarted += (s, m) => Logger.Log($"Backup started: {m}");
                Scheduler.BackupCompleted += (s, data) => Logger.Log($"Backup completed. Success: {data.success}{(data.failedFiles?.Count > 0 ? $", {data.failedFiles.Count} file(s) failed" : "")}");
                Scheduler.ProgressUpdated += (s, data) => { if ((int)data.percentage % 25 == 0) Logger.Log($"Progress: {data.task} - {(int)data.percentage}%"); };

                // 4. Start Scheduler
                Logger.Log("Starting background scheduler...");

                // Set UI dispatcher for thread marshaling (must be done before Start, but Dispatcher exists now)
                Scheduler.SetUiDispatcher(action => Dispatcher.BeginInvoke(action));

                Scheduler.Start();

                Logger.Log("Startup complete. Creating MainWindow...");

                // 5. Explicitly create and show MainWindow — all services are ready.
                base.OnStartup(e);
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                LogCriticalError(ex, "Startup Sequence");
                System.Windows.MessageBox.Show($"The application failed to start.\n\nError: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
            }
        }

        private static void PerformSelfCheck(Core.Data.DatabaseManager db)
        {
            // Run with a hard timeout — never block startup for more than 5 seconds.
            // If the old database is corrupted/locked, just skip and let the user start the app.
            try
            {
                var task = System.Threading.Tasks.Task.Run(() => PerformSelfCheckInner(db));
                if (!task.Wait(TimeSpan.FromSeconds(5)))
                {
                    Logger.Log("WARNING: Self-check timed out after 5 seconds. Skipping.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Self-check failed: {ex.Message}");
            }
        }

        private static void PerformSelfCheckInner(Core.Data.DatabaseManager db)
        {
            var rps = db.GetRestorePoints();

            foreach (var rp in rps)
            {
                if (rp.Status == Core.Models.BackupStatus.InProgress)
                {
                    // Crashed mid-backup — mark as Interrupted so next backup cleans up the directory
                    Logger.Log($"Found orphaned InProgress restore point {rp.Id}. Marking as Interrupted...");
                    try
                    {
                        db.UpdateRestorePointStatus(rp.Id, Core.Models.BackupStatus.Interrupted);
                        Logger.Log($"Marked restore point {rp.Id} as Interrupted for cleanup.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to update orphaned restore point {rp.Id}: {ex.Message}");
                    }
                }
                else if (rp.Status == Core.Models.BackupStatus.Deleting)
                {
                    if (!System.IO.Directory.Exists(rp.Path))
                    {
                        // Background delete already finished — remove DB record
                        Logger.Log($"Deleting restore point {rp.Id} already cleaned from disk. Removing DB record...");
                        try { db.DeleteRestorePoint(rp.Id); }
                        catch (Exception ex) { Logger.Log($"Failed to remove DB record {rp.Id}: {ex.Message}"); }
                    }
                    else
                    {
                        // Directory still exists — keep DB record so next backup queues background delete
                        Logger.Log($"Deleting restore point {rp.Id} still has directory on disk. Will resume cleanup on next backup.");
                    }
                }
            }
        }

        private void LogCriticalError(Exception ex, string context)
        {
            var crashFolder = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "MyLocalBackup");
            System.IO.Directory.CreateDirectory(crashFolder);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var crashFile = System.IO.Path.Combine(crashFolder, $"crash_{timestamp}.txt");

            try { System.IO.File.WriteAllText(crashFile, $"CRITICAL ERROR [{context}]:\n{ex}"); } catch { }
            try { Logger.Log($"CRITICAL ERROR [{context}]: {ex}"); Logger.Flush(); } catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Cancel any in-flight backup before stopping the scheduler
            if (Scheduler?.IsRunning == true)
            {
                Scheduler.CancelBackup();
                // Wait for backup thread to acknowledge cancellation (up to 2 seconds)
                // SpinWait avoids blocking the UI thread with Thread.Sleep
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SpinWait.SpinUntil(() => !Scheduler.IsRunning || sw.ElapsedMilliseconds >= 2000);
            }
            Scheduler?.Dispose();

            // Flush and close logger
            Logger.Shutdown();

            // Release mutex on exit
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();

            base.OnExit(e);
        }
    }
}
