# VYDRIX 3.0 — C: Guardian & System Drive Protection Audit

**Document Version:** 3.0.0  
**Target Environment:** Windows 11 x64, .NET 10.0.13, WinFsp 2.1.25156  
**Evaluation Date:** October 24, 2026  

---

## 1. Executive Summary

A core mission of VYDRIX 3.0 is **system drive protection**: ensuring that large workloads (AAA games, multi-gigabyte Ollama LLMs, development compilers, and package caches) running from a cloud-backed virtual drive do not consume or exhaust the host PC's physical `C:` drive.

The **C: Guardian Service** (`CGuardianService.cs`) runs continuously as a lightweight storage sentinel within VYDRIX, tracking real-time capacity and consumption rate, isolating VYDRIX-owned storage, identifying secondary storage relocation opportunities, and enforcing strict protective throttling.

---

## 2. Empirical Storage Audit of System C: Drive

Measurement captured on the Windows 11 host PC:

```
Total C: Capacity:    193.71 GiB (207,991,336,960 bytes)
Free Space:           35.67 GiB (38,300,500,000 bytes)
Free Percentage:      18.4%
Status:               HEALTHY (Above 10% warning threshold)
```

### Component Breakdown of VYDRIX Footprint on C:
VYDRIX maintains separate, audited accounting of all local files:

| Consumer Component | Measured Size on C: | Category | Relocatable? | Notes |
|:---|:---:|:---:|:---:|:---|
| **VFS Data Cache** (`cache/vfs`) | **89 B** | Bounded Transient Cache | **YES** | Strictly bounded to 512 MiB hard cap in Zero Footprint Mode. Relocatable to `D:\VydrixCache`. |
| **VFS Metadata** (`cache/vfsMeta`) | **< 1 KiB** | Bounded Transient Cache | **YES** | Explicit 16 MiB headroom reserved in planning. |
| **Application Logs** (`logs/`) | **85.0 MiB** | Diagnostics & Logging | **NO** | 4 log files. C: Guardian includes automatic log rotation to cap at 10 MiB. |
| **Database & Profiles** (`vydrix.db`) | **64.0 KiB** | Protected System Data | **NO** | SQLite session history and learned Markov block transitions. |
| **Temporary Files** (`%TEMP%\Vydrix`) | **0 B** | Relocatable Workload | **YES** | Cleaned on process exit or dry-run execution. |
| **Installed Binaries** (`artifacts/`) | **28.4 MiB** | Application Binaries | **NO** | Native WinFsp shim, rclone executable, and .NET runtime DLLs. |

**Total VYDRIX Footprint on C:** **~113.5 MiB** (dominated by rotating diagnostic logs, with zero unbounded cache accumulation).

---

## 3. Secondary Volume Relocation Opportunities

C: Guardian automatically detects physical secondary volumes with > 5 GiB free space:

- **Volume `D:\`:**
  - File System: NTFS (supports sparse files and alternate data streams)
  - Capacity: 228.36 GiB total, **22.59 GiB free**
  - **Ideal Role:** Primary target for `D:\VydrixCache` and `D:\VydrixScratch`, completely offloading all cache chunks and compiler build outputs from `C:`.
- **Volume `E:\`:**
  - Capacity: 31.45 GiB total, **2.38 GiB free** (insufficient headroom; skipped by Guardian).

---

## 4. Truth in Windows Storage: Unavoidable C: Writes

User-mode software cannot intercept or halt kernel-level Windows writes. C: Guardian measures and distinguishes these unavoidable writes from workload-generated files:

1. **Windows Memory Management (`C:\pagefile.sys`):**
   - When a 7B or 8B parameter model is loaded into RAM on a system with 16 GB total RAM, Windows memory manager may swap inactive background services to the pagefile.
2. **GPU Driver Shader Compilation:**
   - NVIDIA driver writes compiled DirectX/Vulkan shaders to `%LOCALAPPDATA%\NVIDIA\DXCache`.
3. **Windows Defender Antivirus Definitions & ETW Logs:**
   - Real-time scanning streams definition updates to `C:\ProgramData\Microsoft\Windows Defender`.
4. **Crash Minidumps:**
   - Unhandled process faults write to `%LOCALAPPDATA%\CrashDumps`.

**Protection Rule:** VYDRIX eliminates 100% of avoidable writes (the actual 15–80 GB game assets and model blobs) and warns the user when memory pressure might induce pagefile expansion.

---

## 5. Critical Threshold Automated Response

When available space on `C:` drops below the **Critical Threshold** (default: 5.0 GiB or 5% free):

1. **Halts Speculative Prefetch:** Automatically switches `StreamingEngine.SetDemandOnly(true)`, instantly dropping all background fetch queues.
2. **Pauses Cache Warming:** Halts `GhostWarmUpService` pre-warming.
3. **Reclaims Inactive Game Chunks:** Triggers `SmartCacheManager.ReclaimGameCacheAsync()` with 1.5s handle settling.
4. **Log Truncation:** Rotates and truncates application logs older than the two newest sessions.
5. **Surfaces Urgent UI Banner:** Displays actionable alert in `SmartCacheView.xaml` with one-click dry-run report.
6. **NEVER Deletes Protected Data:** Guaranteed zero deletion of cloud originals (`G:`), user source code, game saves, or OAuth credentials.
