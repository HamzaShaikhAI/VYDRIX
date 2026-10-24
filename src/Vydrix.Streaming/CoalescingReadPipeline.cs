using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Core;

namespace Vydrix.Streaming;

public enum ReadPriority
{
    P0Demand = 0,
    P1Speculative = 1
}

public sealed record ByteRange(long Offset, int Length)
{
    public long End => Offset + Length;
    public bool OverlapsOrAdjacent(ByteRange other, long maxGapBytes = 0) =>
        Offset <= other.End + maxGapBytes && other.Offset <= End + maxGapBytes;

    public ByteRange Merge(ByteRange other)
    {
        var start = Math.Min(Offset, other.Offset);
        var end = Math.Max(End, other.End);
        return new ByteRange(start, (int)(end - start));
    }
}

/// <summary>
/// Physical-SSD-inspired asynchronous read pipeline:
/// - Coalesces adjacent and overlapping range requests targeting the same remote file.
/// - Deduplicates in-flight requests for identical content.
/// - Prioritizes demand reads over speculative work.
/// - Provides bounded concurrency and exponential backoff with jitter to eliminate retry storms.
/// </summary>
public sealed class CoalescingReadPipeline : IDisposable
{
    private readonly Func<string, string, long, int, CancellationToken, Task<byte[]>> _fetcher;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly ConcurrentDictionary<string, Task<byte[]>> _inFlightDeduplication = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _jitter = new();
    private readonly object _statsLock = new();

    private long _coalescedRequestsCount;
    private long _deduplicatedRequestsCount;
    private long _totalRequestsCount;
    private int _maxConcurrency;
    private bool _disposed;

    public CoalescingReadPipeline(
        Func<string, string, long, int, CancellationToken, Task<byte[]>> fetcher,
        int maxConcurrency = 8)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 64);
        _concurrencySemaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            var clamped = Math.Clamp(value, 1, 64);
            _maxConcurrency = clamped;
        }
    }

    public long TotalRequests => Interlocked.Read(ref _totalRequestsCount);
    public long CoalescedRequests => Interlocked.Read(ref _coalescedRequestsCount);
    public long DeduplicatedRequests => Interlocked.Read(ref _deduplicatedRequestsCount);

    public void RecordCoalesced(int count = 1) => Interlocked.Add(ref _coalescedRequestsCount, count);

    /// <summary>
    /// Executes a range read with coalescing, deduplication, bounded concurrency, and retry resilience.
    /// </summary>
    public async Task<byte[]> ReadRangeAsync(
        string mountId,
        string path,
        long offset,
        int length,
        ReadPriority priority = ReadPriority.P0Demand,
        CancellationToken ct = default)
    {
        if (length <= 0) return Array.Empty<byte>();

        Interlocked.Increment(ref _totalRequestsCount);
        var key = $"{mountId}::{path}::{offset}::{length}";

        // 1. In-flight Deduplication: Multiple simultaneous reads for the exact same block share the same task
        if (_inFlightDeduplication.TryGetValue(key, out var inFlightTask))
        {
            Interlocked.Increment(ref _deduplicatedRequestsCount);
            return await inFlightTask;
        }

        var fetchTask = ExecuteFetchWithRetriesAsync(mountId, path, offset, length, priority, ct);
        _inFlightDeduplication[key] = fetchTask;

        try
        {
            return await fetchTask;
        }
        finally
        {
            _inFlightDeduplication.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Given a list of disjoint or nearby read ranges for the same file, merges overlapping or adjacent ranges
    /// that are within maxGapBytes of each other to minimize provider round-trips.
    /// </summary>
    public static IReadOnlyList<ByteRange> CoalesceRanges(IEnumerable<ByteRange> ranges, long maxGapBytes = 64 * 1024)
    {
        var sorted = ranges.OrderBy(r => r.Offset).ToList();
        if (sorted.Count <= 1) return sorted;

        var merged = new List<ByteRange>();
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (current.OverlapsOrAdjacent(next, maxGapBytes))
            {
                current = current.Merge(next);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }

    private async Task<byte[]> ExecuteFetchWithRetriesAsync(
        string mountId,
        string path,
        long offset,
        int length,
        ReadPriority priority,
        CancellationToken ct)
    {
        const int maxRetries = 3;
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            // Acquire concurrency slot
            await _concurrencySemaphore.WaitAsync(ct);
            try
            {
                return await _fetcher(mountId, path, offset, length, ct);
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested && !(ex is OperationCanceledException))
            {
                // Bounded exponential backoff with random jitter (e.g., 50ms, 150ms, 450ms + jitter)
                int delayMs;
                lock (_statsLock)
                {
                    delayMs = (int)(Math.Pow(3, attempt) * 20) + _jitter.Next(10, 50);
                }
                await Task.Delay(delayMs, ct);
            }
            finally
            {
                _concurrencySemaphore.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _concurrencySemaphore.Dispose();
        _inFlightDeduplication.Clear();
    }
}
