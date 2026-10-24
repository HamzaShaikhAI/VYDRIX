using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vydrix.Core;

namespace Vydrix.Streaming;

public sealed record CachedMetadata(
    string Path,
    bool Exists,
    long Size,
    DateTime ModTime,
    bool IsDirectory,
    DateTime CachedAt,
    TimeSpan Ttl);

/// <summary>
/// Metadata Acceleration: Provides bounded TTL in-memory caching of directory listings and file stat metadata,
/// eliminating repeated remote round-trips for small files and repeated directory scans.
/// </summary>
public sealed class MetadataCache
{
    private readonly ConcurrentDictionary<string, CachedMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Entries, DateTime CachedAt, TimeSpan Ttl)> _dirListings = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(10);

    public bool TryGetFileMetadata(string key, out CachedMetadata? metadata)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            if (DateTime.Now - cached.CachedAt <= cached.Ttl)
            {
                metadata = cached;
                return true;
            }
            _cache.TryRemove(key, out _);
        }

        metadata = null;
        return false;
    }

    public void SetFileMetadata(string key, bool exists, long size, DateTime modTime, bool isDirectory, TimeSpan? ttl = null)
    {
        _cache[key] = new CachedMetadata(
            key,
            exists,
            size,
            modTime,
            isDirectory,
            DateTime.Now,
            ttl ?? DefaultTtl);
    }

    public bool TryGetDirectoryListing(string dirKey, out IReadOnlyList<string>? entries)
    {
        if (_dirListings.TryGetValue(dirKey, out var cached))
        {
            if (DateTime.Now - cached.CachedAt <= cached.Ttl)
            {
                entries = cached.Entries;
                return true;
            }
            _dirListings.TryRemove(dirKey, out _);
        }

        entries = null;
        return false;
    }

    public void SetDirectoryListing(string dirKey, IReadOnlyList<string> entries, TimeSpan? ttl = null)
    {
        _dirListings[dirKey] = (entries.ToList(), DateTime.Now, ttl ?? DefaultTtl);
    }

    public void Invalidate(string pathKey)
    {
        _cache.TryRemove(pathKey, out _);

        // Also invalidate parent directory listing
        var parent = Path.GetDirectoryName(pathKey.Replace('/', '\\'))?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent))
        {
            _dirListings.TryRemove(parent, out _);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        _dirListings.Clear();
    }
}
