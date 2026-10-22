# CLOUDPLAY X

**Cloud-to-SSD virtual storage and intelligent game streaming engine for Windows 10/11 (x64).**

Keep your PC games in cloud storage (Google Drive first), see them as a normal drive letter in File Explorer, and run
them **on your own PC** while CLOUDPLAY X caches only the parts the game actually reads on your SSD — and learns
what to download before you press PLAY.

> CLOUDPLAY X is **not** cloud gaming. Your CPU and GPU run the game. Data that is not cached yet arrives at your
> internet speed (70 Mbps ≈ 8.75 MB/s at best). It gets close to SSD behaviour for data that is already cached, and
> it reports honestly when something is not ready. See [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).

## What it does

- **Real virtual drive** — rclone + WinFsp mount (`X:` etc.), sparse block-level cache on NTFS, writes with safe
  write-back, crash auto-restart, re-attach after app restart, sleep/resume checks.
- **Google Drive** with your own OAuth client (sign-in in your browser; login encrypted with DPAPI).
- **Smart cache** — budget presets (10/20/30/40 GB/custom), protected free space for Windows, physical-vs-logical
  usage of sparse files, pinning, marker-guarded cleanup that never touches cloud files or save games.
- **Prediction engine** — byte-exact read telemetry from rclone, per-game profiles (SQLite), startup / transition /
  sequential predictors with measured accuracy and auto-disable, P0–P3 priority scheduling that never competes with
  the game's own reads, bandwidth-delay-aware concurrency.
- **Game library & launcher** — executable detection, PE import check for missing DLLs, anti-cheat/launcher
  detection, three modes (Cloud Direct, Hybrid Optimized, Full Local), launch monitoring and diagnostics.
- **Optimize & Prepare** — measures your real connection, prefetches the learned startup set within budget and
  verifies it in rclone's own cache index before saying "Ready to launch".
- **Live monitor** — throughput, hit/miss, read waits, stalls, prefetch accuracy, errors/retries; "Unavailable" when
  there is no real source.
- **Game-Aware Mode** (experimental) — telemetry protocol for authorized game plugins (none exist yet).

## Measured (details in [BENCHMARKS.md](BENCHMARKS.md))

DRM-free CPX demo game (54.5 MiB read in 409 reads) over a simulated 70 Mbps / 80 ms cloud link, real rclone +
WinFsp stack on this project's i3-10105F test PC:

| | Load time |
|---|---|
| Local SSD | 0.09–0.12 s |
| Standard rclone mount, default flags, cold | 56.5–65.2 s (downloaded 498–551 MiB) |
| CLOUDPLAY X, cold, no profile | 20.3–21.0 s (117–120 MiB) |
| CLOUDPLAY X, profiled + Optimize & Prepare (~32 s beforehand), level never seen before | 7.7–8.2 s |
| CLOUDPLAY X, fully cached | 0.15–0.27 s |

No commercial game has been tested yet — see [COMPATIBILITY.md](COMPATIBILITY.md).

## Install

Requirements: Windows 10 (1809+) / 11 x64 and an NTFS drive for the cache. No separate .NET install needed.

**Recommended:** download `CloudPlayX-0.7.0-setup.exe` from the **Releases** page of this repository and run it. It installs for the current user
(`%LOCALAPPDATA%\Programs\CloudPlayX`, Start-menu and desktop shortcut, entry in *Installed apps*). If
[WinFsp](https://winfsp.dev) is missing, it installs the official signed WinFsp package too. That is the only step
that asks for administrator permission. The setup is not code-signed, so Windows SmartScreen may show
"Windows protected your PC": click **More info → Run anyway**.

Portable alternative: extract `CloudPlayX-0.7.0-win-x64.zip`, install WinFsp yourself
(`winget install WinFsp.WinFsp`) and double-click `Install.cmd`, or add `-DesktopShortcut` for a desktop icon:

```bash
powershell -ExecutionPolicy Bypass -File Install-CloudPlayX.ps1 -DesktopShortcut
```

Or run `CloudPlayX.exe` directly from the extracted folder (portable).

Uninstall from *Settings → Apps → Installed apps → CLOUDPLAY X*. Settings, library and cache are kept unless you
choose to delete them; cloud files are never touched.

## First steps

1. **First run**: choose the cache folder (CLOUDPLAY X suggests the NTFS drive with the most free space — on the
   development PC, C: had only ~4–7 GB free), the budget and your internet speed in **Mbps**.
2. **Cloud Drives → Connect Google Drive** — follow [docs/GOOGLE_DRIVE_SETUP.md](docs/GOOGLE_DRIVE_SETUP.md).
3. **Add virtual drive** — pick the account and your games folder; the drive appears in File Explorer.
4. **Game Library → Add game** — select an *installed* game folder on the drive. Read the compatibility report.
5. **PLAY** once — the first launch streams on demand and is profiled.
6. Next time, run **Optimize & Prepare** before playing; it downloads the learned startup data while you wait.

## Build from source

Requirements: .NET 10 SDK, WinFsp (for integration tests), PowerShell.

```bash
powershell -ExecutionPolicy Bypass -File installer/fetch-rclone.ps1
```

```bash
dotnet build CloudPlayX.slnx -c Release
```

```bash
dotnet test tests/CloudPlayX.Tests
```

```bash
dotnet test tests/CloudPlayX.IntegrationTests
```

```bash
powershell -ExecutionPolicy Bypass -File installer/build-release.ps1
```

Integration tests mount real drives through rclone + WinFsp against a local throttled server; they need ~1 GB on the
repository drive (`.work/`). The end-to-end game test needs the demo game installed into the test dataset — see
[TEST_REPORT.md](TEST_REPORT.md).

## Documentation

| | |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Components and data flows |
| [BUILD_STATUS.md](BUILD_STATUS.md) | Implemented / tested / partial / blocked |
| [TEST_REPORT.md](TEST_REPORT.md) | Actual test results |
| [BENCHMARKS.md](BENCHMARKS.md) | Methodology and measured results |
| [COMPATIBILITY.md](COMPATIBILITY.md) | Tested filesystem features, apps and games |
| [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) | Unresolved restrictions |
| [docs/GOOGLE_DRIVE_SETUP.md](docs/GOOGLE_DRIVE_SETUP.md) | OAuth client setup |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Problems and fixes |
| [docs/TERABOX_RESEARCH.md](docs/TERABOX_RESEARCH.md) | Why TeraBox is not supported |
| [docs/GAME_AWARE_PROTOCOL.md](docs/GAME_AWARE_PROTOCOL.md) | Telemetry protocol for game plugins |

## Principles

No DRM, anti-cheat, launcher or ownership bypasses. No fabricated metrics or benchmarks. No deletion of cloud data.
No silent security-setting changes. No claims of game support without a real, monitored launch.

## License

[MIT](LICENSE). Bundled third-party components (rclone, WinFsp installer, .NET runtime, NuGet packages) keep their
own licenses: see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Contributions: [CONTRIBUTING.md](CONTRIBUTING.md).
Security reports: [SECURITY.md](SECURITY.md).
