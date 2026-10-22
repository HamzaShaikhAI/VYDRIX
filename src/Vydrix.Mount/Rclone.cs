using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vydrix.Core;
using Vydrix.Diagnostics;

namespace Vydrix.Mount;

public sealed record RcloneInfo(
    string Path,
    string Version,
    Version ParsedVersion,
    IReadOnlySet<string> MountFlags,
    string Sha256 = "",
    bool IsVerified = true)
{
    public static readonly Version MinimumVersion = new(1, 66, 0);
    public bool Supports(string flag) => MountFlags.Contains(flag.TrimStart('-'));
    public bool IsSupportedVersion => ParsedVersion >= MinimumVersion;
}

public sealed record RcloneResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>Cryptographic hash verification for pinned third-party helper binaries (SEC-04 / TECH-02).</summary>
public static class PinnedBinaryHashes
{
    /// <summary>
    /// Known official release SHA-256 hashes (lowercase hex).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // rclone v1.75.2 (windows-amd64 official release)
        ["41eab3e118d264920b62c3327966c5ece0dc4dd715ee617d9e30450d8ead95a4"] = "rclone v1.75.2 (windows-amd64 official release)"
    };

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static bool VerifyBinary(string path, out string sha256)
    {
        sha256 = ComputeFileSha256(path);
        if (KnownHashes.ContainsKey(sha256)) return true;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            var sumFiles = Directory.GetFiles(dir, "SHA256SUMS*", SearchOption.TopDirectoryOnly);
            foreach (var sf in sumFiles)
            {
                var content = File.ReadAllText(sf);
                if (content.Contains(sha256, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}

/// <summary>Locates the rclone binary, reads its version and the set of mount flags it actually supports.</summary>
public static partial class RcloneLocator
{
    [GeneratedRegex(@"rclone v(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^\s+(?:-\w, )?--([a-z0-9\-]+)", RegexOptions.Multiline)]
    private static partial Regex FlagRegex();

    public static IEnumerable<string> CandidatePaths(string? overridePath, string dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath!;
        yield return Path.Combine(AppPaths.AppDirectory, "tools", "rclone", "rclone.exe");
        yield return Path.Combine(AppPaths.AppDirectory, "rclone.exe");
        yield return Path.Combine(dataRoot, "bin", "rclone.exe");
        // Development layout: walk up from bin\Debug\... to the repository's tools folder.
        var dir = new DirectoryInfo(AppPaths.AppDirectory);
        for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "tools", "rclone", "rclone.exe");
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(p.Trim(), "rclone.exe");
    }

    public static async Task<RcloneInfo?> LocateAsync(string? overridePath, string dataRoot, CancellationToken ct = default, bool requireVerified = false)
    {
        foreach (var candidate in CandidatePaths(overridePath, dataRoot).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            var info = await InspectAsync(candidate, ct, requireVerified);
            if (info != null) return info;
        }
        return null;
    }

    public static async Task<RcloneInfo?> InspectAsync(string path, CancellationToken ct = default, bool requireVerified = false)
    {
        string sha256 = "";
        bool isVerified = false;
        try
        {
            isVerified = PinnedBinaryHashes.VerifyBinary(path, out sha256);
        }
        catch { }

        if (requireVerified && !isVerified)
        {
            throw new System.Security.SecurityException(
                $"SEC-04: rclone executable at '{path}' failed SHA-256 verification (hash: {sha256}).");
        }

        var runner = new RcloneRunner(path, env: new Dictionary<string, string?>());
        var v = await runner.RunAsync(["version"], TimeSpan.FromSeconds(20), ct);
        var m = VersionRegex().Match(v.StdOut);
        if (!m.Success) return null;
        var ver = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        var help = await runner.RunAsync(["mount", "--help"], TimeSpan.FromSeconds(20), ct);
        var global = await runner.RunAsync(["help", "flags"], TimeSpan.FromSeconds(20), ct);
        var flags = FlagRegex().Matches(help.StdOut + "\n" + global.StdOut).Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return new RcloneInfo(path, m.Value, ver, flags, sha256, isVerified);
    }
}

/// <summary>
/// Runs rclone with an argument list (no shell parsing) and an explicit environment. Secrets are passed through
/// environment variables only, never on the command line, and command lines are redacted before logging.
/// </summary>
public sealed class RcloneRunner
{
    private readonly IReadOnlyDictionary<string, string?> _env;

    public RcloneRunner(string rclonePath, IReadOnlyDictionary<string, string?> env)
    {
        RclonePath = rclonePath;
        _env = env;
    }

    public string RclonePath { get; }

    public ProcessStartInfo CreateStartInfo(IEnumerable<string> args, IReadOnlyDictionary<string, string?>? extraEnv = null)
    {
        var psi = new ProcessStartInfo(RclonePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _env) psi.Environment[k] = v;
        if (extraEnv != null) foreach (var (k, v) in extraEnv) psi.Environment[k] = v;
        return psi;
    }

    public static string DescribeCommand(ProcessStartInfo psi) =>
        Redactor.Redact(Path.GetFileName(psi.FileName) + " " + string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

    public async Task<RcloneResult> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? extraEnv = null, Action<string>? onStdErrLine = null)
    {
        using var p = new Process { StartInfo = CreateStartInfo(args, extraEnv), EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
        }
        p.WaitForExit(); // flush async readers
        return new RcloneResult(timedOut ? -1 : p.ExitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }
}
