# VYDRIX Zero Footprint Mode — Architecture Audit & Inspection Report

**Date:** 2026-10-24  
**Target Version:** VYDRIX 1.2.0  
**Repository:** `C:\Users\win10\Documents\VYDRIX\vydrix`  
**Current Branch:** `vydrix-main`  
**Commit:** `2c17199`  

---

## 1. Existing Cache Architecture

VYDRIX interfaces with cloud storage via a unified Windows virtual-drive architecture:

```
[ Windows Applications / Games ]
              │ (Win32 I/O, Memory-Mapped Files)
              ▼
    [ WinFsp Driver (G:) ]
              │
              ▼
   [ Bundled rclone v1.75.2 ] ◄─────── [ VYDRIX Desktop / CLI Engine ]
              │                                      │ (RC API: options/set, forget)
     ┌────────┴────────┐                             │
     ▼                 ▼                             ▼
[ Sparse Data ]  [ vfsMeta JSON ]       [ SmartCacheManager (Budget & Eviction) ]
 %LOCALAPPDATA%   %LOCALAPPDATA%                     │
  \Vydrix\cache    \Vydrix\cache                     ▼
                                         [ StreamingEngine (Demand Reads Only) ]
```

### Components
1. **rclone VFS Cache Engine:**
   - Configured with `--vfs-cache-mode full`.
   - Downloads requested file ranges into sparse NTFS files on `C:`, allocating disk blocks on demand (`GetCompressedFileSizeW`).
   - Caches metadata as JSON in `vfsMeta` containing `Size`, `ATime`, `Rs` (array of cached byte ranges `{Pos, Size}`), `Dirty`, and `Fingerprint`.
2. **Cache Root Layout:**
   - Root: `%LOCALAPPDATA%\Vydrix\cache\m-{mountId}\`
   - Data directory: `vfs\{fsName}\...`
   - Metadata directory: `vfsMeta\{fsName}\...`
   - Safety marker: `.vydrix-cache` (and legacy `.cloudplayx-cache`).
3. **Control Protocol:**
   - Remote Control (RC) over Loopback HTTP (`127.0.0.1:{port}`).
   - Endpoints used: `options/set`, `vfs/forget`, `vfs/stats`, `core/stats`.
4. **Current Default Budget:**
   - `settings.json` previously configured with `BudgetBytes = 5,368,709,120` (5 GiB) and `MaxAge = 30 days`.

---

## 2. Identified Local Storage Paths & Measured Footprints

Empirical measurements gathered from the live Windows 11 host:

| Storage Domain | File System Path | File Count | Logical Size | Physical Disk Usage | Purpose / Contents |
|---|---|---|---|---|---|
| **Production Binaries** | `C:\Users\win10\Documents\VYDRIX\artifacts\VYDRIX` | 305 files | 262.16 MB | 262.20 MB | VYDRIX 1.0 release build, WPF GUI, CLI, .NET runtime |
| **Bundled Tooling** | `artifacts\VYDRIX\tools\rclone\rclone.exe` | 1 file | 85.50 MB | 85.50 MB | Official rclone v1.75.2 win-x64 binary |
| **Staged Binaries** | `artifacts\VYDRIX-2.0-staging` | 69 files | 123.28 MB | 123.30 MB | VYDRIX 2.0 release candidate |
| **VFS Data Cache** | `%LOCALAPPDATA%\Vydrix\cache\m-03ed76a1\vfs` | 0 files | 0 Bytes | 0 Bytes | Sparse chunks for Google Drive (`G:`) |
| **VFS Metadata** | `%LOCALAPPDATA%\Vydrix\cache\m-03ed76a1\vfsMeta` | 0 files | 0 Bytes | 0 Bytes | JSON range indices and file metadata |
| **Cache Marker** | `%LOCALAPPDATA%\Vydrix\cache\m-03ed76a1\.cloudplayx-cache` | 1 file | 89 Bytes | 4.00 KiB | Volume ownership marker |
| **Telemetry Logs** | `%LOCALAPPDATA%\Vydrix\logs` | 4 files | 89.12 MB | 89.12 MB | Structured rclone & application logs (`-vv` telemetry) |
| **SQLite Database** | `%LOCALAPPDATA%\Vydrix\vydrix.db` | 1 file | 65.54 KiB | 68.00 KiB | Game library records, compatibility rules, learned profiles |
| **Settings & State** | `%LOCALAPPDATA%\Vydrix\settings.json` | 1 file | 1.41 KiB | 4.00 KiB | App settings, cache budget, mount configurations |
| **Credentials & Auth** | `%LOCALAPPDATA%\Vydrix\rclone.conf` | 1 file | 545 Bytes | 4.00 KiB | Encrypted Google Drive OAuth token |
| **DPAPI Key** | `%LOCALAPPDATA%\Vydrix\rclone.conf.key` | 1 file | 262 Bytes | 4.00 KiB | Windows DPAPI encrypted key protecting `rclone.conf` |
| **Runtime Lock** | `%LOCALAPPDATA%\Vydrix\instance.lock` | 1 file | 0 Bytes | 0 Bytes | Single-instance mutual exclusion lock |
| **Mounts State** | `%LOCALAPPDATA%\Vydrix\mounts.state.json` | 1 file | 2 Bytes | 4.00 KiB | Last known live mount IDs |

---

## 3. Unavoidable Non-Cache Storage

Zero Footprint Mode minimizes **transient cached cloud blocks**, but does **not** claim literal zero local disk usage. The following storage is strictly non-cache and unavoidable:
1. **Application Binaries & Runtime:** ~123 MB (VYDRIX executable, WPF dependencies, bundled rclone).
2. **Authentication & Cryptographic Material:** ~5 KB (DPAPI key, encrypted OAuth token).
3. **Database & Settings:** ~70 KB (`vydrix.db`, `settings.json`).
4. **Log Rotation Buffer:** Capped at 8 MiB (1 backup = 16 MiB max).
5. **VFS Metadata Overhead:** ~16 MiB reserved within the 512 MiB budget for sparse-range bookkeeping and JSON headers.

---

## 4. Risks & Defects Discovered

1. **Risk 1: Concurrent Request Budget Blowout**
   - *Defect:* If 20 threads simultaneously request un-cached 4 MiB chunks, each thread checking `currentUsage < budget` would individually pass, downloading 80 MiB even if only 10 MiB remained in the budget.
   - *Mitigation:* Implement synchronized `TryReserveCapacity` in `SmartCacheManager` that atomically tracks in-flight reservations and rejects/pauses excess requests.
2. **Risk 2: Out-of-Band File Deletion during Active Mounts**
   - *Defect:* Deleting files directly from `vfs/` or `vfsMeta/` while rclone has open handles triggers `ERROR_SHARING_VIOLATION` or desynchronizes rclone's internal cache table.
   - *Mitigation:* Route live cache evictions strictly through rclone RC `options/set` (lowering `vfs.CacheMaxSize`) and `vfs/forget`. Direct file unlinking is strictly restricted to unmounted folders or post-session cleanup after all handles have drained.
3. **Risk 3: Speculative Prefetch Bloat**
   - *Defect:* `StreamingEngine` contains `MarkovPredictor`, `SequentialPredictor`, and `StartupPredictor` that automatically enqueue background downloads, consuming the 512 MiB budget for speculative data.
   - *Mitigation:* When Zero Footprint Mode is active, hard-disable all speculative predictors. Ensure `ReadAheadBytes = 0`, `BufferSizeBytes = 0`, and prefetch schedulers are paused and drained.
4. **Risk 4: Game Exit Race Condition**
   - *Defect:* Games often spawn launcher helper processes or anti-cheat daemons. If VYDRIX triggers cleanup when the primary process exits while helpers are still flushing data, game data or cache handles can be corrupted.
   - *Mitigation:* Monitor the complete Windows Job Object process tree. Wait for all child processes to terminate, enforce a settling/drain delay (1.5 seconds), check `GetPendingUploadsCountAsync() == 0`, and only then execute cache cleanup.
5. **Risk 5: Metadata Starvation**
   - *Defect:* If data chunks consume 100% of 512 MiB, rclone cannot write new `vfsMeta` JSON files or directory listings.
   - *Mitigation:* Enforce a **16 MiB metadata headroom reservation**, capping raw cached data at 496 MiB.

---

## 5. Proposed Changes & Rollback Strategy

1. **Phase B:** Set default Zero Footprint budget to exactly `536,870,912` bytes (512 MiB). Implement thread-safe in-flight capacity reservations and metadata headroom reservation.
2. **Phase C:** Implement demand-only mode in `StreamingEngine`, disabling Markov, sequential, and startup prefetching.
3. **Phase D:** Enhance game lifecycle monitoring with multi-game process tree tracking and settling drains. Set scheduled maintenance to 2 hours default.
4. **Phase E:** Implement multi-level protection filters (active handles, dirty files, pinned paths, incomplete downloads, metadata files).
5. **Phase F:** Update WPF UI and Settings to expose Zero Footprint Mode with live C: statistics and a "Run safe cleanup now" command.
6. **Rollback Plan:** Configuration additions are backwards-compatible; staged builds do not overwrite production `artifacts/VYDRIX` until verified and promoted.
