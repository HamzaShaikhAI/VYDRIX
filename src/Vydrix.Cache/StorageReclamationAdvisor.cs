using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vydrix.Core;

namespace Vydrix.Cache;

public enum FileReclamationEligibility
{
    EligibleForCloud,
    MustRemainLocal,
    NeedsUserReview
}

public sealed record ReclamationItem(
    string RelativePath,
    string FullPath,
    long SizeBytes,
    FileReclamationEligibility Eligibility,
    string Reason);

public sealed record StorageAdvisorScanResult(
    string ScannedPath,
    int TotalFiles,
    long TotalSizeBytes,
    long EligibleBytes,
    long IneligibleBytes,
    IReadOnlyList<ReclamationItem> EligibleItems,
    IReadOnlyList<ReclamationItem> IneligibleItems,
    IReadOnlyList<string> Recommendations);

/// <summary>
/// Storage Reclamation Advisor: an opt-in analysis tool that scans user-selected local folders,
/// identifies large game assets and candidate files eligible for cloud streaming, and estimates
/// potential local disk savings. Strictly protects saves, credentials, system binaries, and database files.
/// </summary>
public sealed class StorageReclamationAdvisor
{
    private static readonly HashSet<string> CloudEligibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pak", ".bin", ".dat", ".vpk", ".mp4", ".bik", ".bk2", ".wav", ".ogg",
        ".asset", ".bundle", ".unity3d", ".cas", ".idx", ".arc", ".big", ".rpa",
        ".dds", ".tga", ".png", ".jpg", ".wem", ".bnk"
    };

    private static readonly HashSet<string> LocalRequiredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".ini", ".cfg", ".json", ".xml",
        ".sav", ".save", ".db", ".sqlite", ".sqlite3", ".log", ".key"
    };

    private readonly ILogger? _logger;

    public StorageReclamationAdvisor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans an explicitly user-selected directory and classifies files for storage reclamation.
    /// Never scans system directories or drives without explicit user targeting.
    /// </summary>
    public async Task<StorageAdvisorScanResult> ScanSelectedDirectoryAsync(
        string selectedPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var fullPath = Path.GetFullPath(selectedPath);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Selected directory not found: {fullPath}");
            }

            // Guard against scanning root of system drive
            var root = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Scanning an entire drive root is not permitted. Please select a specific game or data folder.");
            }

            var eligible = new List<ReclamationItem>();
            var ineligible = new List<ReclamationItem>();
            var recommendations = new List<string>();

            progress?.Report($"Scanning {fullPath}…");
            var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);

            long totalBytes = 0;
            long eligibleBytes = 0;
            long ineligibleBytes = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var fi = new FileInfo(file);
                var size = fi.Length;
                totalBytes += size;

                var rel = Path.GetRelativePath(fullPath, file);
                var ext = fi.Extension;

                // Check protection rules:
                // 1. Saves and configs
                if (rel.Contains("save", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("Saved Games", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".sav", StringComparison.OrdinalIgnoreCase))
                {
                    ineligible.Add(new ReclamationItem(rel, file, size, FileReclamationEligibility.MustRemainLocal, "Game save / user profile data must remain local."));
                    ineligibleBytes += size;
                    continue;
                }

                // 2. Executables, DLLs, anti-cheat, drivers
                if (LocalRequiredExtensions.Contains(ext))
                {
                    ineligible.Add(new ReclamationItem(rel, file, size, FileReclamationEligibility.MustRemainLocal, $"Executable, DLL, or config file ({ext}) required locally for process launch."));
                    ineligibleBytes += size;
                    continue;
                }

                // 3. Large cloud-eligible asset files (above 1 MiB or known asset extensions)
                if (CloudEligibleExtensions.Contains(ext) || size >= 4 * ByteSize.MiB)
                {
                    eligible.Add(new ReclamationItem(rel, file, size, FileReclamationEligibility.EligibleForCloud, $"Large asset archive ({ext}, {ByteSize.Format(size)}) eligible for cloud streaming."));
                    eligibleBytes += size;
                }
                else
                {
                    // Small miscellaneous data
                    ineligible.Add(new ReclamationItem(rel, file, size, FileReclamationEligibility.NeedsUserReview, "Small asset or script; low storage reclamation benefit."));
                    ineligibleBytes += size;
                }
            }

            recommendations.Add($"Scanned {files.Length} file(s) across {ByteSize.Format(totalBytes)}.");
            if (eligibleBytes > 0)
            {
                var percentage = (double)eligibleBytes / Math.Max(1, totalBytes);
                recommendations.Add($"Estimated recoverable disk space: {ByteSize.Format(eligibleBytes)} ({percentage:P0} of folder).");
                recommendations.Add($"{eligible.Count} large asset file(s) can be offloaded to cloud storage and streamed on-demand.");
            }
            else
            {
                recommendations.Add("No significant cloud-streamable asset files identified in this directory.");
            }

            recommendations.Add($"{ineligible.Count} file(s) ({ByteSize.Format(ineligibleBytes)}) identified as essential local components (executables, configs, saves).");

            return new StorageAdvisorScanResult(
                fullPath,
                files.Length,
                totalBytes,
                eligibleBytes,
                ineligibleBytes,
                eligible,
                ineligible,
                recommendations);
        }, ct);
    }
}
