# VYDRIX — Architecture

VYDRIX runs games and workloads on the user's own PC from files stored in cloud storage. It is built on proven components —
**rclone** (cloud backends + VFS sparse cache) and **WinFsp** (Windows user-mode filesystem) — and adds orchestration,
telemetry, prediction, game management and a desktop UI. No kernel driver and no second cache layer.

```
 ┌────────────────────────── Vydrix.Desktop (WPF, MVVM) ─────────────────────────┐   vydrix.exe (CLI)
 │ Dashboard · Cloud Drives · Game Library · Accelerator · Smart Cache · Perf · ...   │   benchmarks
 └───────────────────────────────────────┬────────────────────────────────────────────┘        │
                                         ▼                                                     ▼
 ┌──────────────────────────────── Vydrix.App: AppHost ──────────────────────────────────────┐
 │ settings · rclone discovery · mounts ⇄ engine wiring · metrics (1 Hz) · disk guard · games     │
 └──────┬──────────────┬────────────────┬───────────────────┬──────────────────┬──────────────────┘
        ▼              ▼                ▼                   ▼                  ▼
  Mount (rclone)   Cache            Streaming            Games              Providers / Diagnostics
  RcloneMountSvc   CacheInspector   StreamingEngine      Library (SQLite)   provider descriptors
  LogTailer+Parser DiskSafetyGuard  predictors           Scanner + PE       redaction, DPAPI,
  RcClient         CacheIndex       PrefetchQueue/Sched  Compatibility      dependency checks,
  GoogleDrive conn CacheMaintenance ProfileStore(SQLite) Launcher (job obj) diagnostic ZIP
                                    GameAware            Hybrid / FullLocal
                                    Simulator            Preparer
        │                                  ▲ AccessEvents (offset, size, hit/miss, wait)
        ▼                                  │
  rclone mount (child process) ── -vv log file ──► LogTailer ──► RcloneLogParser
        │  --vfs-cache-mode full, --buffer-size 0, --rc on 127.0.0.1 (random user/pass via env)
        ▼
  WinFsp drive X:  ⇄  sparse VFS cache on NTFS  ⇄  Google Drive / any rclone backend
```

## Projects

| Project | Responsibility |
|---|---|
| `Vydrix.Core` | Settings (atomic JSON), paths, units (Mbps vs MB/s), models, metric history, disk P/Invoke (sparse physical size, free space, filesystem), read-attribution registry |
| `Vydrix.Diagnostics` | Logging (file + in-memory, redacted), `Redactor`, DPAPI `SecretStore`, WinFsp/dependency checks, sanitized diagnostic ZIP |
| `Vydrix.Mount` | rclone discovery + capability detection (parses `mount --help`/`help flags`), argument-list runner, rc client, private config + encryption, Google Drive OAuth connector (rc `config/create` state machine), mount flag builder, drive letters, `RcloneMountService` (start/ready/health/crash-restart/graceful unmount/adoption), rotation-safe `LogTailer`, `RcloneLogParser` |
| `Vydrix.Cache` | `CacheInspector` (reads rclone `vfsMeta` range lists + `GetCompressedFileSizeW`), `DiskSafetyGuard` (budget vs. reserve, overrun margin, pressure), `CacheMaintenance` (marker-guarded clearing), `CacheIndex` |
| `Vydrix.Streaming` | Prediction engine: predictors, accounting, `PrefetchQueue`, `NetworkGovernor`, `SchedulingPolicy`, `PrefetchScheduler`, `MountReadExecutor`, `ProfileStore`, `StreamingEngine`, Game-Aware mode, deterministic simulator + synthetic traces |
| `Vydrix.Games` | Library, scanner, PE inspector, dependency resolver, compatibility analyzer, Hybrid builder, Full Local installer, launcher (job objects, NTSTATUS decoding, event log), network probe, Optimize & Prepare, pin keeper |
| `Vydrix.Providers` | Provider descriptors and support levels (Google Drive supported; others via rclone untested; TeraBox unsupported) |
| `Vydrix.App` | `AppHost` application service and `MetricsCollector` |
| `Vydrix.Desktop` | WPF UI (MVVM with CommunityToolkit), theme, `Sparkline` chart, dialogs |
| `Vydrix.Cli` | `cpx` CLI; also rclone's `--password-command` helper for DPAPI-protected config encryption |
| `Vydrix.SimCloud` | Throttled local HTTP file server for tests/benchmarks (bandwidth, latency, failures, drops, offline) |

## Key data flows

### Mount
1. `AppHost.MountAsync` → `DiskSafetyGuard.Decide` turns the budget into a safe `--vfs-cache-max-size`
   (current cache + free − reserve, minus a 10 % overrun margin, split across drives).
2. `MountFlagBuilder` emits only flags the installed rclone supports; dangerous extra flags (`*command*`, `rc*`,
   `config`, cache/log paths) are refused.
3. rclone starts with a random loopback rc port; rc user/password and the config password are passed **only via
   environment variables**. Std streams are redirected and drained (a kept-alive mount must not pin parent handles).
4. Ready = drive letter visible **and** rc responds. State is persisted (pid, start time, port, DPAPI-protected rc
   password) so a restarted app re-attaches to running mounts instead of starting duplicates.
5. Health check every 5 s; unexpected exit → auto-restart with exponential backoff (max 5 in 10 min); resume from
   sleep re-verifies mounts.
6. Unmount: wait for pending uploads (≤ 60 s) and for read activity to settle (rclone persists range indexes only
   after handles close + 5 s), then `core/quit`, then kill as a last resort.

### Telemetry (no admin, no injection)
rclone `-vv` with microsecond timestamps logs, per read, `_readAt: size, off`, `vfs cache: looking for range … present
true|false`, and `>_readAt: n, err`. The parser pairs them into `AccessEvent`s with offset, length, rclone's own
cache-hit verdict and the wait time; lookup lines arrive *before* the data and give a live "game is waiting on the
network" signal. Prefetch reads are separated from game reads with a range + time-window registry. ETW was evaluated
and rejected: file I/O tracing needs administrator rights and gives no VFS cache verdict.

### Prediction and prefetch (Universal Mode)
- **Blocks** of 1 MiB relative to the game folder are the unit of learning.
- **Predictors** (deterministic, explainable): `StartupPredictor` (blocks read in a *majority* of recorded launches,
  ordered by median first-access time, Laplace-smoothed confidence), `MarkovPredictor` (block→block transitions,
  confidence = count/(occurrences+1)), `SequentialPredictor` (run detection, depth = ~1 s of measured throughput).
- **Accounting** tracks useful vs. wasted prefetch per predictor; predictors below 25 % accuracy after 64 samples are
  disabled automatically (persisted per game).
- **Queue**: P1 > P2 > P3 then confidence then FIFO; dedupe, in-place upgrade, cancellation by owner/tag, TTL for
  speculative items, session byte budget, and a `Dropped` notification for every unfetched exit.
- **Policy**: P0 (game reads) always wins — while the game waits on the network only P1 runs with one stream; P2/P3
  resume after 750 ms and are rate-limited to the background share while the game is loading; nothing runs under
  disk pressure. Concurrency follows the bandwidth-delay product measured by the `NetworkGovernor`.
- **Executor** reads blocks through the mounted drive, so rclone's own sparse cache stores them.

### Optimize & Prepare
Scan folder → capacity check (budget, reserve) → real throughput probe with `rclone cat` (bypasses the cache) →
profile → compatibility + missing DLLs → essentials (or Hybrid build / Full Local check) → startup prefetch within
the available budget → validation, re-checking coverage against **rclone's on-disk range index** after its
handle-caching delay. Readiness is reported honestly; with no profile it says the startup data is unknown.

### Execution modes
- **Cloud Direct**: launch from `X:\…`.
- **Hybrid Optimized**: local folder mirroring the tree; executables, DLLs, configs and files ≤ 2 MiB copied
  (stamp-checked, user-modified local copies are kept), large files become symbolic links into the drive.
- **Full Local**: `rclone copy` (checksummed, resumable) after explicit approval.
The launcher tracks the process tree with a job object (no kill-on-close), decodes NTSTATUS exit codes, collects
Application event-log entries, writes a launch diagnostics JSON, and marks a mode **Verified** only after ≥ 60 s.

### Game-Aware Mode
Optional, per title. A current-user-only named pipe receives JSON telemetry from an authorized plugin; a region map
associates blocks with regions only after repeated, dominant co-occurrence; region entry schedules P1 (current) and
P2 (likely next, heading-weighted) work, all cancellable by tag. See `docs/GAME_AWARE_PROTOCOL.md`.

## Persistence

| Data | Location |
|---|---|
| Settings | `%LOCALAPPDATA%\Vydrix\settings.json` (atomic replace; corrupt files are kept aside) |
| Library, profiles, sessions, transitions, predictor stats | `vydrix.db` (SQLite, WAL) |
| Cloud logins | `rclone.conf` (rclone-encrypted) + `rclone.conf.key` (DPAPI) |
| Running mounts | `mounts.state.json` (rc password DPAPI-protected) |
| Cache | `<cache root>\m-<drive id>\{vfs,vfsMeta}` + `.vydrix-cache` marker |
| Logs / launch diagnostics | `logs\`, `diagnostics\` |

## Security model

Least privilege (`asInvoker`, never admin); rc bound to 127.0.0.1 with random credentials; secrets only in env vars,
DPAPI or HTTP bodies to localhost; redaction on every log line and export; single-instance and data-folder locks
prevent concurrent cache ownership; cache deletion only inside the configured root and only in marked folders;
cloud data is never deleted by cache management; no DRM/anti-cheat interaction; dependencies are checked, not
silently installed; rclone is pinned and checksum-verified.
