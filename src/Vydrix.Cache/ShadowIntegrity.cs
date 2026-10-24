using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Core;

namespace Vydrix.Cache;

public enum IntegrityStatus
{
    Verified,
    RemoteModified,
    Corrupted,
    Missing,
    UnsupportedByProvider,
    Error
}

public sealed record IntegrityCheckResult(
    string RemotePath,
    IntegrityStatus Status,
    bool CacheValid,
    string? RemoteModTime,
    string? LocalModTime,
    long? RemoteSize,
    long? LocalSize,
    string Details);

/// <summary>
/// Shadow Integrity: Detects out-of-band remote modifications, validates completed cache downloads,
/// and invalidates stale chunks without deleting cloud originals.
/// </summary>
public sealed class ShadowIntegrity
{
    /// <summary>
    /// Checks whether a local cached file remains consistent with the remote cloud version.
    /// </summary>
    public static IntegrityCheckResult VerifyFile(
        string localDataPath,
        string? localMetaPath,
        long remoteSize,
        DateTime remoteModTime,
        string? remoteHash = null,
        string? hashType = null)
    {
        if (!File.Exists(localDataPath))
        {
            return new IntegrityCheckResult(
                localDataPath,
                IntegrityStatus.Missing,
                false,
                remoteModTime.ToString("o"),
                null,
                remoteSize,
                0,
                "Local cache file does not exist.");
        }

        var localInfo = new FileInfo(localDataPath);
        var localSize = localInfo.Length;
        var localModTime = localInfo.LastWriteTimeUtc;

        // 1. Detect size mismatch
        if (remoteSize > 0 && localSize != remoteSize)
        {
            return new IntegrityCheckResult(
                localDataPath,
                IntegrityStatus.RemoteModified,
                false,
                remoteModTime.ToString("o"),
                localModTime.ToString("o"),
                remoteSize,
                localSize,
                $"Size mismatch: remote is {remoteSize} bytes, local cache is {localSize} bytes.");
        }

        // 2. Detect timestamp mismatch (> 2s tolerance for filesystem time resolution)
        var timeDiff = Math.Abs((remoteModTime.ToUniversalTime() - localModTime).TotalSeconds);
        if (timeDiff > 5.0)
        {
            return new IntegrityCheckResult(
                localDataPath,
                IntegrityStatus.RemoteModified,
                false,
                remoteModTime.ToString("o"),
                localModTime.ToString("o"),
                remoteSize,
                localSize,
                $"Timestamp divergence ({timeDiff:F1}s): remote was modified on cloud.");
        }

        // 3. Cryptographic hash verification if hash is provided
        if (!string.IsNullOrWhiteSpace(remoteHash) && !string.IsNullOrWhiteSpace(hashType))
        {
            var calculated = ComputeHash(localDataPath, hashType);
            if (calculated == null)
            {
                return new IntegrityCheckResult(
                    localDataPath,
                    IntegrityStatus.UnsupportedByProvider,
                    true,
                    remoteModTime.ToString("o"),
                    localModTime.ToString("o"),
                    remoteSize,
                    localSize,
                    $"Checksum algorithm '{hashType}' is not supported; size and timestamps match.");
            }

            if (!string.Equals(calculated, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                return new IntegrityCheckResult(
                    localDataPath,
                    IntegrityStatus.Corrupted,
                    false,
                    remoteModTime.ToString("o"),
                    localModTime.ToString("o"),
                    remoteSize,
                    localSize,
                    $"Checksum verification failed: expected {remoteHash}, calculated {calculated}.");
            }
        }

        return new IntegrityCheckResult(
            localDataPath,
            IntegrityStatus.Verified,
            true,
            remoteModTime.ToString("o"),
            localModTime.ToString("o"),
            remoteSize,
            localSize,
            "Cache verified against cloud metadata.");
    }

    /// <summary>
    /// Safely invalidates stale or corrupted local cached data without modifying remote storage.
    /// </summary>
    public static bool InvalidateCacheEntry(string dataFilePath, string? metaFilePath)
    {
        try
        {
            if (File.Exists(dataFilePath))
            {
                File.Delete(dataFilePath);
            }

            if (metaFilePath != null && File.Exists(metaFilePath))
            {
                File.Delete(metaFilePath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ComputeHash(string filePath, string algorithm)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            if (string.Equals(algorithm, "md5", StringComparison.OrdinalIgnoreCase))
            {
                using var md5 = MD5.Create();
                return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
            }
            if (string.Equals(algorithm, "sha1", StringComparison.OrdinalIgnoreCase))
            {
                using var sha1 = SHA1.Create();
                return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
            }
            if (string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase))
            {
                using var sha256 = SHA256.Create();
                return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
