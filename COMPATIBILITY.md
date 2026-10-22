# Compatibility

Only behaviour that was actually executed is listed as verified. Everything else is labelled honestly.

## Test environment

| Item | Value |
|---|---|
| OS | Windows 11 Pro 25H2 (10.0.26200), user account without administrator rights, Developer Mode on |
| CPU / GPU / RAM | Intel Core i3-10105F / AMD Radeon RX 580 2048SP / 16 GB |
| WinFsp | 2.1.25156 (2025) |
| rclone | v1.75.2 (bundled, checksum-verified) |
| .NET | 10.0.3 |
| "Cloud" used in tests | SimCloud (local HTTP server, 127.0.0.1) mounted via rclone's `http` backend; rclone `local` backend for write tests |

**Google Drive was not tested end-to-end in development** because it needs the user's own OAuth client and Google
account. The connection workflow (rclone rc `config/create` state machine) was exercised up to the OAuth step with a
dummy client; the mount, cache and streaming layers are backend-independent and were tested through SimCloud.

## Filesystem behaviour on the virtual drive (rclone 1.75.2 + WinFsp, VFS full cache)

| Operation | Result | Evidence |
|---|---|---|
| Drive letter visible, `DriveInfo.IsReady` | ✅ verified | integration test |
| Directory listing, Unicode and spaces in names | ✅ verified | integration test |
| Whole-file reads, SHA-256 identical to source | ✅ verified | integration test |
| Random range reads (40 random offsets/sizes) | ✅ verified | integration test |
| Offsets beyond 2 GiB / 3 GiB file | ✅ verified | integration test (sparse 3 GiB file) |
| Multiple concurrent handles | ✅ verified | integration test |
| Memory-mapped reads | ✅ verified | integration test |
| File timestamps and sizes | ✅ verified | integration test |
| Executable launch from the drive | ✅ verified (requires `--file-perms 0777`, set by CLOUDPLAY X) | integration test (`where.exe`, `hostname.exe`, demo game) |
| Executable launch through a symbolic link into the drive | ✅ verified | probe + Hybrid launch |
| Writes, rename, mkdir, delete (writable remote) | ✅ verified | integration test (local backend) |
| Pending uploads survive an rclone crash | ✅ verified | integration test |
| Read-only mount rejects creates | ✅ verified (EROFS). Note: `DeleteFile` reports success but the file survives (delete-on-close fails at cleanup) | integration test |
| Share modes / byte-range locks | ⚠ characterized, see `.work/it/locking-result.txt` after a test run; WinFsp/rclone do not provide NTFS locking semantics across processes reliably | integration test (records behaviour) |
| NTFS alternate data streams, hard links, junctions *inside* the drive, ACLs, compression, EFS | ❌ not supported by rclone VFS | rclone documentation |
| Change notifications for remote edits | Depends on the backend (Google Drive polls every minute). `vfs/forget` (Refresh listing) forces re-validation; stale data was **not** served after a remote change | integration test |

## Execution modes

| Mode | Status |
|---|---|
| Cloud Direct | ✅ verified with the CPX demo game and Windows console tools |
| Hybrid Optimized | ✅ verified with the CPX demo game (exe/DLLs/config copied, archives symlinked); needs Developer Mode or admin |
| Full Local Fallback | ✅ implemented (rclone copy, resumable, explicit approval). Copy path exercised in code review; not part of the automated suite |

## Applications and games

| Title | Mode | Result |
|---|---|---|
| `where.exe`, `hostname.exe` (portable Windows tools) | Cloud Direct | ✅ ran, correct output |
| CPX Demo Game (DRM-free test program in `tests/CpxDemoGame`, ~384 MiB of archives) | Cloud Direct, Hybrid | ✅ ran from the drive; profile learned; Prepare verified; load times in BENCHMARKS.md |
| Any commercial game | — | **Not tested.** No licensed game files were available in the development environment. |
| Cyberpunk 2077, GTA V, Black Myth: Wukong | — | **Research targets only, not supported claims.** Expected issues: launcher/ownership checks (Steam/Epic/Rockstar), very large packed archives with scattered access, and (for some editions/online modes) anti-cheat. Treat each as its own compatibility project; Full Local is the safe path. |

## What the compatibility analyzer checks

Executable candidates (PE headers only), architecture, imported DLLs vs. game folder and System32/SysWOW64 with
redistributable suggestions, Steamworks/EOS/Ubisoft markers, kernel anti-cheat markers (EasyAntiCheat, BattlEye,
Vanguard, PunkBuster, XIGNCODE, nProtect), installers vs. installed games, engine hints (Unity, Unreal, RAGE,
REDengine, Source), file counts and long paths. A game is only marked **Verified** after a monitored launch ran for at
least 60 seconds without a failure signature.
