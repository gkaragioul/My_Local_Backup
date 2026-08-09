using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void TestHarness_LoadsCoreAssembly()
    {
        Assert.Equal("MyLocalBackup.Core", typeof(BackupConfig).Assembly.GetName().Name);
    }
}
