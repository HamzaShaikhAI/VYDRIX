using System;
using System.IO;
using Microsoft.Win32;

namespace Vydrix.App;

/// <summary>
/// Manages automatic start-on-login registration via the Windows Current User Run registry key.
/// Operates completely unprivileged (per-user HKCU), stores zero secrets, and fully quotes executable paths.
/// </summary>
public static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VYDRIX";

    /// <summary>Checks whether VYDRIX is currently configured to run on user login.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Registers VYDRIX to run on user login.</summary>
    public static bool Register(string exePath, bool launchMinimized = true)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            var fullPath = Path.GetFullPath(exePath);
            var cmd = $"\"{fullPath}\"" + (launchMinimized ? " --minimized" : "");
            key.SetValue(ValueName, cmd, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes VYDRIX from the Windows login startup registry.</summary>
    public static bool Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
