using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Vydrix.Core;

namespace Vydrix.Cache;

public sealed record PathCapabilitiesReport(
    string Path,
    bool Exists,
    bool CanWrite,
    bool CanExclusiveLock,
    bool CanAtomicRename,
    bool IsCloudMounted,
    double LatencyMs,
    bool IsSafeForScratch,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Cloud-First Storage Broker: Classifies workload files by purpose and optimal storage tier.
/// Enforces deterministic placement (Secondary Physical Drive, Bounded RAM, or strictly bounded C:)
/// and validates path capabilities before execution.
/// </summary>
public sealed class CloudStorageBroker
{
    private readonly CloudBrokerSettings _settings;
    private readonly CacheSettings _cacheSettings;
    private readonly string? _secondaryDriveRoot;

    public CloudStorageBroker(CloudBrokerSettings? settings = null, CacheSettings? cacheSettings = null)
    {
        _settings = settings ?? new CloudBrokerSettings();
        _cacheSettings = cacheSettings ?? new CacheSettings();

        _secondaryDriveRoot = FindUsableSecondaryDriveRoot();
    }

    /// <summary>
    /// Resolves an explicit WorkloadPlacementPolicy for a given workload.
    /// Prioritizes secondary physical volumes (e.g. D:) or bounded RAM to shield C:.
    /// </summary>
    public WorkloadPlacementPolicy ResolvePlacementPolicy(
        string workloadId,
        WorkloadType type,
        string cloudSourceLocation,
        WorkloadProfile profile = WorkloadProfile.StrictCloud)
    {
        var sanitizedId = SanitizeWorkloadId(workloadId);
        CachePlacementOption placement;
        string actualCachePath;
        string scratchLocation;
        long cacheBudget;
        string cleanupPolicy;
        string executionMode;

        var defaultCacheRoot = _cacheSettings.CacheRoot ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vydrix", "cache");

        // 1. Determine Cache Placement & Path
        if (!string.IsNullOrWhiteSpace(_settings.CustomCachePath) && Directory.Exists(Path.GetPathRoot(_settings.CustomCachePath)))
        {
            placement = CachePlacementOption.SecondaryPhysicalDrive;
            actualCachePath = Path.Combine(_settings.CustomCachePath, sanitizedId);
        }
        else if (_secondaryDriveRoot != null)
        {
            placement = CachePlacementOption.SecondaryPhysicalDrive;
            actualCachePath = Path.Combine(_secondaryDriveRoot, "VydrixCache", sanitizedId);
        }
        else if (profile == WorkloadProfile.StrictCloud)
        {
            placement = CachePlacementOption.BoundedC;
            actualCachePath = Path.Combine(defaultCacheRoot, sanitizedId);
        }
        else
        {
            placement = CachePlacementOption.BoundedC;
            actualCachePath = Path.Combine(defaultCacheRoot, sanitizedId);
        }

        // 2. Determine Scratch Location
        if (!string.IsNullOrWhiteSpace(_settings.CustomScratchPath))
        {
            scratchLocation = Path.Combine(_settings.CustomScratchPath, sanitizedId);
        }
        else if (_secondaryDriveRoot != null)
        {
            scratchLocation = Path.Combine(_secondaryDriveRoot, "VydrixScratch", sanitizedId);
        }
        else
        {
            scratchLocation = Path.Combine(Path.GetTempPath(), "VydrixScratch", sanitizedId);
        }

        // 3. Determine Budget based on WorkloadType & Profile
        cacheBudget = profile switch
        {
            WorkloadProfile.StrictCloud => 512 * ByteSize.MiB,
            WorkloadProfile.Balanced => 2 * ByteSize.GiB,
            WorkloadProfile.Performance => 10 * ByteSize.GiB,
            WorkloadProfile.Development => 1 * ByteSize.GiB,
            WorkloadProfile.AiInference => 4 * ByteSize.GiB,
            _ => 512 * ByteSize.MiB
        };

        // 4. Determine Cleanup & Execution
        switch (type)
        {
            case WorkloadType.Game:
                cleanupPolicy = "PostGameDrainCleanup_1.5s";
                executionMode = "VfsDemandStreamWithInflightReservation";
                break;
            case WorkloadType.AiModel:
                cleanupPolicy = "SessionTerminationOrRetainRecent";
                executionMode = "DirectMmapOrLocalBlobCache";
                break;
            case WorkloadType.DevelopmentWorkspace:
                cleanupPolicy = "WorkspaceLifecycleUserManaged";
                executionMode = "CloudSourceWithLocalScratchBuild";
                break;
            case WorkloadType.Dataset:
                cleanupPolicy = "ReadOnlyStreamNoLocalPersistentCopy";
                executionMode = "StreamPipelineBuffer";
                break;
            default:
                cleanupPolicy = "DefaultBoundedEviction";
                executionMode = "StandardStreaming";
                break;
        }

        return new WorkloadPlacementPolicy(
            workloadId,
            type,
            cloudSourceLocation,
            placement,
            actualCachePath,
            cacheBudget,
            scratchLocation,
            BuildOutputLocation: Path.Combine(scratchLocation, "build"),
            cleanupPolicy,
            executionMode);
    }

    /// <summary>
    /// Tests directory capabilities: write access, exclusive locking, atomic renames, latency.
    /// Determines whether a location is safe for databases, compiler scratch, or package caches.
    /// </summary>
    public PathCapabilitiesReport TestPathCapabilities(string targetPath)
    {
        var limitations = new List<string>();
        bool canWrite = false;
        bool canLock = false;
        bool canRename = false;
        double latencyMs = 0.0;
        bool exists = Directory.Exists(targetPath);

        string root = Path.GetPathRoot(targetPath) ?? string.Empty;
        bool isCloud = root.StartsWith("G:", StringComparison.OrdinalIgnoreCase);

        var sw = Stopwatch.StartNew();

        try
        {
            if (!exists)
            {
                Directory.CreateDirectory(targetPath);
                exists = true;
            }

            var testFile = Path.Combine(targetPath, $".vydrix_cap_test_{Guid.NewGuid():N}.tmp");
            var renameTarget = Path.Combine(targetPath, $".vydrix_cap_test_{Guid.NewGuid():N}.renamed");

            // Test 1: Write
            using (var fs = new FileStream(testFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                canWrite = true;
                byte[] sample = [0x56, 0x59, 0x44, 0x52, 0x49, 0x58]; // "VYDRIX"
                fs.Write(sample, 0, sample.Length);
                fs.Flush();

                // Test 2: Exclusive lock verification (second open must fail)
                try
                {
                    using var secondFs = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.None);
                    canLock = false; // Should not have succeeded
                }
                catch (IOException)
                {
                    canLock = true; // Expected: exclusive lock was held
                }
            }

            // Test 3: Atomic rename
            File.Move(testFile, renameTarget);
            canRename = true;

            // Cleanup
            if (File.Exists(renameTarget))
            {
                File.Delete(renameTarget);
            }
        }
        catch (Exception ex)
        {
            limitations.Add($"Path capability test error: {ex.Message}");
        }

        sw.Stop();
        latencyMs = sw.Elapsed.TotalMilliseconds;

        if (isCloud)
        {
            limitations.Add("Target path resides on cloud-backed virtual drive (G:).");
            limitations.Add("Cloud virtual drives may exhibit higher seek latency and intermittent locking timeouts during concurrent compiler or SQLite activity.");
        }

        bool isSafeForScratch = canWrite && canLock && canRename && (!isCloud || latencyMs < 50.0);

        if (!isSafeForScratch && isCloud)
        {
            limitations.Add("RECOMMENDATION: Route compiler intermediate build directories and package caches to a secondary physical volume (e.g. D:) or local scratch rather than directly onto G:.");
        }

        return new PathCapabilitiesReport(
            targetPath,
            exists,
            canWrite,
            canLock,
            canRename,
            isCloud,
            latencyMs,
            isSafeForScratch,
            limitations);
    }

    private static string? FindUsableSecondaryDriveRoot()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (d.DriveType != DriveType.Fixed) continue;
                if (d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Name.StartsWith("G", StringComparison.OrdinalIgnoreCase)) continue; // Don't use virtual drive as physical scratch

                // Choose a physical volume with at least 5 GiB free space
                if (d.AvailableFreeSpace > 5L * ByteSize.GiB)
                {
                    return d.Name; // e.g. "D:\"
                }
            }
        }
        catch { }

        return null;
    }

    private static string SanitizeWorkloadId(string rawId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = rawId.Select(c => invalid.Contains(c) || c == ':' || c == '/' || c == '\\' ? '_' : c).ToArray();
        return new string(chars);
    }
}
