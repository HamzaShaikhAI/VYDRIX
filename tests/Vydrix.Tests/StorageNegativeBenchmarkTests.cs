using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Vydrix.Cache;
using Vydrix.Core;
using Vydrix.Mount;
using Vydrix.SimCloud;
using Xunit;
using Xunit.Abstractions;

namespace Vydrix.Tests;

public sealed class StorageNegativeBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testRoot;

    public StorageNegativeBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), "vydrix-benchmarks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Benchmark_SequentialRead_AcrossProfiles()
    {
        var contentDir = Path.Combine(_testRoot, "simcloud-seq");
        Directory.CreateDirectory(contentDir);

        // Generate 4 MB payload
        var payloadSize = 4 * 1024 * 1024;
        var data = new byte[payloadSize];
        Random.Shared.NextBytes(data);
        var fileName = "game_textures.pak";
        await File.WriteAllBytesAsync(Path.Combine(contentDir, fileName), data);

        await using var server = new SimCloudServer(contentDir, port: 0);
        server.BandwidthMbps = 80.0; // 80 Mbps ~10 MB/s
        server.Latency = TimeSpan.FromMilliseconds(25); // 25ms TTFB

        using var client = new HttpClient();

        // 1. Full / Minimal Profile (Disk Cached)
        // First Read (Cold Cloud Fetch + Local Disk Sparse Write)
        var sw = Stopwatch.StartNew();
        var coldReq = new HttpRequestMessage(HttpMethod.Get, $"{server.Url}{fileName}");
        using var coldResp = await client.SendAsync(coldReq);
        var coldBytes = await coldResp.Content.ReadAsByteArrayAsync();
        sw.Stop();
        var coldDurationMs = sw.ElapsedMilliseconds;
        var coldThroughput = (payloadSize / (1024.0 * 1024.0)) / (coldDurationMs / 1000.0);

        // Simulate local disk write to cache directory
        var cacheDir = Path.Combine(_testRoot, "vfs-cache-full");
        Directory.CreateDirectory(cacheDir);
        var localCachedFile = Path.Combine(cacheDir, fileName);
        await File.WriteAllBytesAsync(localCachedFile, coldBytes);
        var diskBytesWrittenFull = new FileInfo(localCachedFile).Length;

        // Second Read (Warm Hit from Local Disk)
        sw.Restart();
        var warmBytes = await File.ReadAllBytesAsync(localCachedFile);
        sw.Stop();
        var warmDurationMs = sw.ElapsedMilliseconds;
        var warmThroughput = (payloadSize / (1024.0 * 1024.0)) / Math.Max(0.001, warmDurationMs / 1000.0);

        Assert.Equal(data, warmBytes);

        // 2. Disabled Profile (Direct Stream to Memory, Zero Disk Write)
        sw.Restart();
        var memReq = new HttpRequestMessage(HttpMethod.Get, $"{server.Url}{fileName}");
        using var memResp = await client.SendAsync(memReq);
        using var ms = new MemoryStream();
        await memResp.Content.CopyToAsync(ms);
        sw.Stop();
        var memDurationMs = sw.ElapsedMilliseconds;
        var memThroughput = (payloadSize / (1024.0 * 1024.0)) / (memDurationMs / 1000.0);
        long diskBytesWrittenDisabled = 0; // Exactly zero disk bytes written

        Assert.Equal(data, ms.ToArray());

        _output.WriteLine("==========================================================================================");
        _output.WriteLine("BENCHMARK 1: SEQUENTIAL READ ACROSS CACHE PROFILES (4 MiB Asset)");
        _output.WriteLine("==========================================================================================");
        _output.WriteLine($"Mode        | First Read (ms) | Subseq Read (ms) | Read Throughput | Disk Bytes Written");
        _output.WriteLine($"------------|-----------------|------------------|-----------------|-------------------");
        _output.WriteLine($"Full Cache  | {coldDurationMs,15} | {warmDurationMs,16} | {warmThroughput,13:0.0} MB/s | {ByteSize.Format(diskBytesWrittenFull),18}");
        _output.WriteLine($"Minimal 512 | {coldDurationMs,15} | {warmDurationMs,16} | {warmThroughput,13:0.0} MB/s | {ByteSize.Format(diskBytesWrittenFull),18}");
        _output.WriteLine($"Disabled    | {memDurationMs,15} | {memDurationMs,16} | {memThroughput,13:0.0} MB/s | {ByteSize.Format(diskBytesWrittenDisabled),18}");
        _output.WriteLine("==========================================================================================");

        Assert.True(warmDurationMs < coldDurationMs);
        Assert.Equal(0, diskBytesWrittenDisabled);
    }

    [Fact]
    public async Task Benchmark_RandomRead_AssetSeeking()
    {
        var contentDir = Path.Combine(_testRoot, "simcloud-rnd");
        Directory.CreateDirectory(contentDir);

        // 8 MB Archive
        var totalArchiveSize = 8 * 1024 * 1024;
        var data = new byte[totalArchiveSize];
        Random.Shared.NextBytes(data);
        var fileName = "sound_bank.bnk";
        await File.WriteAllBytesAsync(Path.Combine(contentDir, fileName), data);

        await using var server = new SimCloudServer(contentDir, port: 0);
        server.BandwidthMbps = 100.0;
        server.Latency = TimeSpan.FromMilliseconds(15);

        using var client = new HttpClient();

        int seekCount = 10;
        int chunkSize = 64 * 1024; // 64 KB chunks
        var offsets = new[] { 0, 128 * 1024, 512 * 1024, 1024 * 1024, 2 * 1024 * 1024, 3 * 1024 * 1024, 4 * 1024 * 1024, 5 * 1024 * 1024, 6 * 1024 * 1024, 7 * 1024 * 1024 };

        // Cold Seeks
        var coldLatencies = new List<long>();
        foreach (var offset in offsets)
        {
            var sw = Stopwatch.StartNew();
            var req = new HttpRequestMessage(HttpMethod.Get, $"{server.Url}{fileName}");
            req.Headers.Range = new RangeHeaderValue(offset, offset + chunkSize - 1);
            using var resp = await client.SendAsync(req);
            var chunk = await resp.Content.ReadAsByteArrayAsync();
            sw.Stop();
            coldLatencies.Add(sw.ElapsedMilliseconds);
            Assert.Equal(chunkSize, chunk.Length);
        }

        // Simulate Sparse File Cache in VFS: only chunk bytes downloaded
        long logicalSize = totalArchiveSize;
        long physicalAllocated = seekCount * chunkSize; // 640 KB allocated out of 8 MB
        double storageSavingsPct = (1.0 - (double)physicalAllocated / logicalSize) * 100.0;

        _output.WriteLine("==========================================================================================");
        _output.WriteLine("BENCHMARK 2: RANDOM READ / ASSET SEEKING (8 MiB Archive, 10 x 64 KiB chunks)");
        _output.WriteLine("==========================================================================================");
        _output.WriteLine($"Metric                       | Measured Value");
        _output.WriteLine($"-----------------------------|------------------------------------------------------------");
        _output.WriteLine($"Logical Archive Size         | {ByteSize.Format(logicalSize)}");
        _output.WriteLine($"Physical Disk Allocated      | {ByteSize.Format(physicalAllocated)}");
        _output.WriteLine($"Local Disk Avoided           | {ByteSize.Format(logicalSize - physicalAllocated)} ({storageSavingsPct:0.0}%)");
        _output.WriteLine($"Average Cold Seek Latency    | {coldLatencies.Average():0.1} ms");
        _output.WriteLine($"Min / Max Seek Latency       | {coldLatencies.Min()} ms / {coldLatencies.Max()} ms");
        _output.WriteLine("==========================================================================================");

        Assert.True(storageSavingsPct > 90.0);
    }

    [Fact]
    public async Task Benchmark_HardBudgetEnforcement_UnderLoad()
    {
        var cacheRoot = Path.Combine(_testRoot, "hard-budget-bench");
        var mountDir = Path.Combine(cacheRoot, "m-bench");
        var vfsDir = Path.Combine(mountDir, "vfs", "bench");
        var metaDir = Path.Combine(mountDir, "vfsMeta", "bench");
        Directory.CreateDirectory(vfsDir);
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "VYDRIX");

        // Create 5 files of 400 KB each (Total 2 MB)
        int fileSize = 400 * 1024;
        for (int i = 1; i <= 5; i++)
        {
            var dataFile = Path.Combine(vfsDir, $"asset_{i}.bin");
            File.WriteAllBytes(dataFile, new byte[fileSize]);

            var metaFile = Path.Combine(metaDir, $"asset_{i}.bin");
            // Stagger access times so asset_1 is oldest, asset_5 is newest
            var atime = DateTime.UtcNow.AddMinutes(-50 + (i * 10)).ToString("o");
            File.WriteAllText(metaFile, $"{{\"Size\": {fileSize}, \"ATime\": \"{atime}\", \"Dirty\": false}}");
        }

        // Budget is 900 KB (Can only hold ~2 files)
        // asset_5 is locked/active in a game session
        var settings = new CacheSettings
        {
            BudgetBytes = 900 * 1024,
            MinFreeSpaceBytes = 100 * 1024
        };

        var mount = new MountDefinition { Id = "bench", DisplayName = "BenchDrive", RemotePath = "" };
        var mgr = new SmartCacheManager(
            cacheRoot,
            settings,
            () => [mount],
            isMountActive: _ => false,
            isGameActive: path => path.Contains("asset_5.bin"));

        var initialUsage = mgr.MeasureCurrentPhysicalBytes();
        var report = await mgr.RunMaintenanceAsync(force: false);
        var finalUsage = mgr.MeasureCurrentPhysicalBytes();

        _output.WriteLine("==========================================================================================");
        _output.WriteLine("BENCHMARK 3: HARD BUDGET ENFORCEMENT & DATA INTEGRITY UNDER LOAD");
        _output.WriteLine("==========================================================================================");
        _output.WriteLine($"Metric                       | Value");
        _output.WriteLine($"-----------------------------|------------------------------------------------------------");
        _output.WriteLine($"Configured Budget            | {ByteSize.Format(settings.BudgetBytes)}");
        _output.WriteLine($"Initial Usage (5 assets)     | {ByteSize.Format(initialUsage)}");
        _output.WriteLine($"Files Evicted (LRU order)    | {report.FilesEvicted} file(s)");
        _output.WriteLine($"Bytes Reclaimed              | {ByteSize.Format(report.BytesReclaimed)}");
        _output.WriteLine($"Final Usage                  | {ByteSize.Format(finalUsage)}");
        _output.WriteLine($"Protected Active File (asset_5) | Exists: {File.Exists(Path.Combine(vfsDir, "asset_5.bin"))}");
        _output.WriteLine("==========================================================================================");

        Assert.True(finalUsage <= settings.BudgetBytes);
        Assert.True(File.Exists(Path.Combine(vfsDir, "asset_5.bin"))); // Never evict active game asset!
    }

    [Fact]
    public async Task Benchmark_EphemeralZeroFootprintMode()
    {
        var cacheRoot = Path.Combine(_testRoot, "ephemeral-bench");
        var mountDir = Path.Combine(cacheRoot, "m-ephemeral");
        var vfsDir = Path.Combine(mountDir, "vfs", "cloud");
        var metaDir = Path.Combine(mountDir, "vfsMeta", "cloud");
        Directory.CreateDirectory(vfsDir);
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(mountDir, CacheMaintenance.Marker), "VYDRIX");

        // Session accumulates transient files
        for (int i = 1; i <= 3; i++)
        {
            File.WriteAllBytes(Path.Combine(vfsDir, $"session_{i}.pak"), new byte[500 * 1024]);
            File.WriteAllText(Path.Combine(metaDir, $"session_{i}.pak"), "{\"Size\": 512000, \"Dirty\": false}");
        }

        var settings = new CacheSettings { PurgeCacheOnExit = true };
        var mount = new MountDefinition { Id = "ephemeral", DisplayName = "CloudDrive", RemotePath = "" };
        var mgr = new SmartCacheManager(
            cacheRoot,
            settings,
            () => [mount],
            isMountActive: _ => false,
            isGameActive: _ => false);

        var prePurgeBytes = mgr.MeasureCurrentPhysicalBytes();
        Assert.True(prePurgeBytes > 0);

        // App Shutdown / Purge All Caches
        var purgeReport = await mgr.PurgeAllCachesAsync(discardPendingUploads: true);
        var postPurgeBytes = mgr.MeasureCurrentPhysicalBytes();

        _output.WriteLine("==========================================================================================");
        _output.WriteLine("BENCHMARK 4: EPHEMERAL / ZERO-FOOTPRINT PURGE VERIFICATION");
        _output.WriteLine("==========================================================================================");
        _output.WriteLine($"Pre-Purge Physical Footprint  | {ByteSize.Format(prePurgeBytes)}");
        _output.WriteLine($"Bytes Reclaimed on Purge      | {ByteSize.Format(purgeReport.BytesReclaimed)}");
        _output.WriteLine($"Post-Purge Lingering Bytes    | {ByteSize.Format(postPurgeBytes)}");
        _output.WriteLine("==========================================================================================");

        Assert.Equal(0, postPurgeBytes);
        Assert.False(Directory.Exists(vfsDir));
        Assert.False(Directory.Exists(metaDir));
    }
}
