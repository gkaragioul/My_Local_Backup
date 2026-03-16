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
                lock (_initLock)
                {
                    EnsureWriter();
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
            try { handler?.Invoke(formattedMessage); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARNING] OnLog handler threw: {ex}");
            }
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
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARNING] Logger flush failed: {ex.Message}");
            }
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
            // Caller already holds _initLock — no need for a second acquisition
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
                    // Use Move with overwrite (replaces locked-file delete+move race)
                    File.Move(logPath, oldLog, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                // If rotation fails (e.g. .old file locked by another process),
                // just continue — we'll append to the existing large file and retry next init
                Console.Error.WriteLine($"[WARNING] Log rotation failed: {ex.Message}");
            }

            // AutoFlush=false for batched writes; we flush explicitly on backup completion and shutdown
            // Assign via temp variable so if anything throws between creation and assignment, we dispose it
            StreamWriter? newWriter = null;
            try
            {
                newWriter = new StreamWriter(logPath, append: true, encoding: System.Text.Encoding.UTF8, bufferSize: 8192)
                {
                    AutoFlush = false
                };
                _writer = newWriter;
                newWriter = null; // Ownership transferred to _writer — prevent disposal below
            }
            finally
            {
                newWriter?.Dispose();
            }
        }
    }
}
