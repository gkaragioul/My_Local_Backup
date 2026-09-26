using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Storage;

namespace MyLocalBackup.Core.Tests.Storage;

public sealed class RepositoryManagerTests
{
    [Fact]
    public async Task OpenOrCreateAsync_ReopensSameRepositoryOnSameVolume()
    {
        using var temp = new TestDirectory();
        var firstManager = new RepositoryManager(temp.Path, new FixedVolumeIdentityProvider("volume-A"));
        var first = await firstManager.OpenOrCreateAsync();
        var secondManager = new RepositoryManager(temp.Path, new FixedVolumeIdentityProvider("volume-A"));

        var second = await secondManager.OpenOrCreateAsync();

        Assert.NotEqual(Guid.Empty, first.RepositoryId);
        Assert.Equal(first, second);
        Assert.True(File.Exists(temp.GetPath("repository.json")));
    }

    [Fact]
    public async Task OpenOrCreateAsync_DifferentPhysicalVolume_IsRejected()
    {
        using var temp = new TestDirectory();
        await new RepositoryManager(temp.Path, new FixedVolumeIdentityProvider("volume-A")).OpenOrCreateAsync();

        var error = await Assert.ThrowsAsync<DestinationVolumeMismatchException>(
            () => new RepositoryManager(temp.Path, new FixedVolumeIdentityProvider("volume-B")).OpenOrCreateAsync());

        Assert.Contains("volume", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PromoteSnapshotAsync_AtomicallyMovesOnlyVerifiedManifest()
    {
        using var temp = new TestDirectory();
        var manager = new RepositoryManager(temp.Path, new FixedVolumeIdentityProvider("volume-A"));
        var repository = await manager.OpenOrCreateAsync();
        var source = BackupSource.Create(Guid.NewGuid(), temp.GetPath("source"));
        var snapshotId = Guid.NewGuid();
        var staging = await manager.BeginSnapshotAsync(snapshotId);
        await using (var manifest = await ManifestDatabase.CreateAsync(
            staging.ManifestPath, snapshotId, repository.RepositoryId, [source]))
        {
            await manifest.AddEntryAsync(ManifestEntry.Directory(
                source.Id, "empty", DateTimeOffset.UtcNow, FileAttributes.Directory));
            await manifest.SealAsync();
        }

        var promoted = await manager.PromoteSnapshotAsync(staging, new SnapshotSummary(snapshotId, 1, 0, 0));

        Assert.False(Directory.Exists(staging.DirectoryPath));
        var manifestPath = Path.Combine(promoted, "manifest.db");
        var summaryPath = Path.Combine(promoted, "summary.json");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(summaryPath));
        Assert.True(File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(summaryPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task PromoteSnapshotAsync_CorruptManifest_RemainsStagedAndFails()
    {
        using var temp = new TestDirectory();
        var manager = new RepositoryManager(temp.Path, new FixedVolumeIdentityProvider("volume-A"));
        await manager.OpenOrCreateAsync();
        var staging = await manager.BeginSnapshotAsync(Guid.NewGuid());
        await File.WriteAllTextAsync(staging.ManifestPath, "not a sqlite database");

        await Assert.ThrowsAsync<RepositoryCorruptionException>(
            () => manager.PromoteSnapshotAsync(staging, new SnapshotSummary(staging.SnapshotId, 0, 0, 0)));

        Assert.True(Directory.Exists(staging.DirectoryPath));
    }

    [Fact]
    public async Task RecoverAbandonedStagingAsync_RemovesOnlySnapshotStagingChildren()
    {
        using var temp = new TestDirectory();
        var manager = new RepositoryManager(temp.Path, new FixedVolumeIdentityProvider("volume-A"));
        await manager.OpenOrCreateAsync();
        var abandoned = await manager.BeginSnapshotAsync(Guid.NewGuid());
        var outside = temp.GetPath("do-not-delete.txt");
        await File.WriteAllTextAsync(outside, "keep");

        var removed = await manager.RecoverAbandonedSnapshotStagingAsync();

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(abandoned.DirectoryPath));
        Assert.True(File.Exists(outside));
    }

    private sealed class FixedVolumeIdentityProvider(string identity) : IVolumeIdentityProvider
    {
        public string GetIdentity(string path) => identity;
    }
}
