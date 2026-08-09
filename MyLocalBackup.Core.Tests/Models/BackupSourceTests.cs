using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Tests.Models;

public sealed class BackupSourceTests
{
    [Fact]
    public void Create_RejectsEmptySourceId()
    {
        Assert.Throws<BackupConfigurationException>(
            () => BackupSource.Create(Guid.Empty, "C:\\Data"));
    }

    [Fact]
    public void EqualDisplayNames_DoNotCollideWhenIdsDiffer()
    {
        var left = BackupSource.Create(Guid.NewGuid(), "C:\\Team\\Documents", "Documents");
        var right = BackupSource.Create(Guid.NewGuid(), "D:\\Archive\\Documents", "Documents");

        Assert.NotEqual(left.Id, right.Id);
        Assert.NotEqual(left.ManifestKey("file.txt"), right.ManifestKey("file.txt"));
    }

    [Fact]
    public void ManifestKey_RejectsRelativePathTraversal()
    {
        var source = BackupSource.Create(Guid.NewGuid(), "C:\\Data");

        Assert.Throws<BackupConfigurationException>(() => source.ManifestKey("..\\escape.txt"));
    }

    [Fact]
    public void ResolveSources_GivesLegacyFolderAStableIdAcrossReloads()
    {
        using var temp = new TestDirectory();
        var first = new BackupConfig { SourceFolders = [temp.GetPath("source")] };
        var second = new BackupConfig { SourceFolders = [temp.GetPath("source")] };

        var firstSource = Assert.Single(first.ResolveSources());
        var secondSource = Assert.Single(second.ResolveSources());

        Assert.NotEqual(Guid.Empty, firstSource.Id);
        Assert.Equal(firstSource.Id, secondSource.Id);
    }

    [Fact]
    public void ResolveSources_KeepsSameLeafNameSourcesDistinct()
    {
        using var temp = new TestDirectory();
        var config = new BackupConfig
        {
            SourceFolders =
            [
                temp.GetPath("left", "Documents"),
                temp.GetPath("right", "Documents")
            ]
        };

        var sources = config.ResolveSources();

        Assert.Equal(2, sources.Count);
        Assert.NotEqual(sources[0].Id, sources[1].Id);
    }
}
