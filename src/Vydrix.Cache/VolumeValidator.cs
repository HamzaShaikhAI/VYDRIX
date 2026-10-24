using System;
using System.Collections.Generic;
using System.IO;
using Vydrix.Core;

namespace Vydrix.Cache;

/// <summary>
/// Result of validating a volume and directory for use as a VYDRIX VFS cache root.
/// </summary>
public sealed record VolumeValidationResult(
    bool IsValid,
    string FullPath,
    string VolumeRoot,
    string FileSystem,
    bool SupportsSparseFiles,
    long FreeBytes,
    long TotalBytes,
    bool HasWritePermission,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates target storage volumes for VFS caching. Ensures the filesystem is NTFS/ReFS with
/// sparse-file capabilities, verifies read/write permissions, confirms free space reserves,
/// and prevents silent fallback to the system drive (C:) when an alternate volume is configured.
/// </summary>
public static class VolumeValidator
{
    public static VolumeValidationResult Validate(string path, long requiredBudgetBytes = 512 * ByteSize.MiB, long minReserveBytes = 2 * ByteSize.GiB)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return new VolumeValidationResult(
                false, string.Empty, string.Empty, "None", false, 0, 0, false,
                ["Cache path cannot be empty."], []);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return new VolumeValidationResult(
                false, path, string.Empty, "Invalid", false, 0, 0, false,
                [$"Invalid path format: {ex.Message}"], []);
        }

        var volumeRoot = DiskInfo.VolumeRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return new VolumeValidationResult(
                false, fullPath, string.Empty, "Unknown", false, 0, 0, false,
                [$"Could not determine volume root for {fullPath}"], []);
        }

        // 1. Filesystem & Sparse Files Capabilities
        var (fileSystem, supportsSparse) = DiskInfo.VolumeFileSystem(fullPath);
        if (!supportsSparse)
        {
            errors.Add($"Volume {volumeRoot} ({fileSystem}) does not support sparse files. An NTFS or ReFS volume is required to prevent immediate disk allocation.");
        }

        if (!fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) &&
            !fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Volume {volumeRoot} reports '{fileSystem}'. NTFS is strongly recommended for rclone WinFsp sparse caching.");
        }

        // 2. Free Space Checks
        long freeBytes = 0;
        long totalBytes = 0;
        try
        {
            var space = DiskInfo.FreeSpace(fullPath);
            freeBytes = space.FreeForUser;
            totalBytes = space.Total;
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to query free space on volume {volumeRoot}: {ex.Message}");
        }

        var totalNeeded = requiredBudgetBytes + minReserveBytes;
        if (freeBytes < totalNeeded && freeBytes > 0)
        {
            if (freeBytes < minReserveBytes)
            {
                errors.Add($"Volume {volumeRoot} has only {ByteSize.Format(freeBytes)} free space, which violates the {ByteSize.Format(minReserveBytes)} Windows reserve.");
            }
            else
            {
                warnings.Add($"Volume {volumeRoot} free space ({ByteSize.Format(freeBytes)}) is less than requested budget ({ByteSize.Format(requiredBudgetBytes)}) plus reserve ({ByteSize.Format(minReserveBytes)}). Budget will be dynamically capped.");
            }
        }

        // 3. System Drive (C:) Awareness
        var isSystemDrive = volumeRoot.StartsWith("C:", StringComparison.OrdinalIgnoreCase);
        if (isSystemDrive)
        {
            warnings.Add("Cache is located on the Windows system volume (C:). Consider using a secondary volume (e.g. D:) to prevent OS storage growth.");
        }

        // 4. Write Permission Test
        bool hasWritePermission = false;
        try
        {
            Directory.CreateDirectory(fullPath);
            var testProbe = Path.Combine(fullPath, $".vydrix-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testProbe, "VYDRIX_PROBE");
            File.Delete(testProbe);
            hasWritePermission = true;
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add($"Write permission denied on directory: {fullPath}. Check user permissions.");
        }
        catch (Exception ex)
        {
            errors.Add($"Failed write test on {fullPath}: {ex.Message}");
        }

        bool isValid = errors.Count == 0 && hasWritePermission && supportsSparse;

        return new VolumeValidationResult(
            isValid,
            fullPath,
            volumeRoot,
            fileSystem,
            supportsSparse,
            freeBytes,
            totalBytes,
            hasWritePermission,
            errors,
            warnings);
    }
}
