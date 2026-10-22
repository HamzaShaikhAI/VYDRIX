# VYDRIX

**Windows Cloud-Backed Virtual Storage & Streaming System**

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

## License & Attribution

- VYDRIX is licensed under the [MIT License](LICENSE).
- Portions Copyright (c) 2026 CLOUDPLAY X contributors.
- Third-party components (rclone, WinFsp, .NET, NuGet packages) retain their respective licenses as detailed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
