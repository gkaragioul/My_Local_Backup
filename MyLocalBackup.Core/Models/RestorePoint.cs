namespace MyLocalBackup.Core.Models
{
    public enum BackupStatus
    {
        InProgress,
        Completed,
        Failed,
        Interrupted,
        Deleting,
        CompletedWithErrors
    }

    public class RestorePoint
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }  // Always UTC
        public string Path { get; set; } = string.Empty;
        public BackupStatus Status { get; set; }
        public bool IsPinned { get; set; }
        public string TargetDestination { get; set; } = string.Empty;

        /// <summary>Local-time timestamp for display. Use this in UI bindings.</summary>
        public DateTime LocalTimestamp => Timestamp.ToLocalTime();
    }

    public class FileEntry
    {
        public int Id { get; set; }
        public int RestorePointId { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public uint Attributes { get; set; }
    }
}
