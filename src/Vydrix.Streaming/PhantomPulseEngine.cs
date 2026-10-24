using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Vydrix.Core;

namespace Vydrix.Streaming;

/// <summary>
/// Phantom Pulse: Workload-aware heuristic classifier.
/// Analyzes ordinary filesystem access telemetry to select the optimal performance mode
/// (Sequential, RandomRead, SmallFile, LowBandwidth, LowLatency, StrictCache) without inspecting game contents.
/// </summary>
public sealed class PhantomPulseEngine
{
    private readonly object _lock = new();
    private readonly List<AccessEvent> _recentEvents = new();
    private const int SampleWindowSize = 100;

    public PhantomPulseMode? ManualOverride { get; set; }
    public PulseClassification CurrentClassification { get; private set; }

    public PhantomPulseEngine()
    {
        CurrentClassification = new PulseClassification(
            PhantomPulseMode.Auto,
            1.0,
            "Default baseline; waiting for sufficient workload samples.",
            0);
    }

    /// <summary>
    /// Feeds an observed read event into the heuristic classifier.
    /// </summary>
    public void ObserveAccess(AccessEvent e)
    {
        lock (_lock)
        {
            _recentEvents.Add(e);
            if (_recentEvents.Count > SampleWindowSize)
            {
                _recentEvents.RemoveRange(0, _recentEvents.Count - SampleWindowSize);
            }

            if (_recentEvents.Count >= 10)
            {
                ClassifyWorkload();
            }
        }
    }

    private void ClassifyWorkload()
    {
        if (ManualOverride.HasValue)
        {
            CurrentClassification = new PulseClassification(
                ManualOverride.Value,
                1.0,
                $"Manual user override active: {ManualOverride.Value}",
                _recentEvents.Count);
            return;
        }

        var samples = _recentEvents.ToList();
        int count = samples.Count;
        if (count == 0) return;

        // 1. Check for small-file burst: Many distinct paths accessed with length < 128 KiB
        int smallFileReads = samples.Count(s => s.Length < 128 * 1024);
        double smallFileRatio = (double)smallFileReads / count;
        int distinctFiles = samples.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (smallFileRatio > 0.70 && distinctFiles >= 5)
        {
            CurrentClassification = new PulseClassification(
                PhantomPulseMode.SmallFile,
                0.85,
                $"High frequency of small file reads detected ({smallFileRatio:P0} < 128 KiB across {distinctFiles} files).",
                count);
            return;
        }

        // 2. Check for sequential reads: Monotonically increasing offsets within the same file
        int sequentialTransitions = 0;
        for (int i = 1; i < count; i++)
        {
            var prev = samples[i - 1];
            var curr = samples[i];
            if (string.Equals(prev.Path, curr.Path, StringComparison.OrdinalIgnoreCase))
            {
                var diff = curr.Offset - prev.Offset;
                if (diff >= 0 && diff <= 4 * 1024 * 1024)
                {
                    sequentialTransitions++;
                }
            }
        }

        double seqRatio = (double)sequentialTransitions / Math.Max(1, count - 1);
        if (seqRatio > 0.55)
        {
            CurrentClassification = new PulseClassification(
                PhantomPulseMode.Sequential,
                0.90,
                $"Sequential streaming detected ({seqRatio:P0} contiguous reads in loading phase).",
                count);
            return;
        }

        // 3. Check for latency or network dips
        var avgWaitMs = samples.Where(s => s.WaitMs.HasValue).Select(s => s.WaitMs!.Value).DefaultIfEmpty(0).Average();
        if (avgWaitMs > 350.0)
        {
            CurrentClassification = new PulseClassification(
                PhantomPulseMode.LowBandwidth,
                0.80,
                $"Elevated cloud read wait times ({avgWaitMs:F1}ms average latency).",
                count);
            return;
        }

        // 4. Default to RandomRead if random seeks dominate
        CurrentClassification = new PulseClassification(
            PhantomPulseMode.RandomRead,
            0.75,
            $"Scattered asset accesses detected across {distinctFiles} game files.",
            count);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _recentEvents.Clear();
            CurrentClassification = new PulseClassification(
                PhantomPulseMode.Auto,
                1.0,
                "Reset workload profile.",
                0);
        }
    }
}
