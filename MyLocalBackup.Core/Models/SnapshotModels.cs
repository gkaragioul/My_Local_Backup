namespace MyLocalBackup.Core.Models;

public enum SnapshotStatus
{
    Staging,
    Verified,
    Failed,
    Quarantined,
    Deleting
}

public sealed record SnapshotDescriptor(
    Guid SnapshotId,
    Guid RepositoryId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    SnapshotStatus Status);
