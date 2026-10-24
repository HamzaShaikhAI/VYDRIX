# Build status — VYDRIX 3.0 (Archived)

Legend: ✅ implemented and tested on this machine · 🟡 implemented, partially tested / tested only in simulation ·
⚪ implemented, not exercised by automated tests · ⛔ blocked (external dependency) · ❌ not implemented

Environment: Windows 11 Pro 25H2, non-admin user, Developer Mode on, i3-10105F, RX 580, 16 GB, WinFsp 2025,
rclone 1.75.2, .NET 10.0.3. Test results: [TEST_REPORT.md](TEST_REPORT.md).

## M1 — Functional foundation

| Feature | Status | Notes |
|---|---|---|
| Repository, solution, 12 projects, DI-friendly services | ✅ | `Vydrix.slnx` |
| Desktop application (WPF, MVVM, 8 pages, dialogs) | ✅ | Launched on this PC, auto-mounted a drive, live metrics rendered (screenshot verified). Pages driven by UI Automation for screenshots; most dialogs not click-tested |
| rclone integration (discovery, version, flag capability detection) | ✅ | |
| WinFsp detection | ✅ | |
| Google Drive connection (own OAuth client, rc state machine, encryption) | 🟡 | State machine verified up to the OAuth browser step with a dummy client. ⛔ End-to-end needs the user's Google OAuth client/account |
| Real drive letter mount, file reads, cache dir, integrity | ✅ | Integration tests with SHA-256 comparison |
| Settings persistence | ✅ | Atomic JSON; corrupt file recovery tested |

## M2 — Reliable cache and monitoring

| Feature | Status | Notes |
|---|---|---|
| Cache budget, presets, reserve, overrun margin | ✅ | Eviction and low-disk eviction verified with real rclone |
| Physical vs logical usage of sparse files | ✅ | `GetCompressedFileSizeW` + rclone range index |
| Cache reuse after restart | ✅ | 0 bytes re-downloaded |
| Live transfer statistics (rclone rc) | ✅ | Upload speed shown as Unavailable (not separable in rclone stats) |
| Disk safety rules (pause prefetch, warnings) | ✅ | |
| Restart recovery: crash auto-restart, adoption of running mounts, resume from sleep | ✅ / ⚪ / ⚪ | Crash restart tested; adoption and sleep/resume implemented, not automated |
| Network failure handling (offline, 503/429, dropped connections) | ✅ | Errors surfaced, no fabricated data, recovery verified |
| Remote file change detection | ✅ | Stale data not served |
| Write-back and pending-upload recovery | ✅ | |
| Logging, redaction, diagnostics export | ✅ / ⚪ | Redaction unit-tested; export implemented, not automated |

## M3 — Game compatibility layer

| Feature | Status | Notes |
|---|---|---|
| Game library (SQLite) | ✅ | |
| Executable detection + PE import inspection | ✅ | |
| Missing dependency detection (VC++/DirectX/etc.) | ✅ | Unit-level via `vydrix pe`; demo game had none missing |
| Compatibility analyzer (anti-cheat, launchers, installers, engines) | 🟡 | Rules implemented; exercised on the demo game only |
| Launch + process-tree monitoring + failure diagnostics | ✅ | Job objects, NTSTATUS decoding, event log |
| Cloud Direct mode | ✅ | Demo game |
| Hybrid Optimized mode (copy essentials, symlink assets) | ✅ | Demo game |
| Full Local Fallback (rclone copy, approval, resumable) | ⚪ | Implemented in UI and engine; not in the automated suite |
| Optimize & Prepare with verified readiness | ✅ | Coverage verified against rclone's range index |
| Real commercial DRM-free game | ⛔ | No licensed game files available here |

## M4 — Intelligent streaming

| Feature | Status | Notes |
|---|---|---|
| Per-read telemetry (offset, size, hit/miss, wait) from rclone | ✅ | Parser unit-tested on captured lines; live in integration tests |
| Prefetch attribution (separating own reads) | ✅ | |
| Persistent access profiles | ✅ | |
| Startup, Markov, Sequential predictors; confidence; auto-disable | ✅ / 🟡 | Unit-tested; effect measured in simulation and with the demo game |
| P1–P3 queue: dedupe, reprioritize, cancel, TTL, budget, drop notifications | ✅ | |
| Foreground protection (P0), bandwidth-delay concurrency, background rate | ✅ / 🟡 | Policy unit-tested; real effect measured end-to-end |
| Just-in-time startup release during launch | 🟡 | Simulation-validated |
| Cold/warm benchmarks with measurable improvement | ✅ | Real stack over SimCloud + simulation matrix |

## M5 — Advanced game optimization

| Feature | Status | Notes |
|---|---|---|
| Game-Aware interface (pipe protocol, region learning, prediction, cancellation) | 🟡 | Unit-tested with synthetic telemetry incl. real named pipe |
| Game-specific plugins | ⛔ | No authorized integration for any title exists |
| Larger games (Cyberpunk 2077, GTA V, Black Myth: Wukong) | ⛔ | Not owned/available; research targets only |
| Fallback modes | ✅ | Hybrid + Full Local |

## M6 — Providers and release

| Feature | Status | Notes |
|---|---|---|
| TeraBox | ❌ (by decision) | No official API; see docs/TERABOX_RESEARCH.md |
| OneDrive / Dropbox / S3 / NAS via rclone | ⚪ | Mountable through existing rclone remotes; untested |
| Release packaging (self-contained ZIP, install/uninstall scripts, uninstall entry) | ✅ | See TEST_REPORT.md for the install verification on this PC |
| Inno Setup `.exe` installer | ⚪ | Script provided; Inno Setup not installed here |
| Clean-machine installer verification | ⛔ | No clean Windows VM available |
| Code signing | ❌ | |
| Documentation set | ✅ | |
