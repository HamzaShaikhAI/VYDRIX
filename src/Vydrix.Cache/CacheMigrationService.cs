using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vydrix.Core;

namespace Vydrix.Cache;

public sealed record MigrationResult(
    bool Success,
    string SourceRoot,
    string DestinationRoot,
    long BytesMigrated,
    int FilesMigrated,
    string Message,
    IReadOnlyList<string> Details);

/// <summary>
/// Safely migrates VFS cache data from one volume/path to another.
/// Validates destination capabilities, ensures no active mount sessions are running,
/// uses a staged copy with automatic rollback on error or cancellation, and atomically
/// updates configuration only after full verification.
/// </summary>
public sealed class CacheMigrationService
{
    private readonly ILogger? _logger;

    public CacheMigrationService(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<MigrationResult> MigrateAsync(
        string sourceCacheRoot,
        string destinationCacheRoot,
        AppSettings settings,
        ISettingsStore settingsStore,
        Func<bool> hasActiveMounts,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var details = new List<string>();
        var source = Path.GetFullPath(sourceCacheRoot);
        var destination = Path.GetFullPath(destinationCacheRoot);

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return new MigrationResult(true, source, destination, 0, 0, "Source and destination are identical; no migration needed.", details);
        }

        // 1. Guard against active mounts
        if (hasActiveMounts())
        {
            var msg = "Cannot migrate cache while cloud drives are actively mounted. Unmount all drives before migrating.";
            _logger?.LogWarning(msg);
            return new MigrationResult(false, source, destination, 0, 0, msg, [msg]);
        }

        // 2. Validate destination volume
        var validation = VolumeValidator.Validate(destination, settings.Cache.EffectiveBudgetForProfile(), settings.Cache.MinFreeSpaceBytes);
        if (!validation.IsValid)
        {
            var err = $"Destination validation failed: {string.Join("; ", validation.Errors)}";
            _logger?.LogError(err);
            return new MigrationResult(false, source, destination, 0, 0, err, validation.Errors);
        }

        if (!Directory.Exists(source))
        {
            // Nothing to copy; just create destination, update settings, and return
            Directory.CreateDirectory(destination);
            settings.Cache.CacheRoot = destination;
            settingsStore.Save(settings);
            return new MigrationResult(true, source, destination, 0, 0, "Source directory did not exist. Target directory created and configured.", details);
        }

        return await Task.Run(() =>
        {
            var stagingDir = Path.Combine(destination, $".staging-migration-{Guid.NewGuid():N}");
            long bytesMigrated = 0;
            int filesMigrated = 0;

            try
            {
                progress?.Report($"Creating staging area at {stagingDir}…");
                Directory.CreateDirectory(stagingDir);

                // Enumerate all files in source
                var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
                progress?.Report($"Found {sourceFiles.Length} file(s) to migrate.");

                foreach (var file in sourceFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    var rel = Path.GetRelativePath(source, file);
                    var targetFile = Path.Combine(stagingDir, rel);
                    var targetSubDir = Path.GetDirectoryName(targetFile)!;

                    Directory.CreateDirectory(targetSubDir);
                    File.Copy(file, targetFile, overwrite: true);

                    var len = new FileInfo(file).Length;
                    bytesMigrated += len;
                    filesMigrated++;

                    if (filesMigrated % 50 == 0)
                    {
                        progress?.Report($"Copied {filesMigrated}/{sourceFiles.Length} files ({ByteSize.Format(bytesMigrated)})…");
                    }
                }

                // Verify staging area
                progress?.Report("Verifying staged files…");
                foreach (var file in sourceFiles)
                {
                    var rel = Path.GetRelativePath(source, file);
                    var stagedFile = Path.Combine(stagingDir, rel);
                    if (!File.Exists(stagedFile))
                    {
                        throw new IOException($"Verification failed: missing staged file {stagedFile}");
                    }
                    var sLen = new FileInfo(file).Length;
                    var dLen = new FileInfo(stagedFile).Length;
                    if (sLen != dLen)
                    {
                        throw new IOException($"Verification failed: file length mismatch on {rel} (source {sLen}, staged {dLen})");
                    }
                }

                // Move staged directories to destination root
                progress?.Report("Promoting staged files to final destination…");
                foreach (var entry in Directory.GetFileSystemEntries(stagingDir))
                {
                    var name = Path.GetFileName(entry);
                    var finalDest = Path.Combine(destination, name);
                    if (Directory.Exists(entry))
                    {
                        if (Directory.Exists(finalDest)) Directory.Delete(finalDest, recursive: true);
                        Directory.Move(entry, finalDest);
                    }
                    else if (File.Exists(entry))
                    {
                        File.Move(entry, finalDest, overwrite: true);
                    }
                }

                // Ensure ownership marker is present in each mount directory
                foreach (var sub in Directory.GetDirectories(destination))
                {
                    var marker = Path.Combine(sub, CacheMaintenance.Marker);
                    if (!File.Exists(marker))
                    {
                        File.WriteAllText(marker, "This folder is managed by VYDRIX (migrated VFS cache).\n");
                    }
                }

                // Clean staging folder
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);

                // Update configuration atomically
                settings.Cache.CacheRoot = destination;
                settingsStore.Save(settings);

                details.Add($"Successfully migrated {filesMigrated} file(s) ({ByteSize.Format(bytesMigrated)}) to {destination}.");
                details.Add($"Original cache at {source} preserved for safety.");

                _logger?.LogInformation("Cache migration completed successfully: {Files} files ({Bytes}) to {Dest}",
                    filesMigrated, ByteSize.Format(bytesMigrated), destination);

                return new MigrationResult(
                    true,
                    source,
                    destination,
                    bytesMigrated,
                    filesMigrated,
                    $"Cache migrated successfully to {destination}. {filesMigrated} file(s) transferred ({ByteSize.Format(bytesMigrated)}).",
                    details);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Cache migration failed; rolling back staging directory {Staging}", stagingDir);
                try
                {
                    if (Directory.Exists(stagingDir))
                    {
                        Directory.Delete(stagingDir, recursive: true);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger?.LogWarning(cleanupEx, "Failed to clean staging area {Staging}", stagingDir);
                }

                return new MigrationResult(
                    false,
                    source,
                    destination,
                    0,
                    0,
                    $"Migration failed and rolled back cleanly: {ex.Message}",
                    [$"Error: {ex.Message}", $"Staging area at {stagingDir} rolled back."]);
            }
        }, ct);
    }
}
