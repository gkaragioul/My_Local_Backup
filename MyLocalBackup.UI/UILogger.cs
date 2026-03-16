using System.Collections.ObjectModel;
using System.Windows;
using MyLocalBackup.Core;

namespace MyLocalBackup.UI
{
    public static class UILogger
    {
        public static ObservableCollection<string> Logs { get; } = new();
        public static ObservableCollection<string> FailedFiles { get; } = new();

        public static void Initialize()
        {
            Logger.OnLog += (message) =>
            {
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app == null) return;

                    var dispatcher = app.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted) return;

                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        Logs.Insert(0, message);

                        // Keep last 1000 logs
                        if (Logs.Count > 1000) Logs.RemoveAt(Logs.Count - 1);
                    }));
                }
                catch (InvalidOperationException)
                {
                    // Dispatcher has shut down, application is closing
                }
            };
        }

        public static void SetFailedFiles(IReadOnlyList<string>? files)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;

                var dispatcher = app.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted) return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    FailedFiles.Clear();
                    if (files != null)
                    {
                        // Cap at 500 entries to prevent excessive memory usage
                        var limit = Math.Min(files.Count, 500);
                        for (int i = 0; i < limit; i++)
                            FailedFiles.Add(files[i]);
                        if (files.Count > 500)
                            FailedFiles.Add($"... and {files.Count - 500} more (see log file for full list)");
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher has shut down, application is closing
            }
        }
    }
}
