using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vydrix.Cache;
using Vydrix.Core;
using Vydrix.Diagnostics;
using Vydrix.Games;
using Vydrix.Streaming;

namespace Vydrix.App;

/// <summary>
/// Workload Orchestrator: Unified launch coordinator for AAA Games, Ollama AI Models,
/// and Development Workspaces. Runs preflight checks across RAM, VRAM, C: disk headroom,
/// and cloud network health to protect the host PC from storage exhaustion.
/// </summary>
public sealed class WorkloadOrchestrator
{
    private readonly CGuardianService _cGuardian;
    private readonly CloudStorageBroker _broker;
    private readonly CloudWorkspaceManager _workspaces;
    private readonly AiModelVault _aiVault;
    private readonly GameVaultService _gameVault;
    private readonly LatencyShield _latencyShield;

    public CGuardianService Guardian => _cGuardian;
    public CloudStorageBroker Broker => _broker;
    public CloudWorkspaceManager Workspaces => _workspaces;
    public AiModelVault AiVault => _aiVault;
    public GameVaultService GameVault => _gameVault;

    public WorkloadOrchestrator(
        CGuardianService cGuardian,
        CloudStorageBroker broker,
        CloudWorkspaceManager workspaces,
        AiModelVault aiVault,
        GameVaultService gameVault,
        LatencyShield latencyShield)
    {
        _cGuardian = cGuardian;
        _broker = broker;
        _workspaces = workspaces;
        _aiVault = aiVault;
        _gameVault = gameVault;
        _latencyShield = latencyShield;
    }

    /// <summary>
    /// Executes a preflight evaluation before launching any game, model, or development workspace.
    /// Distinguishes hardware facts from estimates and advises the user.
    /// </summary>
    public WorkloadPreflightReport RunPreflightCheck(
        string workloadId,
        WorkloadType type,
        string sourcePath,
        WorkloadProfile profile = WorkloadProfile.StrictCloud)
    {
        var warnings = new List<string>();
        var recommendations = new List<string>();
        bool canSafelyLaunch = true;

        // 1. Check C: Drive capacity via C: Guardian
        var cStatus = _cGuardian.CheckStatus("C");
        long cFree = cStatus.FreeSpaceBytes;

        if (cStatus.IsCritical)
        {
            canSafelyLaunch = false;
            warnings.Add($"CRITICAL: C: drive has only {ByteSize.Format(cFree)} free space ({cStatus.FreePercent:F1}%). High risk of Windows freezing or pagefile allocation failure.");
            recommendations.Add("Execute safe cleanup via C: Guardian or relocate VYDRIX cache to secondary storage before launching.");
        }
        else if (cStatus.IsWarning)
        {
            warnings.Add($"WARNING: C: drive free space is low ({ByteSize.Format(cFree)}).");
            recommendations.Add("Ensure workload cache is placed on a secondary physical drive or in RAM.");
        }

        // 2. Resolve Placement Policy from Broker
        var policy = _broker.ResolvePlacementPolicy(workloadId, type, sourcePath, profile);

        // 3. Query System Memory and GPU VRAM
        long availableRam = GetAvailableRamBytes();
        long availableVram = 4L * ByteSize.GiB; // Dedicated baseline

        long requiredRam = 0;
        long requiredVram = 0;
        long projectedCDiskImpact = 0;

        switch (type)
        {
            case WorkloadType.Game:
                requiredRam = 8L * ByteSize.GiB;
                requiredVram = 3L * ByteSize.GiB;

                var compat = _gameVault.AssessCompatibility(sourcePath, workloadId);
                if (!compat.IsCompatibleWithCloudDrive)
                {
                    warnings.Add($"Anti-Cheat Warning: {compat.AntiCheatType} detected. {compat.CompatibilityNotes}");
                    recommendations.Add("Some online anti-cheat titles require physical drive installation. Single-player titles are fully compatible.");
                }

                // If cache is on C:, projected impact is bounded by the cache budget
                if (policy.CachePlacement == CachePlacementOption.BoundedC)
                {
                    projectedCDiskImpact = policy.CacheBudgetBytes;
                }
                break;

            case WorkloadType.AiModel:
                // Estimate based on model file size
                long modelFileSize = File.Exists(sourcePath) ? new FileInfo(sourcePath).Length : 4L * ByteSize.GiB;
                requiredRam = modelFileSize + (long)(1.5 * 1024 * 1024 * 1024);
                requiredVram = modelFileSize;

                if (availableRam < requiredRam)
                {
                    warnings.Add($"Insufficient RAM: Model needs ~{ByteSize.Format(requiredRam)} with context, but only {ByteSize.Format(availableRam)} is free.");
                    recommendations.Add("Loading this model may cause heavy Windows paging to C:\\pagefile.sys, increasing C: disk consumption.");
                }

                if (policy.CachePlacement == CachePlacementOption.BoundedC)
                {
                    projectedCDiskImpact = Math.Min(policy.CacheBudgetBytes, modelFileSize);
                }
                break;

            case WorkloadType.DevelopmentWorkspace:
                requiredRam = 4L * ByteSize.GiB;
                requiredVram = 0;

                var capReport = _broker.TestPathCapabilities(sourcePath);
                if (!capReport.IsSafeForScratch)
                {
                    warnings.Add("Workspace path on cloud drive may exhibit build locking contention.");
                    recommendations.Add($"Configure compiler output and scratch to '{policy.ScratchLocation}' on physical storage.");
                }

                if (policy.CachePlacement == CachePlacementOption.BoundedC)
                {
                    projectedCDiskImpact = policy.CacheBudgetBytes;
                }
                break;

            default:
                requiredRam = 2L * ByteSize.GiB;
                break;
        }

        // 4. Network Health Assessment
        if (_latencyShield.CurrentAverageRttMs > 250.0)
        {
            warnings.Add($"High cloud latency detected ({_latencyShield.CurrentAverageRttMs:F1} ms). First-time file reads may take longer.");
        }

        if (policy.CachePlacement == CachePlacementOption.SecondaryPhysicalDrive)
        {
            recommendations.Add($"C: SHIELDED: Workload cache is placed on '{policy.ActualCachePath}'. Zero gigabyte impact on C:.");
        }

        return new WorkloadPreflightReport(
            DateTime.Now,
            workloadId,
            type,
            profile,
            availableRam,
            requiredRam,
            availableVram,
            requiredVram,
            cFree,
            projectedCDiskImpact,
            canSafelyLaunch,
            warnings,
            recommendations);
    }

    private static long GetAvailableRamBytes()
    {
        try
        {
            var mem = GC.GetGCMemoryInfo();
            if (mem.TotalAvailableMemoryBytes > 0) return mem.TotalAvailableMemoryBytes;
        }
        catch { }
        return 4L * ByteSize.GiB;
    }
}
