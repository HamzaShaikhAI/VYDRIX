# VYDRIX

**Windows Cloud-Backed Virtual Storage & Streaming System**

> [!WARNING]
> **Project Status: DISCONTINUED / ARCHIVED (v3.0.0-final)**
> VYDRIX has concluded its final engineering phase and is officially decommissioned. It serves as an open-source reference implementation and post-mortem exploring the limits of mounting consumer cloud storage as a virtual gaming and AI storage engine on Windows.
>
> All active cloud credentials and local VFS caches have been disconnected and unmounted. See [Architectural Post-Mortem & Decommission Summary](#architectural-post-mortem--decommission-summary) below for detailed technical findings.

---

VYDRIX is an upgraded, security-hardened virtual cloud drive and predictive asset streaming system for Windows 10/11 (x64). Built upon verified engineering research and architecture from the open-source CloudPlayX reference project, VYDRIX mounts cloud storage as a native Windows drive letter, caches downloaded ranges sparsely on NTFS, and streams files on demand.

> VYDRIX runs local executables on your PC's CPU and GPU. Uncached ranges stream over your network connection and are cached sparsely on your local SSD for near-instant subsequent reads.

## Key Architecture & Features

- **Virtual Drive Mount** — rclone + WinFsp filesystem bridge (`X:`, `Y:`, etc.), sparse NTFS block-level caching, safe write-back, and automated health monitoring.
- **Google Drive Integration** — Connect via user-controlled OAuth 2.0 PKCE client files with DPAPI encrypted local secret storage.
- **Sparse Cache Management** — Configurable budget presets, disk safety guard protecting Windows free space, physical-vs-logical sparse file inspection, and marker-guarded eviction.
- **Predictive Prefetch Engine** — High-precision access telemetry, SQLite-backed per-game profile storage, Startup Laplace, Sequential, and bounded Markov transition predictors.
- **Game Library & Compatibility** — Executable detection, PE import inspection for missing Windows runtimes, three execution modes (Cloud Direct, Hybrid Optimized, Full Local).
- **Security Hardening** (Verified & Tested):
  - **SEC-01:** Explicit credential file selection via file picker; unprompted directory harvesting disabled.
  - **SEC-02:** Passwords delivered on-demand via `--password-command` IPC rather than stored in child process environment blocks.
  - **SEC-04:** Runtime cryptographic SHA-256 verification of rclone and companion binaries before execution.
  - **SEC-05:** Hardened redaction engine masking standard and URL-encoded credentials (`ya29%2E...`, `1%2F%2F...`).
- **Technical Improvements**:
  - **TECH-06:** Upfront Windows Developer Mode detection for NTFS symlink operations in Hybrid mode.
  - **TECH-07:** Circular FIFO eviction in telemetry queues preventing buffer wipes under high IOPS.
  - **TECH-09:** Bounded transition graph with LRU edge eviction to strictly limit memory footprint on 8 GB systems.

## Building from Source

Requirements: .NET 10 SDK, Windows 10/11 x64, PowerShell.

```powershell
# Build entire solution
dotnet build Vydrix.slnx -c Release

# Run automated unit & security regression tests
dotnet test tests/Vydrix.Tests
```

## CLI Usage

The command-line interface is available as `vydrix.exe`:

```powershell
vydrix deps                                  # Check WinFsp, rclone, and storage prerequisites
vydrix remotes                               # List configured cloud storage accounts
vydrix mounts                                # List virtual drive configurations
vydrix mount <mount-id> [--letter X]         # Mount a configured drive in the foreground
vydrix scan <folder> [--mbps 70]             # Scan game assets and generate compatibility report
vydrix pe <file.exe>                         # Inspect PE architecture and missing DLLs
vydrix profile <game-id>                     # Inspect learned access profiles
vydrix simcloud --root <dir> [--port 8080]   # Throttled mock cloud server for testing
vydrix secret emit-config-pass [--home dir]  # IPC helper for rclone --password-command
```

## Upgrading & Migration

VYDRIX includes seamless, automatic migration from legacy `%LOCALAPPDATA%\CloudPlayX` directories:
- Existing `settings.json`, `rclone.conf`, DPAPI keys, and game profiles are migrated to `%LOCALAPPDATA%\Vydrix` without data loss.
- `cloudplayx.db` is migrated as `vydrix.db`.

## Architectural Post-Mortem & Decommission Summary

### 1. The Core Vision
The core product thesis of VYDRIX was ambitious: **allow Windows users to run AAA games, AI model inference (e.g. Ollama), and large build workloads directly from a cloud-backed virtual drive (Google Drive mounted via WinFsp and rclone), using local CPU/GPU compute while keeping local physical C: drive consumption bounded near zero.**

### 2. Engineering Achievements Shipped in v3.0.0
Across three major engineering iterations, VYDRIX implemented a hardened, production-grade virtual filesystem framework:
- **Security & Secret Hardening:** DPAPI encryption for local secrets (`SEC-01`), `--password-command` IPC isolation preventing password leaks in process tables (`SEC-02`), runtime cryptographic SHA-256 binary integrity checking (`SEC-04`), and high-performance redaction masking for OAuth credentials (`SEC-05`).
- **Zero Footprint Mode:** Strict 512 MiB NTFS sparse cache quota with dynamic cleanup triggers upon workload exit.
- **Phantom SSD Architecture:** 3-tier caching hierarchy combining a microsecond in-memory RAM cache (`MemoryHotCache`), sparse NVMe disk cache, and remote range-coalesced request pipelines (`CoalescingReadPipeline`) with multi-factor eviction scoring.
- **C: Guardian & Cloud-First Workspaces:** Automated filesystem shielding, dynamic redirection of scratch and caches to secondary volumes (`D:\`), and isolated development workspace templates.
- **Comprehensive Verification:** 141 automated unit and regression tests (100% pass rate).

### 3. Fundamental Boundaries & Why Virtual Cloud Drives Fail for Heavy Local Workloads
Despite exhaustive optimization, empirical stress testing on real-world Windows 11 hardware demonstrated fundamental physical and protocol bottlenecks inherent to consumer cloud storage mounts:

1. **Cloud API Throttling & Daily Quotas (HTTP 403 / 429):**
   Consumer cloud storage backends (such as Google Drive) enforce strict per-user API rate limits and daily upload/download quotas. Large file operations (such as staging a 1.55 GiB installer or streaming high-throughput game assets) rapidly trigger exponential HTTP 403 backoffs, causing the filesystem layer to stall indefinitely.
2. **The VFS Write-Buffering vs. Storage-Saving Paradox:**
   To provide POSIX/Windows filesystem compatibility, write operations must be buffered locally on NTFS until remote sync completes. When remote transfers stall due to cloud API throttling or network instability, unuploaded dirty buffers (`Dirty: true`) accumulate on local storage. Deleting these buffers would cause silent data loss, while preserving them directly violates the "zero local storage" guarantee.
3. **Random I/O Latency and Memory-Mapped Files (`mmap`):**
   Modern 3D games and machine learning engines rely heavily on unbuffered, memory-mapped random file reads. User-mode filesystem bridges (WinFsp/FUSE) translating random 4KB reads into remote HTTPS range requests introduce millisecond-scale latency penalties (100x–1,000x slower than physical NVMe SSDs). This causes Windows kernel wait queues to saturate, driving disk active time to 100% and causing desktop micro-freezes.
4. **Conclusion:**
   For high-IOPS workloads (AAA games, local LLM weights, heavy compiler outputs), high-speed local NVMe storage or dedicated remote rendering protocols (e.g., GeForce NOW, Moonlight, Parsec) remain fundamentally superior to virtual cloud-drive filesystem mounts.

### 4. Decommission Checklist
In the final decommission:
- **Virtual Drive Unmounted:** Virtual drive `G:` was safely dismounted.
- **Cloud Accounts Disconnected:** Local OAuth tokens (`rclone.conf`) and DPAPI keys (`rclone.conf.key`) were securely deleted.
- **No Remote Modifications:** The user's Google Drive account was untouched; no files were deleted, renamed, or modified on the cloud.
- **Local Cache & Test Bloat Reclaimed:** Reclaimed over 70 GB of logical disk usage on local disk `C:`, including the temporary integration test fixtures and stale VFS cache.
- **Final Release Packaged:** The fully integrated, self-contained `VYDRIX-3.0.0-win-x64.zip` distribution remains preserved in the artifacts directory.

## License & Attribution

- VYDRIX is licensed under the [MIT License](LICENSE).
- Portions Copyright (c) 2026 CLOUDPLAY X contributors.
- Third-party components (rclone, WinFsp, .NET, NuGet packages) retain their respective licenses as detailed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
