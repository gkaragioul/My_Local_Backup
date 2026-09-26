using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Storage;

namespace MyLocalBackup.Core.Tests.Storage;

public sealed class ManifestDatabaseTests
{
    [Fact]
    public async Task AddEntryAsync_DuplicateSourceAndRelativePath_IsRejected()
    {
        using var temp = new TestDirectory();
        var source = BackupSource.Create(Guid.NewGuid(), temp.GetPath("source"));
        await using var manifest = await ManifestDatabase.CreateAsync(
            temp.GetPath("manifest.db"), Guid.NewGuid(), Guid.NewGuid(), [source]);
        var entry = ManifestEntry.Directory(source.Id, "folder", DateTimeOffset.UtcNow, FileAttributes.Directory);

        await manifest.AddEntryAsync(entry);

        await Assert.ThrowsAsync<ManifestConflictException>(() => manifest.AddEntryAsync(entry));
    }

    [Fact]
    public async Task ReadEntriesAsync_RoundTripsFileDirectoryAndLinkMetadata()
    {
        using var temp = new TestDirectory();
        var source = BackupSource.Create(Guid.NewGuid(), temp.GetPath("source"));
        var snapshotId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var fileObject = new StoredObject("sha256", new string('a', 64), 12);
        await using var manifest = await ManifestDatabase.CreateAsync(
            temp.GetPath("manifest.db"), snapshotId, repositoryId, [source]);

        await manifest.AddEntryAsync(ManifestEntry.Directory(
            source.Id, "empty", DateTimeOffset.UnixEpoch, FileAttributes.Directory));
        await manifest.AddEntryAsync(ManifestEntry.File(
            source.Id, "folder\\file.bin", fileObject, DateTimeOffset.UnixEpoch.AddMinutes(1), FileAttributes.Archive));
        await manifest.AddEntryAsync(ManifestEntry.Link(
            source.Id, "shortcut", ManifestEntryKind.SymbolicLink, "..\\target", DateTimeOffset.UnixEpoch));
        await manifest.SealAsync();

        var entries = await manifest.ReadEntriesAsync();

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, item => item.Kind == ManifestEntryKind.Directory && item.RelativePath == "empty");
        Assert.Contains(entries, item => item.Content == fileObject && item.RelativePath == "folder\\file.bin");
        Assert.Contains(entries, item => item.Kind == ManifestEntryKind.SymbolicLink && item.LinkTarget == "..\\target");
        Assert.True((await manifest.VerifyIntegrityAsync()).IsValid);
    }

    [Fact]
    public async Task AddEntryAsync_FileWithoutContentObject_IsRejectedBeforeDatabaseWrite()
    {
        using var temp = new TestDirectory();
        var source = BackupSource.Create(Guid.NewGuid(), temp.GetPath("source"));
        await using var manifest = await ManifestDatabase.CreateAsync(
            temp.GetPath("manifest.db"), Guid.NewGuid(), Guid.NewGuid(), [source]);
        var invalid = new ManifestEntry(
            source.Id,
            "file.txt",
            ManifestEntryKind.File,
            null,
            DateTimeOffset.UtcNow,
            FileAttributes.Archive,
            null);

        await Assert.ThrowsAsync<ArgumentException>(() => manifest.AddEntryAsync(invalid));
    }
}
