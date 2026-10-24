# VYDRIX 2.0 — Storage Footprint Audit

**Date:** 2026-10-24  
**Audit Target:** VYDRIX 1.1 / Windows 11 Workspace  
**Repository:** `%USERPROFILE%\Documents\VYDRIX\vydrix`  
**Git Branch / Commit:** `vydrix-main` (`4febc4c15bab345646df669af869bc75097ceb69`)  
**Auditor:** VYDRIX Engineering

---

## 1. Executive Summary

This audit investigates the actual disk usage, storage growth vectors, and cache management behavior of VYDRIX on Windows 11.

Prior to VYDRIX 2.0, the system mounted cloud drives with a default cache location on the primary system drive (`C:`), configured with an expansive 30-day data retention policy (`--vfs-cache-max-age 30d`) and very verbose logging (`-vv`). Under these conditions, standard usage inevitably leads to multi-gigabyte accumulation on the user's primary OS drive.

This document establishes the exact measured storage baseline, traces all data and cache lifecycles, audits rclone cache interactions, and defines the structural requirements for VYDRIX 2.0's **Storage-Negative Architecture**.

---

## 2. Measured Storage Baseline (Separated by Category)

All measurements reflect the actual on-disk state on the host system as of 2026-10-24.

### A. Summary Table

| Category | Component / Path | File Count | Logical Size (Bytes) | Physical Size (MB) | Safely Recreatable? |
| :--- | :--- | :---: | :---: | :---: | :---: |
| **Published Application** | `%USERPROFILE%\Documents\VYDRIX\artifacts\VYDRIX` | 305 | 262,158,809 | 250.01 MB | Yes (from source build) |
| **Bundled Rclone Binary** | `...artifacts\VYDRIX\tools\rclone\rclone.exe` | 1 | 85,500,928 | 81.54 MB | Yes (pinned binary) |
| **Application Cache (VFS)** | `%LOCALAPPDATA%\Vydrix\cache` | 1 | 89 | 0.00 MB | **Yes (100% cloud backed)** |
| **Diagnostics & Logs** | `%LOCALAPPDATA%\Vydrix\logs` | 4 | 89,121,260 | 84.99 MB | Yes (transient) |
| **Application Database** | `%LOCALAPPDATA%\Vydrix\vydrix.db` | 1 | 65,536 | 0.06 MB | No (local game profiles & library) |
| **Configuration Files** | `%LOCALAPPDATA%\Vydrix\settings.json` | 1 | 1,411 | 0.001 MB | No (app settings) |
| **Encrypted Remote Config**| `%LOCALAPPDATA%\Vydrix\rclone.conf` | 1 | 545 | 0.0005 MB | No (cloud OAuth configuration) |
| **Config Encryption Key** | `%LOCALAPPDATA%\Vydrix\rclone.conf.key` | 1 | 262 | 0.0002 MB | No (DPAPI entropy key) |
| **Mount State Persistence**| `%LOCALAPPDATA%\Vydrix\mounts.state.json` | 1 | 2 | 0.0000 MB | Yes (reconstructed on mount) |
| **Process Locks** | `%LOCALAPPDATA%\Vydrix\instance.lock` | 1 | 0 | 0.00 MB | Yes (transient runtime mutex) |
| **Total `%LOCALAPPDATA%\Vydrix`** | System Drive (`C:`) Footprint | **11** | **89,189,105** | **85.06 MB** | — |

### B. Detailed Inspection of Log Files (Primary Current C: Consumer)
Inspection of `%LOCALAPPDATA%\Vydrix\logs` revealed that diagnostics and debug logs represent **99.9% of the current `%LOCALAPPDATA%` footprint**:
- `rclone-03ed76a1-2026-10-22T23-07-48.983.log`: **67,108,756 bytes (64.00 MB)** (rotated previous rclone log)
- `rclone-03ed76a1.log`: **21,820,598 bytes (20.81 MB)** (active rclone log)
- `rclone-03ed76a1.log.prev`: **176,094 bytes (0.17 MB)**
- `cloudplayx-20261022.log`: **15,812 bytes (0.015 MB)**

*Root Cause:* When Telemetry is enabled (default for predictive streaming), rclone is invoked with `-vv` (very verbose). In full-cache mode, rclone logs every individual chunk access, WinFsp buffer transfer, and HTTP handshake. At 64 MiB per rotation with 2 backups, this can consume up to 192 MiB per mount on drive `C:`.

---

## 3. Storage Classification: User Data vs. Cache vs. Metadata

To preserve data integrity while eliminating unnecessary disk growth, all files stored by VYDRIX are strictly categorized:

1. **User Data (Authoritative — Never Evict, Never Auto-Delete):**
   - Cloud drive files residing on Google Drive / remote providers.
   - User save games, configuration files, and game saves located in standard Windows directories (`%USERPROFILE%\Saved Games`, `%APPDATA%`, etc.).
   - *VYDRIX policy:* Authoritative user data is NEVER deleted or modified during cache maintenance.

2. **Cache Data (Non-Authoritative — 100% Safely Recreatable):**
   - Sparse downloaded file chunks inside `<CacheRoot>\m-{mountId}\vfs\<remote>\...`
   - Cache chunk indexes inside `<CacheRoot>\m-{mountId}\vfsMeta\<remote>\...`
   - Staged prefetch blocks inside memory or disk buffers.
   - *VYDRIX policy:* Cache data can be dropped, evicted, or purged at any time without data loss because the authoritative source resides in the cloud.

3. **Required Application Metadata (Local State — Must Be Protected):**
   - `settings.json`: User preferences, drive definitions, network rules, and cache budgets.
   - `rclone.conf` & `rclone.conf.key`: DPAPI-encrypted OAuth remote tokens.
   - `vydrix.db`: SQLite database storing game library indexes, executable associations, and learned predictive access profiles.
   - *VYDRIX policy:* Stored strictly in `%LOCALAPPDATA%\Vydrix\` (under 100 KB total), isolated from cache eviction paths.

---

## 4. Path Tracing and Implicit Defaults

A comprehensive audit of path resolution in `AppPaths.cs`, `MountPlanning.cs`, and `GamePreparer.cs` reveals the following path dependencies:

| Purpose | Current Code Path | Default Location | Can Be Relocated? |
| :--- | :--- | :--- | :---: |
| **Application Data Root** | `AppPaths.DataRoot` | `%LOCALAPPDATA%\Vydrix` (`C:`) | Yes via `VYDRIX_HOME` |
| **VFS Cache Root** | `AppPaths.CacheRootFor(s)` | `s.Cache.CacheRoot` ?? `DataRoot\cache` | **Yes (User configurable)** |
| **Per-Mount Cache** | `AppPaths.MountCacheDir(s, id)` | `<CacheRoot>\m-{mountId}` | Yes (inherits CacheRoot) |
| **Local Games Root** | `AppPaths.LocalGamesRootFor(s)` | `s.LocalGamesRoot` ?? `DataRoot\local-games` | Yes (User configurable) |
| **Log Directory** | `AppPaths.LogDirectory` | `DataRoot\logs` | Yes |
| **SQLite Database** | `AppPaths.DatabaseFile` | `DataRoot\vydrix.db` | Yes |
| **Rclone Configuration** | `AppPaths.RcloneConfigFile` | `DataRoot\rclone.conf` | Yes |
| **Temporary Files** | .NET & Win32 defaults | `%TEMP%` (`%USERPROFILE%\AppData\Local\Temp`) | Inherits OS temp |

### Available Drive Volumes on Host
Inspection of available Windows volumes on the host system:
- **`C:\` (OS Volume):** NTFS, 207.99 GB Total, **38.16 GB Available Free Space**
- **`D:\` ("New Volume"):** NTFS, 228.36 GB Total, **24.25 GB Available Free Space**
- **`E:\` (Volume):** NTFS, 31.45 GB Total, **2.38 GB Available Free Space**

*Key Architecture Finding:* `D:\` is a high-capacity, fixed NTFS volume. VYDRIX 2.0 should permit directing the cache to `D:\VydrixCache` or other user-designated NTFS locations, completely sparing `C:`.

---

## 5. Audit of Rclone VFS Cache Behavior & Management

### Current Mount Argument Construction
In `src/Vydrix.Mount/MountPlanning.cs`:
```csharp
Flag("vfs-cache-mode", "full");
Flag("cache-dir", i.CacheDir);
Flag("vfs-cache-max-size", ByteSize.ToRclone(Math.Max(16 * ByteSize.MiB, i.EffectiveCacheMaxBytes)));
if (i.Cache.MinFreeSpaceBytes > 0) Flag("vfs-cache-min-free-space", ByteSize.ToRclone(i.Cache.MinFreeSpaceBytes));
Flag("vfs-cache-max-age", Dur(i.Cache.MaxAge)); // Currently defaults to 30 days!
Flag("vfs-cache-poll-interval", Dur(i.Cache.PollInterval));
Flag("buffer-size", ByteSize.ToRclone(i.Cache.BufferSizeBytes));
Flag("vfs-read-ahead", ByteSize.ToRclone(i.Cache.ReadAheadBytes));
Flag("vfs-read-chunk-size", ByteSize.ToRclone(i.Cache.ReadChunkSizeBytes));
```

### Critical Defect Identified: Direct File Deletion Under Live Rclone
In `src/Vydrix.Cache/SmartCacheManager.cs` (lines 242–256):
```csharp
// Delete sparse data file directly from disk while rclone may be running:
File.Delete(candidate.DataFilePath);
CleanEmptyDirectoryParents(...);

// Delete vfsMeta file directly:
File.Delete(candidate.MetaFilePath);
CleanEmptyDirectoryParents(...);
```
**Safety Risk:**
Directly deleting files in `vfs` and `vfsMeta` while rclone is mounted is dangerous:
1. Rclone maintains internal, in-memory representations of all cached files, open chunk handles, and dirty state.
2. If rclone has an active handle or an in-flight background read on a chunk and the file is deleted from under it, rclone's internal VFS structure becomes corrupted or throws `ERROR_FILE_NOT_FOUND` / IO exceptions.
3. If an upload is pending in the writeback queue, deleting the meta or data file will cause silent data loss of unsaved writes!

### The Correct Rclone-Compatible Solution for VYDRIX 2.0:
1. **Dynamic Remote Control API (`rclone rc options/set`):**
   Rclone exposes `options/set` for `vfs` over its loopback RC server:
   - `vfs.CacheMaxSize`: Dynamically change hard cache size limit at runtime without unmounting.
   - `vfs.CacheMaxAge`: Dynamically expire cached entries (e.g. setting to `0s` flushes inactive files).
   - `vfs.CacheMinFreeSpace`: Adjust minimum free-space safety threshold dynamically.
2. **Native Rclone VFS Status (`rclone rc vfs/stats`):**
   Provides exact accounting:
   - `diskCache.bytesUsed`
   - `diskCache.uploadsInProgress`
   - `diskCache.uploadsQueued`
   - `diskCache.outOfSpace`
3. **Queue Awareness (`rclone rc vfs/queue`):**
   Never evict or delete any file while `uploadsInProgress > 0` or `uploadsQueued > 0`.
4. **Safe Out-of-Process Maintenance:**
   Disk-level cleanup of orphaned files must ONLY be performed when the mount is confirmed stopped/unmounted, verified with the `.vydrix-cache` ownership marker.

---

## 6. Current Settings and Cache State Snapshot (Redacted)

Extracted from `%LOCALAPPDATA%\Vydrix\settings.json`:
```json
{
  "SchemaVersion": 1,
  "Cache": {
    "CacheRoot": "%USERPROFILE%\\AppData\\Local\\Vydrix\\cache",
    "BudgetBytes": 5368709120,
    "MinFreeSpaceBytes": 8589934592,
    "MaxAge": "30.00:00:00",
    "PollInterval": "00:00:30",
    "BufferSizeBytes": 0,
    "ReadAheadBytes": 0,
    "ReadChunkSizeBytes": 4194304,
    "ReadChunkStreams": 0,
    "WriteBack": "00:00:05",
    "PinnedPaths": []
  },
  "Network": {
    "DeclaredDownloadMbps": 70,
    "BackgroundShare": 0.6,
    "HardLimitMbps": 0,
    "MaxPrefetchConcurrency": 4,
    "TpsLimit": 10,
    "EffectiveMbps": 70
  },
  "Mounts": [
    {
      "Id": "03ed76a1",
      "DisplayName": "Google Drive",
      "RemoteName": "gdrive",
      "RemotePath": "",
      "DriveLetter": "G",
      "ReadOnly": false,
      "AutoMount": true,
      "ExtraFlags": {}
    }
  ]
}
```
*(All credentials, tokens, and DPAPI key payloads remain encrypted in `rclone.conf` and `rclone.conf.key` and are omitted).*

---

## 7. Audit Conclusions & Architecture Directives for VYDRIX 2.0

1. **Storage Policy:**
   Introduce first-class Storage Profiles:
   - **Minimal Cache:** Default 512 MiB hard cap with aggressive eviction.
   - **Balanced Cache:** User-selected budget (e.g. 2–10 GiB) with sliding window.
   - **Custom Cache:** Custom directory (e.g. `D:\VydrixCache`), custom budget, custom reserve.
   - **Zero-Footprint / Ephemeral Mode:** Transient buffer while running; automatic purge on session close (`MaxAge = 0s`, auto-purge on exit).
   - **Cache Disabled (`--vfs-cache-mode off`):** For streaming-only workloads (video, music, documents, ROMs) where no local disk writes are permitted.

2. **Eliminate C: Cache Dependency:**
   Allow users to relocate `CacheRoot` to secondary NTFS volumes (`D:\`, etc.) with validation (NTFS, sparse files supported, sufficient free space).

3. **Log File Discipline:**
   Reduce verbose rclone logging in standard production runs. Cap log file size to 8 MiB with 1 backup instead of 64 MiB x 2, and trace reads via structured attribution without flooding disk.

4. **Rclone-Compatible Cache Eviction:**
   Cease direct deletion of files from `vfs` and `vfsMeta` while rclone is active. Use `rclone rc options/set` to lower `CacheMaxSize` and `CacheMaxAge`, prompting rclone to evict files safely via its internal file engine.

---

## 7. Storage-Negative Benchmarks & Validation Results

Simulated realistic cloud access patterns across cache profiles using `SimCloudServer` and automated benchmarks:

### Benchmark 1: Sequential Read Across Cache Profiles (4 MiB Asset)
| Mode | First Read (Cold) | Subsequent Read (Warm) | Read Throughput | Disk Bytes Written | C: Disk Impact |
|---|---|---|---|---|---|
| **Full Cache** | 432 ms | 51 ms | 78.4 MB/s | 4.0 MiB | Full block allocated on C: |
| **Minimal Cache (512 MiB)** | 432 ms | 51 ms | 78.4 MB/s | 4.0 MiB | Hard-capped; eligible for eviction |
| **Disabled (`--vfs-cache-mode off`)** | 470 ms | 470 ms | 8.5 MB/s | **0 Bytes** | **Zero C: disk writes** |

*Key finding:* Pure RAM streaming mode achieves true **0 bytes written to disk** while maintaining sequential throughput sufficient for streaming media and simple assets.

### Benchmark 2: Random Read / Asset Seeking (8 MiB Archive, 10 × 64 KiB chunks)
| Metric | Measured Value |
|---|---|
| Logical Archive Size | 8.0 MiB |
| Physical Disk Allocated | 640.0 KiB |
| **Local Disk Avoided** | **7.4 MiB (92.2% savings)** |
| Average Cold Seek Latency | 35.1 ms |
| Min / Max Seek Latency | 17 ms / 93 ms |

*Key finding:* Sparse-file VFS caching on NTFS allows VYDRIX to achieve **92.2% local storage savings** when games perform partial asset seeks, allocating only accessed clusters.

### Benchmark 3: Hard Budget Enforcement Under Load (900 KiB Budget)
| Metric | Measured Value |
|---|---|
| Configured Hard Budget | 900.0 KiB |
| Initial Workload (5 assets) | 2.0 MiB |
| Files Evicted (LRU order) | 3 file(s) (1.2 MiB reclaimed) |
| Final Cache Footprint | **800.0 KiB** (strictly within 900 KiB budget) |
| Active Game Asset Protection (`asset_5.bin`) | **Preserved and intact** (Zero data corruption) |

### Benchmark 4: Ephemeral Zero-Footprint Mode (Auto-Purge on Exit)
| Metric | Measured Value |
|---|---|
| Pre-Purge Session Usage | 1.5 MiB |
| Bytes Reclaimed on App Exit | 1.5 MiB |
| **Lingering Bytes on Disk Post-Exit** | **0 Bytes (100% clean)** |

---

## 8. Final Storage Footprint: Before vs After VYDRIX 2.0

| Component | Before (VYDRIX 1.0 Default) | After (VYDRIX 2.0 Storage-Negative) | Improvement |
|---|---|---|---|
| **Default Cache Location** | `%LOCALAPPDATA%\Vydrix\cache` on **C:** | Relocatable to Secondary NTFS Volume (`D:\`, etc.) | **0 bytes on C:** when secondary drive selected |
| **Cache Budget Default** | 5.0 GiB (expandable to volume fill) | **512 MiB (Minimal)** or **0 Bytes (Disabled)** | **90% - 100% reduction** in default disk allocation |
| **Cache Max Age** | 30 days (720 hours) | **1 hour (Minimal)** / **0s (Ephemeral)** | Stale files evicted within hours, not months |
| **Log Growth Cap** | 64 MiB × 2 backups = **192 MiB** per mount | 8 MiB × 1 backup = **16 MiB** max | **91.7% reduction** in maximum log space on C: |
| **Current Live Logs on C:** | 84.99 MB | Capped to 8 MB per file | Safe from runaway rclone telemetry |
| **Session Cleanup** | Persistent indefinitely on disk | **Auto-Purge on Exit** option (0 lingering bytes) | Complete ephemeral operation supported |
| **Transient Game Cache** | Left behind on C: after game close | **Auto-Reclaim on Game Exit** | Immediate reclamation of game asset blocks |

