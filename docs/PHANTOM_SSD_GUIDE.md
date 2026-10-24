# VYDRIX 0.003.1 (Experimental) — PHANTOM SSD User & Operator Guide

**Near-Physical-SSD Experience + Next-Generation Cloud Storage Intelligence**

---

## 1. Introduction

VYDRIX 0.003.1 (Experimental) (codenamed **PHANTOM SSD**) bridges the gap between cloud storage economics and local solid-state drive responsiveness. By coupling an intelligent three-tier caching hierarchy with real-time workload classification and physical-SSD-inspired request pipelines, VYDRIX delivers an ultra-responsive virtual drive while keeping local disk consumption strictly within your chosen budget (512 MiB by default).

---

## 2. Core Concepts

### A. The 3-Tier Storage Hierarchy

```
┌───────────────────────────────────────────────────────────┐
│ Tier A: Hot Cache (RAM)                                  │
│ Microsecond reads for small frequently accessed files     │
│ (< 512 KiB). Dynamic GC memory pressure auto-trimming.    │
└─────────────────────────────┬─────────────────────────────┘
                              │ Miss
                              ▼
┌───────────────────────────────────────────────────────────┐
│ Tier B: Warm Cache (Local NVMe/SSD)                      │
│ Strictly bounded (512 MiB default) with 16 MiB metadata  │
│ headroom. Managed by composite multi-factor scoring.      │
└─────────────────────────────┬─────────────────────────────┘
                              │ Miss
                              ▼
┌───────────────────────────────────────────────────────────┐
│ Tier C: Cold Storage (Google Drive / Cloud Provider)      │
│ De-duplicated read coalescing, dynamic concurrency,      │
│ jittered retries, and zero writes to remote files.        │
└───────────────────────────────────────────────────────────┘
```

### B. Phantom Pulse (Workload Classifier)
VYDRIX continuously examines file read patterns and categorizes the active workload:
- **Sequential:** Streaming large continuous assets (e.g., game cutscenes, large texture archives). Lookahead streaming is activated.
- **BurstSmallFiles:** Loading hundreds of small config files, DLLs, or shaders during startup. In-memory caching and metadata stat caching are prioritized.
- **RandomRead:** Frequent random seeks across game data archives. Sequential prefetching is suppressed to prevent wasted network bandwidth.
- **LowBandwidth:** Network speeds drop below 2 MB/s. Background tasks pause so active gameplay receives 100% of available bandwidth.
- **Balanced:** Standard operational mode.

### C. Latency Shield
The Latency Shield monitors round-trip times (RTT) and throughput in real time:
- Automatically scales parallel cloud request concurrency between **2 and 12 worker threads**.
- Prevents queue congestion and provider rate-limiting (HTTP 429).
- Surfaces plain-English bottleneck diagnostics directly in the Smart Cache dashboard (e.g. *Cloud Round-Trip Latency*, *Google Drive Rate Limit*, or *Healthy SSD Responsiveness*).

### D. Ghost Warm-Up Service
- Uses past game access profiles to identify critical startup blocks.
- Prepares a dry-run acceleration plan before game execution.
- **Safety First:** In **Zero Footprint Mode**, speculative pre-warming is automatically disabled to guarantee zero storage overflow.

### E. Session Vault & Crash Recovery
- Active game sessions are journaled to disk in `vault_sessions.json`.
- In the event of an unexpected system reboot or power loss, VYDRIX reconciles the journal on next startup.
- Verifies process IDs and creation timestamps (`Process.StartTime`) against the Windows process table to protect against PID reuse.

---

## 3. How to Use Phantom SSD in the Desktop App

1. **Open VYDRIX:** Launch the application from your desktop shortcut or system tray.
2. **Navigate to Smart Cache:**
   - Click the **Smart Cache** tab on the navigation sidebar.
3. **Inspect the Phantom SSD Intelligence Card:**
   - **Mode:** Shows current detected workload (e.g., `Sequential`, `SmallFileBurst`, or `Balanced`).
   - **RAM Hot Cache Toggle:** Click **Enable RAM Cache** to activate Tier A high-speed memory caching for small assets (< 512 KiB).
   - **RAM Cache Usage:** Displays live memory footprint (e.g., `4.2 MiB / 64.0 MiB`).
   - **Bottleneck Diagnosis:** Displays real-time network or disk advice with actionable suggestions.
4. **Run Cloud SSD Self-Test:**
   - Under the **Cloud SSD Self-Test Benchmark** card, click **Run Self-Test Now**.
   - VYDRIX will execute a multi-phase I/O benchmark measuring metadata latency, 4KB random reads, sequential streaming, and multi-threaded scaling.
   - The card will display your overall score (e.g., `100.0/100`) and grade.

---

## 4. CLI Operator Commands

The `vydrix-cli.exe` utility supports inspecting and verifying your setup:

### Check Prerequisites & System Health:
```powershell
vydrix-cli.exe deps
```
Verifies WinFsp, rclone, NTFS sparse-file support, cache volume free space, and .NET runtime.

### List Configured Drives:
```powershell
vydrix-cli.exe mounts
```

### Mount Virtual Drive Manually:
```powershell
vydrix-cli.exe mount gdrive --letter G
```

### Inspect Game Access Profile:
```powershell
vydrix-cli.exe profile <game-id>
```

---

## 5. Storage Contract Tiers

You can select between three contract tiers depending on your local storage preferences:

| Tier | Cache Budget | RAM Cache (Tier A) | Prefetch Policy | Post-Game Cleanup | Recommended For |
|:---|:---:|:---:|:---:|:---:|:---|
| **Strict Zero Footprint (Default)** | 512 MiB | Disabled | Demand Reads Only | Automatic (1.5s drain) | Systems low on C: storage |
| **Balanced Phantom** | 2.0 GiB | Enabled (64 MiB) | Adaptive Lookahead | Periodic (2 hours) | General gaming & productivity |
| **Performance Cloud SSD** | 10.0 GiB | Enabled (256 MiB) | Aggressive Profile | Manual / On Demand | High-speed SSDs with ample room |

---

## 6. Privacy & Security

- **Zero Remote Writes:** VYDRIX never writes, modifies, or deletes cloud originals unless explicitly requested by the user.
- **DPAPI Credential Encryption:** All Google Drive tokens and encryption keys are stored securely using Windows Data Protection API (DPAPI).
- **Storage Time Machine Privacy:** All diagnostic logs and event timelines redact OAuth tokens, authorization headers, and access keys automatically.
