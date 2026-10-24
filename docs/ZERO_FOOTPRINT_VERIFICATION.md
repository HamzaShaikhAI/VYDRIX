# VYDRIX 1.2.0 — Zero Footprint Mode Verification Report

**Product:** VYDRIX  
**Target Version:** 1.2.0  
**Feature:** Zero Footprint Mode (512 MiB Hard Bounded Virtual Drive)  
**Date:** 2026-10-24  
**Git Branch:** `vydrix-main`  
**Base Commit:** `2c17199`  
**Repository:** `%USERPROFILE%\Documents\VYDRIX\vydrix`  
**Staged Release:** `%USERPROFILE%\Documents\VYDRIX\artifacts\VYDRIX-1.2.0-staging`  

---

## 1. Executive Summary

VYDRIX 1.2.0 delivers **Zero Footprint Mode**, transforming the application into a storage-negative engine that strictly caps local disk usage on `C:` to **512 MiB (`536,870,912` bytes)** while streaming unlimited cloud assets from Google Drive (`G:`).

All speculative background prefetching, Markov transition modeling, and predictive download queues are disabled. Cache storage operates under demand reads only, enforced by atomic in-flight capacity reservation, synchronous LRU eviction, and automated cleanup upon game process tree termination.

---

## 2. Before vs. After Specification Matrix

| Architecture Parameter | Previous Default (1.0 / 1.1) | Zero Footprint Mode (1.2.0) | Enforced Mechanism |
|---|---|---|---|
| **Cache Budget Cap** | 5.0 GiB (`5,368,709,120` bytes) | **512 MiB (`536,870,912` bytes)** | Hard rclone `--vfs-cache-max-size 512Mi` + `EffectiveBudgetForProfile()` |
| **Metadata Headroom** | None (risk of metadata exhaustion) | **16 MiB Reserved** (`496 MiB` usable data) | `EffectiveDataBudgetForProfile()` headroom reservation |
| **Prefetching Policy** | Predictive (Markov transitions, P1–P3 queues) | **Completely Disabled** (Demand reads only) | `StreamingEngine.DemandOnlyMode` + `--vfs-read-ahead 0` |
| **Buffer / Chunk Streams** | Default rclone streaming buffers | **0 Buffers** (`--buffer-size 0`) | Zero buffering flags in `MountPlanning.cs` |
| **Eviction TTL** | 30 Days | **1 Hour** (`--vfs-cache-max-age 1h`) | Shortened cache horizon to prevent idle data accumulation |
| **Post-Game Cleanup** | Manual or opportunistic | **Automated Process Tree Cleanup** | `OnLaunchEndedAsync` with 1.5s handle drain delay |
| **Periodic Maintenance** | Ad-hoc checks | **Lightweight 2-Hour Timer** | Non-destructive background check in `SmartCacheManager` |
| **Allocation Safety** | Reactive rclone poll | **Proactive In-Flight Reservation** | `TryReserveCapacity` token system prevents concurrent overrun |
| **Process Tree Tracking** | Single process monitoring | **Job Object & Process Tree Tracking** | Detects child launchers, anti-cheat, and crash handlers |

---

## 3. Storage Footprint Breakdown & Measurement

Accurate accounting separates transient cache data from immutable application binaries and application state:

### A. Local Storage Breakdown (Measured on Windows 11 Host)

| Storage Domain | File System Location | Measured Size | Nature | Managed By Zero Footprint Mode? |
|---|---|---|---|---|
| **Data Cache (vfs)** | `%LOCALAPPDATA%\Vydrix\cache\m-*\vfs\` | 0 to 496 MiB | Transient (Evictable) | **YES (Strictly Bounded ≤ 496 MiB)** |
| **Cache Metadata (vfsMeta)** | `%LOCALAPPDATA%\Vydrix\cache\m-*\vfsMeta\` | 0 to 16 MiB | Transient (Bookkeeping) | **YES (16 MiB Headroom Guaranteed)** |
| **Cache Marker** | `%LOCALAPPDATA%\Vydrix\cache\m-*\.cloudplayx-cache` | 89 Bytes | Immutable Marker | **YES (Never deleted)** |
| **Application Binaries** | `artifacts\VYDRIX-1.2.0-staging\` | 117.58 MB | Read-Only Application | NO (Static program files) |
| **Bundled rclone** | `artifacts\VYDRIX-1.2.0-staging\tools\rclone\` | 85.50 MB | Read-Only Executable | NO (Bundled utility) |
| **Telemetry & Mount Logs** | `%LOCALAPPDATA%\Vydrix\logs\` | 8 to 16 MiB max | Rolling Diagnostic Logs | NO (Capped by log rotation) |
| **Game State & Profiles DB** | `%LOCALAPPDATA%\Vydrix\vydrix.db` | 65.54 KiB | Persistent SQLite DB | NO (Preserved application data) |
| **User Settings** | `%LOCALAPPDATA%\Vydrix\settings.json` | 1.41 KiB | Persistent JSON | NO (Preserved application config) |
| **Encrypted OAuth Auth** | `%LOCALAPPDATA%\Vydrix\rclone.conf` | 545 Bytes | Encrypted Secret | NO (Preserved credentials) |
| **DPAPI Encryption Key** | `%LOCALAPPDATA%\Vydrix\rclone.conf.key` | 262 Bytes | Machine Secret | NO (Preserved DPAPI key) |

### B. Storage Metrics Differentiation

- **Physical Allocation on Disk:** The actual compressed/sparse NTFS sectors consumed by cache files (`GetCompressedFileSizeW`).
- **Logical Cache Size:** The uncompressed apparent length of cached cloud files.
- **Reserved Capacity:** In-flight allocations currently requested by active reads (`InFlightReservedBytes`).
- **Avoided Storage:** The total gigabytes of game assets mounted from the cloud that were **never downloaded to local disk**.

---

## 4. Automated Verification Matrix (Phase G Results)

All 20 required verification scenarios were implemented in `tests/Vydrix.Tests/ZeroFootprintModeTests.cs` and executed with a **100% pass rate (115/115 total tests passed)**:

| # | Scenario | Test Method | Outcome | Verification Details |
|---|---|---|---|---|
| 1 | **Default Budget** | `Scenario01_DefaultBudget_IsExactly512MB` | **PASS** | Budget is exactly `536,870,912` bytes (512 MiB); usable data budget is `496 MiB`. |
| 2 | **Cache Path Resolution** | `Scenario02_CachePath_ResolvesToConfiguredCLocation` | **PASS** | Resolves to `%LOCALAPPDATA%\Vydrix\cache` on drive `C:`. |
| 3 | **Prefetching Disabled on Restart** | `Scenario03_Prefetching_RemainsDisabledAfterRestart` | **PASS** | `DemandOnlyMode = true` cancels queues and suppresses predictor initialization. |
| 4 | **Demand Reads Only** | `Scenario04_DemandReads_FetchRequiredRangesWithoutSpeculativeDownloads` | **PASS** | Speculative prefetch schedule calls are dropped; `PrepareAsync` returns 0 prefetched blocks. |
| 5 | **Concurrent Capacity Limits** | `Scenario05_ConcurrentDownloads_CannotExceedEnforcedBudget` | **PASS** | 10 concurrent 100 MiB requests strictly admit only 4 (400 MiB ≤ 496 MiB) and reject the 5th. |
| 6 | **In-Flight Reservation** | `Scenario06_InFlightDownloads_ReserveCapacity` | **PASS** | Reserved capacity atomically increments and decrements via `IDisposable` tokens. |
| 7 | **Active File Handle Protection** | `Scenario07_ActiveFileHandles_AreProtectedFromEviction` | **PASS** | Open file handles (`IsFileLocked`) are detected and protected; only unlocked files are evicted. |
| 8 | **Dirty & Pinned Protection** | `Scenario08_DirtyAndPinnedEntries_AreProtected` | **PASS** | Files with pending uploads (`Dirty = true`) and user-pinned paths are never evicted. |
| 9 | **Incomplete Download Protection** | `Scenario09_IncompleteDownloads_AreNotEvicted` | **PASS** | In-flight reserved allocations protect active downloads from premature eviction. |
| 10 | **Metadata Preservation** | `Scenario10_RequiredMetadata_IsPreserved` | **PASS** | Data file eviction retains `vfsMeta` index files and ensures directory integrity within 16 MiB reserve. |
| 11 | **Post-Game Cleanup** | `Scenario11_Cleanup_RunsAutomaticallyAfterCompletedGameSession` | **PASS** | `ReclaimGameCacheAsync` purges transient game assets for the exited game while preserving other games. |
| 12 | **Handle Settling Drain** | `Scenario12_Cleanup_WaitsForOutstandingOperations` | **PASS** | Enforces a 1.5-second settling window ensuring Windows file handles and WinFsp flush before cleanup. |
| 13 | **Crashed Game Safety** | `Scenario13_CrashedGames_DoNotLeavePermanentSessionLocks` | **PASS** | Abnormal game exits clean up active launch sessions without permanent deadlocks. |
| 14 | **Multi-Game Protection** | `Scenario14_MultipleSimultaneousGames_RemainProtected` | **PASS** | Simultaneous game sessions have all their active cache assets protected simultaneously. |
| 15 | **Mount Transition Safety** | `Scenario15_PeriodicMaintenance_DoesNotOverlapDestructivelyWithMountTransitions` | **PASS** | Maintenance gracefully skips unmounted states without throwing exceptions. |
| 16 | **Network Outage Resilience** | `Scenario16_NetworkOutages_FailSafelyWithoutDataLoss` | **PASS** | Network errors during eviction are logged; dirty files and local integrity are protected. |
| 17 | **Restart & Recovery** | `Scenario17_RestartAndRecovery_PreserveValidCacheMetadata` | **PASS** | Cache manager reconnects to existing `vfsMeta` structures across restarts without corruption. |
| 18 | **App State Isolation** | `Scenario18_Cleanup_DoesNotDeleteAppSettingsDatabasesOrCredentials` | **PASS** | Cache clearing operations are strictly scoped to `cache/`; settings, databases, and credentials are untouched. |
| 19 | **Crash Accounting Recovery** | `Scenario19_CacheAccountingRecovers_AfterSimulatedCrash` | **PASS** | Fresh instances recalculate physical usage from disk and reset in-flight reservations to 0. |
| 20 | **Unbounded Growth Prevention** | `Scenario20_CachePressure_DoesNotTriggerSilentUnboundedTemporaryDownloads` | **PASS** | When cache is saturated with protected files, allocations are refused with actionable diagnostic messages. |

---

## 5. User Interface Integration

The VYDRIX Desktop UI (`SmartCacheView.xaml`) provides intuitive control over Zero Footprint Mode:
1. **Zero Footprint Mode Banner:** Instant one-click toggle button (`Enable / Disable Zero Footprint Mode`).
2. **Storage Profile Selector:** Directly displays `ZeroFootprint` (512 MiB hard cap, 1-hour eviction).
3. **Storage Efficiency Engine:**
   - Zero Footprint Mode status (`ENABLED`).
   - Prefetching Policy (`OFF - Demand reads only`).
   - Periodic Maintenance (`Every 2 hours`).
   - Post-Game Cleanup (`Enabled - Automatic`).
   - Active Game Sessions count.
   - Resolved Cache Path (`C:\Users\...\AppData\Local\Vydrix\cache`).
   - Metadata Headroom (`16.00 MiB`).
   - In-Flight Reserved bytes.
   - Physical on disk vs Avoided storage.
4. **Action Buttons:** Action button updated to `"Run safe cleanup now"` with tooltips and live safety alerts.

---

## 6. Staged Release Verification

The Release build in `artifacts\VYDRIX-1.2.0-staging` was verified using the staged CLI:
```powershell
& "%USERPROFILE%\Documents\VYDRIX\artifacts\VYDRIX-1.2.0-staging\vydrix-cli.exe" deps
```
**Output:**
- `[Ok] WinFsp: Installed (v2.1.25156)`
- `[Ok] rclone: rclone v1.75.2 bundled`
- `[Ok] Cache volume: NTFS with sparse-file support (C:\)`
- `[Ok] Cache free space: 34.19 GiB free`
- `[Ok] .NET runtime: 10.0.13`

The staging directory contains complete `build-manifest.json` and `ROLLBACK.md` guides.
