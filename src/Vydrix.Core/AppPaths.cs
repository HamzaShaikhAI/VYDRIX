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

    public string CacheRootFor(AppSettings s) => string.IsNullOrWhiteSpace(s.Cache.CacheRoot) ? DefaultCacheRoot : s.Cache.CacheRoot!;

    public string MountCacheDir(AppSettings s, string mountId) => Path.Combine(CacheRootFor(s), "m-" + mountId);

    public string LocalGamesRootFor(AppSettings s) => string.IsNullOrWhiteSpace(s.LocalGamesRoot) ? DefaultLocalGamesRoot : s.LocalGamesRoot!;

    /// <summary>Directory containing the running executables (bundled rclone lives under tools\rclone).</summary>
    public static string AppDirectory => AppContext.BaseDirectory;
}
