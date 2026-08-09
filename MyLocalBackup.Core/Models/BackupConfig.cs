using System.Security.Cryptography;
using System.Text;
using MyLocalBackup.Core.Storage;

namespace MyLocalBackup.Core.Models
{
    public enum ScheduleType
    {
        Manual,
        Automatic
    }

    public enum ScheduleMode
    {
        Interval,
        DailyFixedTime
    }

    public class BackupDestination
    {
        public string Name { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
    }

    public class BackupSourceDefinition
    {
        public Guid Id { get; set; }
        public string RootPath { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public class BackupConfig
    {
        private int _intervalHours = 1;

        public List<string> SourceFolders { get; set; } = new();
        public List<BackupSourceDefinition> Sources { get; set; } = new();
        public BackupDestination? Destination { get; set; }
        public ScheduleType Schedule { get; set; } = ScheduleType.Automatic;

        /// <summary>
        /// Backup interval in hours. Valid range: 1-168 (1 hour to 1 week).
        /// </summary>
        public int IntervalHours
        {
            get => _intervalHours;
            set => _intervalHours = Math.Clamp(value, 1, 168);
        }

        public ScheduleMode ScheduleMode { get; set; } = ScheduleMode.Interval;

        /// <summary>
        /// Time of day for daily fixed-time backups (hours and minutes only).
        /// Stored as "HH:mm" string for JSON serialization.
        /// </summary>
        public string DailyBackupTime { get; set; } = "02:00";

        public bool ShowBackupNotifications { get; set; } = true;
        public bool AutoDeleteOldBackups { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool LaunchAtStartup { get; set; }

        /// <summary>
        /// Folder names to exclude from backup (case-insensitive match on folder name, not full path).
        /// Empty by default — the app backs up everything the user pointed it at.
        /// Advanced users can populate this list via config.json to skip regeneratable artifacts
        /// (e.g. "node_modules", "bin", "obj", ".vs") and speed up backups of dev folders.
        /// </summary>
        public List<string> ExcludedFolderNames { get; set; } = new();

        public IReadOnlyList<BackupSource> ResolveSources()
        {
            var resolved = new List<BackupSource>();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new Dictionary<Guid, string>();

            foreach (var definition in Sources)
            {
                AddSource(definition.Id, definition.RootPath, definition.DisplayName);
            }

            foreach (var legacyPath in SourceFolders)
            {
                var root = PathRules.NormalizeAbsolutePath(legacyPath);
                AddSource(CreateStableLegacySourceId(root), root, displayName: null);
            }

            return resolved;

            void AddSource(Guid requestedId, string rootPath, string? displayName)
            {
                var root = PathRules.NormalizeAbsolutePath(rootPath);
                if (!roots.Add(root))
                {
                    return;
                }

                var id = requestedId == Guid.Empty ? CreateStableLegacySourceId(root) : requestedId;
                if (ids.TryGetValue(id, out var existingRoot) &&
                    !string.Equals(existingRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BackupConfigurationException(
                        $"Backup source ID '{id}' is assigned to both '{existingRoot}' and '{root}'.");
                }

                ids[id] = root;
                resolved.Add(BackupSource.Create(id, root, displayName));
            }
        }

        private static Guid CreateStableLegacySourceId(string normalizedRoot)
        {
            var canonicalBytes = Encoding.UTF8.GetBytes(normalizedRoot.ToUpperInvariant());
            var hash = SHA256.HashData(canonicalBytes);
            var guidBytes = hash.AsSpan(0, 16).ToArray();

            // Mark the generated identifier as a name-based UUID and RFC 4122 variant.
            guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
            return new Guid(guidBytes);
        }
    }
}
