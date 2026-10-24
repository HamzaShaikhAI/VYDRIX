using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Vydrix.Core;

namespace Vydrix.Cache;

/// <summary>
/// Tier A — Hot Memory Cache for small files and metadata blocks.
/// Provides near-instant microsecond reads for frequently hit assets.
/// Automatically trims memory under host system memory pressure.
/// </summary>
public sealed class MemoryHotCache : IDisposable
{
    private sealed class CacheEntry
    {
        public required string Key { get; init; }
        public required byte[] Data { get; init; }
        public DateTime LastAccess { get; set; }
        public int HitCount { get; set; }
        public long Size => Data.Length;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.OrdinalIgnoreCase);

    private long _currentBytes;
    private long _maxBytes;
    private bool _enabled;
    private long _hits;
    private long _misses;
    private bool _disposed;

    /// <summary>Maximum individual item size eligible for Tier A in-memory caching (default 512 KiB).</summary>
    public const long MaxItemSizeBytes = 512 * 1024;

    public MemoryHotCache(bool enabled = false, long maxBytes = 64 * ByteSize.MiB)
    {
        _enabled = enabled;
        _maxBytes = Math.Max(1024, maxBytes);
    }

    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set
        {
            lock (_lock)
            {
                _enabled = value;
                if (!_enabled) ClearInternal();
            }
        }
    }

    public long MaxBytes
    {
        get { lock (_lock) return _maxBytes; }
        set
        {
            lock (_lock)
            {
                _maxBytes = Math.Max(1024, value);
                TrimToCapacity();
            }
        }
    }

    public long CurrentBytes { get { lock (_lock) return _currentBytes; } }
    public int ItemCount { get { lock (_lock) return _entries.Count; } }
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public double HitRatio
    {
        get
        {
            var h = Hits;
            var total = h + Misses;
            return total == 0 ? 0.0 : (double)h / total;
        }
    }

    /// <summary>
    /// Attempts to read cached data from memory.
    /// </summary>
    public bool TryGet(string key, out byte[]? data)
    {
        lock (_lock)
        {
            if (!_enabled)
            {
                data = null;
                Interlocked.Increment(ref _misses);
                return false;
            }

            CheckSystemMemoryPressure();

            if (_entries.TryGetValue(key, out var entry))
            {
                entry.LastAccess = DateTime.Now;
                entry.HitCount++;

                if (_nodes.TryGetValue(key, out var node))
                {
                    _lruOrder.Remove(node);
                    _lruOrder.AddFirst(node);
                }

                data = (byte[])entry.Data.Clone();
                Interlocked.Increment(ref _hits);
                return true;
            }

            data = null;
            Interlocked.Increment(ref _misses);
            return false;
        }
    }

    /// <summary>
    /// Stores an item into the Tier A hot memory cache if eligible.
    /// </summary>
    public bool TryPut(string key, byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > MaxItemSizeBytes)
            return false;

        lock (_lock)
        {
            if (!_enabled) return false;

            CheckSystemMemoryPressure();

            // If already present, update and move to front
            if (_entries.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Size;
                _entries.Remove(key);
                if (_nodes.Remove(key, out var oldNode))
                {
                    _lruOrder.Remove(oldNode);
                }
            }

            var entry = new CacheEntry
            {
                Key = key,
                Data = (byte[])data.Clone(),
                LastAccess = DateTime.Now,
                HitCount = 1
            };

            _entries[key] = entry;
            _currentBytes += entry.Size;
            var newNode = _lruOrder.AddFirst(key);
            _nodes[key] = newNode;

            TrimToCapacity();
            return true;
        }
    }

    /// <summary>
    /// Explicitly invalidates a key from memory (e.g., when a remote file is updated).
    /// </summary>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            if (_entries.Remove(key, out var entry))
            {
                _currentBytes -= entry.Size;
                if (_nodes.Remove(key, out var node))
                {
                    _lruOrder.Remove(node);
                }
                return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock) ClearInternal();
    }

    private void ClearInternal()
    {
        _entries.Clear();
        _lruOrder.Clear();
        _nodes.Clear();
        _currentBytes = 0;
    }

    private void TrimToCapacity() => TrimToBytes(_maxBytes);

    private void TrimToBytes(long targetLimit)
    {
        while (_currentBytes > targetLimit && _lruOrder.Last != null)
        {
            var oldestKey = _lruOrder.Last.Value;
            if (_entries.Remove(oldestKey, out var removed))
            {
                _currentBytes -= removed.Size;
            }
            _nodes.Remove(oldestKey);
            _lruOrder.RemoveLast();
        }
    }

    /// <summary>
    /// Proactively monitors host memory pressure. If the system is under severe memory load (> 92%),
    /// Tier A automatically sheds 50% of its stored items to relieve host memory.
    /// </summary>
    private void CheckSystemMemoryPressure()
    {
        try
        {
            var memInfo = GC.GetGCMemoryInfo();
            if (memInfo.TotalAvailableMemoryBytes > 0)
            {
                double load = (double)memInfo.MemoryLoadBytes / memInfo.TotalAvailableMemoryBytes;
                if (load > 0.92)
                {
                    // Severe system memory load: shed down to half capacity without permanently mutating _maxBytes
                    TrimToBytes(_maxBytes / 2);
                }
            }
        }
        catch
        {
            // Best effort memory inspection
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}
