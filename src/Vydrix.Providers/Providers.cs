namespace Vydrix.Providers;

public enum SupportLevel
{
    /// <summary>Implemented and exercised by VYDRIX's own workflow (connect, mount, stream).</summary>
    Supported,
    /// <summary>Works through the corresponding rclone backend; not tested end-to-end by VYDRIX.</summary>
    AvailableUntested,
    /// <summary>No authorized integration exists; the adapter is a placeholder that explains why.</summary>
    Unsupported
}

public sealed record ProviderCapabilities(bool RangeReads, bool Checksums, bool Writes, bool ChangeNotifications, string? Notes = null);

/// <summary>Describes one cloud backend and how VYDRIX connects to it.</summary>
public interface ICloudProvider
{
    string Id { get; }
    string DisplayName { get; }
    string? RcloneBackend { get; }
    SupportLevel Support { get; }
    ProviderCapabilities Capabilities { get; }
    bool RequiresOAuthClient { get; }
    string SetupSummary { get; }
    string? DocumentationUrl { get; }
}

public sealed record CloudProvider(
    string Id, string DisplayName, string? RcloneBackend, SupportLevel Support, ProviderCapabilities Capabilities,
    bool RequiresOAuthClient, string SetupSummary, string? DocumentationUrl) : ICloudProvider;

public static class ProviderRegistry
{
    public static readonly CloudProvider GoogleDrive = new(
        "gdrive", "Google Drive", "drive", SupportLevel.Supported,
        new ProviderCapabilities(RangeReads: true, Checksums: true, Writes: true, ChangeNotifications: true,
            "MD5 checksums and modification times are used for cache validation; Drive API quotas apply (rate limits are retried with backoff by rclone)."),
        RequiresOAuthClient: true,
        "Create your own OAuth client in Google Cloud Console (Desktop app type), enable the Google Drive API, then paste the client ID and secret into VYDRIX. " +
        "rclone's shared client ID is being retired during 2026, so your own client is required.",
        "https://rclone.org/drive/#making-your-own-client-id");

    public static readonly CloudProvider TeraBox = new(
        "terabox", "TeraBox", null, SupportLevel.Unsupported,
        new ProviderCapabilities(false, false, false, false,
            "No official rclone backend. The community fork that exists authenticates with browser session cookies against undocumented web endpoints, " +
            "which VYDRIX will not use. Revisit when TeraBox publishes a public API with OAuth and byte-range downloads."),
        RequiresOAuthClient: false,
        "Not available. See docs/TERABOX_RESEARCH.md.", null);

    public static IReadOnlyList<CloudProvider> All { get; } =
    [
        GoogleDrive,
        new("onedrive", "Microsoft OneDrive", "onedrive", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, true, true, true), true,
            "Configure with 'rclone config' (backend 'onedrive') using VYDRIX's config file, then add it in Cloud Drives.", "https://rclone.org/onedrive/"),
        new("dropbox", "Dropbox", "dropbox", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, true, true, true), true,
            "Configure with 'rclone config' (backend 'dropbox'); a custom app key is recommended.", "https://rclone.org/dropbox/"),
        new("s3", "S3-compatible storage", "s3", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, true, true, false), false,
            "Configure with 'rclone config' (backend 's3'); access keys stay in the encrypted rclone config.", "https://rclone.org/s3/"),
        new("nas", "Local NAS (SMB/SFTP)", "smb", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, false, true, false), false,
            "Configure with 'rclone config' (backend 'smb' or 'sftp').", "https://rclone.org/smb/"),
        TeraBox,
    ];

    public static CloudProvider? ForBackend(string rcloneType) =>
        All.FirstOrDefault(p => string.Equals(p.RcloneBackend, rcloneType, StringComparison.OrdinalIgnoreCase));
}
