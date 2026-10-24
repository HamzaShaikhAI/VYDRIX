using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vydrix.App;
using Vydrix.Core;
using Vydrix.Diagnostics;
using Vydrix.Games;
using Vydrix.Mount;
using Vydrix.SimCloud;

namespace Vydrix.Cli;

public static class Program
{
    private const string Usage = """
        vydrix - VYDRIX command line

          vydrix deps                                  Check WinFsp, rclone, cache volume and other prerequisites
          vydrix remotes                               List configured cloud accounts (rclone remotes)
          vydrix mounts                                List configured drives
          vydrix mount <mount-id> [--letter X]         Mount a configured drive in the foreground (Ctrl+C to unmount)
          vydrix scan <folder> [--mbps 70]             Scan a game folder and print the compatibility report
          vydrix pe <file.exe>                         Show PE architecture and imported DLLs (+ missing dependencies)
          vydrix profile <game-id>                     Show the learned access profile of a game
          vydrix guardian [--dry-run]                  Inspect C: capacity, rate, VYDRIX footprint, and relocation
          vydrix workspace list                        List cloud project workspaces
          vydrix workspace create <name> [--template T] Create cloud workspace (python, node, dotnet, flutter, git, ai)
          vydrix workspace check <id>                  Test locking, latency, and health of a workspace
          vydrix ollama status                         Inspect Ollama configuration and models storage location
          vydrix ollama config [--path <dir>]          Configure OLLAMA_MODELS to cloud or secondary storage
          vydrix preflight <type> <path> [--profile P] Run preflight check before launching (game, ai, workspace)
          vydrix simcloud --root <dir> [--port 8080] [--mbps 70] [--latency-ms 150]
                                                    Serve a folder as a throttled local test "cloud" (127.0.0.1 only)
          vydrix secret emit-config-pass [--home dir]  (used by rclone --password-command; prints the DPAPI-protected key)

        Options: --home <dir> overrides the data folder (default %LOCALAPPDATA%\Vydrix).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var home = Opt(args, "--home");
        var paths = new AppPaths(home);
        try
        {
            switch (args[0])
            {
                case "secret" when args.Length > 1 && args[1] == "emit-config-pass":
                {
                    var pass = SecretStore.Load(paths.ConfigPasswordFile);
                    if (pass is null)
                    {
                        Console.Error.WriteLine("No configuration key found for this Windows user.");
                        return 1;
                    }
                    Console.Out.Write(pass);
                    return 0;
                }
                case "deps":
                {
                    var rclone = await RcloneLocator.LocateAsync(null, paths.DataRoot);
                    var settings = new JsonSettingsStore(paths.SettingsFile).Load();
                    foreach (var d in DependencyChecker.CheckAll(rclone?.Path, rclone?.Version, paths.CacheRootFor(settings), settings.Cache.MinFreeSpaceBytes))
                        Console.WriteLine($"[{d.State,-7}] {d.Name}: {d.Detail}{(d.Remedy != null ? $"\n           → {d.Remedy}" : "")}");
                    return 0;
                }
                case "remotes":
                {
                    await using var host = AppHost.Create(paths);
                    await host.InitializeAsync(restoreMounts: false);
                    foreach (var r in host.Remotes) Console.WriteLine($"{r.Name,-20} {r.Type}");
                    if (host.Remotes.Count == 0) Console.WriteLine("No cloud accounts configured. Connect Google Drive in the VYDRIX app.");
                    return 0;
                }
                case "mounts":
                {
                    var s = new JsonSettingsStore(paths.SettingsFile).Load();
                    foreach (var m in s.Mounts) Console.WriteLine($"{m.Id}  {m.DisplayName,-20} {m.RemoteSpec,-30} letter={m.DriveLetter ?? "auto"} auto={m.AutoMount}");
                    return 0;
                }
                case "mount" when args.Length > 1:
                {
                    await using var host = AppHost.Create(paths);
                    if (!host.TryAcquireInstanceLock())
                    {
                        Console.Error.WriteLine("Another VYDRIX instance is running with this data folder.");
                        return 2;
                    }
                    await host.InitializeAsync(restoreMounts: false);
                    var def = host.Settings.Mounts.FirstOrDefault(m => m.Id == args[1] || m.DisplayName == args[1])
                              ?? throw new ArgumentException($"No configured drive '{args[1]}'. See 'vydrix mounts'.");
                    if (Opt(args, "--letter") is { } l) def.DriveLetter = l;
                    var s = await host.MountAsync(def);
                    Console.WriteLine($"Mounted {def.RemoteSpec} at {s.DriveLetter}:. Press Ctrl+C to unmount.");
                    var done = new TaskCompletionSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                    await done.Task;
                    await host.ShutdownAsync(keepMounts: false);
                    return 0;
                }
                case "scan" when args.Length > 1:
                {
                    var scan = GameScanner.Scan(args[1]);
                    var mbps = double.TryParse(Opt(args, "--mbps"), out var v) ? v : 70;
                    var r = CompatibilityAnalyzer.Analyze(scan, null, mbps, null, 0);
                    if (r.Executable != null && PeInspector.TryRead(Path.Combine(args[1], r.Executable)) is { } pe)
                        CompatibilityAnalyzer.AddDependencyIssues(r, DependencyResolver.Check(pe, Path.GetDirectoryName(Path.Combine(args[1], r.Executable))!, args[1]));
                    Console.WriteLine($"{scan.FileCount:N0} files, {ByteSize.Format(scan.TotalBytes)}, engine: {scan.Engine ?? "unknown"}, scanned in {scan.Elapsed.TotalSeconds:0.00} s");
                    Console.WriteLine("Executables:");
                    foreach (var e in scan.Executables) Console.WriteLine($"  [{e.Score,3}] {e.RelativePath} ({e.Reason})");
                    Console.WriteLine($"Markers: {string.Join(", ", scan.Markers)}");
                    Console.WriteLine($"Compatibility: {r.Summary}; recommended mode: {r.RecommendedMode}");
                    foreach (var i in r.Issues) Console.WriteLine($"  {i.Severity,-7} {i.Message}{(i.Suggestion != null ? $"\n          → {i.Suggestion}" : "")}");
                    return 0;
                }
                case "pe" when args.Length > 1:
                {
                    var pe = PeInspector.TryRead(args[1]);
                    if (pe is null) { Console.Error.WriteLine("Not a readable PE file."); return 1; }
                    Console.WriteLine($"{pe.Machine} {pe.Subsystem} .NET={pe.IsDotNet}");
                    foreach (var i in pe.AllImports) Console.WriteLine("  " + i);
                    foreach (var d in DependencyResolver.Check(pe, Path.GetDirectoryName(Path.GetFullPath(args[1]))!, Path.GetDirectoryName(Path.GetFullPath(args[1]))!))
                        Console.WriteLine($"  MISSING {d.Dll}: {d.Problem}{(d.Remedy != null ? " → " + d.Remedy : "")}");
                    return 0;
                }
                case "profile" when args.Length > 1:
                {
                    var store = new Vydrix.Streaming.ProfileStore(paths.DatabaseFile);
                    var s = store.Summary(args[1], TimeSpan.FromMinutes(2));
                    Console.WriteLine($"Sessions: {s.Sessions}, distinct blocks: {s.DistinctBlocks}, startup estimate: {ByteSize.Format(s.StartupBytesEstimate)}, last: {s.LastSession}");
                    foreach (var p in store.LoadPredictorStats(args[1]))
                        Console.WriteLine($"  {p.Predictor,-12} issued {p.Issued} useful {p.Useful} wasted {p.Wasted} accuracy {(p.Accuracy is { } a ? a.ToString("P0") : "n/a")}{(p.Disabled ? " DISABLED" : "")}");
                    return 0;
                }
                case "guardian":
                {
                    var guardian = new CGuardianService(dataRoot: paths.DataRoot);
                    if (args.Contains("--dry-run"))
                    {
                        var dry = guardian.GenerateDryRunCleanupReport();
                        Console.WriteLine("=== VYDRIX C: GUARDIAN DRY-RUN REPORT ===");
                        Console.WriteLine($"Current VYDRIX Usage: {ByteSize.Format(dry.CurrentUsageBytes)}");
                        Console.WriteLine($"Projected Reclaimable: {ByteSize.Format(dry.ProjectedReclaimableBytes)}");
                        Console.WriteLine("Recommended Actions:");
                        foreach (var a in dry.RecommendedActions) Console.WriteLine($"  • {a}");
                        return 0;
                    }

                    var status = guardian.CheckStatus("C");
                    Console.WriteLine("=== C: GUARDIAN STORAGE HEALTH ===");
                    Console.WriteLine($"Total C: Volume:   {ByteSize.Format(status.TotalSizeBytes)}");
                    Console.WriteLine($"Free Space:        {ByteSize.Format(status.FreeSpaceBytes)} ({status.FreePercent:0.0}% free)");
                    Console.WriteLine($"Consumption Rate:  {status.ConsumptionRateMBPerMin:0.0} MB/min");
                    Console.WriteLine($"Status:            {(status.IsCritical ? "CRITICAL ALERT" : status.IsWarning ? "WARNING" : "HEALTHY")}");
                    if (!string.IsNullOrEmpty(status.ActiveAttributedWorkload))
                        Console.WriteLine($"Active Workload:   {status.ActiveAttributedWorkload}");

                    Console.WriteLine("\n=== VYDRIX Storage Consumers on C: ===");
                    foreach (var c in status.TopConsumers)
                    {
                        Console.WriteLine($"  {c.Name,-20} {ByteSize.Format(c.SizeBytes),10}  ({c.Category})");
                    }

                    if (status.RelocationOpportunities.Count > 0)
                    {
                        Console.WriteLine("\n=== Secondary Volume Relocation Opportunities ===");
                        foreach (var o in status.RelocationOpportunities)
                            Console.WriteLine($"  • {o}");
                    }
                    return 0;
                }
                case "workspace" when args.Length > 1 && args[1] == "list":
                {
                    var mgr = new CloudWorkspaceManager(Path.Combine(paths.DataRoot, "workspaces.json"));
                    var list = mgr.GetWorkspaces();
                    Console.WriteLine($"=== CLOUD PROJECT WORKSPACES ({list.Count}) ===");
                    foreach (var w in list)
                    {
                        Console.WriteLine($"[{w.WorkspaceId}] {w.Name} ({w.Template})");
                        Console.WriteLine($"  Cloud:   {w.CloudPath}");
                        Console.WriteLine($"  Scratch: {w.LocalScratchPath}");
                    }
                    if (list.Count == 0) Console.WriteLine("No workspaces created yet. Use 'vydrix workspace create <name>'.");
                    return 0;
                }
                case "workspace" when args.Length > 2 && args[1] == "create":
                {
                    var mgr = new CloudWorkspaceManager(Path.Combine(paths.DataRoot, "workspaces.json"));
                    var templateStr = Opt(args, "--template") ?? "DotNet";
                    var template = Enum.TryParse<CloudWorkspaceTemplate>(templateStr, true, out var t) ? t : CloudWorkspaceTemplate.DotNet;
                    var w = mgr.CreateWorkspace(args[2], template);
                    Console.WriteLine($"Created workspace '{w.Name}' [{w.WorkspaceId}] ({w.Template})");
                    Console.WriteLine($"Cloud Source:   {w.CloudPath}");
                    Console.WriteLine($"Local Scratch:  {w.LocalScratchPath}");
                    return 0;
                }
                case "workspace" when args.Length > 2 && args[1] == "check":
                {
                    var mgr = new CloudWorkspaceManager(Path.Combine(paths.DataRoot, "workspaces.json"));
                    var rep = await mgr.VerifyWorkspaceHealthAsync(args[2]);
                    Console.WriteLine($"=== WORKSPACE HEALTH: {rep.WorkspaceId} ===");
                    Console.WriteLine($"Accessible:      {rep.IsAccessible}");
                    Console.WriteLine($"Lock Test:       {(rep.LockTestPassed ? "PASSED" : "FAILED")}");
                    Console.WriteLine($"Watcher Support: {(rep.WatcherSupported ? "PASSED" : "WARNING")}");
                    Console.WriteLine($"Latency:         {rep.LatencyMs:0.0} ms");
                    foreach (var warn in rep.Warnings) Console.WriteLine($"  ⚠ {warn}");
                    return rep.IsAccessible && rep.LockTestPassed ? 0 : 1;
                }
                case "ollama" when args.Length > 1 && args[1] == "status":
                {
                    var vault = new AiModelVault();
                    var st = vault.InspectOllamaEnvironment();
                    Console.WriteLine("=== OLLAMA AI MODEL VAULT STATUS ===");
                    Console.WriteLine($"Installed:        {st.IsInstalled} {(st.InstalledPath != null ? $"({st.InstalledPath})" : "")}");
                    Console.WriteLine($"Models Path:      {st.CurrentModelsPath}");
                    Console.WriteLine($"Target Cloud:     {st.TargetCloudPath}");
                    Console.WriteLine($"Configured:       {st.EnvironmentConfigured}");
                    Console.WriteLine($"Recommendation:   {st.RecommendedAction}");
                    return 0;
                }
                case "ollama" when args.Length > 1 && args[1] == "config":
                {
                    var target = Opt(args, "--path") ?? @"G:\ai\models";
                    var vault = new AiModelVault(target);
                    bool ok = vault.ConfigureOllamaModelsPath(target);
                    Console.WriteLine(ok
                        ? $"Successfully configured OLLAMA_MODELS to '{target}'."
                        : $"Failed to configure OLLAMA_MODELS to '{target}'.");
                    return ok ? 0 : 1;
                }
                case "preflight" when args.Length > 2:
                {
                    await using var host = AppHost.Create(paths);
                    var typeStr = args[1];
                    var type = typeStr.ToLowerInvariant() switch
                    {
                        "game" => WorkloadType.Game,
                        "ai" or "model" => WorkloadType.AiModel,
                        "workspace" or "ws" => WorkloadType.DevelopmentWorkspace,
                        _ => WorkloadType.Game
                    };

                    var profStr = Opt(args, "--profile") ?? "StrictCloud";
                    var profile = Enum.TryParse<WorkloadProfile>(profStr, true, out var p) ? p : WorkloadProfile.StrictCloud;

                    var rep = host.Orchestrator.RunPreflightCheck($"cli_{Path.GetFileName(args[2])}", type, args[2], profile);
                    Console.WriteLine("=== WORKLOAD PREFLIGHT CHECK ===");
                    Console.WriteLine($"Workload:       {rep.WorkloadId} ({rep.Type}, Profile: {rep.Profile})");
                    Console.WriteLine($"Launch Verdict: {(rep.CanSafelyLaunch ? "SAFE TO LAUNCH" : "BLOCKED / WARNING")}");
                    Console.WriteLine($"Free RAM:       {ByteSize.Format(rep.AvailableRamBytes)} (Required: ~{ByteSize.Format(rep.RequiredRamBytes)})");
                    Console.WriteLine($"C: Drive Free:  {ByteSize.Format(rep.CDiskFreeBytes)}");
                    Console.WriteLine($"Projected C:    {ByteSize.Format(rep.ProjectedCDiskImpactBytes)}");
                    if (rep.Warnings.Count > 0)
                    {
                        Console.WriteLine("\nWarnings:");
                        foreach (var w in rep.Warnings) Console.WriteLine($"  ⚠ {w}");
                    }
                    if (rep.Recommendations.Count > 0)
                    {
                        Console.WriteLine("\nRecommendations:");
                        foreach (var r in rep.Recommendations) Console.WriteLine($"  ℹ {r}");
                    }
                    return rep.CanSafelyLaunch ? 0 : 1;
                }
                case "simcloud":
                {
                    var root = Opt(args, "--root") ?? throw new ArgumentException("--root is required");
                    var port = int.TryParse(Opt(args, "--port"), out var p) ? p : 8080;
                    await using var server = new SimCloudServer(root, port)
                    {
                        BandwidthMbps = double.TryParse(Opt(args, "--mbps"), out var mb) ? mb : 0,
                        Latency = TimeSpan.FromMilliseconds(double.TryParse(Opt(args, "--latency-ms"), out var lat) ? lat : 0)
                    };
                    Console.WriteLine($"Serving {server.Root} at {server.Url} ({server.BandwidthMbps:0} Mbps, {server.Latency.TotalMilliseconds:0} ms). Ctrl+C to stop.");
                    var done = new TaskCompletionSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                    await done.Task;
                    Console.WriteLine($"Requests {server.Requests}, bytes sent {ByteSize.Format(server.BytesSent)}");
                    return 0;
                }
            }
            Console.Error.WriteLine("Unknown command.\n\n" + Usage);
            return 64;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Error: " + Redactor.Redact(e.Message));
            return 1;
        }
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
