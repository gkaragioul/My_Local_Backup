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

    public class BackupConfig
    {
        private int _intervalHours = 1;

        public List<string> SourceFolders { get; set; } = new();
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
    }
}
