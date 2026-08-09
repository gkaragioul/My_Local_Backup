using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Storage;

namespace MyLocalBackup.Core.Tests.Storage;

public sealed class PathRulesTests
{
    [Fact]
    public void NormalizeAbsolutePath_ProducesStableDirectoryPath()
    {
        using var temp = new TestDirectory();
        var path = System.IO.Path.Combine(temp.Path, "folder", "..", "data") + System.IO.Path.DirectorySeparatorChar;

        var result = PathRules.NormalizeAbsolutePath(path);

        Assert.Equal(System.IO.Path.Combine(temp.Path, "data"), result);
    }

    [Fact]
    public void NormalizeRelativePath_RejectsParentTraversal()
    {
        var error = Assert.Throws<BackupConfigurationException>(
            () => PathRules.NormalizeRelativePath(System.IO.Path.Combine("safe", "..", "..", "escape.txt")));

        Assert.Contains("escape", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("C:\\absolute.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("/rooted.txt")]
    public void NormalizeRelativePath_RejectsRootedPaths(string path)
    {
        Assert.Throws<BackupConfigurationException>(() => PathRules.NormalizeRelativePath(path));
    }

    [Fact]
    public void EnsureNoSourceDestinationOverlap_RejectsDestinationInsideSource()
    {
        using var temp = new TestDirectory();
        var source = BackupSource.Create(Guid.NewGuid(), temp.GetPath("source"));
        var destination = temp.GetPath("source", "backups");

        var error = Assert.Throws<BackupConfigurationException>(
            () => PathRules.EnsureNoSourceDestinationOverlap([source], destination));

        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureNoSourceDestinationOverlap_RejectsSourceInsideDestination()
    {
        using var temp = new TestDirectory();
        var destination = temp.GetPath("repository");
        var source = BackupSource.Create(Guid.NewGuid(), temp.GetPath("repository", "source"));

        Assert.Throws<BackupConfigurationException>(
            () => PathRules.EnsureNoSourceDestinationOverlap([source], destination));
    }

    [Fact]
    public void EnsureNoSourceDestinationOverlap_AcceptsIndependentRoots()
    {
        using var temp = new TestDirectory();
        var source = BackupSource.Create(Guid.NewGuid(), temp.GetPath("source"));

        PathRules.EnsureNoSourceDestinationOverlap([source], temp.GetPath("repository"));
    }
}
