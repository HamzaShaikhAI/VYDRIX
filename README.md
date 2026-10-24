<div align="center">

<img src="assets/branding/vydrix-logo-dark.png" alt="VYDRIX Logo" width="380" />

# VYDRIX

**Windows Cloud-Backed Virtual Storage & Streaming Engine**

[![Release](https://img.shields.io/badge/release-v3.0.0--final-blue.svg)](https://github.com/HamzaShaikhAI/VYDRIX/releases/tag/v3.0.0-final)
[![Status](https://img.shields.io/badge/status-discontinued%20%7C%20archived-critical.svg)](https://github.com/HamzaShaikhAI/VYDRIX)
[![CI](https://github.com/HamzaShaikhAI/VYDRIX/actions/workflows/ci.yml/badge.svg)](https://github.com/HamzaShaikhAI/VYDRIX/actions/workflows/ci.yml)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey.svg)](https://github.com/HamzaShaikhAI/VYDRIX)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

<br />

> **Discontinued — final release; no further development or support promised.**  
> VYDRIX has reached the conclusion of its engineering roadmap. The project is officially archived as an open-source reference implementation and empirical study on the limits of cloud-backed virtual filesystems on Windows.

</div>

---

> [!WARNING]
> **Production Workload Notice**  
> Google Drive-backed virtual filesystems are **not recommended** for production gaming, local AI model execution (e.g., Ollama, PyTorch), or latency-sensitive build pipelines. High-concurrency random I/O, synchronous memory mapping, and remote API rate limits make consumer cloud mounts unsuitable as physical SSD replacements.

---

## Overview

VYDRIX was an engineering project designed to investigate a high-demand computing thesis: **Can a Windows PC run large AAA games and AI models from cloud storage without filling the local physical C: drive?**

Built on top of [rclone](https://rclone.org/) and [WinFsp](https://winfsp.dev/), VYDRIX mounted remote storage (Google Drive) as a native Windows drive letter (`G:`), streamed data on demand, cached blocks sparsely on NTFS, and executed binaries locally using the PC's CPU, GPU, and RAM.

### Features Engineered in v3.0.0
- **Zero Footprint Mode:** Enforces a strict 512 MiB NTFS sparse cache quota with automated post-game cleanup.
- **Phantom SSD Caching Engine:** A three-tier caching hierarchy combining a microsecond in-memory RAM cache (`MemoryHotCache`), bounded local NVMe/SSD sparse storage, and remote range-coalesced request streaming (`CoalescingReadPipeline`).
- **Telemetry & Predictive Prefetching:** High-precision read telemetry paired with Laplace-smoothed startup and Markov transition predictors to accelerate subsequent launches.
- **C: Guardian & Cloud-First Workspaces:** System drive protection preventing unbounded cache spills onto physical `C:`, routing scratch spaces to secondary drives or RAM.
- **Security Hardening (SEC-01 – SEC-05):** DPAPI encryption for local configurations, `--password-command` IPC isolation preventing password disclosure in process tables, and cryptographic SHA-256 binary integrity verification.

---

## Architectural Post-Mortem

Despite comprehensive architectural tuning and 141 passing automated tests, empirical stress testing on Windows 11 hardware demonstrated fundamental physical and protocol limits that make cloud-backed virtual drives impractical for heavy gaming and local AI workloads:

### 1. The VFS Write-Buffering vs. Storage-Negative Paradox
To maintain standard Windows filesystem semantics, write operations (such as saving game progress, extracting archives, or moving an installer like `OllamaSetup.exe` into the drive) must be staged locally in an NTFS cache until the remote upload completes. If upstream transfers stall due to cloud API throttling or network instability, unuploaded dirty buffers (`Dirty: true`) remain trapped on the local disk. Deleting these buffers results in silent data loss, while retaining them breaks the core product promise of zero local storage growth.

### 2. Upstream API Rate Limits and Daily Quotas
Consumer cloud providers (including Google Drive) enforce strict per-user API rate limits and daily transfer quotas. Burst-heavy workloads—such as game initialization or streaming gigabytes of model weights—generate hundreds of concurrent HTTP range requests, rapidly triggering `HTTP 403 (User Rate Limit Exceeded)` and `HTTP 429` backoffs that stall the virtual filesystem.

### 3. Remote HTTP Latency vs. Kernel Memory-Mapped I/O (`mmap`)
Modern games and machine learning engines rely heavily on unbuffered, memory-mapped random file reads (`mmap`). Emulating physical NVMe read performance (sub-0.05 ms latency) over a user-mode filesystem driver (WinFsp/FUSE) backed by remote HTTPS calls (50–150 ms round-trip time) creates severe Windows kernel wait states. Under high concurrency, Windows Task Manager active disk time reaches 100%, causing desktop micro-freezes and audio stutter.

### 4. Architectural Verdict
Consumer cloud object storage is architected for asynchronous file synchronization, not low-latency random block I/O. For heavy local compute workloads, physical NVMe SSDs or dedicated remote rendering protocols (e.g., GeForce NOW, Moonlight, Parsec) remain fundamentally superior.

---

## Final Release Download

The verified final release distribution of VYDRIX is available on the GitHub Releases page:

📦 **[Download VYDRIX v3.0.0-final](https://github.com/HamzaShaikhAI/VYDRIX/releases/tag/v3.0.0-final)**

### Verified Release Artifacts

| Parameter | Value |
| :--- | :--- |
| **Archive File** | `VYDRIX-3.0.0-win-x64.zip` |
| **Release Tag** | `v3.0.0-final` |
| **Git Commit** | [`c3775ff`](https://github.com/HamzaShaikhAI/VYDRIX/commit/c3775ff034e55e5c2f3e2c9ec89407cb5a736b9b) |
| **Platform** | Windows 10 / Windows 11 (x64) |
| **Package Size** | 34,701,809 bytes (33.09 MB) |
| **SHA-256 Checksum** | `10770FB49C49BD24D11B183457500892CAB51FB47BD624AD09DF78FC833E3B47` |
| **Runtime Requirements** | .NET 10.0 x64, WinFsp 2.1+ |

To verify the integrity of the downloaded archive in PowerShell:
```powershell
(Get-FileHash -Path .\VYDRIX-3.0.0-win-x64.zip -Algorithm SHA256).Hash -eq "10770FB49C49BD24D11B183457500892CAB51FB47BD624AD09DF78FC833E3B47"
# Expected output: True
```

---

## Building from Source

### Prerequisites
- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (v10.0.100 or later)
- [WinFsp](https://winfsp.dev/) (required for virtual drive mounting and integration tests)
- PowerShell 7+ or Windows PowerShell 5.1

### Compilation & Automated Tests

```powershell
# Clone the repository
git clone https://github.com/HamzaShaikhAI/VYDRIX.git
cd VYDRIX

# Restore solution dependencies
dotnet restore Vydrix.slnx

# Compile Release binaries
dotnet build Vydrix.slnx -c Release --no-restore

# Run the complete automated unit test suite (141 tests)
dotnet test tests/Vydrix.Tests/Vydrix.Tests.csproj -c Release --no-build
```

---

## CLI Reference

The command-line interface is available via `vydrix-cli.exe` (or `vydrix.exe` in the root distribution):

```powershell
vydrix deps                                  # Verify WinFsp, rclone, and storage prerequisites
vydrix remotes                               # List configured cloud storage accounts
vydrix mounts                                # Inspect virtual drive configurations
vydrix mount <mount-id> [--letter X]         # Mount a configured drive in the foreground
vydrix guardian                              # Inspect C: drive storage shield and capacity
vydrix workspace list                        # List cloud project workspaces
vydrix scan <folder> [--mbps 70]             # Analyze asset compatibility and missing runtime DLLs
vydrix pe <file.exe>                         # Inspect PE architecture and imports
vydrix profile <game-id>                     # Inspect learned access profiles and predictors
vydrix simcloud --root <dir> [--port 8080]   # Throttled mock cloud server for testing
vydrix secret emit-config-pass [--home dir]  # IPC helper for rclone --password-command
```

---

## Credits & Upstream Attribution

- **VYDRIX Engineering & Architecture:** Copyright (c) 2026 VYDRIX contributors.
- **Reference Baseline:** Portions Copyright (c) 2026 CLOUDPLAY X contributors. VYDRIX originated as an upgraded, security-hardened fork of the open-source CloudPlayX v0.7.0 architecture.
- **Underlying Ecosystem:**
  - [rclone](https://rclone.org/) — Cloud storage synchronization and VFS caching.
  - [WinFsp](https://winfsp.dev/) — Windows user-mode filesystem SPI.
  - [.NET & WPF](https://github.com/dotnet) — Application runtime and desktop UI.
  - [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM architecture.
  - [SQLite](https://www.sqlite.org/) — Local access profiles and game library storage.

For complete third-party dependency licenses, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

## License

VYDRIX is licensed under the [MIT License](LICENSE).
