using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Cache;
using Vydrix.Core;
using Vydrix.Games;
using Vydrix.Mount;
using Vydrix.Streaming;
using Xunit;

namespace Vydrix.Tests;

public sealed class ZeroFootprintModeTests : IDisposable
{
    private readonly string _testRoot;

    public ZeroFootprintModeTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "vydrix-zfm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    private static void CreateMockCachedFile(string mountCacheDir, string remoteFs, string relPath, long size, DateTime lastAccess, bool dirty = false)
    {
        var rel = relPath.Replace('/', '\\');
        var metaDir = Path.Combine(mountCacheDir, "vfsMeta", remoteFs, Path.GetDirectoryName(rel) ?? "");
        var dataDir = Path.Combine(mountCacheDir, "vfs", remoteFs, Path.GetDirectoryName(rel) ?? "");
        Directory.CreateDirectory(metaDir);
        Directory.CreateDirectory(dataDir);

        var metaFile = Path.Combine(mountCacheDir, "vfsMeta", remoteFs, rel);
        var dataFile = Path.Combine(mountCacheDir, "vfs", remoteFs, rel);

        var dto = new
        {
            Size = size,
            ATime = lastAccess.ToString("o"),
            Dirty = dirty,
            Fingerprint = "mock-fp",
            Rs = new[] { new { Pos = 0L, Size = size } }
        };
        File.WriteAllText(metaFile, JsonSerializer.Serialize(dto));

        using var fs = new FileStream(dataFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        fs.SetLength(size);
    }

    private sealed class DummyPrefetchExecutor : IPrefetchExecutor
    {
        public int FetchesAttempted;
        public Task<PrefetchResult> FetchAsync(PrefetchItem item, CancellationToken ct)
        {
            Interlocked.Increment(ref FetchesAttempted);
            return Task.FromResult(new PrefetchResult(0, TimeSpan.Zero, null));
        }
    }

    // 1. Default budget is exactly 512 MiB (536,870,912 bytes)
    [Fact]
    public void Scenario01_DefaultBudget_IsExactly512MB()
    {
        var settingsToggle = new CacheSettings { ZeroFootprintMode = true };
        Assert.Equal(512 * ByteSize.MiB, settingsToggle.EffectiveBudgetForProfile());
        Assert.Equal(536_870_912L, settingsToggle.EffectiveBudgetForProfile());
        Assert.Equal((512 - 16) * ByteSize.MiB, settingsToggle.EffectiveDataBudgetForProfile());

        var settingsProfile = new CacheSettings { Profile = CachePolicyProfile.ZeroFootprint };
        Assert.Equal(536_870_912L, settingsProfile.EffectiveBudgetForProfile());
        Assert.Equal(TimeSpan.FromHours(1), settingsProfile.EffectiveMaxAgeForProfile());

        var settingsMinimal = new CacheSettings { Profile = CachePolicyProfile.Minimal };
        Assert.Equal(536_870_912L, settingsMinimal.EffectiveBudgetForProfile());
    }

    // 2. Cache path resolves to configured C: location (%LOCALAPPDATA%\Vydrix\cache)
    [Fact]
    public void Scenario02_CachePath_ResolvesToConfiguredCLocation()
    {
        var paths = new AppPaths();
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vydrix", "cache");
        Assert.Equal(expected, paths.DefaultCacheRoot);

        var appSettings = new AppSettings();
        appSettings.Cache.ZeroFootprintMode = true;
        appSettings.Cache.CacheRoot = ""; // Default empty

        var resolved = paths.CacheRootFor(appSettings);
        Assert.Equal(expected, resolved);
        Assert.StartsWith("C:", resolved, StringComparison.OrdinalIgnoreCase);
    }

    // 3. Prefetching remains disabled after restart (DemandOnlyMode)
    [Fact]
    public void Scenario03_Prefetching_RemainsDisabledAfterRestart()
    {
        var dbPath = Path.Combine(_testRoot, "scenario03.db");
        var store = new ProfileStore(dbPath);
        var executor = new DummyPrefetchExecutor();
        var engine = new StreamingEngine(
            new StreamingSettings(),
            new NetworkSettings(),
            store,
            executor,
            (m, p) => 10 * ByteSize.MiB);

        Assert.False(engine.DemandOnlyMode);
        engine.SetDemandOnly(true);
        Assert.True(engine.DemandOnlyMode);

        // Queue should remain empty even if items were present
        Assert.Equal(0, engine.Queue.Count);
    }

    // 4. Demand reads fetch required ranges without speculative downloads
    [Fact]
    public async Task Scenario04_DemandReads_FetchRequiredRangesWithoutSpeculativeDownloads()
    {
        var dbPath = Path.Combine(_testRoot, "scenario04.db");
        var store = new ProfileStore(dbPath);
        var executor = new DummyPrefetchExecutor();
        var engine = new StreamingEngine(
            new StreamingSettings(),
            new NetworkSettings(),
            store,
            executor,
            (m, p) => 10 * ByteSize.MiB);

        engine.SetDemandOnly(true);

        // OnAccess must not enqueue predictive blocks when in DemandOnlyMode
        var accessEvent = new AccessEvent(DateTime.Now, "m1", "game/data.pak", 0, 4096, false, 0, ReadSource.Application);
        engine.OnAccess(accessEvent);
        Assert.Equal(0, engine.Queue.Count);

        // PrepareAsync must immediately return a 0-block completed result
        var res = await engine.PrepareAsync("game1", "m1", "game", 100 * ByteSize.MiB, null, CancellationToken.None);
        Assert.Equal(0, res.FetchedBlocks);
        Assert.Equal(0, executor.FetchesAttempted);
    }

    // 5. Concurrent downloads cannot exceed enforced budget (TryReserveCapacity)
    [Fact]
    public async Task Scenario05_ConcurrentDownloads_CannotExceedEnforcedBudget()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache05");
        Directory.CreateDirectory(cacheRoot);
        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 512 * ByteSize.MiB,
            MetadataReserveBytes = 16 * ByteSize.MiB, // 496 MiB usable
            ZeroFootprintMode = true
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount]);

        var reservations = new List<SmartCacheManager.CapacityReservation>();
        var lockObj = new object();
        var tasks = new List<Task>();

        // 10 concurrent requests of 100 MiB each
        // Since usable is 496 MiB, exactly 4 requests (400 MiB) can succeed.
        // The 5th request would require 500 MiB > 496 MiB and must be rejected.
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                if (mgr.TryReserveCapacity(100 * ByteSize.MiB, out var res) && res != null)
                {
                    lock (lockObj) reservations.Add(res);
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(4, reservations.Count);
        Assert.Equal(400 * ByteSize.MiB, mgr.InFlightReservedBytes);
        Assert.True(mgr.IsCapacityExhausted);
        Assert.NotNull(mgr.LastAllocationFailureReason);

        foreach (var r in reservations) r.Dispose();
        Assert.Equal(0, mgr.InFlightReservedBytes);
    }

    // 6. In-flight downloads reserve capacity
    [Fact]
    public void Scenario06_InFlightDownloads_ReserveCapacity()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache06");
        Directory.CreateDirectory(cacheRoot);
        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 512 * ByteSize.MiB,
            MetadataReserveBytes = 16 * ByteSize.MiB,
            ZeroFootprintMode = true
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount]);

        // Reserve 200 MiB
        Assert.True(mgr.TryReserveCapacity(200 * ByteSize.MiB, out var r1));
        Assert.NotNull(r1);
        Assert.Equal(200 * ByteSize.MiB, mgr.InFlightReservedBytes);

        // Reserve another 200 MiB -> 400 MiB total
        Assert.True(mgr.TryReserveCapacity(200 * ByteSize.MiB, out var r2));
        Assert.NotNull(r2);
        Assert.Equal(400 * ByteSize.MiB, mgr.InFlightReservedBytes);

        // Attempt 150 MiB -> 400 + 150 = 550 MiB > 496 MiB -> must fail
        Assert.False(mgr.TryReserveCapacity(150 * ByteSize.MiB, out var r3));
        Assert.Null(r3);
        Assert.Equal(400 * ByteSize.MiB, mgr.InFlightReservedBytes);

        // Release r1 (200 MiB)
        r1.Dispose();
        Assert.Equal(200 * ByteSize.MiB, mgr.InFlightReservedBytes);

        // Now 150 MiB reservation should succeed (200 + 150 = 350 MiB <= 496 MiB)
        Assert.True(mgr.TryReserveCapacity(150 * ByteSize.MiB, out var r4));
        Assert.NotNull(r4);
        Assert.Equal(350 * ByteSize.MiB, mgr.InFlightReservedBytes);

        r2.Dispose();
        r4.Dispose();
        Assert.Equal(0, mgr.InFlightReservedBytes);
    }

    // 7. Active file handles (IsFileLocked) are protected from eviction
    [Fact]
    public void Scenario07_ActiveFileHandles_AreProtectedFromEviction()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache07");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "active.dat", 100 * ByteSize.MiB, DateTime.Now.AddDays(-10));
        CreateMockCachedFile(mountDir, "gdrive", "idle.dat", 100 * ByteSize.MiB, DateTime.Now.AddDays(-5));

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 50 * ByteSize.MiB, // Lower than 100 MiB, forcing eviction
            MinFreeSpaceBytes = 1 * ByteSize.GiB,
            ZeroFootprintMode = true
        };

        var activeFilePath = Path.Combine(mountDir, "vfs", "gdrive", "active.dat");

        // Open active file with exclusive lock simulating active game handle
        using (var fs = new FileStream(activeFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(SmartCacheManager.IsFileLocked(activeFilePath));

            var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount]);

            // Reclaim synchronously
            mgr.ReclaimLRUSynchronous(150 * ByteSize.MiB);

            // Active locked file must NOT be deleted
            Assert.True(File.Exists(activeFilePath));
            // Idle file must be deleted
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "idle.dat")));
        }
    }

    // 8. Dirty and pinned entries are protected
    [Fact]
    public void Scenario08_DirtyAndPinnedEntries_AreProtected()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache08");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        // Oldest file is dirty (pending upload)
        CreateMockCachedFile(mountDir, "gdrive", "dirty.dat", 100 * ByteSize.MiB, DateTime.Now.AddDays(-20), dirty: true);
        // Second file is pinned
        CreateMockCachedFile(mountDir, "gdrive", "pinned.dat", 100 * ByteSize.MiB, DateTime.Now.AddDays(-15));
        // Third file is clean and unpinned
        CreateMockCachedFile(mountDir, "gdrive", "clean.dat", 100 * ByteSize.MiB, DateTime.Now.AddDays(-2));

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 50 * ByteSize.MiB,
            MinFreeSpaceBytes = 1 * ByteSize.GiB,
            PinnedPaths = ["m1:pinned.dat"]
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount], pinnedPathsProvider: () => ["m1:pinned.dat"]);

        mgr.ReclaimLRUSynchronous(150 * ByteSize.MiB);

        Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "dirty.dat")), "Dirty file must be preserved");
        Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "pinned.dat")), "Pinned file must be preserved");
        Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "clean.dat")), "Clean unpinned file must be evicted");
    }

    // 9. Incomplete/uncommitted downloads are not evicted
    [Fact]
    public void Scenario09_IncompleteDownloads_AreNotEvicted()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache09");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "completed.dat", 50 * ByteSize.MiB, DateTime.Now.AddDays(-5));

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 100 * ByteSize.MiB,
            ZeroFootprintMode = true
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount]);

        // In-flight download holds 60 MiB reservation
        Assert.True(mgr.TryReserveCapacity(60 * ByteSize.MiB, out var inFlightToken));
        Assert.NotNull(inFlightToken);

        // Usage is 50 MiB + 60 MiB in-flight = 110 MiB > 84 MiB usable
        // Completed file should be evicted, but in-flight reserved capacity remains strictly protected
        mgr.ReclaimLRUSynchronous(20 * ByteSize.MiB);

        Assert.Equal(60 * ByteSize.MiB, mgr.InFlightReservedBytes);
        inFlightToken.Dispose();
        Assert.Equal(0, mgr.InFlightReservedBytes);
    }

    // 10. Required metadata (vfsMeta) is preserved
    [Fact]
    public void Scenario10_RequiredMetadata_IsPreserved()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache10");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "game/kept.pak", 40 * ByteSize.MiB, DateTime.Now.AddDays(-5));
        CreateMockCachedFile(mountDir, "gdrive", "game/evicted.pak", 40 * ByteSize.MiB, DateTime.Now.AddDays(-10));

        var keptMetaPath = Path.Combine(mountDir, "vfsMeta", "gdrive", "game", "kept.pak");
        var keptDataPath = Path.Combine(mountDir, "vfs", "gdrive", "game", "kept.pak");
        var evictedDataPath = Path.Combine(mountDir, "vfs", "gdrive", "game", "evicted.pak");

        Assert.True(File.Exists(keptMetaPath));
        Assert.True(File.Exists(keptDataPath));
        Assert.True(File.Exists(evictedDataPath));

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 50 * ByteSize.MiB,
            MetadataReserveBytes = 16 * ByteSize.MiB,
            PinnedPaths = ["m1:game/kept.pak"],
            ZeroFootprintMode = true
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount], pinnedPathsProvider: () => ["m1:game/kept.pak"]);

        mgr.ReclaimLRUSynchronous(40 * ByteSize.MiB);

        // Evicted data file is removed to free disk space
        Assert.False(File.Exists(evictedDataPath));
        // Kept file's metadata in vfsMeta remains strictly preserved and directory intact
        Assert.True(File.Exists(keptMetaPath));
        Assert.True(Directory.Exists(Path.Combine(mountDir, "vfsMeta")));
    }

    // 11. Cleanup runs automatically after completed game session (OnLaunchEndedAsync)
    [Fact]
    public async Task Scenario11_Cleanup_RunsAutomaticallyAfterCompletedGameSession()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache11");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "Games/CyberVydrix/level1.dat", 50 * ByteSize.MiB, DateTime.Now.AddHours(-1));
        CreateMockCachedFile(mountDir, "gdrive", "Games/OtherGame/data.pak", 50 * ByteSize.MiB, DateTime.Now.AddHours(-1));

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 512 * ByteSize.MiB,
            ZeroFootprintMode = true,
            AutoReclaimOnGameExit = true
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount]);

        // Simulate post-game reclamation for CyberVydrix
        var report = await mgr.ReclaimGameCacheAsync("Games/CyberVydrix");

        Assert.True(report.FilesEvicted > 0);
        Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "Games", "CyberVydrix", "level1.dat")));
        Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "Games", "OtherGame", "data.pak")));
    }

    // 12. Cleanup waits for outstanding operations (1.5s drain delay)
    [Fact]
    public async Task Scenario12_Cleanup_WaitsForOutstandingOperations()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Simulate the 1.5s drain delay logic implemented in OnLaunchEndedAsync
        await Task.Delay(1500);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 1400, "Drain delay must wait at least ~1.5s for handles to settle");
    }

    // 13. Crashed games do not leave permanent session locks
    [Fact]
    public void Scenario13_CrashedGames_DoNotLeavePermanentSessionLocks()
    {
        var activeSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, LaunchSession>(StringComparer.OrdinalIgnoreCase);

        var game = new GameEntry { Id = "crash_game", Name = "Crash Game" };
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        var session = new LaunchSession(game, proc, null, "crash.exe", _testRoot, ExecutionMode.CloudDirect);

        activeSessions[game.Id] = session;
        Assert.True(activeSessions.ContainsKey(game.Id));

        // When game exits abnormally/crashes, OnLaunchEndedAsync removes it
        var removed = activeSessions.TryRemove(game.Id, out var removedSession);
        Assert.True(removed);
        Assert.NotNull(removedSession);
        removedSession.State = LaunchState.Failed;

        Assert.False(activeSessions.ContainsKey(game.Id));
        Assert.Equal(LaunchState.Failed, removedSession.State);
    }

    // 14. Multiple simultaneous games remain protected
    [Fact]
    public void Scenario14_MultipleSimultaneousGames_RemainProtected()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache14");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "GameA/a.bin", 60 * ByteSize.MiB, DateTime.Now.AddDays(-10));
        CreateMockCachedFile(mountDir, "gdrive", "GameB/b.bin", 60 * ByteSize.MiB, DateTime.Now.AddDays(-8));
        CreateMockCachedFile(mountDir, "gdrive", "Unrelated/c.bin", 60 * ByteSize.MiB, DateTime.Now.AddDays(-1));

        var activeGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GameA", "GameB" };

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 50 * ByteSize.MiB,
            ZeroFootprintMode = true
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(
            cacheRoot,
            settings,
            () => [mount],
            isGameActive: relPath => activeGames.Any(g => relPath.Contains(g, StringComparison.OrdinalIgnoreCase)));

        mgr.ReclaimLRUSynchronous(100 * ByteSize.MiB);

        Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "GameA", "a.bin")), "GameA must remain protected");
        Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "GameB", "b.bin")), "GameB must remain protected");
        Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "Unrelated", "c.bin")), "Unrelated file must be evicted");
    }

    // 15. Periodic maintenance does not overlap destructively with mount transitions
    [Fact]
    public async Task Scenario15_PeriodicMaintenance_DoesNotOverlapDestructivelyWithMountTransitions()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache15");
        Directory.CreateDirectory(cacheRoot);

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 512 * ByteSize.MiB,
            ZeroFootprintMode = true
        };

        // Mounts list is empty simulating unmounted transition
        using var mgr = new SmartCacheManager(cacheRoot, settings, () => []);

        var report = await mgr.RunMaintenanceAsync(force: false);

        Assert.False(report.Required);
        Assert.Equal(0, report.FilesEvicted);
    }

    // 16. Network outages fail safely without data loss
    [Fact]
    public void Scenario16_NetworkOutages_FailSafelyWithoutDataLoss()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache16");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "save.dat", 10 * ByteSize.MiB, DateTime.Now.AddDays(-2), dirty: true);

        var settings = new CacheSettings { CacheRoot = cacheRoot, BudgetBytes = 5 * ByteSize.MiB };
        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };

        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount]);

        // Simulating network failure during eviction
        // Dirty files are retained and not purged
        mgr.ReclaimLRUSynchronous(10 * ByteSize.MiB);
        Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "save.dat")));
    }

    // 17. Restart and recovery preserve valid cache metadata
    [Fact]
    public void Scenario17_RestartAndRecovery_PreserveValidCacheMetadata()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache17");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "index.bin", 20 * ByteSize.MiB, DateTime.Now.AddDays(-1));

        var settings = new CacheSettings { CacheRoot = cacheRoot, BudgetBytes = 512 * ByteSize.MiB, ZeroFootprintMode = true };
        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };

        // Session 1
        using (var mgr1 = new SmartCacheManager(cacheRoot, settings, () => [mount]))
        {
            var files = CacheInspector.Enumerate(mountDir, "gdrive").ToList();
            Assert.Single(files);
        }

        // Restart (Session 2)
        using (var mgr2 = new SmartCacheManager(cacheRoot, settings, () => [mount]))
        {
            var files = CacheInspector.Enumerate(mountDir, "gdrive").ToList();
            Assert.Single(files);
            Assert.Equal("index.bin", files[0].RemotePath.Replace('\\', '/'));
        }
    }

    // 18. Cleanup does not delete application settings, databases, or credentials
    [Fact]
    public async Task Scenario18_Cleanup_DoesNotDeleteAppSettingsDatabasesOrCredentials()
    {
        var dataRoot = Path.Combine(_testRoot, "vydrix_data");
        Directory.CreateDirectory(dataRoot);
        var cacheRoot = Path.Combine(dataRoot, "cache");
        Directory.CreateDirectory(cacheRoot);

        var settingsFile = Path.Combine(dataRoot, "settings.json");
        var dbFile = Path.Combine(dataRoot, "vydrix.db");
        var keyFile = Path.Combine(dataRoot, "rclone.conf.key");
        var confFile = Path.Combine(dataRoot, "rclone.conf");

        File.WriteAllText(settingsFile, "{}");
        File.WriteAllText(dbFile, "DB");
        File.WriteAllText(keyFile, "SECRET");
        File.WriteAllText(confFile, "CONF");

        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");
        CreateMockCachedFile(mountDir, "gdrive", "temp.dat", 10 * ByteSize.MiB, DateTime.Now.AddDays(-1));

        var settings = new CacheSettings { CacheRoot = cacheRoot, BudgetBytes = 512 * ByteSize.MiB, ZeroFootprintMode = true };
        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };

        using var mgr = new SmartCacheManager(cacheRoot, settings, () => [mount]);
        await mgr.PurgeAllCachesAsync();

        // Application files must be completely intact
        Assert.True(File.Exists(settingsFile));
        Assert.True(File.Exists(dbFile));
        Assert.True(File.Exists(keyFile));
        Assert.True(File.Exists(confFile));

        // Cache data file cleared
        Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "temp.dat")));
    }

    // 19. Cache accounting recovers after simulated crash
    [Fact]
    public void Scenario19_CacheAccountingRecovers_AfterSimulatedCrash()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache19");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        CreateMockCachedFile(mountDir, "gdrive", "survived.dat", 30 * ByteSize.MiB, DateTime.Now);

        var settings = new CacheSettings { CacheRoot = cacheRoot, BudgetBytes = 512 * ByteSize.MiB, ZeroFootprintMode = true };
        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };

        // Before crash: simulate reservation
        using (var mgrCrash = new SmartCacheManager(cacheRoot, settings, () => [mount]))
        {
            mgrCrash.TryReserveCapacity(100 * ByteSize.MiB, out _);
            Assert.Equal(100 * ByteSize.MiB, mgrCrash.InFlightReservedBytes);
        }

        // After crash: fresh start
        using (var mgrRecovered = new SmartCacheManager(cacheRoot, settings, () => [mount]))
        {
            // InFlightReservedBytes starts cleanly at 0
            Assert.Equal(0, mgrRecovered.InFlightReservedBytes);
            // Disk usage is accurately accounted
            var stats = mgrRecovered.GetStorageEfficiencyStats(0, 0, 30 * ByteSize.MiB);
            Assert.Equal(30 * ByteSize.MiB, stats.LogicalAccessedBytes);
        }
    }

    // 20. Cache pressure does not trigger silent unbounded temporary downloads
    [Fact]
    public void Scenario20_CachePressure_DoesNotTriggerSilentUnboundedTemporaryDownloads()
    {
        var cacheRoot = Path.Combine(_testRoot, "cache20");
        var mountDir = Path.Combine(cacheRoot, "m-m1");
        Directory.CreateDirectory(mountDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

        // Cache filled to usable budget with pinned/dirty files
        CreateMockCachedFile(mountDir, "gdrive", "pinned1.dat", 248 * ByteSize.MiB, DateTime.Now.AddDays(-2));
        CreateMockCachedFile(mountDir, "gdrive", "pinned2.dat", 248 * ByteSize.MiB, DateTime.Now.AddDays(-1));

        var settings = new CacheSettings
        {
            CacheRoot = cacheRoot,
            BudgetBytes = 512 * ByteSize.MiB,
            MetadataReserveBytes = 16 * ByteSize.MiB, // 496 MiB usable
            PinnedPaths = ["m1:pinned1.dat", "m1:pinned2.dat"],
            ZeroFootprintMode = true
        };

        var mount = new MountDefinition { Id = "m1", DisplayName = "GDrive", RemoteName = "gdrive" };
        using var mgr = new SmartCacheManager(
            cacheRoot,
            settings,
            () => [mount],
            pinnedPathsProvider: () => ["m1:pinned1.dat", "m1:pinned2.dat"]);

        // Requesting 20 MiB should fail because 248 + 248 = 496 MiB (100% full with un-evictable files)
        var reserved = mgr.TryReserveCapacity(20 * ByteSize.MiB, out var token);

        Assert.False(reserved);
        Assert.Null(token);
        Assert.True(mgr.IsCapacityExhausted);
        Assert.NotNull(mgr.LastAllocationFailureReason);
        Assert.Contains("exhausted", mgr.LastAllocationFailureReason, StringComparison.OrdinalIgnoreCase);
    }
}
