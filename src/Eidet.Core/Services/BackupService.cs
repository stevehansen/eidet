using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Raven.Client.Documents;
using Raven.Client.Documents.Smuggler;

namespace Eidet.Core.Services;

public class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDocumentStore _store;
    private readonly string _databaseName;

    public BackupService(IDocumentStore store)
    {
        _store = store;
        _databaseName = store.Database;
    }

    public static string GetDefaultBackupDir()
    {
        var configDir = ConfigManager.GetConfigDir();
        return Path.Combine(configDir, "backups");
    }

    public static string GetBackupDir(BackupConfig config) =>
        string.IsNullOrEmpty(config.BackupDir) ? GetDefaultBackupDir() : config.BackupDir;

    public static string GenerateBackupPath(string backupDir) =>
        Path.Combine(backupDir, $"eidet-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.eidetbackup");

    public async Task<(string Path, BackupManifest Manifest)> CreateBackupAsync(
        string backupDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(backupDir);
        var backupPath = GenerateBackupPath(backupDir);

        // Export via Smuggler to temp file
        var tempDump = Path.GetTempFileName();
        try
        {
            var options = new DatabaseSmugglerExportOptions
            {
                OperateOnTypes = DatabaseItemType.Documents | DatabaseItemType.Indexes,
            };
            var operation = await _store.Smuggler.ExportAsync(options, tempDump, ct);
            await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(10));

            // Compute checksum
            var checksum = await ComputeChecksumAsync(tempDump, ct);

            // Get document count and repo IDs
            var stats = await _store.Maintenance.SendAsync(
                new Raven.Client.Documents.Operations.GetStatisticsOperation(), ct);

            // Build manifest
            var manifest = new BackupManifest
            {
                EidetVersion = EidetVersion.Current,
                CreatedAt = DateTime.UtcNow,
                DatabaseName = _databaseName,
                DocumentCount = stats.CountOfDocuments,
                Checksum = checksum,
            };

            // Create ZIP archive
            using (var zip = ZipFile.Open(backupPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tempDump, "backup.ravendbdump", CompressionLevel.Optimal);
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var stream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, ct);
            }

            return (backupPath, manifest);
        }
        finally
        {
            File.Delete(tempDump);
        }
    }

    public async Task<BackupManifest> ValidateBackupAsync(string backupPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Backup file not found", backupPath);

        using var zip = ZipFile.OpenRead(backupPath);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Backup missing manifest.json");
        var dumpEntry = zip.GetEntry("backup.ravendbdump")
            ?? throw new InvalidDataException("Backup missing backup.ravendbdump");

        using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, JsonOptions, ct)
            ?? throw new InvalidDataException("Invalid manifest");

        // Verify checksum
        var tempDump = Path.GetTempFileName();
        try
        {
            dumpEntry.ExtractToFile(tempDump, overwrite: true);
            var actualChecksum = await ComputeChecksumAsync(tempDump, ct);
            if (!string.IsNullOrEmpty(manifest.Checksum) && manifest.Checksum != actualChecksum)
                throw new InvalidDataException($"Checksum mismatch: expected {manifest.Checksum}, got {actualChecksum}");
        }
        finally
        {
            File.Delete(tempDump);
        }

        return manifest;
    }

    public async Task RestoreAsync(string backupPath, CancellationToken ct = default)
    {
        await ValidateBackupAsync(backupPath, ct);

        using var zip = ZipFile.OpenRead(backupPath);
        var dumpEntry = zip.GetEntry("backup.ravendbdump")!;

        var tempDump = Path.GetTempFileName();
        try
        {
            dumpEntry.ExtractToFile(tempDump, overwrite: true);

            var options = new DatabaseSmugglerImportOptions
            {
                OperateOnTypes = DatabaseItemType.Documents | DatabaseItemType.Indexes,
                SkipRevisionCreation = true,
            };
            var operation = await _store.Smuggler.ImportAsync(options, tempDump, ct);
            await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(10));
        }
        finally
        {
            File.Delete(tempDump);
        }
    }

    public static List<(string Path, DateTime Modified, long Size)> ListBackups(string backupDir)
    {
        if (!Directory.Exists(backupDir))
            return [];

        return Directory.GetFiles(backupDir, "*.eidetbackup")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (f.FullName, f.LastWriteTimeUtc, f.Length))
            .ToList();
    }

    public static int PruneBackups(string backupDir, int retainCount)
    {
        var backups = ListBackups(backupDir);
        var toDelete = backups.Skip(retainCount).ToList();
        foreach (var (path, _, _) in toDelete)
            File.Delete(path);
        return toDelete.Count;
    }

    private static async Task<string> ComputeChecksumAsync(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
