# Known limitations

These are real, current limitations — measured or confirmed, not hypothetical.

## Physics of the network

- **Uncached data arrives at internet speed.** 70 Mbps is at most 8.75 MB/s (measured 69.8 Mbps through the mount on
  a 70 Mbps simulated link). No caching or prediction makes a cold read as fast as an SSD. CLOUDPLAY X approaches SSD
  behaviour only for data that is already cached.
- **Prediction does not create bandwidth.** In the simulation benchmarks, a learned profile alone barely changed load
  time while the game itself saturated the link; the large gain came from *Optimize & Prepare*, which downloads the
  startup set **before** launch (simulated at 70 Mbps / 150 ms: 184 s of preparation cut a 236 s load to 76 s; measured with the demo game: a 32 s Prepare cut a 20 s cold load to 7.7 s). Total download time is the
  same; it is moved out of the loading screen.
- **Unseen content streams on demand.** A level never loaded before is not in any profile; its data is fetched when
  the game asks for it (demo game: level phase 7.6 s on a cold cache vs ~0.01 s on SSD).
- **A cache smaller than the working set defeats preparation** (simulated: prepared data was evicted before use).

## rclone / WinFsp behaviour (rclone 1.75.2)

- The cache size limit is **not instantaneous**: open files are never evicted and cleanup runs every poll interval
  (CLOUDPLAY X uses 30 s), so usage can briefly exceed the limit. A 10 % margin is reserved and CLOUDPLAY X pauses its
  own prefetching under disk pressure.
- **Range index persistence:** rclone writes a file's downloaded-range index only after the last handle closes plus
  its 5 s handle-caching delay. After a crash or forced kill, data for files open at that moment stays on disk but is
  treated as uncached and downloaded again. Graceful unmount waits for activity to settle before quitting.
- **First read after reconnecting may fail** for ~5 s (errored handle in rclone's handle cache), then recovers with
  correct data. Errors are always surfaced; data is never fabricated.
- `vfs/stats.inUse` stays at 1 on an idle mount, so idleness is judged from telemetry instead.
- rclone's stats do not separate upload and download speed: **upload speed is shown as Unavailable** (pending upload
  count is shown).
- When an rclone on-the-fly remote's connection parameters change (e.g. a different URL/port), rclone uses a
  different cache sub-folder. Named remotes such as `gdrive:` are stable.
- No NTFS alternate data streams, hard links, ACLs, EFS or reliable cross-process byte-range locking on the drive.
- Windows `DeleteFile` on a read-only mount reports success although the file is not deleted.

## Telemetry

- Per-read telemetry comes from rclone's debug log (`-vv`). It is exact (offset, size, rclone's own hit/miss verdict,
  wait time) but costs some CPU and log I/O while profiling is enabled (log rotates at 64 MiB). It can be turned off in
  Settings, which also disables profiles and hit/miss metrics.
- rclone does not record which process issued a read. CLOUDPLAY X separates its own prefetch reads by registering
  each prefetch's byte range and time window; a game read of the same range during that window would be counted as
  prefetch (rare, minor effect on accuracy figures).
- Windows Defender's on-access scan reads the start and end of files when they are opened; on a cold cache these
  reads cause extra downloads. CLOUDPLAY X does not change security settings.
- ETW file I/O tracing was not used: it requires administrator rights, and rclone's log already provides byte-exact
  VFS-level data.

## Games

- **No commercial game has been tested.** Only the DRM-free CPX demo game and Windows console tools were executed.
- Launcher-managed games (Steam, Epic, Ubisoft, Rockstar) generally need their launcher and a local installation;
  CLOUDPLAY X never bypasses ownership checks, DRM or anti-cheat.
- Kernel anti-cheat titles are flagged "Requires local installation".
- "Verified" requires a monitored run of ≥ 60 s; short test programs never become Verified.
- Hybrid mode needs Developer Mode (or admin) for unprivileged symbolic links. Writes into a *symlinked* file go to
  the cloud drive (and fail on read-only mounts); new files and copied files are local.
- Pinning is best effort: rclone evicts by least-recent access, so pinned files are kept "recent" by touching them;
  a budget smaller than the pinned set still evicts.
- **Game-Aware Mode has no real game integration yet.** The pipe protocol, region learning and scheduling are
  implemented and unit-tested with synthetic telemetry only.

## Providers

- **Google Drive end-to-end was not exercised in development** (requires the user's OAuth client and account).
  The OAuth state machine was tested up to the browser step with a dummy client.
- Google OAuth apps left in *Testing* mode expire refresh tokens after 7 days.
- **TeraBox is not supported** (no official API; see docs/TERABOX_RESEARCH.md).
- OneDrive, Dropbox, S3 and NAS work through rclone but are untested by CLOUDPLAY X and must be configured with
  `rclone config` against CLOUDPLAY X's config file.

## Packaging

- The release is a self-contained per-user ZIP with an install script (Start-menu shortcut, optional desktop
  shortcut, uninstall entry). An Inno Setup `.exe` is produced only if Inno Setup is installed; it was not available
  in the build environment.
- The installer was verified on the development machine only, not on a clean Windows installation.
- The binaries are not code-signed; Windows SmartScreen may warn on first start.
