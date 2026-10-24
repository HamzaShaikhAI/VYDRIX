using System;
using System.Collections.Generic;
using System.Linq;
using Vydrix.Core;

namespace Vydrix.Cache;

/// <summary>
/// Scored eviction candidate with multi-factor intelligence (recency, frequency, refetch cost, and protection).
/// Higher score = evicted first.
/// </summary>
public sealed record ScoredCandidate(
    string MountId,
    string MountName,
    string RemotePath,
    long PhysicalBytes,
    DateTime LastAccess,
    string? DataFilePath,
    string? MetaFilePath,
    bool IsMounted,
    double EvictionScore,
    bool IsProtected,
    string? ProtectionReason);

/// <summary>
/// Multi-factor intelligent eviction scorer for the Phantom Cache Engine.
/// Replaces naive LRU timestamp ordering with a weighted composite score.
/// </summary>
public static class EvictionScorer
{
    /// <summary>
    /// Calculates the composite eviction score for a candidate file.
    /// Returns double.NegativeInfinity if the file is protected (pinned, dirty, locked, or active in game).
    /// </summary>
    public static double CalculateScore(
        DateTime now,
        DateTime lastAccess,
        long physicalBytes,
        int accessCount,
        double recentNetworkRttMs,
        bool isPinned,
        bool isDirty,
        bool isActiveInGame,
        bool isFileLocked,
        out bool isProtected,
        out string? protectionReason)
    {
        if (isPinned)
        {
            isProtected = true;
            protectionReason = "Pinned by user";
            return double.NegativeInfinity;
        }

        if (isDirty)
        {
            isProtected = true;
            protectionReason = "Dirty / pending upload";
            return double.NegativeInfinity;
        }

        if (isActiveInGame)
        {
            isProtected = true;
            protectionReason = "Active game session asset";
            return double.NegativeInfinity;
        }

        if (isFileLocked)
        {
            isProtected = true;
            protectionReason = "File open / locked by process";
            return double.NegativeInfinity;
        }

        isProtected = false;
        protectionReason = null;

        // 1. Recency: Older idle time yields higher eviction score (e.g., 2 hours idle = 4 points, 24 hours = 48 points)
        var idleHours = Math.Max(0.01, (now - lastAccess).TotalHours);
        var recencyScore = Math.Min(120.0, idleHours * 2.0);

        // 2. Frequency: Frequently accessed files should be retained longer
        var frequencyScore = 50.0 / Math.Max(1, accessCount);

        // 3. Refetch Cost: Large files under high latency are penalised (lower score) to avoid expensive refetches
        var mbSize = physicalBytes / (1024.0 * 1024.0);
        var refetchCostPenalty = Math.Min(35.0, (mbSize * 0.4) + (recentNetworkRttMs > 150 ? 8.0 : 0.0));

        var compositeScore = recencyScore + frequencyScore - refetchCostPenalty;
        return compositeScore;
    }

    /// <summary>
    /// Sorts candidates so that eligible files with the highest eviction score are placed first.
    /// Protected files (NegativeInfinity) are sorted to the very end.
    /// </summary>
    public static IReadOnlyList<ScoredCandidate> RankCandidates(IEnumerable<ScoredCandidate> candidates)
    {
        return candidates
            .OrderByDescending(c => c.EvictionScore)
            .ThenBy(c => c.LastAccess)
            .ToList();
    }
}
