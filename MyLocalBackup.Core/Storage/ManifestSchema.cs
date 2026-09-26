using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Storage;

public enum ManifestEntryKind
{
    File,
    Directory,
    SymbolicLink,
    Junction
}

public sealed record ManifestEntry(
    Guid SourceId,
    string RelativePath,
    ManifestEntryKind Kind,
    StoredObject? Content,
    DateTimeOffset LastWriteUtc,
    FileAttributes Attributes,
    string? LinkTarget)
{
    public static ManifestEntry File(
        Guid sourceId,
        string relativePath,
        StoredObject content,
        DateTimeOffset lastWriteUtc,
        FileAttributes attributes) =>
        new(sourceId, relativePath, ManifestEntryKind.File, content, lastWriteUtc, attributes, null);

    public static ManifestEntry Directory(
        Guid sourceId,
        string relativePath,
        DateTimeOffset lastWriteUtc,
        FileAttributes attributes) =>
        new(sourceId, relativePath, ManifestEntryKind.Directory, null, lastWriteUtc, attributes, null);

    public static ManifestEntry Link(
        Guid sourceId,
        string relativePath,
        ManifestEntryKind kind,
        string linkTarget,
        DateTimeOffset lastWriteUtc)
    {
        if (kind is not ManifestEntryKind.SymbolicLink and not ManifestEntryKind.Junction)
        {
            throw new ArgumentException("A link entry must be a symbolic link or junction.", nameof(kind));
        }

        return new ManifestEntry(sourceId, relativePath, kind, null, lastWriteUtc, FileAttributes.ReparsePoint, linkTarget);
    }
}

public sealed record ManifestIntegrityResult(bool IsValid, string? Error)
{
    public static ManifestIntegrityResult Valid { get; } = new(true, null);
    public static ManifestIntegrityResult Invalid(string error) => new(false, error);
}

public sealed class ManifestConflictException : IOException
{
    public ManifestConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record SnapshotSummary(Guid SnapshotId, long EntryCount, long FileCount, long LogicalBytes);

public sealed record SnapshotStagingArea(Guid SnapshotId, string DirectoryPath, string ManifestPath);
