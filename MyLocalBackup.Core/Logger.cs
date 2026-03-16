namespace MyLocalBackup.Core
{
    public static class Logger
    {
        private static readonly object _initLock = new object();
        private static volatile StreamWriter? _writer;
        private static volatile bool _isShutdown;

        public static event Action<string>? OnLog;

        public static void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var formattedMessage = $"[{timestamp}] {message}";

            // Console for background debugging
            Console.WriteLine(formattedMessage);

            // Buffered file logging - much faster than File.AppendAllText per call
            try
            {
                EnsureWriter();
                lock (_initLock)
                {
                    _writer?.WriteLine(formattedMessage);
                }
            }
            catch (Exception ex)
            {
                // Fallback: Print to stderr so we know logging is failing
                Console.Error.WriteLine($"[CRITICAL] Logger failed to write to file: {ex.Message}");
            }

            // Trigger event for UI — capture handler to avoid race on concurrent unsubscribe
            var handler = OnLog;
            handler?.Invoke(formattedMessage);
        }

        /// <summary>
        /// Flush buffered log entries to disk. Call on backup completion or app shutdown.
        /// </summary>
        public static void Flush()
        {
            try
            {
                lock (_initLock)
                {
                    _writer?.Flush();
                }
            }
            catch { }
        }

        /// <summary>
        /// Close the log file. Call on app shutdown.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                lock (_initLock)
                {
                    _isShutdown = true;
                    _writer?.Flush();
                    _writer?.Dispose();
                    _writer = null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CRITICAL] Logger shutdown error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the path to the log file.
        /// </summary>
        public static string GetLogPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "MyLocalBackup", "logs.txt");
        }

        private static void EnsureWriter()
        {
            if (_writer != null || _isShutdown) return;

            lock (_initLock)
            {
                if (_writer != null || _isShutdown) return;

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var folder = Path.Combine(appData, "MyLocalBackup");
                Directory.CreateDirectory(folder);
                var logPath = Path.Combine(folder, "logs.txt");

                // Rotate log if it exceeds 5 MB
                try
                {
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                    {
                        var oldLog = logPath + ".old";
                        if (File.Exists(oldLog)) File.Delete(oldLog);
                        File.Move(logPath, oldLog);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARNING] Log rotation failed: {ex.Message}");
                }

                // AutoFlush=false for batched writes; we flush explicitly on backup completion and shutdown
                _writer = new StreamWriter(logPath, append: true, encoding: System.Text.Encoding.UTF8, bufferSize: 8192)
                {
                    AutoFlush = false
                };
            }
        }
    }
}
