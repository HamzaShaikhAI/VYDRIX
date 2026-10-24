# VYDRIX 0.003.1 (Experimental) — Storage Contract Specification
## Cloud-First Computing: Workload Isolation, Cache Boundaries & System Drive Protection

**Document Version:** 0.003.1 (Archived)  
**Effective Date:** October 24, 2026  
**Target Platform:** Windows 11 / Windows 10 x64 (.NET 10, WinFsp, rclone)  

---

## 1. Core Principle & Mission

The central mission of VYDRIX Cloud-First Computing is:

> **Enable users to run demanding local workloads—AAA games, Ollama AI models, software development workspaces, compilers, and large datasets—backed by cloud storage (e.g. Google Drive `G:`), utilizing local PC compute (CPU, RAM, GPU), without those workloads filling or degrading the physical `C:` system drive.**

To achieve this without compromising stability or data integrity, VYDRIX enforces an explicit **Storage Contract** that categorizes all data, provides deterministic placement policies, and acknowledges the physical realities of the Windows operating system.

---

## 2. The Four Storage Categories

All data managed or touched by VYDRIX-orchestrated workloads is divided into four distinct tiers:

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                     VYDRIX STORAGE CONTRACT                                     │
├───────────────────────────────┬──────────────────────────────────┬───────────────────────────────┤
│ Category                      │ Primary Location                 │ Eviction / Deletion Policy    │
├───────────────────────────────┼──────────────────────────────────┼───────────────────────────────┤
│ 1. Cloud-Persistent Data      │ Cloud Drive (G:\...)             │ NEVER automatically deleted   │
│ 2. Bounded Transient Cache    │ NVMe / RAM (Tier A / Tier B)     │ Bounded (512 MiB default)     │
│ 3. Relocatable Workload Data  │ Secondary Physical (D:\...)      │ Workload-lifecycle cleanup    │
│ 4. Protected System Data      │ Windows System Volume (C:\...)   │ IMMUTABLE / Untouched by app  │
└───────────────────────────────┴──────────────────────────────────┴───────────────────────────────┘
```

### Category 1: Cloud-Persistent Data
- **Examples:** Complete game installations, Ollama/GGUF AI model weights, Git source repositories, raw asset libraries, machine learning datasets, archives, and official build distributions.
- **Location:** Hosted primarily on remote cloud storage and presented via the virtual mount (e.g., `G:\games`, `G:\ai\models`, `G:\workspaces`).
- **Policy:** 
  - Authoritative source of truth.
  - **NEVER** deleted, overwritten, or modified by cache maintenance or emergency cleanup.
  - Local reads are serviced through on-demand chunk streaming.

### Category 2: Bounded Transient Cache
- **Examples:** Downloaded sparse file chunks, recently accessed game textures, read lookahead buffers, speculative prefetch blocks, and VFS metadata indices.
- **Location:** 
  - Tier A: In-memory RAM buffer (`MemoryHotCache`, default 64 MiB).
  - Tier B: Local fast disk cache (`cache/vfs`, strictly capped at 512 MiB by default on `C:`, or relocated to secondary drive `D:`).
- **Policy:**
  - Evictable under multi-factor scoring (`EvictionScorer`) when budget or free-space thresholds are approached.
  - Protected while open by active workload handles.
  - Safe to purge at any time after active handles close.

### Category 3: Relocatable Workload Data
- **Examples:** Compiler intermediate artifacts (`bin/`, `obj/`, `target/`), package manager caches (`node_modules/.cache`, `.nuget/packages`, `pip/cache`, `.dart_tool`), application-specific temporary scratch files, model KV-cache scratchpads, and DirectX/Vulkan shader caches (where game/driver supports custom paths).
- **Location:** 
  - **Preferred:** Secondary physical volume (e.g., `D:\VydrixScratch`, `D:\VydrixCache`) or bounded RAM disk.
  - **Restricted:** Never placed on `C:` unless explicitly configured with a hard byte quota.
  - **Virtual Mount Prohibition:** Never placed directly on `G:` if the workload requires POSIX file locking, atomic renames, or high-concurrency memory-mapped random writes that cloud VFS backends cannot durably handle.
- **Policy:**
  - Ephemeral or lifecycle-bound: cleanable on workload termination, project clean, or explicit user purge.

### Category 4: Protected System Data
- **Examples:** Windows OS components (`C:\Windows`), kernel drivers, WinFsp driver binaries, pagefile (`pagefile.sys`), swapfile (`swapfile.sys`), essential user profile registry hives (`NTUSER.DAT`), credentials, and core VYDRIX configurations.
- **Location:** Physical `C:` volume.
- **Policy:**
  - **IMMUTABLE:** VYDRIX never alters, cleans, intercepts, or redirects core system data.
  - Monitored exclusively by **C: Guardian** for capacity health and external exhaustion warnings.

---

## 3. Truth in Windows Storage: Unavoidable C: Writes

VYDRIX maintains complete engineering transparency: **No user-mode software can guarantee that `C:` remains literally untouched during local execution.**

The following `C:` write sources are unavoidable on Windows 11 and must be accurately measured and isolated:

1. **Windows Memory Management (Paging):**
   - When large AI models or AAA games allocate physical RAM, Windows may page inactive background working sets to `C:\pagefile.sys`.
2. **GPU Driver Shader Compilation:**
   - NVIDIA (`%LOCALAPPDATA%\NVIDIA\DXCache`), AMD, and Intel drivers write precompiled binary shaders to the user's profile on `C:` unless redirected via vendor-specific driver environment variables.
3. **Windows Defender & Antivirus:**
   - Real-time protection scans incoming network bytes and write buffers, updating internal signature caches and definitions on `C:`.
4. **Application Crash Dumps & Windows Error Reporting (WER):**
   - Unhandled exceptions in games or runtime processes generate crash minidumps under `%LOCALAPPDATA%\CrashDumps`.
5. **System Event Logs & Telemetry:**
   - Windows Event Log service (`svchost.exe -k LocalServiceNetworkRestricted`) continuously writes ETW events to `C:\Windows\System32\winevt\Logs`.
6. **Essential Process Metadata:**
   - VYDRIX's own configuration, encrypted DPAPI key, and SQLite session database (`vydrix.db`) require minimal local persistence (~4 MiB).

**VYDRIX's Objective:** Eliminate all *avoidable* storage consumption (gigabytes of game assets, AI model weights, build caches, and debug dumps) while accurately measuring and reporting the unavoidable system baseline.

---

## 4. Per-Workload Placement Specification

Every workload orchestrated by VYDRIX is governed by an explicit `WorkloadPlacementPolicy`:

| Workload Type | Cloud Source Location | Allowed Cache Location | Scratch / Build Location | Cleanup Trigger | Supported Mode |
|:---|:---|:---|:---|:---|:---|
| **AAA Game** | `G:\games\<title>` | Secondary `D:\` or Bounded `C:` (512 MiB) | Game Save: User Profile; Shaders: Driver Default | Post-game drain (1.5s delay) | Full Streaming |
| **Ollama AI Model** | `G:\ai\models` (`OLLAMA_MODELS`) | Tier A RAM (64 MiB) + Secondary `D:\` | In-Memory KV Cache | Session termination | Demand Blobs |
| **Development Project** | `G:\workspaces\<project>` | Secondary `D:\VydrixCache` | Secondary `D:\VydrixScratch\<id>` | Post-build / User Purge | Hybrid Cloud |
| **Machine Learning Dataset**| `G:\datasets\<name>` | Secondary `D:\` or Bounded `C:` | In-Memory Pipeline Buffer | Process exit | Read-Only Stream |

---

## 5. Storage Placement Hierarchy

When a workload requires local caching or scratch storage, VYDRIX resolves the destination in strict order:

```
1. Secondary Physical Drive (e.g. D:\VydrixCache)
   ├── Highest capacity (e.g. 24+ GB free)
   ├── Full NTFS capability, zero impact on C:
   └── Automatic detection by C: Guardian
           │ (If unavailable)
           ▼
2. Bounded System RAM Cache (Tier A)
   ├── Microsecond latency, zero disk writes
   ├── Auto-trimmed on GC memory pressure
   └── Capped at safe fraction of available physical RAM
           │ (If unavailable)
           ▼
3. Bounded C: Drive Cache (Zero Footprint Profile)
   ├── Hard 512 MiB cap with 16 MiB explicit metadata reserve
   ├── Multi-factor LRU eviction
   └── Automatic shutdown if C: enters Critical threshold
```

If no safe writable location with sufficient headroom exists, VYDRIX **fails safely and informs the user**, rather than silently consuming unmanaged storage on `C:`.
