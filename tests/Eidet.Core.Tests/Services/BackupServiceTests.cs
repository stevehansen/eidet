using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class BackupServiceTests
{
    [Fact]
    public void BackupManifest_DefaultValues()
    {
        var manifest = new BackupManifest();
        Assert.Equal(1, manifest.Version);
        Assert.Equal("", manifest.EidetVersion);
        Assert.Equal(0, manifest.DocumentCount);
        Assert.Empty(manifest.RepoIds);
        Assert.Equal("", manifest.Checksum);
    }

    [Fact]
    public void BackupManifest_SetsProperties()
    {
        var manifest = new BackupManifest
        {
            EidetVersion = "0.1.0",
            DatabaseName = "Eidet",
            DocumentCount = 42,
            RepoIds = ["repo-a", "repo-b"],
            Checksum = "sha256:abc123",
        };

        Assert.Equal("0.1.0", manifest.EidetVersion);
        Assert.Equal(42, manifest.DocumentCount);
        Assert.Equal(2, manifest.RepoIds.Count);
    }

    [Fact]
    public void BackupConfig_Defaults()
    {
        var config = new BackupConfig();
        Assert.Equal("", config.BackupDir);
        Assert.Equal(10, config.RetainCount);
        Assert.Equal(0, config.AutoBackupIntervalHours);
    }

    [Fact]
    public void GetDefaultBackupDir_ReturnsValidPath()
    {
        var dir = BackupService.GetDefaultBackupDir();
        Assert.False(string.IsNullOrEmpty(dir));
        Assert.EndsWith("backups", dir);
    }

    [Fact]
    public void GetBackupDir_UsesConfigWhenSet()
    {
        var config = new BackupConfig { BackupDir = "/custom/path" };
        Assert.Equal("/custom/path", BackupService.GetBackupDir(config));
    }

    [Fact]
    public void GetBackupDir_UsesDefaultWhenEmpty()
    {
        var config = new BackupConfig { BackupDir = "" };
        var dir = BackupService.GetBackupDir(config);
        Assert.EndsWith("backups", dir);
    }

    [Fact]
    public void GenerateBackupPath_HasCorrectFormat()
    {
        var path = BackupService.GenerateBackupPath("/backup");
        Assert.StartsWith("/backup", path);
        Assert.EndsWith(".eidetbackup", path);
        Assert.Contains("eidet-backup-", path);
    }

    [Fact]
    public void ListBackups_EmptyDirectory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Empty(BackupService.ListBackups(tempDir));
    }

    [Fact]
    public void ListBackups_NonexistentDirectory_ReturnsEmpty()
    {
        Assert.Empty(BackupService.ListBackups("/nonexistent/path"));
    }

    [Fact]
    public void PruneBackups_EmptyDirectory_ReturnsZero()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Equal(0, BackupService.PruneBackups(tempDir, 5));
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void PruneBackups_RetainsCorrectCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create 5 fake backup files
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(tempDir, $"eidet-backup-{i:D4}.eidetbackup");
                File.WriteAllText(path, "test");
                Thread.Sleep(10); // Ensure different timestamps
            }

            var deleted = BackupService.PruneBackups(tempDir, 3);
            Assert.Equal(2, deleted);

            var remaining = Directory.GetFiles(tempDir, "*.eidetbackup");
            Assert.Equal(3, remaining.Length);
        }
        finally { Directory.Delete(tempDir, true); }
    }
}
