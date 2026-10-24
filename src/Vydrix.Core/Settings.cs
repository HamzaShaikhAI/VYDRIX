using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vydrix.Core;

public enum CachePolicyProfile
{
    /// <summary>Zero Footprint Mode: 512 MiB strictly bounded hard cap on C:, prefetching disabled, game-aware cleanup, periodic maintenance.</summary>
    ZeroFootprint,

    /// <summary>512 MiB conservative hard cap with aggressive eviction and transient caching.</summary>
    Minimal,

    /// <summary>Standard balanced budget (default: 5 GiB) suitable for modern gaming.</summary>
    Balanced,

    /// <summary>User-configured budget, custom cache path, and free-space reserve.</summary>
    Custom,

    /// <summary>Disk cache disabled entirely (--vfs-cache-mode off); stream directly into RAM.</summary>
    Disabled
}

public enum VfsCacheMode
{
    /// <summary>Full sparse caching on disk. Required for memory-mapped binaries and random seeks.</summary>
    Full,

    /// <summary>Only files opened for write are cached to disk; reads stream directly.</summary>
    Minimal,

    /// <summary>Files opened with write intent are cached to disk.</summary>
    Writes,

    /// <summary>Zero disk cache: all reads stream into RAM buffer. Memory-mapped executables unsupported.</summary>
    Off
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public CacheSettings Cache { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();
    public List<MountDefinition> Mounts { get; set; } = new();
    public string? RclonePathOverride { get; set; }
    public bool RestoreMountsOnStartup { get; set; } = true;
    public bool KeepMountsAfterExit { get; set; } = false;
    public bool EncryptRcloneConfig { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    public string? LocalGamesRoot { get; set; }
    public bool StartOnSignIn { get; set; } = false;
    public bool AutoReconnectDrives { get; set; } = true;
    public bool LaunchMinimizedToTray { get; set; } = false;
    public PhantomSettings Phantom { get; set; } = new();
}

public sealed class PhantomSettings
{
    public ZeroFootprintContractTier ContractTier { get; set; } = ZeroFootprintContractTier.Strict;
    public PhantomPulseMode PulseMode { get; set; } = PhantomPulseMode.Auto;
    public PhantomPulseMode? ManualPulseOverride { get; set; }
    public bool GhostWarmUpEnabled { get; set; } = false;
    public long GhostWarmUpMaxBytes { get; set; } = 64 * ByteSize.MiB;
    public bool MemoryHotCacheEnabled { get; set; } = false;
    public long MemoryHotCacheMaxBytes { get; set; } = 64 * ByteSize.MiB;
    public bool LatencyShieldEnabled { get; set; } = true;
    public bool ShadowIntegrityEnabled { get; set; } = true;
    public int CoalescingWindowMs { get; set; } = 25;
    public long CoalescingMaxSpanBytes { get; set; } = 2 * ByteSize.MiB;
    public int MaxConcurrentRequests { get; set; } = 8;
    public bool ReadDeduplicationEnabled { get; set; } = true;
}

public sealed class CacheSettings
{
    /// <summary>Current storage policy profile.</summary>
    public CachePolicyProfile Profile { get; set; } = CachePolicyProfile.Balanced;

    /// <summary>rclone --vfs-cache-mode setting (Full, Minimal, Writes, Off).</summary>
    public VfsCacheMode CacheMode { get; set; } = VfsCacheMode.Full;

    /// <summary>Root directory for rclone VFS caches. Each mount gets its own subdirectory.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>Total physical disk budget for all mount caches.</summary>
    public long BudgetBytes { get; set; } = 5 * ByteSize.GiB;

    /// <summary>Free space that must always remain on the cache volume for Windows.</summary>
    public long MinFreeSpaceBytes { get; set; } = 8 * ByteSize.GiB;

    /// <summary>How long unused cached data may stay before rclone evicts it.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval for lightweight background cache maintenance checks (default: 2 hours).</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Enable automatic periodic cache cleanup based on budget and reserve.</summary>
    public bool AutoMaintenanceEnabled { get; set; } = true;

    /// <summary>Automatically purge local cache data on application shutdown or drive unmount.</summary>
    public bool PurgeCacheOnExit { get; set; } = false;

    /// <summary>Automatically reclaim transient game cache when the monitored game process tree exits.</summary>
    public bool AutoReclaimOnGameExit { get; set; } = true;

    /// <summary>Low-space mode: dynamically reduce cache budget as volume free space declines.</summary>
    public bool LowSpaceAdaptiveMode { get; set; } = true;

    /// <summary>
    /// rclone --buffer-size. In full cache mode rclone reads buffer-size + read-ahead past every read position.
    /// Measured: default 16M causes large speculative downloads for random game reads, so the default here is 0 and
    /// the prediction engine owns read-ahead.
    /// </summary>
    public long BufferSizeBytes { get; set; } = 0;

    public long ReadAheadBytes { get; set; } = 0;

    public long ReadChunkSizeBytes { get; set; } = 4 * ByteSize.MiB;

    public int ReadChunkStreams { get; set; } = 0;

    public TimeSpan WriteBack { get; set; } = TimeSpan.FromSeconds(5);

    public List<string> PinnedPaths { get; set; } = new();

    /// <summary>Whether Zero Footprint Mode is active (strictly bounded 512 MiB budget on C:, demand reads only, post-game cleanup, 2-hr maintenance).</summary>
    public bool ZeroFootprintMode
    {
        get => Profile == CachePolicyProfile.ZeroFootprint;
        set
        {
            if (value)
            {
                Profile = CachePolicyProfile.ZeroFootprint;
                AutoReclaimOnGameExit = true;
                MaintenanceInterval = TimeSpan.FromHours(2);
                BufferSizeBytes = 0;
                ReadAheadBytes = 0;
            }
            else if (Profile == CachePolicyProfile.ZeroFootprint)
            {
                Profile = CachePolicyProfile.Balanced;
            }
        }
    }

    /// <summary>Headroom reserved within the cache budget for filesystem structures and vfsMeta JSON files (default: 16 MiB).</summary>
    public long MetadataReserveBytes { get; set; } = 16 * ByteSize.MiB;

    /// <summary>Computes the effective budget for the active profile.</summary>
    public long EffectiveBudgetForProfile() => Profile switch
    {
        CachePolicyProfile.ZeroFootprint => 512 * ByteSize.MiB,
        CachePolicyProfile.Minimal => 512 * ByteSize.MiB,
        CachePolicyProfile.Disabled => 0,
        CachePolicyProfile.Balanced => BudgetBytes > 0 ? BudgetBytes : 5 * ByteSize.GiB,
        _ => BudgetBytes
    };

    /// <summary>Computes the effective data budget (excluding metadata reserve headroom).</summary>
    public long EffectiveDataBudgetForProfile() => Math.Max(16 * ByteSize.MiB, EffectiveBudgetForProfile() - MetadataReserveBytes);

    /// <summary>Computes the effective max age for the active profile.</summary>
    public TimeSpan EffectiveMaxAgeForProfile() => Profile switch
    {
        CachePolicyProfile.ZeroFootprint => TimeSpan.FromHours(1),
        CachePolicyProfile.Minimal => TimeSpan.FromHours(1),
        CachePolicyProfile.Disabled => TimeSpan.Zero,
        _ => MaxAge
    };
}

public sealed class NetworkSettings
{
    /// <summary>User-declared internet speed in megabits per second (used until a measurement exists).</summary>
    public double DeclaredDownloadMbps { get; set; } = 70;

    /// <summary>Last measured throughput (Mbps), null if never measured.</summary>
    public double? MeasuredDownloadMbps { get; set; }
    public double? MeasuredLatencyMs { get; set; }
    public DateTimeOffset? MeasuredAt { get; set; }

    /// <summary>Maximum share of bandwidth background (P2/P3) prefetch may use.</summary>
    public double BackgroundShare { get; set; } = 0.6;

    /// <summary>Optional hard rclone --bwlimit in Mbps (0 = unlimited).</summary>
    public double HardLimitMbps { get; set; } = 0;

    public int MaxPrefetchConcurrency { get; set; } = 4;

    /// <summary>rclone --tpslimit for provider API calls (0 = unlimited). Protects Google Drive quotas.</summary>
    public double TpsLimit { get; set; } = 10;

    public double EffectiveMbps => MeasuredDownloadMbps ?? DeclaredDownloadMbps;
}

public sealed class StreamingSettings
{
    public bool PredictionEnabled { get; set; } = true;

    /// <summary>Record per-read telemetry from rclone debug logs. Costs some CPU while active.</summary>
    public bool ReadProfilingEnabled { get; set; } = true;

    public long BlockSizeBytes { get; set; } = ByteSize.MiB;

    public double MinConfidence { get; set; } = 0.4;

    /// <summary>Upper bound of bytes the engine may prefetch speculatively in one game session.</summary>
    public long SessionPrefetchBudgetBytes { get; set; } = 4 * ByteSize.GiB;

    public TimeSpan StartupWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How far ahead (in game-progress time) learned startup data is released for prefetch during a launch.</summary>
    public TimeSpan StartupHorizon { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Predictors with accuracy below this after enough samples are disabled automatically.</summary>
    public double MinPredictorAccuracy { get; set; } = 0.25;

    public int PredictorEvaluationSamples { get; set; } = 64;
}

public sealed class MountDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; set; } = "Cloud Drive";

    /// <summary>rclone remote name without the trailing colon, e.g. "gdrive".</summary>
    public string RemoteName { get; set; } = "";

    /// <summary>Optional path inside the remote, e.g. "Games".</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>Preferred drive letter, e.g. "X". Null = pick automatically.</summary>
    public string? DriveLetter { get; set; }

    public bool ReadOnly { get; set; }
    public bool AutoMount { get; set; } = true;

    /// <summary>Extra backend-specific options passed as connection string parameters (non-secret only).</summary>
    public Dictionary<string, string> ExtraFlags { get; set; } = new();

    [JsonIgnore] public string RemoteSpec => string.IsNullOrEmpty(RemotePath) ? RemoteName + ":" : $"{RemoteName}:{RemotePath.TrimStart('/')}";
}

public sealed record StorageEfficiencyStats(
    long PhysicalAllocatedBytes,
    long LogicalAccessedBytes,
    long AvoidedStorageBytes,
    long NetworkBytesDownloaded,
    double CacheHitRatio,
    long PersistentCacheBytes,
    long TransientCacheBytes,
    long ProtectedBytes,
    long VolumeFreeBytes,
    long VolumeReserveBytes,
    string CacheVolumeFileSystem,
    bool LowSpaceActive);

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    string Path { get; }
}

/// <summary>JSON settings persisted atomically (temp file + replace) so a crash cannot leave a truncated file.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    public JsonSettingsStore(string path) => Path = path;

    public string Path { get; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return new AppSettings();
            try
            {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch (JsonException)
            {
                // Keep the damaged file for diagnostics and start from defaults.
                File.Copy(Path, Path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            if (File.Exists(Path)) File.Replace(tmp, Path, null);
            else File.Move(tmp, Path);
        }
    }
}
