using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Cache;
using Vydrix.Core;
using Vydrix.Mount;
using Xunit;

namespace Vydrix.Tests;

public sealed class StorageNegativeArchitectureTests : IDisposable
{
    private readonly string _testRoot;

    public StorageNegativeArchitectureTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "vydrix-test-storage-neg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true); } catch { }
    }

    [Fact]
    public void StoragePolicy_Minimal_enforces_512MB_budget_and_short_max_age()
    {
        var settings = new CacheSettings
        {
            Profile = CachePolicyProfile.Minimal,
            BudgetBytes = 20 * ByteSize.GiB,
            MaxAge = TimeSpan.FromDays(30)
        };

        Assert.Equal(512 * ByteSize.MiB, settings.EffectiveBudgetForProfile());
        Assert.Equal(TimeSpan.FromHours(1), settings.EffectiveMaxAgeForProfile());
    }

    [Fact]
    public void StoragePolicy_Disabled_maps_to_vfs_cache_mode_off()
    {
        var settings = new CacheSettings
        {
            Profile = CachePolicyProfile.Disabled
        };

        Assert.Equal(0, settings.EffectiveBudgetForProfile());
        Assert.Equal(TimeSpan.Zero, settings.EffectiveMaxAgeForProfile());

        var input = new MountPlanInput(
            new MountDefinition { DisplayName = "Cloud", RemoteName = "gdrive" },
            'X',
            settings,
            new NetworkSettings(),
            Path.Combine(_testRoot, "cache"),
            Path.Combine(_testRoot, "log.txt"),
            settings.EffectiveBudgetForProfile(),
            false,
            5572,
            "drive");

        var rclone = new RcloneInfo("rclone.exe", "v1.75.2", new Version(1, 75, 2), new HashSet<string> { "vfs-cache-mode", "cache-dir" });
        var plan = MountFlagBuilder.Build(input, rclone);

        var args = plan.Arguments.ToList();
        var modeIdx = args.IndexOf("--vfs-cache-mode");
        Assert.True(modeIdx >= 0);
        Assert.Equal("off", args[modeIdx + 1]);
        Assert.DoesNotContain("--cache-dir", args);
    }

    [Fact]
    public void StoragePolicy_Minimal_configures_tight_rclone_flags()
    {
        var settings = new CacheSettings
        {
            Profile = CachePolicyProfile.Minimal
        };

        var input = new MountPlanInput(
            new MountDefinition { DisplayName = "Cloud", RemoteName = "gdrive" },
            'G',
            settings,
            new NetworkSettings(),
            Path.Combine(_testRoot, "cache"),
            Path.Combine(_testRoot, "log.txt"),
            settings.EffectiveBudgetForProfile(),
            false,
            5572,
            "drive");

        var flags = new HashSet<string> { "vfs-cache-mode", "cache-dir", "vfs-cache-max-size", "vfs-cache-max-age", "vfs-cache-poll-interval", "log-file-max-size" };
        var rclone = new RcloneInfo("rclone.exe", "v1.75.2", new Version(1, 75, 2), flags);
        var plan = MountFlagBuilder.Build(input, rclone);

        var args = plan.Arguments.ToList();
        var sizeIdx = args.IndexOf("--vfs-cache-max-size");
        Assert.True(sizeIdx >= 0);
        Assert.Equal("512Mi", args[sizeIdx + 1]);

        var ageIdx = args.IndexOf("--vfs-cache-max-age");
        Assert.True(ageIdx >= 0);
        Assert.Equal("1h", args[ageIdx + 1]);

        var pollIdx = args.IndexOf("--vfs-cache-poll-interval");
        Assert.True(pollIdx >= 0);
        Assert.Equal("15s", args[pollIdx + 1]);

        // Capped log size
        var logSizeIdx = args.IndexOf("--log-file-max-size");
        Assert.True(logSizeIdx >= 0);
        Assert.Equal("8Mi", args[logSizeIdx + 1]);
    }

    [Fact]
    public void VolumeValidator_validates_ntfs_volume_and_sparse_files()
    {
        var targetDir = Path.Combine(_testRoot, "valid-cache");
        var res = VolumeValidator.Validate(targetDir, 512 * ByteSize.MiB, 1 * ByteSize.GiB);

        Assert.True(res.IsValid);
        Assert.True(res.SupportsSparseFiles);
        Assert.True(res.HasWritePermission);
        Assert.False(string.IsNullOrEmpty(res.VolumeRoot));
    }

    [Fact]
    public void VolumeValidator_rejects_empty_or_invalid_path()
    {
        var resEmpty = VolumeValidator.Validate("");
        Assert.False(resEmpty.IsValid);
        Assert.Contains(resEmpty.Errors, e => e.Contains("empty"));

        var resInvalid = VolumeValidator.Validate("Z:\\NonExistentVolume_9999\\cache");
        Assert.False(resInvalid.IsValid);
    }

    [Fact]
    public void VolumeValidator_warns_on_system_drive_c()
    {
        var targetDir = @"C:\Users\win10\AppData\Local\Vydrix\cache";
        var res = VolumeValidator.Validate(targetDir, 512 * ByteSize.MiB, 1 * ByteSize.GiB);

        Assert.Contains(res.Warnings, w => w.Contains("Windows system volume (C:)"));
    }

    [Fact]
    public async Task CacheMigrationService_blocks_migration_when_mounts_are_active()
    {
        var source = Path.Combine(_testRoot, "src-cache");
        var dest = Path.Combine(_testRoot, "dst-cache");
        Directory.CreateDirectory(source);

        var service = new CacheMigrationService();
        var settings = new AppSettings();
        var store = new JsonSettingsStore(Path.Combine(_testRoot, "settings.json"));

        var res = await service.MigrateAsync(source, dest, settings, store, hasActiveMounts: () => true);

        Assert.False(res.Success);
        Assert.Contains("actively mounted", res.Message);
    }

    [Fact]
    public async Task CacheMigrationService_migrates_cache_with_validation_and_atomically_updates_settings()
    {
        var source = Path.Combine(_testRoot, "src-cache");
        var dest = Path.Combine(_testRoot, "dst-cache");
        var mountDir = Path.Combine(source, "m-test01");
        Directory.CreateDirectory(Path.Combine(mountDir, "vfs"));
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "VYDRIX_CACHE");
        File.WriteAllText(Path.Combine(mountDir, "vfs", "game.pak"), "MOCK_GAME_ASSETS_12345");

        var service = new CacheMigrationService();
        var settings = new AppSettings { Cache = new CacheSettings { CacheRoot = source } };
        var settingsPath = Path.Combine(_testRoot, "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        store.Save(settings);

        var res = await service.MigrateAsync(source, dest, settings, store, hasActiveMounts: () => false);

        Assert.True(res.Success);
        Assert.Equal(dest, settings.Cache.CacheRoot);
        Assert.True(File.Exists(Path.Combine(dest, "m-test01", "vfs", "game.pak")));

        // Verify settings stored atomically
        var loaded = store.Load();
        Assert.Equal(dest, loaded.Cache.CacheRoot);
    }

    [Fact]
    public void DiskSafetyGuard_low_space_mode_scales_down_budget_when_free_space_approaches_reserve()
    {
        long reserve = 8 * ByteSize.GiB;
        long requestedBudget = 10 * ByteSize.GiB;
        long volumeFree = 10 * ByteSize.GiB; // Only 2 GB above reserve
        long currentCache = 500 * ByteSize.MiB;

        // With LowSpaceAdaptiveMode enabled
        var decisionAdaptive = DiskSafetyGuard.Decide(requestedBudget, reserve, volumeFree, currentCache, 1, lowSpaceAdaptive: true);

        Assert.True(decisionAdaptive.BudgetReduced);
        Assert.True(decisionAdaptive.EffectiveMaxBytes <= 2 * ByteSize.GiB);
        Assert.Contains(decisionAdaptive.Warnings, w => w.Contains("Low-space mode active"));
    }

    [Fact]
    public async Task SmartCacheManager_detects_capacity_exhaustion_when_all_candidates_are_protected()
    {
        var cacheRoot = Path.Combine(_testRoot, "exhaust-cache");
        var mountDir = Path.Combine(cacheRoot, "m-test");
        var vfsDir = Path.Combine(mountDir, "vfs", "cloud");
        Directory.CreateDirectory(vfsDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "VYDRIX");

        // Create a pinned file that occupies space
        var pinnedFile = Path.Combine(vfsDir, "important.pak");
        File.WriteAllText(pinnedFile, new string('A', 1000));

        var metaDir = Path.Combine(mountDir, "vfsMeta", "cloud");
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "important.pak"), "{}");

        var settings = new CacheSettings
        {
            BudgetBytes = 500, // Budget is less than file size
            MinFreeSpaceBytes = 100,
            PinnedPaths = ["test:cloud/important.pak"]
        };

        var mount = new MountDefinition { Id = "test", DisplayName = "Cloud", RemotePath = "" };
        var mgr = new SmartCacheManager(
            cacheRoot,
            settings,
            () => [mount],
            isMountActive: _ => false,
            isGameActive: _ => false,
            pinnedPathsProvider: () => ["test:cloud/important.pak"]);

        var report = await mgr.RunMaintenanceAsync(force: true);

        Assert.True(mgr.IsCapacityExhausted);
        Assert.NotNull(mgr.CapacityExhaustionMessage);
        Assert.Contains("capacity exhausted", mgr.CapacityExhaustionMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, report.FilesEvicted); // Pinned file was protected!
    }

    [Fact]
    public async Task SmartCacheManager_reclaims_transient_game_cache_on_game_exit()
    {
        var cacheRoot = Path.Combine(_testRoot, "game-exit-cache");
        var mountDir = Path.Combine(cacheRoot, "m-gamedrive");
        var vfsDir = Path.Combine(mountDir, "vfs", "gamedrive", "DoomGame");
        Directory.CreateDirectory(vfsDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "VYDRIX");

        var transientData = Path.Combine(vfsDir, "level01.pak");
        File.WriteAllText(transientData, "MOCK_LEVEL_DATA");

        var metaDir = Path.Combine(mountDir, "vfsMeta", "gamedrive", "DoomGame");
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "level01.pak"), "{}");

        var settings = new CacheSettings { AutoReclaimOnGameExit = true };
        var mount = new MountDefinition { Id = "gamedrive", DisplayName = "GDrive", RemotePath = "" };

        var mgr = new SmartCacheManager(
            cacheRoot,
            settings,
            () => [mount],
            isMountActive: _ => false,
            isGameActive: _ => false);

        var report = await mgr.ReclaimGameCacheAsync("DoomGame");

        Assert.True(report.FilesEvicted >= 1);
        Assert.False(File.Exists(transientData)); // Transient level data evicted
    }

    [Fact]
    public async Task SmartCacheManager_purges_all_caches_in_zero_footprint_mode()
    {
        var cacheRoot = Path.Combine(_testRoot, "zero-footprint-cache");
        var mountDir = Path.Combine(cacheRoot, "m-purge");
        var vfsDir = Path.Combine(mountDir, "vfs", "remote");
        Directory.CreateDirectory(vfsDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "VYDRIX");
        File.WriteAllText(Path.Combine(vfsDir, "chunk.bin"), "DATA_CHUNK");

        var settings = new CacheSettings { PurgeCacheOnExit = true };
        var mount = new MountDefinition { Id = "purge", DisplayName = "PurgeMount", RemotePath = "" };

        var mgr = new SmartCacheManager(
            cacheRoot,
            settings,
            () => [mount],
            isMountActive: _ => false,
            isGameActive: _ => false);

        var report = await mgr.PurgeAllCachesAsync();

        Assert.True(report.FilesEvicted >= 1);
        Assert.False(Directory.Exists(vfsDir)); // Entire VFS subfolder purged
    }

    [Fact]
    public void StorageEfficiencyStats_measures_allocated_and_avoided_storage_honestly()
    {
        var cacheRoot = Path.Combine(_testRoot, "stats-cache");
        Directory.CreateDirectory(cacheRoot);

        var settings = new CacheSettings();
        var mgr = new SmartCacheManager(cacheRoot, settings, () => []);

        long logicalAccessed = 25 * ByteSize.GiB;
        long networkDownloaded = 2 * ByteSize.GiB;
        double hitRatio = 0.88;

        var stats = mgr.GetStorageEfficiencyStats(networkDownloaded, hitRatio, logicalAccessed);

        Assert.Equal(logicalAccessed, stats.LogicalAccessedBytes);
        Assert.Equal(logicalAccessed, stats.AvoidedStorageBytes); // Since physical on disk is 0, avoided is full logical
        Assert.Equal(networkDownloaded, stats.NetworkBytesDownloaded);
        Assert.Equal(hitRatio, stats.CacheHitRatio);
    }

    [Fact]
    public async Task StorageReclamationAdvisor_classifies_game_assets_as_eligible_and_saves_configs_as_local()
    {
        var scanFolder = Path.Combine(_testRoot, "LocalGameFolder");
        Directory.CreateDirectory(Path.Combine(scanFolder, "bin"));
        Directory.CreateDirectory(Path.Combine(scanFolder, "content"));
        Directory.CreateDirectory(Path.Combine(scanFolder, "saves"));

        File.WriteAllText(Path.Combine(scanFolder, "bin", "game.exe"), "EXE_BYTES");
        File.WriteAllText(Path.Combine(scanFolder, "bin", "engine.dll"), "DLL_BYTES");
        File.WriteAllText(Path.Combine(scanFolder, "saves", "slot1.sav"), "SAVE_DATA");
        File.WriteAllText(Path.Combine(scanFolder, "config.ini"), "CONFIG");

        // Large asset file
        var bigAsset = Path.Combine(scanFolder, "content", "textures.pak");
        using (var fs = new FileStream(bigAsset, FileMode.Create))
        {
            fs.SetLength(5 * ByteSize.MiB);
        }

        var advisor = new StorageReclamationAdvisor();
        var res = await advisor.ScanSelectedDirectoryAsync(scanFolder);

        Assert.True(res.EligibleItems.Count >= 1);
        Assert.Contains(res.EligibleItems, i => i.RelativePath.EndsWith("textures.pak"));

        // Verify executables and saves are protected
        Assert.Contains(res.IneligibleItems, i => i.RelativePath.EndsWith("game.exe"));
        Assert.Contains(res.IneligibleItems, i => i.RelativePath.EndsWith("slot1.sav"));
        Assert.True(res.EligibleBytes >= 5 * ByteSize.MiB);
    }
}
