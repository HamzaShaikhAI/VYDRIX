using System.Security.Cryptography;
using System.Text;

namespace Vydrix.Diagnostics;

/// <summary>
/// Stores small secrets with Windows DPAPI (CurrentUser scope). The protected blob can only be decrypted by the same
/// Windows user on the same machine, so copying the file elsewhere (backups, cloud sync) does not leak the secret.
/// Supports transparent migration and loading of legacy CloudPlayX DPAPI keys.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] VydrixEntropy = Encoding.UTF8.GetBytes("Vydrix.v1.secret");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("CloudPlayX.v1.secret");

    public static void Save(string path, string secret)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), VydrixEntropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    public static string? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        try
        {
            var data = ProtectedData.Unprotect(bytes, VydrixEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            // Fall back to legacy CloudPlayX entropy for migrated keys
            try
            {
                var data = ProtectedData.Unprotect(bytes, LegacyEntropy, DataProtectionScope.CurrentUser);
                var secret = Encoding.UTF8.GetString(data);
                // Upgrade on read: re-encrypt with modern Vydrix entropy
                try { Save(path, secret); } catch { }
                return secret;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), VydrixEntropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, VydrixEntropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, LegacyEntropy, DataProtectionScope.CurrentUser));
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Cryptographically random URL-safe token.</summary>
    public static string RandomToken(int bytes = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
