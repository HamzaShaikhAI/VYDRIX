using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vydrix.Core;

namespace Vydrix.Cache;

/// <summary>
/// Detailed outcome of a cache maintenance execution.
/// </summary>
public sealed record MaintenanceReport(
    DateTime Timestamp,
    bool Required,
    string Reason,
    long BytesReclaimed,
    int FilesEvicted,
    long PhysicalUsageBefore,
    long PhysicalUsageAfter,
    long VolumeFreeBefore,
    long VolumeFreeAfter,
    IReadOnlyList<string> Details);

/// <summary>
/// Manages the smart lifecycle of local VFS cache data on SSD.
/// Executes lightweight background checks every 1-2 hours without expensive disk scans
/// when storage is healthy, safely evicts LRU files when budget or reserve thresholds are
/// breached, and strictly protects pinned, dirty, locked, and active game session files.
/// </summary>
public sealed class SmartCacheManager : IDisposable
{
    private readonly string _cacheRoot;
    private readonly CacheSettings _settings;
    private readonly Func<IEnumerable<MountDefinition>> _mountsProvider;
    private readonly Func<string, bool> _isMountActive;
    private readonly Func<string, bool> _isGameActive;
    private readonly Func<IReadOnlyList<string>> _pinnedPathsProvider;
    private readonly ILogger? _logger;
    private readonly Timer? _timer;
    private readonly object _sync = new();
    private bool _isDisposed;

    public SmartCacheManager(
        string cacheRoot,
        CacheSettings settings,
        Func<IEnumerable<MountDefinition>> mountsProvider,
        Func<string, bool>? isMountActive = null,
        Func<string, bool>? isGameActive = null,
        Func<IReadOnlyList<string>>? pinnedPathsProvider = null,
        ILogger? logger = null)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mountsProvider = mountsProvider ?? throw new ArgumentNullException(nameof(mountsProvider));
        _isMountActive = isMountActive ?? (_ => false);
        _isGameActive = isGameActive ?? (_ => false);
        _pinnedPathsProvider = pinnedPathsProvider ?? (() => _settings.PinnedPaths);
        _logger = logger;

        if (_settings.AutoMaintenanceEnabled && _settings.MaintenanceInterval > TimeSpan.Zero)
        {
            _timer = new Timer(OnTimerTick, null, _settings.MaintenanceInterval, _settings.MaintenanceInterval);
        }
    }

    public MaintenanceReport? LastReport { get; private set; }
    public event Action<MaintenanceReport>? MaintenanceCompleted;

    private void OnTimerTick(object? state)
    {
        _ = RunMaintenanceAsync(force: false, trigger: "scheduled");
    }

    /// <summary>
    /// Executes cache maintenance. If <paramref name="force"/> is false, runs a lightweight check first:
    /// if free disk space is well above the reserve and estimated cache usage is within budget, skips
    /// scanning the entire disk.
    /// </summary>
    public async Task<MaintenanceReport> RunMaintenanceAsync(bool force = false, string trigger = "manual", CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            lock (_sync)
            {
                var timestamp = DateTime.Now;
                var details = new List<string>();

                if (!Directory.Exists(_cacheRoot))
                {
                    var emptyReport = new MaintenanceReport(
                        timestamp, false, "Cache root directory does not exist", 0, 0, 0, 0, 0, 0, ["Cache root not found."]);
                    LastReport = emptyReport;
                    MaintenanceCompleted?.Invoke(emptyReport);
                    return emptyReport;
                }

                var (volumeFree, volumeTotal) = DiskInfo.FreeSpace(_cacheRoot);
                var reserve = _settings.MinFreeSpaceBytes;
                var budget = _settings.BudgetBytes;

                // 1. Lightweight pre-check: if free space is comfortable and not forced, check quick metrics
                bool underDiskPressure = volumeFree < reserve;
                if (!force && !underDiskPressure)
                {
                    // Check if volume free space is safe and no urgent disk pressure exists
                    // Let's do a fast measurement of current cache folders
                    var quickUsage = MeasureCurrentPhysicalBytes();
                    if (quickUsage <= budget)
                    {
                        var skippedReport = new MaintenanceReport(
                            timestamp,
                            false,
                            $"Lightweight check: free space ({ByteSize.Format(volumeFree)}) and cache ({ByteSize.Format(quickUsage)}) within limits",
                            0,
                            0,
                            quickUsage,
                            quickUsage,
                            volumeFree,
                            volumeFree,
                            [$"Trigger: {trigger}", "No eviction needed; budget and reserve satisfied."]);

                        _logger?.LogDebug("SmartCacheManager lightweight check passed. No eviction required.");
                        LastReport = skippedReport;
                        MaintenanceCompleted?.Invoke(skippedReport);
                        return skippedReport;
                    }
                }

                _logger?.LogInformation("SmartCacheManager starting cache maintenance (Trigger: {Trigger}, Free: {Free}, Reserve: {Reserve}, Budget: {Budget})",
                    trigger, ByteSize.Format(volumeFree), ByteSize.Format(reserve), ByteSize.Format(budget));

                var physicalBefore = MeasureCurrentPhysicalBytes();
                long bytesReclaimed = 0;
                int filesEvicted = 0;

                // 2. Identify candidate files across all mount caches
                var candidateFiles = new List<EvictionCandidate>();
                var pinnedList = _pinnedPathsProvider();
                var mounts = _mountsProvider().ToList();

                foreach (var mount in mounts)
                {
                    var mountCacheDir = Path.Combine(_cacheRoot, "m-" + mount.Id);
                    if (!Directory.Exists(mountCacheDir)) continue;

                    // Verify safety marker
                    if (!File.Exists(Path.Combine(mountCacheDir, CacheMaintenance.Marker)) &&
                        !File.Exists(Path.Combine(mountCacheDir, CacheMaintenance.LegacyMarker)))
                    {
                        details.Add($"Skipping unmarked folder: m-{mount.Id}");
                        continue;
                    }

                    var isMounted = _isMountActive(mount.Id);
                    var enumerated = CacheInspector.Enumerate(mountCacheDir, mount.RemotePath).ToList();

                    foreach (var cf in enumerated)
                    {
                        // Check protection rules:
                        // 1. Dirty files: pending upload to cloud -> NEVER evict
                        if (cf.Dirty)
                        {
                            details.Add($"Protected (dirty/pending upload): {cf.RemotePath}");
                            continue;
                        }

                        // 2. Pinned files: user explicitly pinned -> NEVER evict
                        var fullKey = $"{mount.Id}:{cf.RemotePath}";
                        if (pinnedList.Any(p => string.Equals(p, fullKey, StringComparison.OrdinalIgnoreCase) ||
                                                p.EndsWith(":" + cf.RemotePath, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(p, cf.RemotePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            details.Add($"Protected (pinned): {cf.RemotePath}");
                            continue;
                        }

                        // 3. Active game files: belonging to currently running game session -> NEVER evict
                        if (_isGameActive(cf.RemotePath))
                        {
                            details.Add($"Protected (active game): {cf.RemotePath}");
                            continue;
                        }

                        // 4. In-use / locked files: open by active process
                        var dataFile = FindDataFilePath(mountCacheDir, cf.RemotePath, mount.RemotePath);
                        if (dataFile != null && IsFileLocked(dataFile))
                        {
                            details.Add($"Protected (file locked/open): {cf.RemotePath}");
                            continue;
                        }

                        var metaFile = FindMetaFilePath(mountCacheDir, cf.RemotePath, mount.RemotePath);

                        candidateFiles.Add(new EvictionCandidate(
                            mount.Id,
                            mount.DisplayName,
                            cf.RemotePath,
                            cf.PhysicalBytes,
                            cf.LastAccess,
                            dataFile,
                            metaFile,
                            isMounted));
                    }
                }

                // 3. Determine target bytes to reclaim
                long neededForBudget = Math.Max(0, physicalBefore - budget);
                long neededForReserve = Math.Max(0, reserve - volumeFree);
                long targetToReclaim = Math.Max(neededForBudget, neededForReserve);

                if (force && targetToReclaim == 0)
                {
                    // If manually forced with no overrun, reclaim expired files (older than MaxAge)
                    targetToReclaim = long.MaxValue;
                }

                // 4. Sort eligible candidates by LRU (oldest access time first)
                candidateFiles.Sort((a, b) => a.LastAccess.CompareTo(b.LastAccess));

                foreach (var candidate in candidateFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    // If not forced and we have met the budget & reserve reclamation target, stop
                    if (!force && bytesReclaimed >= targetToReclaim && targetToReclaim > 0)
                        break;

                    // If forced and target was MaxValue, only evict files older than MaxAge unless still over budget
                    if (force && targetToReclaim == long.MaxValue && (DateTime.Now - candidate.LastAccess) < _settings.MaxAge)
                    {
                        if (physicalBefore - bytesReclaimed <= budget)
                            break;
                    }

                    try
                    {
                        long freedForFile = 0;

                        // Delete sparse data file
                        if (candidate.DataFilePath != null && File.Exists(candidate.DataFilePath))
                        {
                            var fi = new FileInfo(candidate.DataFilePath);
                            freedForFile = candidate.PhysicalBytes > 0 ? candidate.PhysicalBytes : fi.Length;
                            File.Delete(candidate.DataFilePath);
                            CleanEmptyDirectoryParents(Path.GetDirectoryName(candidate.DataFilePath)!, Path.Combine(_cacheRoot, "m-" + candidate.MountId, "vfs"));
                        }

                        // Delete vfsMeta file
                        if (candidate.MetaFilePath != null && File.Exists(candidate.MetaFilePath))
                        {
                            File.Delete(candidate.MetaFilePath);
                            CleanEmptyDirectoryParents(Path.GetDirectoryName(candidate.MetaFilePath)!, Path.Combine(_cacheRoot, "m-" + candidate.MountId, "vfsMeta"));
                        }

                        bytesReclaimed += freedForFile;
                        filesEvicted++;
                        details.Add($"Evicted (LRU {candidate.LastAccess:g}, freed {ByteSize.Format(freedForFile)}): [{candidate.MountName}] {candidate.RemotePath}");
                    }
                    catch (Exception ex)
                    {
                        details.Add($"Error evicting {candidate.RemotePath}: {ex.Message}");
                        _logger?.LogWarning(ex, "Failed to evict cached file {RemotePath}", candidate.RemotePath);
                    }
                }

                var physicalAfter = MeasureCurrentPhysicalBytes();
                var (volumeFreeAfter, _) = DiskInfo.FreeSpace(_cacheRoot);

                var reason = underDiskPressure
                    ? $"Disk pressure relief (reclaimed {ByteSize.Format(bytesReclaimed)})"
                    : neededForBudget > 0
                        ? $"Cache budget enforcement (reclaimed {ByteSize.Format(bytesReclaimed)})"
                        : $"Maintenance ({trigger}): evicted {filesEvicted} file(s)";

                var report = new MaintenanceReport(
                    timestamp,
                    true,
                    reason,
                    bytesReclaimed,
                    filesEvicted,
                    physicalBefore,
                    physicalAfter,
                    volumeFree,
                    volumeFreeAfter,
                    details);

                _logger?.LogInformation("SmartCacheManager completed maintenance: {Reason}. Files evicted: {Files}, Reclaimed: {Bytes}",
                    report.Reason, report.FilesEvicted, ByteSize.Format(report.BytesReclaimed));

                LastReport = report;
                MaintenanceCompleted?.Invoke(report);
                return report;
            }
        }, ct);
    }

    /// <summary>
    /// Prunes unpinned, non-dirty cached files that have not been accessed within <paramref name="maxAge"/>.
    /// </summary>
    public async Task<MaintenanceReport> PruneUnpinnedOlderThanAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            lock (_sync)
            {
                var cutoff = DateTime.Now - maxAge;
                var details = new List<string>();
                var timestamp = DateTime.Now;

                var (volumeFreeBefore, _) = DiskInfo.FreeSpace(_cacheRoot);
                var physicalBefore = MeasureCurrentPhysicalBytes();
                long bytesReclaimed = 0;
                int filesEvicted = 0;

                var pinnedList = _pinnedPathsProvider();
                var mounts = _mountsProvider().ToList();

                foreach (var mount in mounts)
                {
                    var mountCacheDir = Path.Combine(_cacheRoot, "m-" + mount.Id);
                    if (!Directory.Exists(mountCacheDir)) continue;

                    if (!File.Exists(Path.Combine(mountCacheDir, CacheMaintenance.Marker)) &&
                        !File.Exists(Path.Combine(mountCacheDir, CacheMaintenance.LegacyMarker)))
                        continue;

                    var files = CacheInspector.Enumerate(mountCacheDir, mount.RemotePath).ToList();
                    foreach (var cf in files)
                    {
                        if (cf.Dirty) continue;
                        if (cf.LastAccess > cutoff) continue;

                        var fullKey = $"{mount.Id}:{cf.RemotePath}";
                        if (pinnedList.Any(p => string.Equals(p, fullKey, StringComparison.OrdinalIgnoreCase) ||
                                                p.EndsWith(":" + cf.RemotePath, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        if (_isGameActive(cf.RemotePath)) continue;

                        var dataFile = FindDataFilePath(mountCacheDir, cf.RemotePath, mount.RemotePath);
                        if (dataFile != null && IsFileLocked(dataFile)) continue;

                        var metaFile = FindMetaFilePath(mountCacheDir, cf.RemotePath, mount.RemotePath);

                        try
                        {
                            long freed = 0;
                            if (dataFile != null && File.Exists(dataFile))
                            {
                                freed = cf.PhysicalBytes;
                                File.Delete(dataFile);
                                CleanEmptyDirectoryParents(Path.GetDirectoryName(dataFile)!, Path.Combine(_cacheRoot, "m-" + mount.Id, "vfs"));
                            }
                            if (metaFile != null && File.Exists(metaFile))
                            {
                                File.Delete(metaFile);
                                CleanEmptyDirectoryParents(Path.GetDirectoryName(metaFile)!, Path.Combine(_cacheRoot, "m-" + mount.Id, "vfsMeta"));
                            }

                            bytesReclaimed += freed;
                            filesEvicted++;
                            details.Add($"Pruned expired ({cf.LastAccess:g}): {cf.RemotePath}");
                        }
                        catch (Exception ex)
                        {
                            details.Add($"Error pruning {cf.RemotePath}: {ex.Message}");
                        }
                    }
                }

                var physicalAfter = MeasureCurrentPhysicalBytes();
                var (volumeFreeAfter, _) = DiskInfo.FreeSpace(_cacheRoot);

                var report = new MaintenanceReport(
                    timestamp,
                    true,
                    $"Pruned files unaccessed for > {maxAge.TotalDays:0.#} days",
                    bytesReclaimed,
                    filesEvicted,
                    physicalBefore,
                    physicalAfter,
                    volumeFreeBefore,
                    volumeFreeAfter,
                    details);

                LastReport = report;
                MaintenanceCompleted?.Invoke(report);
                return report;
            }
        }, ct);
    }

    /// <summary>
    /// Safely tests whether a file is currently open or locked by another process (e.g. rclone or a game).
    /// </summary>
    public static bool IsFileLocked(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private long MeasureCurrentPhysicalBytes()
    {
        long total = 0;
        if (!Directory.Exists(_cacheRoot)) return 0;

        foreach (var mount in _mountsProvider())
        {
            var mountDir = Path.Combine(_cacheRoot, "m-" + mount.Id);
            if (!Directory.Exists(mountDir)) continue;

            try
            {
                foreach (var cf in CacheInspector.Enumerate(mountDir, mount.RemotePath))
                {
                    total += cf.PhysicalBytes;
                }
            }
            catch
            {
                // Best-effort enumeration
            }
        }
        return total;
    }

    private static string? FindDataFilePath(string mountCacheDir, string remotePath, string? remoteRoot)
    {
        var rel = CacheInspector.StripRoot(remotePath, remoteRoot ?? "").Replace('/', '\\').TrimStart('\\');
        var vfsDir = Path.Combine(mountCacheDir, "vfs");
        if (!Directory.Exists(vfsDir)) return null;

        foreach (var sub in Directory.EnumerateDirectories(vfsDir))
        {
            var p = Path.Combine(sub, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? FindMetaFilePath(string mountCacheDir, string remotePath, string? remoteRoot)
    {
        var rel = CacheInspector.StripRoot(remotePath, remoteRoot ?? "").Replace('/', '\\').TrimStart('\\');
        var metaDir = Path.Combine(mountCacheDir, "vfsMeta");
        if (!Directory.Exists(metaDir)) return null;

        foreach (var sub in Directory.EnumerateDirectories(metaDir))
        {
            var p = Path.Combine(sub, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void CleanEmptyDirectoryParents(string startDir, string boundaryRoot)
    {
        try
        {
            var current = startDir;
            var boundary = Path.GetFullPath(boundaryRoot).TrimEnd('\\');

            while (!string.IsNullOrEmpty(current) &&
                   !string.Equals(Path.GetFullPath(current).TrimEnd('\\'), boundary, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFullPath(current).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
                {
                    Directory.Delete(current, false);
                    current = Path.GetDirectoryName(current);
                }
                else
                {
                    break;
                }
            }
        }
        catch
        {
            // Best effort directory cleanup
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _timer?.Dispose();
    }

    private sealed record EvictionCandidate(
        string MountId,
        string MountName,
        string RemotePath,
        long PhysicalBytes,
        DateTime LastAccess,
        string? DataFilePath,
        string? MetaFilePath,
        bool IsMounted);
}
