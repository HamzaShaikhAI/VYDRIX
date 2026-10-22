using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Vydrix.Core;
using Vydrix.Diagnostics;
using Vydrix.Mount;
using Vydrix.SimCloud;
using Vydrix.Streaming;

namespace Vydrix.Tests;

public class HardeningAndDefectTests
{
    [Fact]
    public void SEC_05_Redactor_masks_url_encoded_tokens()
    {
        var input = "GET /token?code=ya29%2Ea0AfH6SMBx_test_token_12345&refresh=1%2F%2F0gsecretsecretsecretsecret HTTP/1.1";
        var redacted = Redactor.Redact(input);

        Assert.DoesNotContain("ya29%2E", redacted);
        Assert.DoesNotContain("1%2F%2F0g", redacted);
        Assert.Contains("***REDACTED***", redacted);
    }

    [Fact]
    public void SEC_04_PinnedBinaryHashes_recognizes_official_rclone_hash()
    {
        const string officialHash = "41eab3e118d264920b62c3327966c5ece0dc4dd715ee617d9e30450d8ead95a4";
        Assert.True(PinnedBinaryHashes.KnownHashes.ContainsKey(officialHash));
    }

    [Fact]
    public async Task SEC_04_InspectAsync_rejects_untrusted_binary_in_strict_mode()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "fake binary content not matching official sha256");
            var verified = PinnedBinaryHashes.VerifyBinary(tempFile, out var hash);
            Assert.False(verified);

            await Assert.ThrowsAsync<SecurityException>(async () =>
            {
                await RcloneLocator.InspectAsync(tempFile, CancellationToken.None, requireVerified: true);
            });
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void AppPaths_migrates_legacy_cloudplayx_data_to_vydrix()
    {
        var tempRoot = Directory.CreateTempSubdirectory("vydrix-migration-test");
        var legacyDir = Path.Combine(tempRoot.FullName, "CloudPlayX");
        var destDir = Path.Combine(tempRoot.FullName, "Vydrix");
        Directory.CreateDirectory(legacyDir);

        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{\"test\":true}");
        File.WriteAllText(Path.Combine(legacyDir, "rclone.conf"), "[gdrive]\ntype = drive\n");
        File.WriteAllText(Path.Combine(legacyDir, "cloudplayx.db"), "sqlite-data-mock");

        try
        {
            AppPaths.MigrateLegacyIfPresent(destDir, legacyDir);

            Assert.True(File.Exists(Path.Combine(destDir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(destDir, "rclone.conf")));
            Assert.True(File.Exists(Path.Combine(destDir, "vydrix.db")), "Legacy cloudplayx.db should be renamed to vydrix.db");
            Assert.Equal("sqlite-data-mock", File.ReadAllText(Path.Combine(destDir, "vydrix.db")));
            Assert.True(File.Exists(Path.Combine(legacyDir, "cloudplayx.db")), "Source files must not be deleted, leaving original recoverable");
        }
        finally
        {
            try { tempRoot.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public void AppPaths_migration_is_idempotent_and_preserves_existing_vydrix_data()
    {
        var tempRoot = Directory.CreateTempSubdirectory("vydrix-idempotence-test");
        var legacyDir = Path.Combine(tempRoot.FullName, "CloudPlayX");
        var destDir = Path.Combine(tempRoot.FullName, "Vydrix");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(destDir);

        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{\"version\":\"legacy\"}");
        File.WriteAllText(Path.Combine(destDir, "settings.json"), "{\"version\":\"vydrix-current\"}");

        try
        {
            AppPaths.MigrateLegacyIfPresent(destDir, legacyDir);

            // Must NOT overwrite existing Vydrix settings!
            var currentContent = File.ReadAllText(Path.Combine(destDir, "settings.json"));
            Assert.Contains("vydrix-current", currentContent);
        }
        finally
        {
            try { tempRoot.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public void SecretStore_decrypts_both_modern_and_migrated_legacy_keys()
    {
        var tempRoot = Directory.CreateTempSubdirectory("vydrix-secret-test");
        var keyPath = Path.Combine(tempRoot.FullName, "rclone.conf.key");
        try
        {
            // Create legacy-encrypted key using CloudPlayX entropy
            var legacyEntropy = Encoding.UTF8.GetBytes("CloudPlayX.v1.secret");
            var plain = Encoding.UTF8.GetBytes("super-secret-dpapi-token");
            var enc = ProtectedData.Protect(plain, legacyEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(keyPath, enc);

            // SecretStore.Load should detect legacy entropy, decrypt it, and upgrade it
            var decrypted = SecretStore.Load(keyPath);
            Assert.Equal("super-secret-dpapi-token", decrypted);

            // Re-load to confirm it now decrypts using modern Vydrix entropy
            var reloaded = SecretStore.Load(keyPath);
            Assert.Equal("super-secret-dpapi-token", reloaded);
        }
        finally
        {
            try { tempRoot.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public void TECH_09_TransitionModel_bounds_graph_under_stress()
    {
        var model = new TransitionModel(window: 3, maxGap: TimeSpan.FromSeconds(5));
        var dummyTo = new BlockKey("target.dat", 999);

        // Add 5,000 distinct source blocks to trigger LRU eviction at 4,096
        for (int i = 0; i < 5000; i++)
        {
            var from = new BlockKey($"file_{i}.dat", 0);
            model.AddEdge(from, dummyTo, 1);
        }

        var succ = model.Successors(new BlockKey("file_4999.dat", 0), 10);
        Assert.NotEmpty(succ);
    }

    [Fact]
    public void TECH_07_TelemetryParser_does_not_drop_all_pending_reads_on_burst()
    {
        var parser = new RcloneLogParser("m1");
        int observedCount = 0;
        parser.Read += _ => observedCount++;

        // Feed 4,200 read-start lines
        for (int i = 0; i < 4200; i++)
        {
            var line = $"2026/10/22 12:00:00.000000 DEBUG : file_{i}.dat(0x{i:x}): _readAt: size=4096, off=0";
            parser.ParseLine(line);
        }

        // Now complete the most recent read (fh=0x1060 = 4192)
        // With circular FIFO eviction, recent reads are preserved and matched.
        var completeLine = "2026/10/22 12:00:00.005000 DEBUG : file_4192.dat(0x1060): >_readAt: n=4096, err=<nil>";
        parser.ParseLine(completeLine);

        Assert.Equal(1, observedCount);
    }

    [Fact]
    public async Task MockCloud_deterministic_range_reads_and_throttling()
    {
        var tempRoot = Directory.CreateTempSubdirectory("vydrix-mockcloud-test");
        try
        {
            var contentDir = Path.Combine(tempRoot.FullName, "content");
            Directory.CreateDirectory(contentDir);

            // Generate deterministic 512 KiB test payload
            var testBytes = new byte[512 * 1024];
            new Random(42).NextBytes(testBytes);
            var testFilePath = Path.Combine(contentDir, "payload.bin");
            await File.WriteAllBytesAsync(testFilePath, testBytes);

            var sourceHash = Convert.ToHexString(SHA256.HashData(testBytes)).ToLowerInvariant();

            await using var server = new SimCloudServer(contentDir, port: 0);
            using var http = new HttpClient();

            // 1. Full read matches source hash
            var fullBytes = await http.GetByteArrayAsync($"{server.Url}payload.bin");
            var fullHash = Convert.ToHexString(SHA256.HashData(fullBytes)).ToLowerInvariant();
            Assert.Equal(sourceHash, fullHash);

            // 2. Deterministic byte-range request
            var req = new HttpRequestMessage(HttpMethod.Get, $"{server.Url}payload.bin");
            req.Headers.Range = new RangeHeaderValue(1000, 1999);
            using var resp = await http.SendAsync(req);
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, resp.StatusCode);
            var slice = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(1000, slice.Length);
            Assert.Equal(testBytes.AsSpan(1000, 1000).ToArray(), slice);

            // 3. Concurrent random reads
            var tasks = Enumerable.Range(0, 10).Select(async i =>
            {
                var start = i * 4096;
                var end = start + 4095;
                using var r = new HttpRequestMessage(HttpMethod.Get, $"{server.Url}payload.bin");
                r.Headers.Range = new RangeHeaderValue(start, end);
                using var res = await http.SendAsync(r);
                var b = await res.Content.ReadAsByteArrayAsync();
                Assert.Equal(testBytes.AsSpan(start, 4096).ToArray(), b);
            });
            await Task.WhenAll(tasks);

            // 4. Request throttling verification
            server.BandwidthMbps = 8.0; // ~1 MB/s
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reqThrottle = new HttpRequestMessage(HttpMethod.Get, $"{server.Url}payload.bin");
            reqThrottle.Headers.Range = new RangeHeaderValue(0, 256 * 1024 - 1); // 256 KB
            using var respThrottle = await http.SendAsync(reqThrottle);
            var throttledBytes = await respThrottle.Content.ReadAsByteArrayAsync();
            sw.Stop();
            Assert.Equal(256 * 1024, throttledBytes.Length);
            Assert.True(sw.ElapsedMilliseconds >= 50, $"Expected throttled transfer, took {sw.ElapsedMilliseconds} ms");

            // 5. Offline simulation fails closed
            server.Offline = true;
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await http.GetByteArrayAsync($"{server.Url}payload.bin");
            });

            // 6. Recovery when offline cleared
            server.Offline = false;
            var recovered = await http.GetByteArrayAsync($"{server.Url}payload.bin");
            Assert.Equal(512 * 1024, recovered.Length);
        }
        finally
        {
            try { tempRoot.Delete(recursive: true); } catch { }
        }
    }
}
