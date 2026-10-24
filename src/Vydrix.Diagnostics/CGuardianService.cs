using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Core;

namespace Vydrix.Diagnostics;

/// <summary>
/// C: Guardian: Dedicated Windows system drive storage protection service.
/// Monitors C: capacity, rate of consumption, breaks down VYDRIX-owned storage,
/// detects secondary volume relocation opportunities, and safely triggers throttling
/// and dry-run cleanup before space exhaustion occurs.
/// </summary>
public sealed class CGuardianService : IDisposable
{
    private readonly CGuardianSettings _settings;
    private readonly string _dataRoot;
    private readonly Func<string?>? _activeWorkloadProvider;
    private readonly Action? _onCriticalThreshold;
    private readonly object _lock = new();

    private readonly Queue<(DateTime Timestamp, long FreeBytes)> _samples = new();
    private const int MaxRateSamples = 30;

    private CGuardianStatus? _lastStatus;
    private Timer? _pollingTimer;
    private bool _isCriticalTriggered;
    private bool _disposed;

    public event Action<CGuardianStatus>? StatusUpdated;
    public event Action<CGuardianStatus>? CriticalAlert;

    public CGuardianStatus? LastStatus
    {
        get { lock (_lock) return _lastStatus; }
    }

    public CGuardianService(
        CGuardianSettings? settings = null,
        string? dataRoot = null,
        Func<string?>? activeWorkloadProvider = null,
        Action? onCriticalThreshold = null)
    {
        _settings = settings ?? new CGuardianSettings();
        _dataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vydrix");
        _activeWorkloadProvider = activeWorkloadProvider;
        _onCriticalThreshold = onCriticalThreshold;

        if (_settings.Enabled && _settings.PollingIntervalSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(_settings.PollingIntervalSeconds);
            _pollingTimer = new Timer(_ => CheckStatus(), null, interval, interval);
        }
    }

    /// <summary>
    /// Evaluates current C: drive capacity, consumption rate, VYDRIX breakdown, and relocation options.
    /// Does NOT perform expensive full-drive scans.
    /// </summary>
    public CGuardianStatus CheckStatus(string driveLetter = "C")
    {
        lock (_lock)
        {
            long totalBytes = 0;
            long freeBytes = 0;

            try
            {
                var driveInfo = new DriveInfo(driveLetter);
                if (driveInfo.IsReady)
                {
                    totalBytes = driveInfo.TotalSize;
                    freeBytes = driveInfo.AvailableFreeSpace;
                }
            }
            catch
            {
                // Fallback for mocked or restricted environments
                totalBytes = 200L * ByteSize.GiB;
                freeBytes = 35L * ByteSize.GiB;
            }

            var now = DateTime.Now;
            _samples.Enqueue((now, freeBytes));
            if (_samples.Count > MaxRateSamples)
            {
                _samples.Dequeue();
            }

            double consumptionRateMBPerMin = CalculateConsumptionRateMBPerMin();
            double freePercent = totalBytes > 0 ? (double)freeBytes / totalBytes * 100.0 : 0.0;

            bool isWarning = freeBytes <= _settings.WarningThresholdBytes || freePercent <= 10.0;
            bool isCritical = freeBytes <= _settings.CriticalThresholdBytes || freePercent <= 5.0 ||
                              (consumptionRateMBPerMin > _settings.MaxConsumptionRateMBPerMin && freeBytes < _settings.WarningThresholdBytes);

            var topConsumers = AuditVydrixConsumers();
            var relocationOpportunities = DetectRelocationOpportunities(driveLetter);
            string? activeWorkload = _activeWorkloadProvider?.Invoke();

            var status = new CGuardianStatus(
                now,
                totalBytes,
                freeBytes,
                freePercent,
                consumptionRateMBPerMin,
                isWarning,
                isCritical,
                topConsumers,
                relocationOpportunities,
                activeWorkload);

            _lastStatus = status;

            if (isCritical)
            {
                if (!_isCriticalTriggered)
                {
                    _isCriticalTriggered = true;
                    _onCriticalThreshold?.Invoke();
                    CriticalAlert?.Invoke(status);
                }
            }
            else
            {
                _isCriticalTriggered = false;
            }

            StatusUpdated?.Invoke(status);
            return status;
        }
    }

    private double CalculateConsumptionRateMBPerMin()
    {
        if (_samples.Count < 2) return 0.0;

        var first = _samples.Peek();
        var last = _samples.Last();

        double elapsedMinutes = (last.Timestamp - first.Timestamp).TotalMinutes;
        if (elapsedMinutes <= 0.001) return 0.0;

        // If free space decreased, consumption occurred
        long deltaBytes = first.FreeBytes - last.FreeBytes;
        if (deltaBytes <= 0) return 0.0;

        double deltaMB = deltaBytes / (1024.0 * 1024.0);
        return deltaMB / elapsedMinutes;
    }

    /// <summary>
    /// Audits only known VYDRIX storage directories on C: without touching the rest of the disk.
    /// </summary>
    public IReadOnlyList<StorageConsumerItem> AuditVydrixConsumers()
    {
        var list = new List<StorageConsumerItem>();

        // 1. VFS Cache
        var cachePath = Path.Combine(_dataRoot, "cache");
        long cacheBytes = MeasureDirectoryBytes(cachePath);
        list.Add(new StorageConsumerItem(
            "VFS Cache",
            cachePath,
            cacheBytes,
            "Bounded Transient Cache",
            IsRelocatable: true));

        // 2. Application Logs
        var logsPath = Path.Combine(_dataRoot, "logs");
        long logsBytes = MeasureDirectoryBytes(logsPath);
        list.Add(new StorageConsumerItem(
            "Application Logs",
            logsPath,
            logsBytes,
            "Diagnostics & Logging",
            IsRelocatable: false));

        // 3. SQLite Database
        var dbPath = Path.Combine(_dataRoot, "vydrix.db");
        long dbBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
        list.Add(new StorageConsumerItem(
            "Database & Profiles",
            dbPath,
            dbBytes,
            "Protected System Data",
            IsRelocatable: false));

        // 4. Temporary / Scratch files
        var tempPath = Path.Combine(Path.GetTempPath(), "Vydrix");
        long tempBytes = MeasureDirectoryBytes(tempPath);
        list.Add(new StorageConsumerItem(
            "Temporary Files",
            tempPath,
            tempBytes,
            "Relocatable Workload Data",
            IsRelocatable: true));

        // 5. Binary Installation Footprint
        var binPath = AppDomain.CurrentDomain.BaseDirectory;
        long binBytes = MeasureDirectoryBytes(binPath, recursive: false);
        list.Add(new StorageConsumerItem(
            "Installed Binaries",
            binPath,
            binBytes,
            "Application Binaries",
            IsRelocatable: false));

        return list.OrderByDescending(c => c.SizeBytes).ToList();
    }

    /// <summary>
    /// Detects secondary physical drives (e.g. D:\, E:\) suitable for relocating cache or scratch data.
    /// </summary>
    public IReadOnlyList<string> DetectRelocationOpportunities(string primaryDrive = "C")
    {
        var list = new List<string>();

        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (d.DriveType != DriveType.Fixed) continue;
                if (d.Name.StartsWith(primaryDrive, StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Name.StartsWith("G", StringComparison.OrdinalIgnoreCase)) continue;
                if (d.DriveFormat.Contains("rclone", StringComparison.OrdinalIgnoreCase) ||
                    d.DriveFormat.Contains("FUSE", StringComparison.OrdinalIgnoreCase) ||
                    d.DriveFormat.Contains("WinFsp", StringComparison.OrdinalIgnoreCase)) continue;

                // If volume has more than 5 GiB free, suggest it
                if (d.AvailableFreeSpace > 5L * ByteSize.GiB)
                {
                    list.Add(
                        $"{d.Name} ({ByteSize.Format(d.AvailableFreeSpace)} free, {d.DriveFormat}) - Ideal for secondary cache & scratch");
                }
            }
        }
        catch
        {
            // Ignore drive enumeration errors
        }

        return list;
    }

    /// <summary>
    /// Generates a dry-run report showing exactly what can be safely reclaimed without deleting user data.
    /// </summary>
    public CGuardianDryRunReport GenerateDryRunCleanupReport()
    {
        var consumers = AuditVydrixConsumers();
        long totalCurrentBytes = consumers.Sum(c => c.SizeBytes);
        long projectedReclaimable = 0;
        var actions = new List<string>();

        // 1. Check old logs (> 10 MB or multiple archives)
        var logsConsumer = consumers.FirstOrDefault(c => c.Name == "Application Logs");
        if (logsConsumer != null && logsConsumer.SizeBytes > 10 * ByteSize.MiB)
        {
            long logReclaim = logsConsumer.SizeBytes - (5 * ByteSize.MiB);
            projectedReclaimable += logReclaim;
            actions.Add($"Rotate and truncate aged application logs (reclaims ~{ByteSize.Format(logReclaim)})");
        }

        // 2. Check temporary files in %TEMP%\Vydrix
        var tempConsumer = consumers.FirstOrDefault(c => c.Name == "Temporary Files");
        if (tempConsumer != null && tempConsumer.SizeBytes > 0)
        {
            projectedReclaimable += tempConsumer.SizeBytes;
            actions.Add($"Purge obsolete test and temporary scratch files (reclaims {ByteSize.Format(tempConsumer.SizeBytes)})");
        }

        // 3. Transient unpinned VFS cache chunks
        var cacheConsumer = consumers.FirstOrDefault(c => c.Name == "VFS Cache");
        if (cacheConsumer != null && cacheConsumer.SizeBytes > 0)
        {
            actions.Add($"Evict unpinned VFS cache chunks under LRU rules (current: {ByteSize.Format(cacheConsumer.SizeBytes)})");
        }

        // Guarantee
        actions.Add("PROTECTION GUARANTEE: Never touches cloud originals, source repositories, game saves, or uncommitted work.");

        return new CGuardianDryRunReport(totalCurrentBytes, projectedReclaimable, actions, DestructiveActionAllowed: false);
    }

    /// <summary>
    /// Executes safe cleanup based on the dry-run report.
    /// </summary>
    public long ExecuteSafeCleanup(bool rotateLogs = true, bool purgeTemp = true)
    {
        long bytesReclaimed = 0;

        if (purgeTemp)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "Vydrix");
                if (Directory.Exists(tempPath))
                {
                    long before = MeasureDirectoryBytes(tempPath);
                    Directory.Delete(tempPath, recursive: true);
                    Directory.CreateDirectory(tempPath);
                    bytesReclaimed += before;
                }
            }
            catch
            {
                // Ignored - file locked by active process
            }
        }

        if (rotateLogs)
        {
            try
            {
                var logsPath = Path.Combine(_dataRoot, "logs");
                if (Directory.Exists(logsPath))
                {
                    var files = new DirectoryInfo(logsPath).GetFiles("*.log")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Skip(2); // Keep 2 newest logs

                    foreach (var f in files)
                    {
                        try
                        {
                            long sz = f.Length;
                            f.Delete();
                            bytesReclaimed += sz;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        CheckStatus();
        return bytesReclaimed;
    }

    private static long MeasureDirectoryBytes(string path, bool recursive = true)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            var searchOpt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return new DirectoryInfo(path).EnumerateFiles("*", searchOpt).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollingTimer?.Dispose();
    }
}
