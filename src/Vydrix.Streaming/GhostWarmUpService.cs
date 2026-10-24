using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Core;

namespace Vydrix.Streaming;

/// <summary>
/// Ghost Warm-Up: Optional launch-acceleration planner.
/// Analyzes executable headers and past session profiles to predict the highest-value initial blocks.
/// Strictly bounded by budget, disabled by default in Zero Footprint Mode, and immediately preempted
/// by any foreground demand read.
/// </summary>
public sealed class GhostWarmUpService
{
    private readonly ProfileStore _store;
    private readonly Func<long> _availableBudgetProvider;
    private CancellationTokenSource? _activeWarmUpCts;

    public GhostWarmUpService(ProfileStore store, Func<long> availableBudgetProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _availableBudgetProvider = availableBudgetProvider ?? throw new ArgumentNullException(nameof(availableBudgetProvider));
    }

    /// <summary>
    /// Evaluates past session history and produces a dry-run plan without initiating any downloads.
    /// </summary>
    public GhostWarmUpPlan GenerateDryRunPlan(string gameId, bool isZeroFootprintMode, long maxAllowedBytes = 64 * ByteSize.MiB)
    {
        if (isZeroFootprintMode)
        {
            return new GhostWarmUpPlan(
                gameId,
                0,
                0,
                Array.Empty<string>(),
                "Ghost Warm-Up is strictly disabled under Zero Footprint Mode (demand reads only).",
                false);
        }

        var availableBudget = _availableBudgetProvider();
        if (availableBudget <= 32 * ByteSize.MiB)
        {
            return new GhostWarmUpPlan(
                gameId,
                0,
                0,
                Array.Empty<string>(),
                $"Insufficient cache headroom ({ByteSize.Format(availableBudget)} available); skipping speculative warm-up.",
                false);
        }

        var (sessions, history) = _store.LoadHistory(gameId);
        if (sessions == 0 || history.Count == 0)
        {
            return new GhostWarmUpPlan(
                gameId,
                0,
                0,
                Array.Empty<string>(),
                "No previous session profile found for this title; demand reads will be used.",
                false);
        }

        // Identify startup blocks accessed in the first 5000ms
        var targetBlocks = history
            .Where(b => b.MedianFirstAccessMs <= 5000.0 && b.SessionsSeen >= Math.Max(1, sessions / 2))
            .OrderBy(b => b.MedianFirstAccessMs)
            .Take(64) // cap to 64 blocks (64 MiB max)
            .ToList();

        long plannedBytes = targetBlocks.Count * (1024 * 1024); // Assuming 1MB blocks
        var highValueFiles = targetBlocks.Select(b => b.Block.Path).Distinct().ToList();

        bool feasible = plannedBytes <= maxAllowedBytes && plannedBytes < availableBudget * 0.5;

        return new GhostWarmUpPlan(
            gameId,
            plannedBytes,
            targetBlocks.Count,
            highValueFiles,
            feasible
                ? $"Identified {targetBlocks.Count} critical startup blocks ({ByteSize.Format(plannedBytes)}) observed across {sessions} launches."
                : $"Planned {ByteSize.Format(plannedBytes)} exceeds safe headroom or configured warm-up ceiling.",
            feasible);
    }

    /// <summary>
    /// Executes the warm-up plan. If a demand read arrives, the warm-up is immediately cancelled.
    /// </summary>
    public async Task<GhostWarmUpResult> ExecuteWarmUpAsync(
        GhostWarmUpPlan plan,
        Func<string, long, CancellationToken, Task<bool>> blockPrefetcher,
        CancellationToken externalCt = default)
    {
        if (!plan.FeasibleUnderBudget || plan.BlockCount == 0)
        {
            return new GhostWarmUpResult(false, 0, 0, TimeSpan.Zero, plan.Justification);
        }

        _activeWarmUpCts?.Cancel();
        _activeWarmUpCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _activeWarmUpCts.Token;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int prefetchedBlocks = 0;
        long prefetchedBytes = 0;

        try
        {
            foreach (var file in plan.HighValueFiles)
            {
                ct.ThrowIfCancellationRequested();
                bool success = await blockPrefetcher(file, 0, ct);
                if (success)
                {
                    prefetchedBlocks++;
                    prefetchedBytes += 1024 * 1024;
                }
            }

            sw.Stop();
            return new GhostWarmUpResult(true, prefetchedBytes, prefetchedBlocks, sw.Elapsed, null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new GhostWarmUpResult(false, prefetchedBytes, prefetchedBlocks, sw.Elapsed, "Preempted by foreground demand read or user cancellation.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new GhostWarmUpResult(false, prefetchedBytes, prefetchedBlocks, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// Immediately halts any in-flight speculative warm-up (called as soon as a game begins demand reads).
    /// </summary>
    public void Preempt()
    {
        _activeWarmUpCts?.Cancel();
    }
}
