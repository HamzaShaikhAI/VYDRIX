using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Vydrix.Core;

namespace Vydrix.Games;

public sealed record GameCompatibilityRecord(
    string GameId,
    string Title,
    string CloudPath,
    string AntiCheatType,
    bool IsCompatibleWithCloudDrive,
    string CompatibilityNotes,
    string SaveGameLocation,
    string ShaderCachePolicy,
    long RecommendedCacheBudgetBytes,
    bool SupportsDemandStreaming);

public sealed record MultiGamePressureReport(
    int ActiveGameCount,
    long AggregateRamWorkingSetBytes,
    long EstimatedVramBytes,
    double AggregateCpuPercent,
    bool HighPressureWarning,
    string Advice);

/// <summary>
/// Game Vault Service: Enhances game streaming capabilities with cloud path awareness,
/// anti-cheat compatibility triage, save-game protection, shader cache policies,
/// and multi-game concurrent resource monitoring.
/// </summary>
public sealed class GameVaultService
{
    /// <summary>
    /// Inspects a game directory for anti-cheat, launcher dependencies, and file structures.
    /// Provides an honest compatibility assessment for cloud-backed virtual drive execution.
    /// </summary>
    public GameCompatibilityRecord AssessCompatibility(string gameDir, string? titleOverride = null)
    {
        var title = titleOverride ?? Path.GetFileName(gameDir.TrimEnd('\\', '/'));
        var gameId = $"game_{title.ToLowerInvariant().Replace(' ', '_')}";

        string antiCheatType = "None / DRM-Free";
        bool isCompatible = true;
        var notes = new List<string>();

        if (!Directory.Exists(gameDir))
        {
            return new GameCompatibilityRecord(
                gameId,
                title,
                gameDir,
                "Unknown",
                false,
                "Game directory does not exist or cloud drive is unmounted.",
                "Unknown",
                "Default",
                512 * ByteSize.MiB,
                false);
        }

        // 1. Anti-Cheat Detection
        try
        {
            var files = Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories).Take(500).ToList();

            if (files.Any(f => f.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)))
            {
                antiCheatType = "EasyAntiCheat (Kernel Driver)";
                isCompatible = false;
                notes.Add("EasyAntiCheat monitors filesystem drivers and may flag WinFsp user-mode mounts as untrusted or trigger error 10011.");
            }
            else if (files.Any(f => f.Contains("BEService", StringComparison.OrdinalIgnoreCase) || f.Contains("BattlEye", StringComparison.OrdinalIgnoreCase)))
            {
                antiCheatType = "BattlEye (Kernel Driver)";
                isCompatible = false;
                notes.Add("BattlEye may block game launch when game binaries execute from a virtual filesystem.");
            }
            else if (files.Any(f => f.Contains("vgk.sys", StringComparison.OrdinalIgnoreCase) || f.Contains("vgc.exe", StringComparison.OrdinalIgnoreCase)))
            {
                antiCheatType = "Riot Vanguard (Ring 0)";
                isCompatible = false;
                notes.Add("Vanguard strictly prohibits non-physical drive execution.");
            }
            else
            {
                notes.Add("No kernel-level anti-cheat detected. Fully compatible with VYDRIX VFS streaming.");
            }
        }
        catch (Exception ex)
        {
            notes.Add($"File scan warning: {ex.Message}");
        }

        // 2. Save Game Location
        string saveLocation = ResolveSaveGameLocation(title);

        // 3. Shader Cache Policy
        string shaderPolicy = "GPU Driver Default (Redirected via driver settings if supported)";

        long recommendedBudget = isCompatible ? 512 * ByteSize.MiB : 2 * ByteSize.GiB;

        return new GameCompatibilityRecord(
            gameId,
            title,
            gameDir,
            antiCheatType,
            isCompatible,
            string.Join(" ", notes),
            saveLocation,
            shaderPolicy,
            recommendedBudget,
            SupportsDemandStreaming: isCompatible);
    }

    /// <summary>
    /// Identifies the game save path to ensure save games are NEVER treated as transient cache.
    /// </summary>
    public string ResolveSaveGameLocation(string gameTitle)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string[] potentialPaths =
        [
            Path.Combine(userProfile, "Saved Games", gameTitle),
            Path.Combine(localAppData, gameTitle, "Saved"),
            Path.Combine(appData, gameTitle),
            Path.Combine(userProfile, "Documents", "My Games", gameTitle)
        ];

        foreach (var p in potentialPaths)
        {
            if (Directory.Exists(p)) return p;
        }

        return Path.Combine(userProfile, "Saved Games", gameTitle);
    }

    /// <summary>
    /// Monitors cumulative resource pressure when multiple games or large processes run simultaneously.
    /// </summary>
    public MultiGamePressureReport MonitorMultiGamePressure(IReadOnlyList<int> activeProcessIds)
    {
        int validCount = 0;
        long totalRamWorkingSet = 0;

        foreach (var pid in activeProcessIds)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    validCount++;
                    totalRamWorkingSet += proc.WorkingSet64;
                }
            }
            catch { }
        }

        // Estimated VRAM ~ 80% of active game working set
        long estimatedVram = (long)(totalRamWorkingSet * 0.8);
        bool highPressure = validCount > 1 && totalRamWorkingSet > 8L * ByteSize.GiB;

        string advice = highPressure
            ? $"Multiple concurrent games detected ({validCount} active). Combined RAM usage ({ByteSize.Format(totalRamWorkingSet)}) is high. Cloud streaming latency may increase due to thread contention."
            : $"Resource load normal across {validCount} active process(es).";

        return new MultiGamePressureReport(
            validCount,
            totalRamWorkingSet,
            estimatedVram,
            0.0,
            highPressure,
            advice);
    }
}
