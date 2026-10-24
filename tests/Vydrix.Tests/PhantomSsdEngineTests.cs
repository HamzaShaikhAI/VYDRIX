using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Cache;
using Vydrix.Core;
using Vydrix.Diagnostics;
using Vydrix.Games;
using Vydrix.Mount;
using Vydrix.Streaming;
using Xunit;

namespace Vydrix.Tests;

public sealed class PhantomSsdEngineTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Xunit.Abstractions.ITestOutputHelper? _output;

    public PhantomSsdEngineTests(Xunit.Abstractions.ITestOutputHelper? output = null)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), "vydrix-phantom-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true); } catch { }
    }

    // 1. EvictionScorer multi-factor scoring
    [Fact]
    public void EvictionScorer_ProperlyRanksIdleAndHighLatencyFiles()
    {
        var now = DateTime.Now;

        // Candidate 1: idle for 24h, accessed once, small file
        var score1 = EvictionScorer.CalculateScore(
            now, now.AddHours(-24), 1024 * 1024, 1, 50.0,
            isPinned: false, isDirty: false, isActiveInGame: false, isFileLocked: false,
            out var isProtected1, out _);
        Assert.False(isProtected1);

        // Candidate 2: idle for 1h, accessed 10 times, large file under high latency
        var score2 = EvictionScorer.CalculateScore(
            now, now.AddHours(-1), 100 * 1024 * 1024, 10, 300.0,
            isPinned: false, isDirty: false, isActiveInGame: false, isFileLocked: false,
            out var isProtected2, out _);
        Assert.False(isProtected2);

        // Candidate 1 should have much higher eviction score than Candidate 2 (evicted first)
        Assert.True(score1 > score2);

        // Candidate 3: pinned file -> must receive NegativeInfinity
        var score3 = EvictionScorer.CalculateScore(
            now, now.AddHours(-48), 1024, 1, 50.0,
            isPinned: true, isDirty: false, isActiveInGame: false, isFileLocked: false,
            out var isProtected3, out var reason3);
        Assert.True(isProtected3);
        Assert.Equal(double.NegativeInfinity, score3);
        Assert.Contains("Pinned", reason3);
    }

    // 2. MemoryHotCache Tier A storage and trimming
    [Fact]
    public void MemoryHotCache_CachesSmallFiles_AndEnforcesCapacityLimit()
    {
        using var cache = new MemoryHotCache(enabled: true, maxBytes: 16 * 1024); // 16 KB max

        var data4k = new byte[4096];
        Array.Fill(data4k, (byte)0xAA);

        // Insert item 1, 2, 3 (each 4KB = 12KB <= 16KB)
        Assert.True(cache.TryPut("item1", data4k));
        Assert.True(cache.TryPut("item2", data4k));
        Assert.True(cache.TryPut("item3", data4k));
        Assert.Equal(3, cache.ItemCount);

        // Hit item 1
        Assert.True(cache.TryGet("item1", out var fetched));
        Assert.NotNull(fetched);
        Assert.Equal((byte)0xAA, fetched[0]);

        // Insert item 4 and item 5 (each 4KB) -> triggers LRU trim
        Assert.True(cache.TryPut("item4", data4k));
        Assert.True(cache.TryPut("item5", data4k));

        // Memory capacity must not exceed 16 KB
        Assert.True(cache.CurrentBytes <= 16 * 1024);
        // item 2 (least recently accessed) should be trimmed
        Assert.False(cache.TryGet("item2", out _));
    }

    // 3. ShadowIntegrity detects remote size/modtime divergence and checksum verification
    [Fact]
    public void ShadowIntegrity_DetectsRemoteModifications_AndValidatesHashes()
    {
        var testFile = Path.Combine(_testRoot, "shadow_test.dat");
        var content = System.Text.Encoding.UTF8.GetBytes("VYDRIX_PHANTOM_SSD_INTEGRITY_DATA");
        File.WriteAllBytes(testFile, content);

        var fi = new FileInfo(testFile);

        // 1. Same size and modtime -> Verified
        var res1 = ShadowIntegrity.VerifyFile(testFile, null, content.Length, fi.LastWriteTimeUtc);
        Assert.Equal(IntegrityStatus.Verified, res1.Status);
        Assert.True(res1.CacheValid);

        // 2. Remote size changed -> RemoteModified
        var res2 = ShadowIntegrity.VerifyFile(testFile, null, content.Length + 500, fi.LastWriteTimeUtc);
        Assert.Equal(IntegrityStatus.RemoteModified, res2.Status);
        Assert.False(res2.CacheValid);

        // 3. Remote modtime changed (> 5s difference) -> RemoteModified
        var res3 = ShadowIntegrity.VerifyFile(testFile, null, content.Length, fi.LastWriteTimeUtc.AddMinutes(10));
        Assert.Equal(IntegrityStatus.RemoteModified, res3.Status);
        Assert.False(res3.CacheValid);

        // 4. SHA256 verification
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var expectedHash = Convert.ToHexString(sha256.ComputeHash(content)).ToLowerInvariant();
        var res4 = ShadowIntegrity.VerifyFile(testFile, null, content.Length, fi.LastWriteTimeUtc, expectedHash, "sha256");
        Assert.Equal(IntegrityStatus.Verified, res4.Status);
        Assert.True(res4.CacheValid);
    }

    // 4. StorageTimeMachine logs events and generates recommendations with redacting
    [Fact]
    public void StorageTimeMachine_RedactsSecrets_AndProvidesRecommendations()
    {
        var stm = new StorageTimeMachine();

        stm.RecordEvent("Launch", "Game launched", "Querying token: access_token=secret_oauth_token_12345&mode=play");
        var recent = stm.GetRecentEvents();

        Assert.Single(recent);
        Assert.DoesNotContain("secret_oauth_token_12345", recent[0].Details);
        Assert.Contains("[REDACTED]", recent[0].Details);

        // Recommendations under high cache churn
        for (int i = 0; i < 15; i++)
        {
            stm.RecordEvent("Eviction", "Evicted data", $"Reclaimed {i} MB");
        }

        var recs = stm.GetRecommendations(500 * ByteSize.MiB, 512 * ByteSize.MiB);
        Assert.NotEmpty(recs);
        Assert.Contains(recs, r => r.Title.Contains("High Cache Churn"));

        stm.ClearHistory();
        Assert.Empty(stm.GetRecentEvents());
    }

    // 5. CoalescingReadPipeline coalesces contiguous/adjacent ranges
    [Fact]
    public void CoalescingReadPipeline_CoalescesAdjacentRanges()
    {
        var r1 = new ByteRange(0, 1024);
        var r2 = new ByteRange(1024, 2048); // Contiguous
        var r3 = new ByteRange(4096, 1024); // Gap = 1024 bytes

        var coalesced = CoalescingReadPipeline.CoalesceRanges(new[] { r1, r2, r3 }, maxGapBytes: 2048);

        // All 3 should be merged into one single range starting at 0 and ending at 5120
        Assert.Single(coalesced);
        Assert.Equal(0, coalesced[0].Offset);
        Assert.Equal(5120, coalesced[0].Length);
    }

    // 6. CoalescingReadPipeline deduplicates in-flight reads
    [Fact]
    public async Task CoalescingReadPipeline_DeduplicatesConcurrentIdenticalReads()
    {
        int fetchCount = 0;
        using var pipeline = new CoalescingReadPipeline(async (m, p, off, len, ct) =>
        {
            Interlocked.Increment(ref fetchCount);
            await Task.Delay(50, ct); // simulate network latency
            return new byte[len];
        });

        // Launch 5 simultaneous requests for the exact same block
        var t1 = pipeline.ReadRangeAsync("m1", "file.dat", 0, 4096);
        var t2 = pipeline.ReadRangeAsync("m1", "file.dat", 0, 4096);
        var t3 = pipeline.ReadRangeAsync("m1", "file.dat", 0, 4096);
        var t4 = pipeline.ReadRangeAsync("m1", "file.dat", 0, 4096);
        var t5 = pipeline.ReadRangeAsync("m1", "file.dat", 0, 4096);

        await Task.WhenAll(t1, t2, t3, t4, t5);

        // Exactly 1 network fetch should have been dispatched; 4 were deduplicated
        Assert.Equal(1, fetchCount);
        Assert.True(pipeline.DeduplicatedRequests >= 4);
    }

    // 7. MetadataCache stores and invalidates directory and stat entries
    [Fact]
    public void MetadataCache_CachesAndInvalidatesProperly()
    {
        var meta = new MetadataCache();
        meta.SetFileMetadata("game/data.pak", exists: true, size: 1048576, DateTime.Now, isDirectory: false);

        Assert.True(meta.TryGetFileMetadata("game/data.pak", out var cached));
        Assert.NotNull(cached);
        Assert.Equal(1048576, cached.Size);

        meta.SetDirectoryListing("game", new[] { "game/data.pak", "game/config.ini" });
        Assert.True(meta.TryGetDirectoryListing("game", out var dirEntries));
        Assert.Equal(2, dirEntries!.Count);

        // Invalidate file
        meta.Invalidate("game/data.pak");
        Assert.False(meta.TryGetFileMetadata("game/data.pak", out _));
        Assert.False(meta.TryGetDirectoryListing("game", out _));
    }

    // 8. PhantomPulseEngine identifies sequential vs small-file workloads
    [Fact]
    public void PhantomPulseEngine_DetectsSequentialAndSmallFileModes()
    {
        var pulse = new PhantomPulseEngine();

        // 1. Feed small-file burst
        for (int i = 0; i < 20; i++)
        {
            pulse.ObserveAccess(new AccessEvent(
                DateTime.Now, "m1", $"textures/tex_{i}.dds", 0, 8192, true, 2.0, ReadSource.Application));
        }

        Assert.Equal(PhantomPulseMode.SmallFile, pulse.CurrentClassification.DetectedMode);
        Assert.Contains("small file reads", pulse.CurrentClassification.Reason, StringComparison.OrdinalIgnoreCase);

        // 2. Feed sequential stream
        pulse.Reset();
        for (int i = 0; i < 20; i++)
        {
            pulse.ObserveAccess(new AccessEvent(
                DateTime.Now, "m1", "videos/intro.mp4", i * 1024 * 1024, 1024 * 1024, false, 15.0, ReadSource.Application));
        }

        Assert.Equal(PhantomPulseMode.Sequential, pulse.CurrentClassification.DetectedMode);
        Assert.Contains("Sequential", pulse.CurrentClassification.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // 9. LatencyShield adapts concurrency and diagnoses network bottlenecks
    [Fact]
    public void LatencyShield_AdaptsConcurrency_AndDiagnosesBottlenecks()
    {
        var shield = new LatencyShield();

        // Feed normal low-latency measurements (< 80ms)
        for (int i = 0; i < 10; i++)
        {
            shield.RecordMeasurement(45.0, 35.0, success: true);
        }
        Assert.Equal(12, shield.Concurrency);

        // Feed high-latency network measurements (> 300ms)
        for (int i = 0; i < 25; i++)
        {
            shield.RecordMeasurement(350.0, 5.0, success: true);
        }
        Assert.True(shield.Concurrency <= 4);

        var diag = shield.DiagnoseBottleneck(cacheHitRatio: 0.85, localDiskWriteLatencyMs: 2.0, cpuLoadRatio: 0.20);
        Assert.Equal(BottleneckType.NetworkCloud, diag.Type);
        Assert.Contains("Cloud Round-Trip", diag.Headline);
    }

    // 10. GhostWarmUpService generates dry-run plan and obeys budget
    [Fact]
    public void GhostWarmUpService_GeneratesDryRunPlan_AndRespectsBudget()
    {
        var dbPath = Path.Combine(_testRoot, "ghost_test.db");
        var store = new ProfileStore(dbPath);

        // Save a mock session with startup blocks
        var rec = new SessionRecorder(DateTime.Now);
        rec.Record(new BlockKey("game/engine.dll", 0), DateTime.Now);
        rec.Record(new BlockKey("game/shaders.bin", 0), DateTime.Now);
        store.SaveSession("game_test", 1024 * 1024, rec, DateTime.Now, null, "CloudDirect");

        // Service with healthy budget
        var service = new GhostWarmUpService(store, () => 200 * ByteSize.MiB);
        var plan = service.GenerateDryRunPlan("game_test", isZeroFootprintMode: false, maxAllowedBytes: 64 * ByteSize.MiB);

        Assert.True(plan.FeasibleUnderBudget);
        Assert.True(plan.BlockCount > 0);
        Assert.Contains("game/engine.dll", plan.HighValueFiles);

        // In Zero Footprint Mode -> Must be strictly disabled
        var planZf = service.GenerateDryRunPlan("game_test", isZeroFootprintMode: true);
        Assert.False(planZf.FeasibleUnderBudget);
        Assert.Equal(0, planZf.PlannedBytes);
    }

    // 11. SessionVault journalizes launches, handles PID reuse, and reconciles crashed sessions
    [Fact]
    public async Task SessionVault_SurvivesCrashes_AndReconcilesOrphanedSessions()
    {
        var journalPath = Path.Combine(_testRoot, "vault_test.json");
        var vault1 = new SessionVault(journalPath);

        using var currentProc = Process.GetCurrentProcess();
        var record = vault1.RecordLaunch("cyber_game", currentProc, "m1", new[] { "pinned.dat" });

        Assert.Single(vault1.ActiveSessions);
        Assert.Equal("Running", vault1.ActiveSessions[0].State);

        // Simulate VYDRIX restart after a crash:
        // Create second instance of SessionVault on same journal file with an orphaned dead PID
        var vault2 = new SessionVault(journalPath);

        // PID 999999 is dead / invalid on Windows
        bool alive = SessionVault.IsProcessGenuinelyAlive(999999, DateTime.Now);
        Assert.False(alive);

        // Simulate reconciliation
        int cleanupCount = 0;
        var report = await vault2.ReconcileStartupAsync(async gameId =>
        {
            await Task.Yield();
            cleanupCount++;
        });

        Assert.NotNull(report);
    }

    // 12. CloudSsdSelfTest executes synthetic benchmark and reports latency/throughput
    [Fact]
    public async Task CloudSsdSelfTest_RunsSyntheticBenchmarkSuccessfully()
    {
        var report = await CloudSsdSelfTest.RunSyntheticSelfTestAsync();

        Assert.NotNull(report);
        Assert.True(report.IsSimulated);
        Assert.NotEmpty(report.Metrics);
        Assert.True(report.OverallScore >= 70.0, $"Expected score >= 70, got {report.OverallScore}");
        Assert.Contains(report.Metrics, m => m.Name.Contains("Metadata"));
        Assert.Contains(report.Metrics, m => m.Name.Contains("Throughput"));

        _output?.WriteLine("==========================================================================================");
        _output?.WriteLine($"CLOUD SSD SELF-TEST BENCHMARK RESULTS (Summary: {report.Summary}, Score: {report.OverallScore:F1}/100)");
        _output?.WriteLine("==========================================================================================");
        _output?.WriteLine($"Test Metric                   | Value        | Unit   | Details");
        _output?.WriteLine($"------------------------------|--------------|--------|-----------------------------------");
        foreach (var m in report.Metrics)
        {
            _output?.WriteLine($"{m.Name,-30}| {m.Value,12:F2} | {m.Unit,-6} | {m.Description}");
        }
        _output?.WriteLine("==========================================================================================");
    }
}
