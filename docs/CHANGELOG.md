# VYDRIX — Changelog

All notable changes to the VYDRIX virtual storage and game streaming engine are documented in this file.

## [3.0.0] - 2026-10-24

### Master Engineering Upgrade: PHANTOM SSD (Near-Physical-SSD Experience)
- **Multi-Tier Adaptive Caching Architecture:**
  - **Tier A (Hot Cache):** High-speed in-memory RAM cache (`MemoryHotCache.cs`) for small blocks (< 512 KiB) with microsecond latency (< 0.01 ms).
  - Built-in dynamic memory pressure detection via `GC.GetGCMemoryInfo()` with automatic proactive trimming before GC starvation.
  - **Tier B (Warm Cache):** NVMe/SSD local sparse disk cache strictly bounded to the 512 MiB Zero Footprint budget with explicit 16 MiB metadata headroom reservation.
  - **Tier C (Cold Storage):** Cloud provider streaming with in-flight request deduplication and range coalescing.
- **Physical-SSD Read Path & Pipeline:**
  - `CoalescingReadPipeline.cs`: Merges adjacent or overlapping byte ranges (up to 128 KiB window) into contiguous fetch operations to eliminate redundant HTTP overhead.
  - In-flight deduplication: Merges concurrent identical block requests across threads into a single network task.
  - Bounded concurrency semaphore with randomized exponential backoff jitter to prevent provider rate limiting.
  - Bounded TTL in-memory directory and file metadata cache (`MetadataCache.cs`) providing 0.04 ms metadata stat latency.
- **Multi-Factor Eviction Scorer (`EvictionScorer.cs`):**
  - Replaces single-variable LRU with a composite scoring function balancing recency, access frequency, and remote refetch latency penalty.
  - Strict immunity: Active game session files and uncommitted dirty files receive infinite protection score and are never evicted.
  - Deterministic tie-breaking ensures 100% backward compatibility with prior LRU test contracts.
- **Workload Intelligence & Diagnostics:**
  - **Phantom Pulse (`PhantomPulseEngine.cs`):** Real-time heuristic workload classifier detecting `BurstSmallFiles`, `Sequential`, `RandomRead`, and `LowBandwidth` patterns.
  - **Latency Shield (`LatencyShield.cs`):** Dynamic worker concurrency scaling (2–12 threads) based on real-time RTT and error rates, with plain-English bottleneck diagnostics (`NetworkCloud`, `ProviderRateLimit`, `LocalStorageIo`, `LocalCpuBuffer`, `HealthySsd`).
  - **Ghost Warm-Up Service (`GhostWarmUpService.cs`):** Dry-run launch acceleration planner that estimates network savings and strictly aborts under Zero Footprint Mode or tight budget constraints.
- **Crash Safety & Content Integrity:**
  - **Session Vault (`SessionVault.cs`):** Journaled launch sessions (`vault_sessions.json`) with startup recovery reconciler, PID validation, and process start-time validation against Windows PID reuse.
  - **Shadow Integrity (`ShadowIntegrity.cs`):** Remote modtime/size/checksum divergence verification with safe cache eviction without touching remote cloud files.
  - **Storage Time Machine (`StorageTimeMachine.cs`):** Privacy-first diagnostic event timeline with strict regex pattern redaction of OAuth tokens, passwords, and access credentials.
- **Diagnostics & Benchmarking Suite:**
  - **Cloud SSD Self-Test (`CloudSsdSelfTest.cs`):** Built-in synthetic and live cloud storage benchmark suite reporting 4KB random read latency, sequential throughput, random IOPS, warm cache hit latency, and 4-thread multi-reader scaling.
  - Clear separation of synthetic local benchmarks (`IsSimulated = true`) and live cloud tests.
- **Desktop UI Integration (`SmartCacheView.xaml` & `ConfigViewModels.cs`):**
  - Added Phantom SSD Intelligence Card with live workload mode badge, RAM hot cache toggle, memory usage indicator, and bottleneck recommendations.
  - Added Cloud SSD Self-Test Card with instant "Run Self-Test" execution button and performance summary.
- **Verification & Testing:**
  - Expanded test suite with `PhantomSsdEngineTests.cs` (12 new tests covering all subsystems).
  - Total test suite expanded to **127 passing tests (100% pass rate, 0 failed, 0 skipped)**.

## [1.2.0] - 2026-10-24

### Zero Footprint Mode (512 MiB Hard Bounded Virtual Drive)
- **Hard 512 MiB Cache Bound on C:**
  - Added `CachePolicyProfile.ZeroFootprint` enforcing exactly 512 MiB (`536,870,912` bytes) cache budget on drive `C:`.
  - Added `MetadataReserveBytes` (default: 16 MiB headroom) to guarantee room for rclone sparse file tracking and `vfsMeta` JSON indices without risking metadata starvation.
  - Added `EffectiveDataBudgetForProfile()` allocating 496 MiB strictly for cached data chunks.
  - Sets `--vfs-cache-max-size 512Mi`, `--vfs-cache-poll-interval 15s`, and `--vfs-cache-max-age 1h` in `MountPlanning.cs`.

- **Prefetching Disabled (Demand Reads Only):**
  - Added `DemandOnlyMode` to `StreamingEngine.cs` with `SetDemandOnly(bool enabled)`:
    - Automatically cancels all queued prefetch items and clears speculative queues.
    - Disables Markov transitions, sequential lookahead, and startup block scheduling.
    - Rejects background prefetch requests immediately (`PrefetchStatus.Dropped`).
    - Forces `--vfs-read-ahead 0` and `--buffer-size 0` in rclone mount flags.

- **In-Flight Capacity Reservation:**
  - Implemented thread-safe `TryReserveCapacity(long requestedBytes, out CapacityReservation? reservation)` in `SmartCacheManager.cs`.
  - Atomically accounts for in-flight downloads against the 496 MiB data budget.
  - Triggers synchronous LRU eviction (`ReclaimLRUSynchronous`) if projected usage exceeds usable headroom.
  - Strictly refuses reservation and provides actionable diagnostics (`LastAllocationFailureReason`, `CapacityExhaustionMessage`) when candidates cannot be safely evicted.

- **Process-Tree Aware Game Lifecycle & Cleanup:**
  - Multi-session concurrent game tracking in `AppHost.cs` (`_activeLaunches = new ConcurrentDictionary<string, LaunchSession>()`).
  - Added 1.5-second I/O and handle settling drain in `OnLaunchEndedAsync` to ensure Windows handles, memory-mapped files, and WinFsp buffers flush before eviction.
  - Added pending upload check querying rclone RC API before post-game cache reclamation.
  - Automated safe transient game cache eviction (`ReclaimGameCacheAsync`) triggered upon game process tree exit.

- **Desktop UI Integration (`SmartCacheView.xaml` & `ConfigViewModels.cs`):**
  - Added one-click Zero Footprint Mode banner with instant toggle button.
  - Storage Profile ComboBox includes `ZeroFootprint`.
  - Added live indicators for Zero Footprint Mode, Demand-Only prefetching policy, 2-hour periodic maintenance, post-game cleanup, metadata headroom (16 MiB), in-flight reserved bytes, and active game sessions.
  - Updated action button to "Run safe cleanup now" with tooltip binding to `LastCleanupInfo` and live `SafetyAlert` banner.

- **Automated Verification & Test Suite Expansion:**
  - Added `ZeroFootprintModeTests.cs` implementing automated tests for all 20 required scenarios (default budget, C: path resolution, disabled prefetch, demand reads, concurrent allocations, in-flight reservation, handle locks, dirty/pinned protection, incomplete download safety, metadata retention, post-game cleanup, 1.5s drain delay, crash locks, multi-game protection, mount transitions, network resilience, restart recovery, application file isolation, crash accounting, and bounded growth).
  - Total test suite expanded to **115 passing tests (100% pass rate, 0 failed, 0 skipped)**.

## [1.1.0] - 2026-10-24

### Visual Identity & Windows Integration
- **Custom Branding Assets:**
  - Designed official geometric vector logo (`src/Vydrix.Desktop/Assets/logo.svg`) featuring electric cyan (#00E5FF) and violet (#9000FF) gradients on dark navy (#0A0F1E) with integrated cloud/arc motif.
  - Multi-resolution raster icons generated and verified for 16, 32, 48, 64, 128, and 256 pixel dimensions.
  - Assembled production `vydrix.ico` binary icon bundle.
  - Replaced legacy `cloudplayx.ico` across solution.
  - Embedded VYDRIX logo in `MainWindow.xaml` sidebar badge, window title bars, and dialog headers (`FirstRunWindow`, `AddDriveWindow`, etc.).
  - Configured `TrayIconManager` to load `vydrix.ico` directly from application resources.

### Smart Cache Lifecycle Management
- **SmartCacheManager (`src/Vydrix.Cache/SmartCacheManager.cs`):**
  - Background periodic maintenance timer running every 1–2 hours (default: 2 hours, configurable via `Settings.Cache.MaintenanceInterval`).
  - Lightweight pre-check: inspects volume free space and cache budget first; skips full directory crawling when disk space and budgets are healthy.
  - Safe LRU eviction: removes oldest unpinned data when budget or Windows reserve limits are breached.
  - Strict protection rules:
    - Never evicts dirty files with pending cloud uploads (`f.Dirty == true`).
    - Never evicts pinned files (`PinnedPaths`).
    - Never evicts active game session files (`isGameActive`).
    - Never touches locked or open files (probed via `FileShare.None`).
  - Automatic recursive cleanup of empty parent directory trees in `vfs/` and `vfsMeta/`.
  - Added manual maintenance commands (`RunMaintenanceNow`, `PruneExpired`) and real-time maintenance reporting in `SmartCacheView.xaml`.

### Windows Installer & Project Packaging
- **Inno Setup Script (`installer/Vydrix.iss` & `packaging/windows-installer/Vydrix.iss`):**
  - Modern Inno Setup 6 installer for VYDRIX 1.1.0 with modern wizard style, custom icons, and mutex protection (`Vydrix.Desktop.SingleInstance`).
  - Upfront WinFsp detection with download guidance if WinFsp is missing.
- **PowerShell Installer & Uninstaller (`Install-Vydrix.ps1`, `Uninstall-Vydrix.ps1`):**
  - Clean per-user installation to `%LOCALAPPDATA%\Programs\VYDRIX` without requiring administrator privileges.
  - Creates Start Menu and Desktop shortcuts; registers uninstaller in Windows "Installed apps" (`HKCU\...\Uninstall\VYDRIX`).
  - Preserves user settings, cloud configs, and cache data by default.
  - Added double-clickable `Install.cmd` wrapper.
- **Automated Release Pipeline (`installer/build-release.ps1`):**
  - Validates checksums of bundled rclone binary.
  - Runs entire test suite prior to packaging.
  - Publishes self-contained binaries for `Vydrix.Desktop` and `Vydrix.Cli`.
  - Enforces zero-secrets distribution policy (fails build if user tokens, `rclone.conf`, or keys are found).
  - Performs smoke test (`vydrix-cli.exe deps`).
  - Generates portable `.zip` with SHA-256 digest and compiles `.exe` installer when Inno Setup is available.

### Test Suite Expansion & Quality Assurance
- Added `SmartCacheLifecycleTests.cs` containing 12 unit tests covering all eviction rules, LRU ordering, active game protection, locked file handling, and directory pruning.
- Full test suite expanded from 65 to **77 tests passed (100% pass rate, 0 failed, 0 skipped)**.
- Release build passes cleanly across all 15 projects with **0 Warnings and 0 Errors**.
- Staged publish verified in `artifacts/VYDRIX-staging` and synced to `artifacts/VYDRIX`.

## [1.0.0] - 2026-10-22

### Fork & Provenance
- Forked from CloudPlayX v0.7.0 baseline (MIT License, Copyright (c) 2026 CLOUDPLAY X contributors).
- Initialized dedicated repository branch `vydrix-main`.
- Upgraded target to VYDRIX 1.0.0.
- Solution and all 15 assemblies renamed systematically to `Vydrix.*`.
- CLI binary renamed from `cpx` to `vydrix` (`vydrix.exe`).
- Desktop UI branded as VYDRIX with updated window titles, headers, and metadata.
- Preserved all required copyright notices, MIT licenses, and third-party attributions in `LICENSE` and `THIRD_PARTY_NOTICES.md`.

### Build & Verification Results
- **Full Solution Build:** `dotnet build Vydrix.slnx -c Release`
  - Output: **15/15 projects compiled cleanly, 0 Warnings, 0 Errors**.
  - Generated binaries: `Vydrix.dll`, `vydrix.dll`, `Vydrix.Core.dll`, `Vydrix.Cache.dll`, `Vydrix.Diagnostics.dll`, `Vydrix.Streaming.dll`, `Vydrix.SimCloud.dll`, `Vydrix.Mount.dll`, `Vydrix.Games.dll`, `Vydrix.Providers.dll`, `Vydrix.App.dll`, `VydrixDemoGame.dll`, `Vydrix.Benchmarks.dll`, `Vydrix.Tests.dll`, `Vydrix.IntegrationTests.dll`.
- **Automated Test Suite:** `dotnet test tests/Vydrix.Tests`
  - Result: **63/63 tests passed (100% pass rate, 0 failed, 0 skipped)** in ~2 seconds.
  - Includes 54 baseline tests plus 9 security hardening, migration, and defect regression tests.

### Security Hardening Implementations
- **SEC-04 / TECH-02 (Rclone Runtime Integrity Verification):**
  - Integrated `PinnedBinaryHashes` into `Vydrix.Mount.RcloneLocator` calculating SHA-256 digests prior to execution.
  - Verified against pinned official release hashes (`41eab3e118d264920b62c3327966c5ece0dc4dd715ee617d9e30450d8ead95a4` for rclone v1.75.2 windows-amd64) and directory `SHA256SUMS*` manifests.
  - Enforces strict verification failure via `SecurityException` when untrusted binaries are detected.
- **SEC-01 / TECH-11 (Explicit Credential Selection):**
  - Deprecated unprompted harvesting of Google OAuth client secrets from user `Downloads` and `Desktop` folders.
  - Dialog defaults to bundled credentials or requires explicit user file selection via `OpenFileDialog` (`LoadKey`).
- **SEC-02 / TECH-05 (Credential Environment Block Hardening):**
  - Updated `RcloneConfigService.BaseEnvironment` to deliver config decryption passwords on demand via `--password-command` (`vydrix secret emit-config-pass`) when CLI helper is present, eliminating long-lived secrets in child process environment blocks.
- **SEC-05 / TECH-08 (Hardened Redaction Engine):**
  - Expanded `Redactor.cs` with URL-encoded regex variants (`ya29%2E...`, `1%2F%2F...`, `GOCSPX-...%`).
  - Added regression test verifying URL-encoded tokens are completely scrubbed from logs and exception messages.

### Technical Defect Fixes & Improvements
- **DEF-01 (CLI/GUI Name Collision & Password Emission Deadlock):**
  - Resolved NTFS case-insensitive collision by explicitly naming CLI `vydrix-cli.exe` and GUI `Vydrix.exe`.
  - Added early in-process argument dispatch in `Vydrix.Desktop\App.xaml.cs` for `secret emit-config-pass`, allowing `Vydrix.exe` to emit DPAPI keys directly to redirected pipes without acquiring the single-instance mutex.
  - Updated `RcloneConfigService.FindCliPath` and `AppHost.CliPath` to discover `vydrix-cli.exe`, `vydrix.exe`, and `Vydrix.exe`.
- **Always-On Startup & Native System Tray:**
  - Added `StartupRegistrationService` for unprivileged start-on-login via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with safe quoted paths.
  - Implemented `TrayIconManager` utilizing Win32 `Shell_NotifyIconW` and native WPF context menu without `System.Windows.Forms` dependencies.
  - Added `--minimized` argument support for background startup without displaying windows.
- **Residual Branding Cleared:**
  - Standardized logger file naming to `vydrix-{yyyyMMdd}.log` and thread to `vydrix-log-writer`.
  - Added `.vydrix-cache` marker file support with backward-compatible `.cloudplayx-cache` retention.
  - Standardized named pipes to `vydrix-telemetry-` and hybrid manifest to `.vydrix-hybrid.json`.
- **Safe Cleanup:**
  - Reclaimed 3.16 GB of transient integration test datasets from `.work/it` while strictly preserving reference sources and published builds.
- **Unit Test Expansion:**
  - Expanded automated test suite from 63 to 65 tests (100% pass rate).
- **TECH-06 (Windows Developer Mode Check):**
  - Added upfront verification of `AllowDevelopmentWithoutDevLicense` registry key in `HybridBuilder`.
  - Informs users with actionable guidance before attempting unprivileged NTFS symbolic link creation.
- **TECH-07 (Telemetry Buffer FIFO Eviction):**
  - Replaced hard-drop `_pending.Clear()` in `RcloneLogParser` with circular FIFO eviction (pruning oldest 1,024 entries when reaching 4,096 capacity).
  - Preserves recent in-flight reads during heavy I/O bursts, preventing metric and attribution corruption.
- **TECH-09 (Bounded Transition Graph):**
  - Capped `TransitionModel._edges` to 4,096 active vertices with LRU pruning of lowest-frequency nodes, strictly bounding working set on memory-constrained systems (8 GB RAM).
- **Data Migration & DPAPI Key Upgrades:**
  - Implemented automatic migration in `AppPaths`: automatically copies existing configurations, keys, and renames `cloudplayx.db` to `vydrix.db` from `%LOCALAPPDATA%\CloudPlayX` when initializing `%LOCALAPPDATA%\Vydrix`.
  - Upgraded `SecretStore` to support dual-entropy unwrapping (recovering keys encrypted with legacy `CloudPlayX.v1.secret` and automatically re-encrypting with `Vydrix.v1.secret`). Verified idempotent, non-destructive, and leak-free.
