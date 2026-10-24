<div align="center">

<img src="assets/branding/vydrix-logo-dark.png" alt="VYDRIX Logo" width="360" />

# VYDRIX

**Experimental Windows Cloud-Backed Virtual Storage Prototype**

[![Version](https://img.shields.io/badge/version-0.003.1-blue.svg)](https://github.com/HamzaShaikhAI/VYDRIX/releases/tag/v0.003.1)
[![Status](https://img.shields.io/badge/status-discontinued%20%7C%20failed%20experiment-critical.svg)](https://github.com/HamzaShaikhAI/VYDRIX)
[![CI](https://github.com/HamzaShaikhAI/VYDRIX/actions/workflows/ci.yml/badge.svg)](https://github.com/HamzaShaikhAI/VYDRIX/actions/workflows/ci.yml)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey.svg)](https://github.com/HamzaShaikhAI/VYDRIX)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

<br />

> [!CAUTION]
> **DISCONTINUED — FAILED EXPERIMENT**  
> **Project:** VYDRIX  
> **Version:** `0.003.1`  
> **Support:** No future development, bug fixes, or support are promised.  
> **Purpose:** An experimental attempt to use consumer cloud storage as a Windows virtual drive for games and AI workloads without consuming physical local disk space. The project **failed to achieve its core goal** and is archived solely as a technical reference and post-mortem.

</div>

---

## Technical Post-Mortem: Why the Experiment Failed

The core thesis of VYDRIX was that a Windows PC could run large games and AI models from cloud storage (Google Drive) while maintaining near-zero local disk footprint on `C:`. Real-world stress testing on Windows 11 demonstrated that this model is fundamentally flawed under consumer cloud storage and user-mode filesystem constraints.

### 1. The VFS Cache Bound Does Not Restrict Writes
The underlying rclone VFS cache configuration (`--vfs-cache-max-size`) manages *read eviction*, but it **does not strictly limit active or pending writes**. When an application writes or moves large files (e.g. an installer, game updates, or model checkpoints) onto the virtual drive, the files are written directly to the local NTFS cache on physical disk.

### 2. Upstream API Throttling Traps Data Locally
Files written to the virtual drive must remain buffered locally on NTFS until remote uploads complete. Consumer cloud APIs (such as Google Drive) enforce strict rate limits and daily upload quotas. Under burst-heavy write activity, uploads quickly encounter `HTTP 403 (User Rate Limit Exceeded)` errors, stalling synchronization. Because discarding dirty buffers causes immediate data corruption, the unuploaded files remain trapped on the local drive, directly violating the product's storage-saving promise.

### 3. Severe Latency Penalties on Random I/O and Memory Mapping
Modern 3D games and machine learning runtimes (e.g., Ollama, PyTorch) perform intensive random 4KB reads and rely on memory-mapped file access (`mmap`). Emulating physical SSD speeds over a user-mode filesystem bridge (WinFsp) backed by remote HTTPS range requests introduces latency orders of magnitude higher than physical storage (50–150 ms round-trips vs. < 0.05 ms on NVMe). This saturates Windows kernel I/O queues, causing Task Manager disk active time to spike to 100% and creating desktop micro-freezes.

### 4. Summary Verdict
VYDRIX did not reliably satisfy its near-zero-local-storage goal. For heavy, latency-sensitive workloads like AAA gaming and large model weights, high-speed physical NVMe storage or dedicated remote streaming protocols (such as GeForce NOW or Moonlight) remain fundamentally necessary.

---

## Experimental Architecture Summary

During development of the `0.003.1` prototype, several subsystems were implemented to investigate virtual drive behavior:

- **Virtual Drive Bridge:** WinFsp user-mode filesystem service backed by an rclone subprocess mounting Google Drive as drive `G:`.
- **Sparse Cache Management:** NTFS sparse file allocation tracking physical vs. logical cluster usage.
- **Predictive Prefetching Engine:** Telemetry parser paired with Laplace-smoothed startup and Markov transition predictors to anticipate read patterns.
- **Three-Tier Cache Concept:** In-memory RAM hot cache (`MemoryHotCache`), local sparse disk cache, and range-coalescing HTTP read pipelines (`CoalescingReadPipeline`).
- **C: Shielding Heuristics:** Capacity monitoring designed to redirect scratch files and caches to secondary volumes (`D:\`) or RAM when physical `C:` headroom is depleted.
- **Security Hardening:** DPAPI key encryption for local credentials, `--password-command` IPC isolation preventing password disclosure in process tables, and runtime cryptographic SHA-256 binary integrity checks.

---

## Final Archive Download

The final archived Windows x64 binary distribution package is available on GitHub Releases:

📦 **[Download VYDRIX v0.003.1](https://github.com/HamzaShaikhAI/VYDRIX/releases/tag/v0.003.1)**

### Release Artifact Information

| Parameter | Value |
| :--- | :--- |
| **Archive File** | `VYDRIX-0.003.1-win-x64.zip` |
| **Archive Version** | `0.003.1` |
| **Release Tag** | [`v0.003.1`](https://github.com/HamzaShaikhAI/VYDRIX/releases/tag/v0.003.1) |
| **Platform** | Windows 10 / Windows 11 (x64) |
| **Package Size** | 34,701,809 bytes (33.09 MB) |
| **SHA-256 Checksum** | `10770FB49C49BD24D11B183457500892CAB51FB47BD624AD09DF78FC833E3B47` |
| **Prerequisites** | .NET 10.0 x64, WinFsp 2.1+ |

To verify the integrity of the downloaded archive in PowerShell:
```powershell
(Get-FileHash -Path .\VYDRIX-0.003.1-win-x64.zip -Algorithm SHA256).Hash -eq "10770FB49C49BD24D11B183457500892CAB51FB47BD624AD09DF78FC833E3B47"
# Expected output: True
```

---

## Building from Source

### Prerequisites
- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (v10.0.100 or later)
- [WinFsp](https://winfsp.dev/) (required for virtual drive mounting)
- PowerShell 7+ or Windows PowerShell 5.1

### Build & Test Commands

```powershell
# Clone the repository
git clone https://github.com/HamzaShaikhAI/VYDRIX.git
cd VYDRIX

# Restore solution dependencies
dotnet restore Vydrix.slnx

# Build Release binaries
dotnet build Vydrix.slnx -c Release --no-restore

# Run the unit test suite
dotnet test tests/Vydrix.Tests/Vydrix.Tests.csproj -c Release --no-build
```

---

## CLI Reference

The experimental command-line interface is available via `vydrix-cli.exe` (or `vydrix.exe` in the root distribution):

| Command | Description |
| :--- | :--- |
| `vydrix deps` | Check WinFsp, rclone, and storage prerequisites |
| `vydrix remotes` | List configured cloud storage accounts |
| `vydrix mounts` | Inspect virtual drive configurations |
| `vydrix mount <mount-id> [--letter X]` | Mount a configured drive in the foreground |
| `vydrix guardian` | Inspect physical drive capacity and storage allocations |
| `vydrix workspace list` | List development workspace configurations |
| `vydrix scan <folder> [--mbps 70]` | Inspect game files and missing runtime dependencies |
| `vydrix pe <file.exe>` | Inspect PE architecture and imports |
| `vydrix profile <game-id>` | Inspect learned telemetry access profiles |
| `vydrix simcloud --root <dir> [--port 8080]` | Throttled mock cloud HTTP server for test simulation |
| `vydrix secret emit-config-pass [--home dir]` | IPC helper for rclone `--password-command` |

---

## Credits & Upstream Attribution

- **VYDRIX:** Copyright (c) 2026 VYDRIX contributors.
- **Reference Baseline:** Portions Copyright (c) 2026 CLOUDPLAY X contributors. VYDRIX originated as an experimental fork of the open-source CloudPlayX v0.7.0 reference code.
- **Dependencies:**
  - [rclone](https://rclone.org/) — Cloud storage synchronization and VFS caching.
  - [WinFsp](https://winfsp.dev/) — Windows user-mode filesystem driver and SPI.
  - [.NET & WPF](https://github.com/dotnet) — Application framework and desktop interface.
  - [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM architecture.
  - [SQLite](https://www.sqlite.org/) — Profile and game metadata storage.

For third-party dependency licenses, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

## License

VYDRIX is licensed under the [MIT License](LICENSE).
