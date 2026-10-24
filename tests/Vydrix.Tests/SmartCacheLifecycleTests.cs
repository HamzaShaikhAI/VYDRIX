using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Vydrix.Cache;
using Vydrix.Core;
using Xunit;

namespace Vydrix.Tests;

public class SmartCacheLifecycleTests
{
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
            Fingerprint = "test-fp",
            Rs = new[] { new { Pos = 0L, Size = size } }
        };
        File.WriteAllText(metaFile, JsonSerializer.Serialize(dto));

        using var fs = new FileStream(dataFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        fs.SetLength(size);
    }

    [Fact]
    public async Task Test01_Lightweight_check_skips_eviction_when_budget_and_reserve_satisfied()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test01");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            CreateMockCachedFile(mountDir, "gdrive", "file1.dat", 1024 * 1024, DateTime.Now.AddDays(-2));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 50 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(
                tempDir.FullName,
                settings,
                () => [mount]);

            var report = await mgr.RunMaintenanceAsync(force: false);

            Assert.False(report.Required);
            Assert.Equal(0, report.FilesEvicted);
            Assert.Equal(0, report.BytesReclaimed);
            Assert.Contains("within limits", report.Reason);
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "file1.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test02_Enforces_cache_budget_when_exceeded()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test02");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            // 3 files of 2 MB each = 6 MB total
            CreateMockCachedFile(mountDir, "gdrive", "old.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-10));
            CreateMockCachedFile(mountDir, "gdrive", "mid.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-5));
            CreateMockCachedFile(mountDir, "gdrive", "new.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-1));

            // Budget is 3 MB -> needs to reclaim at least 3 MB
            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 3 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            var report = await mgr.RunMaintenanceAsync(force: false);

            Assert.True(report.Required);
            Assert.True(report.FilesEvicted >= 2);
            Assert.True(report.BytesReclaimed >= 3 * ByteSize.MiB);

            // Oldest files should have been evicted first
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "old.dat")));
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "mid.dat")));
            // Newest file should remain
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "new.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test03_Relieves_disk_pressure_when_volume_reserve_breached()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test03");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            CreateMockCachedFile(mountDir, "gdrive", "file.dat", 1024 * 1024, DateTime.Now.AddDays(-3));

            // Set reserve ridiculously high to simulate disk pressure (larger than total volume free)
            var (free, _) = DiskInfo.FreeSpace(tempDir.FullName);
            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 100 * ByteSize.GiB,
                MinFreeSpaceBytes = free + 10 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            var report = await mgr.RunMaintenanceAsync(force: false);

            Assert.True(report.Required);
            Assert.Equal(1, report.FilesEvicted);
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "file.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test04_Evicts_in_strict_LRU_order()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test04");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            var t0 = DateTime.Now.AddDays(-20);
            var t1 = DateTime.Now.AddDays(-10);
            var t2 = DateTime.Now.AddDays(-1);

            CreateMockCachedFile(mountDir, "gdrive", "file_t0.dat", 2 * ByteSize.MiB, t0);
            CreateMockCachedFile(mountDir, "gdrive", "file_t1.dat", 2 * ByteSize.MiB, t1);
            CreateMockCachedFile(mountDir, "gdrive", "file_t2.dat", 2 * ByteSize.MiB, t2);

            // Budget of 5 MB requires reclaiming 1 MB -> exactly 1 file (2 MB) must be evicted: file_t0
            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 5 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            var report = await mgr.RunMaintenanceAsync(force: false);

            Assert.Equal(1, report.FilesEvicted);
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "file_t0.dat")));
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "file_t1.dat")));
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "file_t2.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test05_Strictly_protects_pinned_files()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test05");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            // Oldest file is pinned
            CreateMockCachedFile(mountDir, "gdrive", "important.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-30));
            CreateMockCachedFile(mountDir, "gdrive", "ordinary.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-5));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 3 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB,
                PinnedPaths = ["mount1:important.dat"]
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(
                tempDir.FullName,
                settings,
                () => [mount],
                pinnedPathsProvider: () => ["mount1:important.dat"]);

            var report = await mgr.RunMaintenanceAsync(force: false);

            // Pinned file must NOT be deleted even though it is older
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "important.dat")));
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "ordinary.dat")));
            Assert.Equal(1, report.FilesEvicted);
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test06_Strictly_protects_dirty_files()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test06");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            // Oldest file is dirty (pending upload)
            CreateMockCachedFile(mountDir, "gdrive", "pending_upload.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-30), dirty: true);
            CreateMockCachedFile(mountDir, "gdrive", "clean.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-5), dirty: false);

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 3 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            var report = await mgr.RunMaintenanceAsync(force: false);

            // Dirty file must be strictly preserved!
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "pending_upload.dat")));
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "clean.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test07_Strictly_protects_active_game_files()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test07");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            // Oldest file belongs to active game
            CreateMockCachedFile(mountDir, "gdrive", "ActiveGame/assets.pak", 2 * ByteSize.MiB, DateTime.Now.AddDays(-20));
            CreateMockCachedFile(mountDir, "gdrive", "OtherGame/other.pak", 2 * ByteSize.MiB, DateTime.Now.AddDays(-5));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 3 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(
                tempDir.FullName,
                settings,
                () => [mount],
                isGameActive: path => path.StartsWith("ActiveGame", StringComparison.OrdinalIgnoreCase));

            var report = await mgr.RunMaintenanceAsync(force: false);

            // Active game file must not be evicted
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "ActiveGame", "assets.pak")));
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "OtherGame", "other.pak")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test08_Strictly_protects_locked_or_in_use_files()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test08");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            CreateMockCachedFile(mountDir, "gdrive", "locked.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-20));
            CreateMockCachedFile(mountDir, "gdrive", "unlocked.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-5));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 3 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var lockedPath = Path.Combine(mountDir, "vfs", "gdrive", "locked.dat");
            // Hold exclusive write lock on the oldest file
            using (var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
                using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

                var report = await mgr.RunMaintenanceAsync(force: false);

                // Locked file must be preserved
                Assert.True(File.Exists(lockedPath));
                Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "unlocked.dat")));
            }
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test09_Cleans_empty_parent_directories_after_eviction()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test09");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            CreateMockCachedFile(mountDir, "gdrive", "Nested/Deep/Dir/lonely.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-10));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 1 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            var report = await mgr.RunMaintenanceAsync(force: false);

            Assert.Equal(1, report.FilesEvicted);
            // The empty directories should be removed cleanly
            Assert.False(Directory.Exists(Path.Combine(mountDir, "vfs", "gdrive", "Nested", "Deep", "Dir")));
            Assert.False(Directory.Exists(Path.Combine(mountDir, "vfsMeta", "gdrive", "Nested", "Deep", "Dir")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test10_Prunes_unpinned_expired_files_by_max_age()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test10");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            CreateMockCachedFile(mountDir, "gdrive", "expired.dat", 1 * ByteSize.MiB, DateTime.Now.AddDays(-45));
            CreateMockCachedFile(mountDir, "gdrive", "recent.dat", 1 * ByteSize.MiB, DateTime.Now.AddDays(-5));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 50 * ByteSize.GiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB,
                MaxAge = TimeSpan.FromDays(30)
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            var report = await mgr.PruneUnpinnedOlderThanAsync(TimeSpan.FromDays(30));

            Assert.Equal(1, report.FilesEvicted);
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "expired.dat")));
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "recent.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test11_Ignores_unmarked_directories()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test11");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            // DO NOT create .vydrix-cache marker file!

            CreateMockCachedFile(mountDir, "gdrive", "unmarked.dat", 2 * ByteSize.MiB, DateTime.Now.AddDays(-20));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 1 * ByteSize.MiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            var report = await mgr.RunMaintenanceAsync(force: true);

            // Must NOT touch unmarked directories!
            Assert.Equal(0, report.FilesEvicted);
            Assert.True(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "unmarked.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test12_Manual_maintenance_force_flag_triggers_sweep()
    {
        var tempDir = Directory.CreateTempSubdirectory("smartcache-test12");
        try
        {
            var mountDir = Path.Combine(tempDir.FullName, "m-mount1");
            Directory.CreateDirectory(mountDir);
            File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "vydrix");

            CreateMockCachedFile(mountDir, "gdrive", "old_expired.dat", 1 * ByteSize.MiB, DateTime.Now.AddDays(-40));

            var settings = new CacheSettings
            {
                CacheRoot = tempDir.FullName,
                BudgetBytes = 100 * ByteSize.GiB,
                MinFreeSpaceBytes = 1 * ByteSize.GiB,
                MaxAge = TimeSpan.FromDays(30)
            };

            var mount = new MountDefinition { Id = "mount1", DisplayName = "Test Mount", RemoteName = "gdrive" };
            using var mgr = new SmartCacheManager(tempDir.FullName, settings, () => [mount]);

            // Force = true bypasses the lightweight pre-check and triggers expired eviction
            var report = await mgr.RunMaintenanceAsync(force: true, trigger: "manual");

            Assert.True(report.Required);
            Assert.Equal(1, report.FilesEvicted);
            Assert.False(File.Exists(Path.Combine(mountDir, "vfs", "gdrive", "old_expired.dat")));
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }
}
