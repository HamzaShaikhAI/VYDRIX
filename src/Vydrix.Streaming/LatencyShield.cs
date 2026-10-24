using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Vydrix.Core;

namespace Vydrix.Streaming;

/// <summary>
/// Latency Shield: Network-aware controller that measures cloud round-trip times, throughput, and error rates.
/// Dynamically adapts concurrency, suppresses speculative work during network strain,
/// and provides plain-English bottleneck diagnostics.
/// </summary>
public sealed class LatencyShield
{
    private readonly object _lock = new();
    private readonly Queue<double> _rttSamples = new();
    private readonly Queue<double> _throughputSamples = new();
    private const int MaxSamples = 60;

    private int _concurrency = 8;
    private long _totalRequests;
    private long _failedRequests;
    private long _retriedRequests;

    public bool Enabled { get; set; } = true;
    public int Concurrency => _concurrency;
    public double CurrentAverageRttMs { get; private set; }
    public double CurrentAverageThroughputMBps { get; private set; }

    public void RecordMeasurement(double rttMs, double throughputMBps, bool success, bool retried = false)
    {
        lock (_lock)
        {
            _totalRequests++;
            if (!success) _failedRequests++;
            if (retried) _retriedRequests++;

            if (rttMs > 0)
            {
                _rttSamples.Enqueue(rttMs);
                if (_rttSamples.Count > MaxSamples) _rttSamples.Dequeue();
                CurrentAverageRttMs = _rttSamples.Average();
            }

            if (throughputMBps > 0)
            {
                _throughputSamples.Enqueue(throughputMBps);
                if (_throughputSamples.Count > MaxSamples) _throughputSamples.Dequeue();
                CurrentAverageThroughputMBps = _throughputSamples.Average();
            }

            if (Enabled)
            {
                AdaptParameters();
            }
        }
    }

    private void AdaptParameters()
    {
        // Concurrency adaptation based on RTT and error rate:
        // - Fast network (< 80ms RTT, 0 errors): scale concurrency up to 12
        // - Medium network (80 - 250ms RTT): keep 6 - 8
        // - Degraded network (> 250ms RTT or > 5% errors): throttle concurrency down to 2 - 4 to avoid queue congestion
        double errorRate = _totalRequests > 0 ? (double)_failedRequests / _totalRequests : 0.0;

        if (errorRate > 0.10 || CurrentAverageRttMs > 350.0)
        {
            _concurrency = 2; // Strict throttle
        }
        else if (errorRate > 0.04 || CurrentAverageRttMs > 200.0)
        {
            _concurrency = 4;
        }
        else if (CurrentAverageRttMs < 80.0 && errorRate == 0.0)
        {
            _concurrency = 12; // High-performance parallel pipeline
        }
        else
        {
            _concurrency = 8; // Balanced default
        }
    }

    /// <summary>
    /// Diagnoses the primary operational bottleneck in plain English with actionable advice.
    /// </summary>
    public BottleneckDiagnosis DiagnoseBottleneck(double cacheHitRatio, double localDiskWriteLatencyMs, double cpuLoadRatio)
    {
        lock (_lock)
        {
            double errorRate = _totalRequests > 0 ? (double)_failedRequests / _totalRequests : 0.0;

            // 1. Provider Rate Limiting (HTTP 429 / TPS overflow)
            if (errorRate > 0.15)
            {
                return new BottleneckDiagnosis(
                    BottleneckType.ProviderRateLimit,
                    "Cloud Provider Rate Limiting",
                    "Google Drive API has throttled request frequency (HTTP 429 / TPS limit exceeded).",
                    CurrentAverageRttMs,
                    CurrentAverageThroughputMBps,
                    errorRate,
                    "Enable request coalescing and lower concurrent request limit to reduce provider query rate.");
            }

            // 2. Network / Cloud Latency
            if (CurrentAverageRttMs > 250.0)
            {
                return new BottleneckDiagnosis(
                    BottleneckType.NetworkCloud,
                    "High Cloud Round-Trip Latency",
                    $"Cloud round-trip time is elevated ({CurrentAverageRttMs:F0}ms average RTT).",
                    CurrentAverageRttMs,
                    CurrentAverageThroughputMBps,
                    errorRate,
                    "Prioritize demand reads, pause speculative background prefetching, and avoid downloading large files simultaneously.");
            }

            // 3. Local Storage I/O
            if (localDiskWriteLatencyMs > 60.0)
            {
                return new BottleneckDiagnosis(
                    BottleneckType.LocalStorageIo,
                    "Local Disk I/O Saturation",
                    $"Local drive write latency is elevated ({localDiskWriteLatencyMs:F0}ms write time on cache volume).",
                    CurrentAverageRttMs,
                    CurrentAverageThroughputMBps,
                    errorRate,
                    "Consider migrating cache root to a faster secondary NVMe SSD (e.g. D:) or enabling Tier A memory cache.");
            }

            // 4. Local CPU / Buffer Bottleneck
            if (cpuLoadRatio > 0.90)
            {
                return new BottleneckDiagnosis(
                    BottleneckType.LocalCpuBuffer,
                    "System CPU Saturation",
                    $"Host CPU utilization is near 100% ({cpuLoadRatio:P0}). Processing I/O completion buffers is delayed.",
                    CurrentAverageRttMs,
                    CurrentAverageThroughputMBps,
                    errorRate,
                    "Lower active game background tasks and reduce concurrent stream count.");
            }

            // 5. Healthy SSD Performance
            return new BottleneckDiagnosis(
                BottleneckType.HealthySsd,
                "Phantom SSD Performing Optima",
                $"Virtual drive responding within target parameters ({CurrentAverageRttMs:F0}ms RTT, {cacheHitRatio:P1} hit ratio).",
                CurrentAverageRttMs,
                CurrentAverageThroughputMBps,
                errorRate,
                "System is operating in near-physical-SSD mode with optimal throughput.");
        }
    }
}
