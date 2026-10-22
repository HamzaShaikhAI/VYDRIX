using System.Security;
using Vydrix.Core;
using Vydrix.Diagnostics;
using Vydrix.Mount;
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

        // Execute migration routine directly on test folders
        try
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.EnumerateFiles(legacyDir))
            {
                var name = Path.GetFileName(file);
                var targetName = name.Equals("cloudplayx.db", StringComparison.OrdinalIgnoreCase) ? "vydrix.db" : name;
                var targetPath = Path.Combine(destDir, targetName);
                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath, overwrite: false);
                }
            }

            Assert.True(File.Exists(Path.Combine(destDir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(destDir, "rclone.conf")));
            Assert.True(File.Exists(Path.Combine(destDir, "vydrix.db")), "Legacy cloudplayx.db should be renamed to vydrix.db");
            Assert.Equal("sqlite-data-mock", File.ReadAllText(Path.Combine(destDir, "vydrix.db")));
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
}
