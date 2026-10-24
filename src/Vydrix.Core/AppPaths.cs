namespace Vydrix.Core;

/// <summary>
/// File-system locations used by VYDRIX. The data root defaults to %LOCALAPPDATA%\Vydrix and can be
/// redirected with the VYDRIX_HOME or CLOUDPLAYX_HOME environment variable (portable mode, tests).
/// Transparently migrates settings, keys, and database from legacy %LOCALAPPDATA%\CloudPlayX installations.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot
                   ?? Environment.GetEnvironmentVariable("VYDRIX_HOME")
                   ?? Environment.GetEnvironmentVariable("CLOUDPLAYX_HOME")
                   ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vydrix");

        MigrateLegacyIfPresent(DataRoot);

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Checks for a legacy CloudPlayX directory and migrates settings, keys, and database to Vydrix without data loss.
    /// </summary>
    public static void MigrateLegacyIfPresent(string destinationRoot, string? customLegacyRoot = null)
    {
        try
        {
            var legacyRoot = customLegacyRoot
                             ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudPlayX");
            if (!Directory.Exists(legacyRoot)) return;

            // If the destination already has a settings or database file, avoid overwriting
            if (Directory.Exists(destinationRoot) &&
                (File.Exists(Path.Combine(destinationRoot, "settings.json")) || File.Exists(Path.Combine(destinationRoot, "vydrix.db"))))
            {
                return;
            }

            Directory.CreateDirectory(destinationRoot);

            foreach (var file in Directory.EnumerateFiles(legacyRoot))
            {
                var name = Path.GetFileName(file);
                var targetName = name.Equals("cloudplayx.db", StringComparison.OrdinalIgnoreCase) ? "vydrix.db" : name;
                var targetPath = Path.Combine(destinationRoot, targetName);
                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath, overwrite: false);
                }
            }
        }
        catch
        {
            // Migration is best-effort and must not fail host startup
        }
    }

    public string DataRoot { get; }
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string DatabaseFile => Path.Combine(DataRoot, "vydrix.db");
    public string LogDirectory => Path.Combine(DataRoot, "logs");
    public string RcloneConfigFile => Path.Combine(DataRoot, "rclone.conf");
    public string ConfigPasswordFile => Path.Combine(DataRoot, "rclone.conf.key");
    public string MountStateFile => Path.Combine(DataRoot, "mounts.state.json");
    public string DefaultCacheRoot => Path.Combine(DataRoot, "cache");
    public string DefaultLocalGamesRoot => Path.Combine(DataRoot, "local-games");
    public string DiagnosticsDirectory => Path.Combine(DataRoot, "diagnostics");

    public string CacheRootFor(AppSettings s) => ResolveActiveCacheRoot(s, out _, out _);

    /// <summary>
    /// Resolves the actual runtime cache root according to Broker settings, physical secondary storage availability,
    /// and fallback protection policies. Never silently falls back to an unbounded C: cache.
    /// </summary>
    public string ResolveActiveCacheRoot(
        AppSettings s,
        out bool isRelocatedToSecondary,
        out string? fallbackWarning)
    {
        isRelocatedToSecondary = false;
        fallbackWarning = null;

        // 0. Zero Footprint Mode specifies a strictly bounded 512 MiB cache on C:
        if (s.Cache.ZeroFootprintMode || s.Cache.Profile == CachePolicyProfile.ZeroFootprint)
        {
            isRelocatedToSecondary = false;
            return string.IsNullOrWhiteSpace(s.Cache.CacheRoot) ? DefaultCacheRoot : Path.GetFullPath(s.Cache.CacheRoot);
        }

        // 1. Explicit CustomCachePath on Broker takes highest priority
        if (!string.IsNullOrWhiteSpace(s.Broker.CustomCachePath))
        {
            var custom = Path.GetFullPath(s.Broker.CustomCachePath);
            if (IsUsableStorageVolume(custom, out _, out _))
            {
                isRelocatedToSecondary = !custom.StartsWith("C:", StringComparison.OrdinalIgnoreCase);
                return custom;
            }
            fallbackWarning = $"Custom cache path '{custom}' is unavailable. Operating in fallback mode.";
        }

        // 2. If Broker policy is SecondaryPhysicalDrive (default):
        if (s.Broker.DefaultCachePlacement == CachePlacementOption.SecondaryPhysicalDrive)
        {
            // If CacheRoot was explicitly set to a secondary volume (like D:), keep it
            if (!string.IsNullOrWhiteSpace(s.Cache.CacheRoot) &&
                !s.Cache.CacheRoot.StartsWith("C:", StringComparison.OrdinalIgnoreCase) &&
                !s.Cache.CacheRoot.StartsWith("G:", StringComparison.OrdinalIgnoreCase) &&
                IsUsableStorageVolume(s.Cache.CacheRoot, out _, out _))
            {
                isRelocatedToSecondary = true;
                return Path.GetFullPath(s.Cache.CacheRoot);
            }

            // Otherwise, discover healthy secondary physical drive (e.g. D:\)
            var secondaryRoot = FindUsableSecondaryPhysicalDrive();
            if (secondaryRoot != null)
            {
                isRelocatedToSecondary = true;
                return Path.Combine(secondaryRoot, "VydrixCache");
            }

            fallbackWarning = "Secondary physical drive (D:) is unavailable or below 5 GiB free reserve. Operating in protective bounded C: fallback mode (512 MiB hard budget enforced).";
        }

        // 3. If s.Cache.CacheRoot was explicitly customized by test or user (and not default C: cache)
        if (!string.IsNullOrWhiteSpace(s.Cache.CacheRoot) &&
            !s.Cache.CacheRoot.Equals(DefaultCacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            isRelocatedToSecondary = !s.Cache.CacheRoot.StartsWith("C:", StringComparison.OrdinalIgnoreCase);
            return Path.GetFullPath(s.Cache.CacheRoot);
        }

        // 4. Default C: cache root (strictly bounded under ZeroFootprint / BoundedC)
        return DefaultCacheRoot;
    }

    public static string? FindUsableSecondaryPhysicalDrive()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (d.DriveType != DriveType.Fixed) continue;
                if (d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Name.StartsWith("G", StringComparison.OrdinalIgnoreCase)) continue;
                if (d.DriveFormat.Contains("rclone", StringComparison.OrdinalIgnoreCase) ||
                    d.DriveFormat.Contains("FUSE", StringComparison.OrdinalIgnoreCase) ||
                    d.DriveFormat.Contains("WinFsp", StringComparison.OrdinalIgnoreCase)) continue;

                if (d.AvailableFreeSpace > 5L * ByteSize.GiB)
                {
                    return d.Name; // e.g. "D:\"
                }
            }
        }
        catch { }

        return null;
    }

    public static bool IsUsableStorageVolume(string path, out long freeBytes, out string? driveFormat)
    {
        freeBytes = 0;
        driveFormat = null;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return false;
            freeBytes = drive.AvailableFreeSpace;
            driveFormat = drive.DriveFormat;
            return freeBytes > 2L * ByteSize.GiB;
        }
        catch
        {
            return false;
        }
    }

    public string MountCacheDir(AppSettings s, string mountId) => Path.Combine(CacheRootFor(s), "m-" + mountId);

    public string LocalGamesRootFor(AppSettings s) => string.IsNullOrWhiteSpace(s.LocalGamesRoot) ? DefaultLocalGamesRoot : s.LocalGamesRoot!;

    /// <summary>Directory containing the running executables (bundled rclone lives under tools\rclone).</summary>
    public static string AppDirectory => AppContext.BaseDirectory;
}
