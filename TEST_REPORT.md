# Test report — VYDRIX (Archived Release Baseline)

All tests below were **actually executed** on the development PC on 2026-10-10/11:
Windows 11 Pro 25H2 (10.0.26200), non-admin user, Developer Mode on, Intel i3-10105F, RX 580, 16 GB RAM,
WinFsp 2.1.25156, rclone v1.75.2, .NET 10.0.3. Nothing was run on a clean machine or in CI.

## Summary

| Suite | Result |
|---|---|
| Unit tests (`tests/Vydrix.Tests`) | **54 / 54 passed** (0.7.0 release build, 2026-10-12) |
| Integration tests (`tests/Vydrix.IntegrationTests`, real rclone + WinFsp mounts) | **27 / 27** — final full run 26 passed, 1 failed (test-design flaw, see below), fixed and re-run: passed |
| End-to-end game flow (in the integration suite) | passed |
| Release build + `vydrix deps` smoke test in published folder | passed |
| Install / uninstall of the release ZIP (custom folder) | passed (see notes) |
| Desktop UI: launch, auto-mount, all 8 pages rendered via UI Automation | passed after fixing one bug (below) |
| Google Drive end-to-end | **not run** — needs the user's own OAuth client and Google account |
| Commercial games | **not run** — no licensed game files available |

## How to reproduce

```bash
dotnet test tests/Vydrix.Tests
```

The integration suite creates a ~0.9 GB dataset under `.work/it/` on the first run. The end-to-end game test needs
the demo game copied into that dataset:

```bash
dotnet build tests/VydrixDemoGame -c Release
```

```bash
powershell -Command "$g='.work/it/dataset/Games/CPX Demo Game'; New-Item -ItemType Directory -Force $g; Copy-Item tests/VydrixDemoGame/bin/Release/net10.0-windows/* $g -Exclude *.pdb; & \"$g/CpxDemoGame.exe\" --generate $g"
```

```bash
dotnet test tests/Vydrix.IntegrationTests
```

## Unit tests (52)

Units (Mbps vs MB/s, size parsing, block math), settings round trip and corrupt-file recovery, redaction of tokens /
client secrets / rc passwords / query secrets, DPAPI round trip and fail-closed, rclone log parser on **lines captured
from rclone 1.75.2** (offsets, hit/miss, wait time, EOF, errors, rate-limit and auth classification, attribution of
prefetch reads), mount flag builder (safe defaults, unsupported flags → warnings, blocked dangerous extra flags),
drive-letter allocation, cache inspector on rclone metadata, disk-safety decisions, marker-guarded cache clearing,
prefetch queue (priority order, dedupe, in-place upgrade, cancellation, TTL, budget, drop notifications, explicit
requests), scheduling policy (P0 protection), network governor (bandwidth-delay concurrency), Markov / sequential /
startup predictors, accounting and auto-disable, SQLite profile store, simulator determinism and SSD/cold sanity,
warm-profile improvement in simulation, Game-Aware region validation, neighbour scheduling, fast-travel cancellation,
heading weighting, and a real named-pipe telemetry round trip.

## Integration tests (27) — real mounts

| Test | Result | Measured detail |
|---|---|---|
| Drive visible, directory listing, Unicode/spaces | ✅ | `X:` fixed drive, WinFsp |
| Whole-file SHA-256 matches source | ✅ | |
| 40 random range reads match source bytes | ✅ | |
| Offsets beyond 2 GiB in a 3 GiB sparse file | ✅ | |
| Partial reads create a sparse cache; warm reads use no network | ✅ | 3 × 64 KiB cold reads fetched ~0.9 MiB; warm reads: 0 bytes, telemetry reported hits |
| Multiple concurrent handles | ✅ | |
| Memory-mapped read | ✅ | |
| File locking characterized | ✅ | behaviour recorded in `.work/it/locking-result.txt` |
| Executables launch from the drive | ✅ | `where.exe`, `hostname.exe` |
| Metadata (size, mtime) | ✅ | |
| Cache reused after unmount/remount | ✅ | **0 bytes** re-downloaded |
| rclone crash → automatic restart, data correct | ✅ | recovered in ~3 s |
| LRU eviction at `--vfs-cache-max-size` | ✅ | oldest file evicted, newest kept, cloud original untouched |
| Low disk space → eviction via min-free-space | ✅ | range index emptied |
| Remote file change → stale data not served | ✅ | |
| Offline: uncached read fails, cached read works, recovery | ✅ | error after 0.1 s; correct data after reconnect |
| Transient HTTP 503/429 and dropped connections | ✅ | all bytes correct with injected faults |
| Cold throughput at 10/30/70/100/500 Mbps | ✅ | 9.2 / 29.3 / 69.8 / 100.2 / 493 Mbps achieved |
| Read-only mount rejects writes | ✅ | creates fail with EROFS; DeleteFile reports success but file survives |
| Writes, rename, mkdir, delete reach the remote | ✅ | |
| Pending uploads survive an rclone crash | ✅ | uploaded after restart |
| End-to-end demo game flow | ✅ | see below |

### End-to-end game flow (AppHost + demo game, SimCloud 70 Mbps / 80 ms)

| Step | Result |
|---|---|
| Add game from the drive | exe detected, "No blockers found", Hybrid recommended |
| Launch 1, cold, Cloud Direct | load 20.3 s, 115 MiB from cloud, profile saved |
| Launch 2, new level | load 8.2 s (startup + menu cached) |
| Cache cleared → Optimize & Prepare | measured 73.7 Mbps; 145 planned blocks, 140 fetched (139 MiB) in 21 s; **145/145 verified in rclone's cache index** → "Ready to launch" |
| Launch 3, unseen level | load 9.2 s; startup 0.29 s + menu 1.2 s (cold: ~1.1 s + ~10 s); level data streamed |
| Hybrid build + launch 4 | exe/DLLs copied, archives symlinked; load 5.6 s from the local folder |

## Bugs found by testing and fixed

1. **False readiness** — Prepare reported "Ready, 100 % cached" after fetching 0 bytes because in-memory
   "known cached" entries survived a cache clear. Fixed: entries expire, are dropped on unmount, and readiness is
   verified against rclone's on-disk range index.
2. **Prepare hang** — queued items that expired/were cancelled never reported back. Fixed: every unfetched exit
   raises a drop notification; explicit Prepare items do not expire; stuck detection added.
3. **Orphaned rclone pinned the parent's stdout** (test host hang). Fixed: rclone std streams are redirected and drained.
4. **Range index lost on quick quit** — rclone persists it only after handles close + 5 s. Fixed: graceful unmount
   waits for activity to settle.
5. **Startup window measured in wall time** — slow links pushed startup data out of the window. Fixed: game-progress
   time (wall time minus network waits).
6. **Launch-time prefetch of uncertain data hurt slow links** (simulation). Fixed: only consistently seen data
   (confidence ≥ 0.7) is released during a launch, just in time.
7. **Network governor under-estimated link rate** on high-latency single streams. Fixed.
8. **UI**: duplicate resource key crashed startup; Smart Cache preset buttons threw a type error. Both fixed and
   re-verified by UI Automation screenshots.
9. **Installer**: uninstaller could close any CloudPlayX process; message overstated removal. Fixed.

## Notes and failures

- The one failure in the final full run was `Transient…(dropRate: 0.3)`: the random fault injector happened to drop
  no connection in that run (all data was still verified correct), so the "a fault was injected" assertion failed.
  The test now keeps reading until a fault really occurs; re-run passed (2/2).
- Install test used a custom folder; the uninstaller's guard deliberately leaves folders whose name lacks
  "CloudPlayX" (it removed shortcuts and the uninstall entry, and now says so). Default installs are removed fully.
- During development an external, transient system-wide commit-memory exhaustion killed three rclone processes at
  once; this motivated the auto-restart feature (now tested).

## 0.7.0 setup.exe (2026-10-12)

Executed on the same PC (WinFsp already installed) with `CloudPlayX-0.7.0-setup.exe`, silent mode, custom folder:

| Check | Result |
|---|---|
| Install: files, rclone, uninstaller present; product version 0.7.0 | passed |
| Installed app starts (window "CLOUDPLAY X", separate test data folder) | passed |
| Setup while the app is running | refused (exit 1, AppMutex) |
| Setup while an rclone process runs from the install folder (mounted drive) | refused with the "drive still mounted" message (exit 7) |
| Upgrade over an existing install | passed |
| Uninstall removes program files and the *Installed apps* entry, keeps user data | passed |
| WinFsp auto-install from the bundled signed MSI | **not tested**: WinFsp was already installed here; needs a clean PC or VM |