using System.Collections.Concurrent;
using Vydrix.Cache;
using Vydrix.Core;
using Vydrix.Diagnostics;
using Vydrix.Games;
using Vydrix.Mount;
using Vydrix.Streaming;
using Vydrix.Streaming.GameAware;
using Microsoft.Extensions.Logging;

namespace Vydrix.App;

/// <summary>
/// The VYDRIX application service: owns settings, rclone, mounts, the prediction engine, the game library and
/// metrics, and wires real telemetry from rclone into the engine. UI and CLI are thin layers over this class.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, string> _remoteTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (NamedPipeTelemetryServer Server, GameAwareController Controller)> _gameAware = new();
    private readonly ConcurrentDictionary<string, LaunchSession> _activeLaunches = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _diskPressure;
    private Timer? _diskTimer;
    private FileStream? _instanceLock;

    private AppHost(AppPaths paths)
    {
        Paths = paths;
        SettingsStore = new JsonSettingsStore(paths.SettingsFile);
        Settings = SettingsStore.Load();
        LogSink = new MemoryLogSink();
        LogProvider = new AppLoggerProvider(paths.LogDirectory, LogSink);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(LogProvider));
        _log = LoggerFactory.CreateLogger<AppHost>();
        RcloneConfig = new RcloneConfigService(paths, LoggerFactory.CreateLogger<RcloneConfigService>());
        Drive = new GoogleDriveConnector(RcloneConfig, LoggerFactory.CreateLogger<GoogleDriveConnector>());
        Mounts = new RcloneMountService(paths, RcloneConfig, LoggerFactory.CreateLogger<RcloneMountService>());
        Profiles = new ProfileStore(paths.DatabaseFile);
        Library = new GameLibrary(paths.DatabaseFile);
        CacheIndex = new CacheIndex();
        Engine = new StreamingEngine(Settings.Streaming, Settings.Network, Profiles,
            new MountReadExecutor(id => Mounts.Get(id) is { State: MountState.Mounted } s ? s.Root : null, id => Mounts.Get(id)?.Attribution),
            FileSize, _ => !_diskPressure, CacheIndex.IsCached, LoggerFactory.CreateLogger<StreamingEngine>());
        Launcher = new GameLauncher(paths.DiagnosticsDirectory);
        Preparer = new GamePreparer(Engine, Profiles, RcloneConfig, CacheIndex);
        Installer = new FullLocalInstaller(RcloneConfig);
        Metrics = new MetricsCollector(this);
        Pins = new PinKeeper(PinnedTargets);
        MigrationService = new CacheMigrationService(LoggerFactory.CreateLogger<CacheMigrationService>());
        StorageAdvisor = new StorageReclamationAdvisor(LoggerFactory.CreateLogger<StorageReclamationAdvisor>());

        CacheManager = new SmartCacheManager(
            paths.CacheRootFor(Settings),
            Settings.Cache,
            () => Settings.Mounts,
            id => Mounts.Get(id)?.State == MountState.Mounted,
            path => _activeLaunches.Values.Any(l => l.State == LaunchState.Running && (string.IsNullOrEmpty(l.Game.RootPath) || path.Contains(l.Game.RootPath, StringComparison.OrdinalIgnoreCase))),
            () => Settings.Cache.PinnedPaths,
            async (mountId, targetBudget) =>
            {
                if (Mounts.Get(mountId)?.Rc is { } rc)
                {
                    await rc.SetVfsOptionsAsync(cacheMaxSize: targetBudget);
                    return true;
                }
                return false;
            },
            async (mountId, remotePath) =>
            {
                if (Mounts.Get(mountId)?.Rc is { } rc)
                {
                    await rc.VfsForgetAsync(file: remotePath);
                    return true;
                }
                return false;
            },
            async mountId =>
            {
                if (Mounts.Get(mountId)?.Rc is { } rc)
                {
                    return await rc.GetPendingUploadsCountAsync();
                }
                return 0;
            },
            LoggerFactory.CreateLogger<SmartCacheManager>());

        Mounts.ReadObserved += Engine.OnAccess;
        Mounts.LookupObserved += (mount, path, off, present, pre) => Engine.OnLookup(mount, path, off, present, pre);
        Mounts.StatusChanged += s =>
        {
            _log.LogInformation("Mount {Id}: {State} {Message}", s.MountId, s.State, s.Message);
            if (s.State is MountState.Stopped or MountState.Failed)
            {
                CacheIndex.Unregister(s.MountId);
                // rclone may have evicted or lost data while the mount was down; forget what we believed was cached.
                Engine.Queue.ForgetCached(k => k.Mount == s.MountId);
            }
            StateChanged?.Invoke();
        };
        Engine.ApplicationBlockRead += (session, block) =>
        {
            if (_gameAware.TryGetValue(session.GameId, out var ga)) ga.Controller.OnApplicationRead(block);
        };
    }

    public AppPaths Paths { get; }
    public ISettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public MemoryLogSink LogSink { get; }
    public AppLoggerProvider LogProvider { get; }
    public ILoggerFactory LoggerFactory { get; }
    public RcloneInfo? Rclone { get; private set; }
    public RcloneConfigService RcloneConfig { get; }
    public GoogleDriveConnector Drive { get; }
    public RcloneMountService Mounts { get; }
    public ProfileStore Profiles { get; }
    public GameLibrary Library { get; }
    public CacheIndex CacheIndex { get; }
    public StreamingEngine Engine { get; }
    public GameLauncher Launcher { get; }
    public GamePreparer Preparer { get; }
    public FullLocalInstaller Installer { get; }
    public MetricsCollector Metrics { get; }
    public PinKeeper Pins { get; }
    public SmartCacheManager CacheManager { get; }
    public CacheMigrationService MigrationService { get; }
    public StorageReclamationAdvisor StorageAdvisor { get; }
    public IReadOnlyList<DependencyStatus> Dependencies { get; private set; } = [];
    public IReadOnlyList<RemoteEntry> Remotes { get; private set; } = [];
    public LaunchSession? CurrentLaunch { get; private set; }
    public IReadOnlyDictionary<string, LaunchSession> ActiveLaunches => _activeLaunches;
    public bool DiskPressure => _diskPressure;
    public string CliPath =>
        File.Exists(Path.Combine(AppPaths.AppDirectory, "vydrix-cli.exe")) ? Path.Combine(AppPaths.AppDirectory, "vydrix-cli.exe") :
        File.Exists(Path.Combine(AppPaths.AppDirectory, "vydrix.exe")) ? Path.Combine(AppPaths.AppDirectory, "vydrix.exe") :
        File.Exists(Path.Combine(AppPaths.AppDirectory, "Vydrix.exe")) ? Path.Combine(AppPaths.AppDirectory, "Vydrix.exe") :
        Path.Combine(AppPaths.AppDirectory, "cpx.exe");
    public string CpxPath => CliPath;

    /// <summary>
    /// Path to the VYDRIX GUI executable (Vydrix.exe) for use in startup registration.
    /// Falls back to the CLI path if the desktop binary cannot be located (e.g. unit-test host).
    /// </summary>
    public string DesktopExePath =>
        File.Exists(Path.Combine(AppPaths.AppDirectory, "Vydrix.exe")) ? Path.Combine(AppPaths.AppDirectory, "Vydrix.exe") :
        CliPath;

    /// <summary>Cumulative prefetch accounting over finished sessions (plus live sessions in <see cref="StreamingEngine.PrefetchTotals"/>).</summary>
    public long FinishedUsefulBytes, FinishedWastedBytes;
    public SessionSummary? LastSession { get; private set; }

    public event Action? StateChanged;

    public static AppHost Create(AppPaths? paths = null) => new(paths ?? new AppPaths());

    /// <summary>
    /// Prevents two VYDRIX instances from owning the same data/cache directories (concurrent cache ownership
    /// would corrupt rclone's cache). Returns false if another instance holds the lock.
    /// </summary>
    public bool TryAcquireInstanceLock()
    {
        try
        {
            _instanceLock = new FileStream(Path.Combine(Paths.DataRoot, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
    }

    public async Task InitializeAsync(bool restoreMounts = true, CancellationToken ct = default)
    {
        _log.LogInformation("VYDRIX starting (data root {Root})", Paths.DataRoot);
        if (Settings.Cache.ZeroFootprintMode)
        {
            _log.LogInformation("Zero Footprint Mode active: setting demand-only reads (speculative prefetch disabled)");
            Engine.SetDemandOnly(true);
        }
        Rclone = await RcloneLocator.LocateAsync(Settings.RclonePathOverride, Paths.DataRoot, ct);
        if (Rclone != null)
        {
            Mounts.SetRclone(Rclone);
            _log.LogInformation("Using {Version} at {Path}", Rclone.Version, Rclone.Path);
        }
        else _log.LogError("rclone not found");
        RefreshDependencies();
        await RefreshRemotesAsync(ct);
        _diskTimer = new Timer(_ => CheckDisk(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        if (Rclone != null)
        {
            foreach (var s in await Mounts.AdoptExistingAsync(ct))
                CacheIndex.Register(s.Id, s.CacheDir, s.Definition.RemotePath);
            if (restoreMounts && Settings.RestoreMountsOnStartup)
            {
                foreach (var def in Settings.Mounts.Where(m => m.AutoMount))
                {
                    if (Mounts.Get(def.Id) != null) continue;
                    try { await MountAsync(def, ct); }
                    catch (Exception e) { _log.LogError("Could not restore mount {Name}: {Error}", def.DisplayName, e.Message); }
                }
            }
        }
        Metrics.Start();
        StateChanged?.Invoke();
    }

    public void RefreshDependencies()
    {
        Dependencies = DependencyChecker.CheckAll(Rclone?.Path, Rclone?.Version, Paths.CacheRootFor(Settings), Settings.Cache.MinFreeSpaceBytes);
    }

    public async Task RefreshRemotesAsync(CancellationToken ct = default)
    {
        if (Rclone is null) return;
        try
        {
            Remotes = await RcloneConfig.ListRemotesAsync(Rclone, ct);
            foreach (var r in Remotes) _remoteTypes[r.Name] = r.Type;
        }
        catch (Exception e) { _log.LogWarning("Listing rclone remotes failed: {Error}", e.Message); }
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    public string? RemoteTypeOf(string remoteName) =>
        remoteName.StartsWith(':') ? remoteName.TrimStart(':') : _remoteTypes.GetValueOrDefault(remoteName);

    /// <summary>Budget rclone may use for one mount, after protecting the free-space reserve.</summary>
    public CacheBudgetDecision BudgetFor(string mountId)
    {
        var root = Paths.CacheRootFor(Settings);
        Directory.CreateDirectory(root);
        var current = CacheInspector.Measure(Paths.MountCacheDir(Settings, mountId));
        var mounts = Math.Max(1, Settings.Mounts.Count(m => m.AutoMount || Mounts.Get(m.Id) != null));
        return DiskSafetyGuard.Decide(Settings.Cache.BudgetBytes, Settings.Cache.MinFreeSpaceBytes, current.VolumeFreeBytes, current.PhysicalBytes, mounts);
    }

    public async Task<MountSession> MountAsync(MountDefinition def, CancellationToken ct = default)
    {
        if (Rclone is null) throw new InvalidOperationException("rclone was not found. See Diagnostics.");
        var budget = BudgetFor(def.Id);
        foreach (var w in budget.Warnings) _log.LogWarning("{Warning}", w);
        var cacheDir = Paths.MountCacheDir(Settings, def.Id);
        var req = new MountRequest(def, Settings.Cache, Settings.Network, cacheDir, Math.Max(16 * ByteSize.MiB, budget.EffectiveMaxBytes),
            Settings.Streaming.ReadProfilingEnabled, RemoteTypeOf(def.RemoteName));
        var s = await Mounts.MountAsync(req, ct);
        CacheIndex.Register(def.Id, cacheDir, def.RemotePath);
        if (s.DriveLetter.ToString() != def.DriveLetter)
        {
            def.DriveLetter ??= s.DriveLetter.ToString();
        }
        StateChanged?.Invoke();
        return s;
    }

    public async Task UnmountAsync(string mountId)
    {
        await Mounts.UnmountAsync(mountId, TimeSpan.FromSeconds(60));
        CacheIndex.Unregister(mountId);
        StateChanged?.Invoke();
    }

    private long? FileSize(string mountId, string mountPath)
    {
        var s = Mounts.Get(mountId);
        if (s is null) return null;
        try
        {
            var fi = new FileInfo(Path.Combine(s.Root, mountPath.Replace('/', '\\')));
            return fi.Exists ? fi.Length : null;
        }
        catch (IOException) { return null; }
    }

    private IReadOnlyList<(MountSession, string)> PinnedTargets()
    {
        var list = new List<(MountSession, string)>();
        foreach (var p in Settings.Cache.PinnedPaths)
        {
            // Stored as "<mountId>:<path in mount>".
            var i = p.IndexOf(':');
            if (i <= 0) continue;
            if (Mounts.Get(p[..i]) is { } m) list.Add((m, p[(i + 1)..]));
        }
        return list;
    }

    private void CheckDisk()
    {
        try
        {
            var root = Paths.CacheRootFor(Settings);
            Directory.CreateDirectory(root);
            var (free, _) = DiskInfo.FreeSpace(root);
            var pressure = DiskSafetyGuard.Pressure(free, Settings.Cache.MinFreeSpaceBytes);
            var was = _diskPressure;
            _diskPressure = pressure != Cache.DiskPressure.Normal;
            if (_diskPressure && !was) _log.LogWarning("Disk space low ({Free} free, reserve {Reserve}); prefetching paused", ByteSize.Format(free), ByteSize.Format(Settings.Cache.MinFreeSpaceBytes));
            if (!_diskPressure && was) _log.LogInformation("Disk space recovered; prefetching resumed");
        }
        catch (Exception) { }
    }

    // ---------------- games ----------------

    public async Task<GameEntry> AddGameAsync(string mountId, string rootInMount, string? name = null, CancellationToken ct = default)
    {
        var mount = Mounts.Get(mountId) ?? throw new InvalidOperationException("The drive is not mounted.");
        var root = Path.Combine(mount.Root, rootInMount.Replace('/', '\\'));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var scan = await Task.Run(() => GameScanner.Scan(root, ct: ct), ct);
        var g = new GameEntry
        {
            Name = name ?? Path.GetFileName(root.TrimEnd('\\')),
            MountId = mountId,
            RootPath = rootInMount.Replace('\\', '/').Trim('/'),
            Executable = scan.Executables.FirstOrDefault()?.RelativePath,
        };
        var summary = Profiles.Summary(g.Id, Settings.Streaming.StartupWindow);
        g.Compatibility = CompatibilityAnalyzer.Analyze(scan, g.Executable, Settings.Network.EffectiveMbps, null, summary.Sessions);
        if (g.Executable != null && PeInspector.TryRead(Path.Combine(root, g.Executable)) is { } pe)
        {
            g.Compatibility.Architecture = pe.Machine.ToString();
            CompatibilityAnalyzer.AddDependencyIssues(g.Compatibility, DependencyResolver.Check(pe, Path.GetDirectoryName(Path.Combine(root, g.Executable))!, root));
        }
        g.Mode = g.Compatibility.RecommendedMode == ExecutionMode.FullLocal ? ExecutionMode.CloudDirect : g.Compatibility.RecommendedMode;
        if (g.Mode == ExecutionMode.HybridOptimized && !DependencyChecker.CanCreateSymlinks()) g.Mode = ExecutionMode.CloudDirect;
        Library.Save(g);
        _log.LogInformation("Added game {Name} ({Files} files, {Size}); compatibility: {Level}", g.Name, scan.FileCount, ByteSize.Format(scan.TotalBytes), g.Compatibility.Level);
        return g;
    }

    public async Task<PrepareReport> PrepareAsync(GameEntry g, IProgress<IReadOnlyList<PrepareStep>>? steps, IProgress<PrepareProgress>? progress, CancellationToken ct)
    {
        if (Rclone is null) throw new InvalidOperationException("rclone was not found.");
        var mount = Mounts.Get(g.MountId);
        if (mount is null && Settings.Mounts.FirstOrDefault(m => m.Id == g.MountId) is { } def && g.Mode != ExecutionMode.FullLocal)
            mount = await MountAsync(def, ct);
        var report = await Preparer.PrepareAsync(g, mount, Settings, Rclone, steps, progress, ct);
        Library.Save(g);
        SaveSettings();
        _log.LogInformation("Prepare {Game}: {Status} - {Headline}", g.Name, report.Status, report.Headline);
        return report;
    }

    public async Task<LaunchSession> PlayAsync(GameEntry g, CancellationToken ct = default)
    {
        if (CurrentLaunch is { State: LaunchState.Running }) throw new InvalidOperationException($"{CurrentLaunch.Game.Name} is already running.");
        MountSession? mount = null;
        if (g.Mode != ExecutionMode.FullLocal)
        {
            mount = Mounts.Get(g.MountId);
            if (mount is not { State: MountState.Mounted })
            {
                var def = Settings.Mounts.FirstOrDefault(m => m.Id == g.MountId) ?? throw new InvalidOperationException("The game's cloud drive is not configured.");
                mount = await MountAsync(def, ct);
            }
        }
        GameSession? session = null;
        if (mount != null && Settings.Streaming.ReadProfilingEnabled)
        {
            session = Engine.BeginSession(g.Id, mount.Id, g.RootPath, g.Mode.ToString());
            if (g.GameAwareEnabled) StartGameAware(g, session);
        }
        LaunchSession launch;
        try
        {
            launch = Launcher.Launch(g, mount?.Root);
        }
        catch
        {
            if (session != null) Engine.EndSession(g.Id, save: false);
            StopGameAware(g.Id);
            throw;
        }
        CurrentLaunch = launch;
        _activeLaunches[g.Id] = launch;
        g.LastPlayed = DateTime.Now;
        Library.Save(g);
        _log.LogInformation("Launched {Game} in {Mode} mode: {Exe}", g.Name, g.Mode, launch.Executable);
        StateChanged?.Invoke();
        _ = launch.Completion.ContinueWith(async t => await OnLaunchEndedAsync(g, t.Result), TaskScheduler.Default);
        return launch;
    }

    public void OnLaunchEnded(GameEntry g, LaunchDiagnostics d) => _ = OnLaunchEndedAsync(g, d);

    public async Task OnLaunchEndedAsync(GameEntry g, LaunchDiagnostics d)
    {
        StopGameAware(g.Id);
        var summary = Engine.EndSession(g.Id);
        if (summary != null)
        {
            LastSession = summary;
            Interlocked.Add(ref FinishedUsefulBytes, summary.UsefulPrefetchBytes);
            Interlocked.Add(ref FinishedWastedBytes, summary.WastedPrefetchBytes);
        }
        var fresh = Library.Get(g.Id) ?? g;
        var verified = !d.LikelyFailure && d.Runtime >= TimeSpan.FromSeconds(60);
        if (verified && !fresh.VerifiedModes.Contains(d.Mode)) fresh.VerifiedModes.Add(d.Mode);
        if (verified && fresh.Compatibility != null && fresh.Compatibility.Level < CompatibilityLevel.NeedsLocalInstall)
            fresh.Compatibility.Level = CompatibilityLevel.Verified;
        fresh.LastLaunchResult = d.LikelyFailure
            ? $"Failed {d.Started:g}: {string.Join(" ", d.Notes)}"
            : $"Ran {Bandwidth.FormatDuration(d.Runtime)} on {d.Started:g} ({d.Mode})" + (summary != null
                ? $"; reads hit {summary.Hits}/{summary.Hits + summary.Misses}, stalls {summary.StallMs / 1000:0.0} s"
                : "");
        Library.Save(fresh);
        _log.Log(d.LikelyFailure ? LogLevel.Warning : LogLevel.Information, "{Game} exited: {Result}", g.Name, fresh.LastLaunchResult);

        // 1. Draining/settling delay: allow Windows file handles, memory mapped buffers, and WinFsp I/O to flush
        await Task.Delay(1500);

        // 2. Wait for any pending uploads if rclone RC is available
        if (Mounts.Get(g.MountId)?.Rc is { } rc)
        {
            try
            {
                var pending = await rc.GetPendingUploadsCountAsync();
                if (pending > 0)
                {
                    _log.LogInformation("Game {Name} has {Count} pending uploads; waiting up to 10s for upload drain", g.Name, pending);
                    for (int i = 0; i < 10 && pending > 0; i++)
                    {
                        await Task.Delay(1000);
                        pending = await rc.GetPendingUploadsCountAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to query pending uploads for {Game}", g.Name);
            }
        }

        // 3. Mark launch state as exited and release active session protection
        if (_activeLaunches.TryRemove(g.Id, out var activeLaunch))
        {
            activeLaunch.State = LaunchState.Exited;
        }

        if (CurrentLaunch?.Game.Id == g.Id)
        {
            CurrentLaunch.State = LaunchState.Exited;
        }

        StateChanged?.Invoke();

        // 4. Safe post-game cache reclamation
        if (Settings.Cache.AutoReclaimOnGameExit && !string.IsNullOrEmpty(g.RootPath))
        {
            _log.LogInformation("Game {Name} exited; triggering safe transient cache reclamation for {Root}", g.Name, g.RootPath);
            await CacheManager.ReclaimGameCacheAsync(g.RootPath);
        }

        _activeLaunches.TryRemove(g.Id, out _);
        StateChanged?.Invoke();
    }

    private void StartGameAware(GameEntry g, GameSession session)
    {
        var server = new NamedPipeTelemetryServer(g.Id);
        var controller = new GameAwareController(new RegionAssetMap(), server,
            (p, tag) => Engine.ScheduleExternal(session, p, tag), prefix => Engine.CancelTagged(session, prefix));
        _gameAware[g.Id] = (server, controller);
        _log.LogInformation("Game-Aware telemetry listening on \\\\.\\pipe\\{Pipe}", server.PipeName);
    }

    private void StopGameAware(string gameId)
    {
        if (!_gameAware.TryRemove(gameId, out var ga)) return;
        ga.Controller.Dispose();
        _ = ga.Server.DisposeAsync();
    }

    public async Task ShutdownAsync(bool keepMounts)
    {
        Metrics.Dispose();
        if (keepMounts)
        {
            Mounts.DetachAll();
            _log.LogInformation("Exiting; drives stay mounted and will be re-attached on next start");
        }
        else
        {
            await Mounts.UnmountAllAsync(TimeSpan.FromSeconds(60));
            _log.LogInformation("Exiting; all drives unmounted");

            if (Settings.Cache.PurgeCacheOnExit)
            {
                _log.LogInformation("PurgeCacheOnExit enabled; purging local VFS cache data");
                try { await CacheManager.PurgeAllCachesAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Failed to purge cache on exit"); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _diskTimer?.Dispose();
        Pins.Dispose();
        Metrics.Dispose();
        CacheManager.Dispose();
        foreach (var id in _gameAware.Keys.ToList()) StopGameAware(id);
        await Engine.DisposeAsync();
        await Mounts.DisposeAsync();
        CacheIndex.Dispose();
        LoggerFactory.Dispose();
        _instanceLock?.Dispose();
    }
}




