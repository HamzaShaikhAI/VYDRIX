# VYDRIX 0.003.1 (Experimental) — PHANTOM SSD Technical Gap Report & Baseline Audit

**Date:** 2026-10-24  
**Target:** VYDRIX 0.003.1 (Experimental).0 "Phantom SSD"  
**Branch:** `feature/phantom-ssd` (branched from `vydrix-main` commit `67b5e84`)  
**Host Environment:** Windows 11 x64, .NET 10.0.13, WinFsp 2.1.25156, rclone v1.75.2  
**Baseline Verification:** 115 / 115 tests passing cleanly across all projects  

---

## 1. Baseline Architecture Review

The current VYDRIX 1.2.0 release implements:
1. **Virtual Filesystem Mount:** WinFsp user-mode filesystem driver interfacing with bundled rclone v1.75.2 in `--vfs-cache-mode full` mode.
2. **Cache Management:** `SmartCacheManager` supporting Zero Footprint Mode (512 MiB hard cap on `C:`), 16 MiB metadata headroom reservation, and post-game process exit cleanup with a 1.5s drain delay.
3. **Streaming & Prediction:** `StreamingEngine` with predictive Markov model (disabled in demand-only Zero Footprint Mode).
4. **Game Process Tracking:** `GameLauncher` using Windows Job Objects and `ProcessTreeTracker`.

---

## 2. Technical Gaps & Limitations Identified

| Architecture Area | Current Baseline (1.2.0) | Identified Gap / Limitation | VYDRIX 0.003.1 (Experimental) Target Solution |
|---|---|---|---|
| **Cache Tiers** | Single disk cache tier (`vfs/` on disk). | No fast Tier A (Hot) memory tier for small files or metadata; repeated small reads hit disk I/O. | **Adaptive Tiered Cache:** Tier A (Hot memory cache with OS memory pressure awareness), Tier B (Warm disk cache), Tier C (Cold cloud). |
| **Eviction Policy** | Basic LRU timestamp ordering. | Does not factor in access frequency, refetch cost, provider latency, or retention benefit. | **Intelligent Eviction Scoring:** Multi-factor composite score combining recency, frequency, refetch cost, and session status. |
| **Read Pipeline** | Direct 1:1 rclone VFS access. | Overlapping/adjacent range reads are not coalesced; concurrent reads for identical blocks trigger duplicate requests. | **Coalescing Read Pipeline:** In-flight deduplication, temporal window range coalescing, bounded concurrency, backpressure. |
| **Workload Awareness** | Static prefetch toggle (`DemandOnlyMode` on/off). | Cannot adapt dynamically to game access patterns (e.g. sequential level loads vs random asset streams). | **Phantom Pulse Engine:** Real-time heuristics identifying Sequential, RandomRead, SmallFile, LowBandwidth, LowLatency, and StrictCache workloads. |
| **Network Adaptation** | Static bandwidth limits (`--bwlimit`). | No real-time RTT / throughput / error tracking; cannot distinguish network lag from disk or CPU bottlenecks. | **Latency Shield:** Continuous RTT, throughput, retry tracking with adaptive concurrency and plain-English bottleneck diagnostics. |
| **Launch Acceleration** | Speculative prefetching disabled or full profile replay. | Either 0 prefetch or unbounded prefetch; no cost/benefit estimation or immediate cancellation on demand reads. | **Ghost Warm-Up:** Optional launch-acceleration planner with dry-run estimation, strict budget bounding, and demand-read preemption. |
| **Session Accounting** | In-memory `ConcurrentDictionary`. | Crashes, power loss, or forced termination can leave orphaned locks; PID reuse is not checked against process start time. | **Session Vault:** Persistent, crash-safe session journal with PID start-time identity validation and startup recovery reconciler. |
| **Integrity & Staleness** | Passive trust in local cache. | Remote file updates made out-of-band on Google Drive can leave stale chunks; interrupted downloads lack verification. | **Shadow Integrity:** ETag/hash and modtime verification, remote change detection, and corrupted block invalidation. |
| **Contract Transparency** | Unclear distinction between cache budget and total footprint. | Users may confuse cache budget with total disk usage (binaries, logs, databases). | **Zero-Footprint Contract:** Explicit Strict, Balanced, and Performance modes with detailed footprint accounting. |
| **Performance Benchmarks** | No built-in test suite for users. | No way to measure real vs synthetic latency, p95, IOPS, or throughput on the user's machine. | **Cloud SSD Self-Test:** Comprehensive benchmark suite (metadata, small files, sequential, random, concurrent) reporting p95 and throughput. |
| **Diagnostic History** | Static rolling logs. | No way to explain why a game was slow, which files caused cache churn, or how to tune settings. | **Storage Time Machine:** Privacy-preserving diagnostic history, session analytics, and storage reclamation recommendations. |

---

## 3. Rollback Procedure & Baseline Checkpoint

- **Git Baseline Commit:** `67b5e84` on branch `vydrix-main`.
- **Active Working Branch:** `feature/phantom-ssd`.
- **Rollback Command:**
  ```powershell
  git checkout vydrix-main
  ```
- **Live User Data & Mount:** Google Drive mount on `G:`, settings in `%LOCALAPPDATA%\Vydrix`, and staging in `artifacts\VYDRIX-1.2.0-staging` are protected and preserved.
