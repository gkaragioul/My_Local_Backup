using System.Text.Json;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Configuration
{
    public class ConfigManager
    {
        private readonly string _configPath;
        public BackupConfig Config { get; private set; }
        public bool ConfigWasCorrupted { get; private set; }

        public ConfigManager()
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyLocalBackup");
            Directory.CreateDirectory(appData);
            _configPath = Path.Combine(appData, "config.json");
            
            Config = LoadConfig();
        }

        private BackupConfig LoadConfig()
        {
            if (!File.Exists(_configPath))
            {
                return new BackupConfig();
            }

            try
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<BackupConfig>(json) ?? new BackupConfig();

                // Don't allow network paths as backup destination — unreliable and not supported
                if (config.Destination != null && !string.IsNullOrEmpty(config.Destination.RootPath))
                {
                    var destPath = config.Destination.RootPath;
                    if (destPath.StartsWith(@"\\") || destPath.StartsWith("//"))
                    {
                        Logger.Log($"WARNING: Config destination is a network path ({destPath}). Clearing for safety.");
                        config.Destination = null;
                    }
                }

                return config;
            }
            catch (Exception ex)
            {
                Logger.Log($"WARNING: Config file corrupted, resetting to defaults: {ex}");
                // Save a backup of the corrupted file so the user can recover their settings
                try
                {
                    var bakPath = _configPath + ".bak";
                    File.Copy(_configPath, bakPath, overwrite: true);
                    Logger.Log($"Corrupted config saved to: {bakPath}");
                }
                catch (Exception backupEx) { Logger.Log($"WARNING: Could not save backup of corrupted config: {backupEx.Message}"); }
                ConfigWasCorrupted = true;
                return new BackupConfig();
            }
        }

        private readonly object _saveLock = new();

        public void SaveConfig()
        {
            lock (_saveLock)
            {
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(Config, options);

                    // Atomic write: write to temp file, then rename to prevent corruption on crash
                    var tempPath = _configPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _configPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Logger.Log($"CRITICAL: Failed to save config: {ex}");
                    // Clean up temp file if it exists
                    try { var tmp = _configPath + ".tmp"; if (File.Exists(tmp)) File.Delete(tmp); }
                    catch (Exception cleanupEx) { Logger.Log($"WARNING: Failed to clean up temp config file: {cleanupEx.Message}"); }
                }
            }
        }
    }
}
