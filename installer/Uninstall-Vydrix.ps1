<#
.SYNOPSIS
  Uninstalls VYDRIX from the current Windows user account.
.DESCRIPTION
  Removes program files, Start Menu shortcuts, and uninstaller registry entries.
  User data (settings, saved credentials, and local SSD cache) in %LOCALAPPDATA%\Vydrix is
  preserved by default so unuploaded writes and configured drives are not lost, with an
  optional switch to purge them.
.PARAMETER PurgeUserData
  Also removes %LOCALAPPDATA%\Vydrix (settings, database, and cache).
#>
param(
    [switch]$PurgeUserData,
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"

$installDir = $PSScriptRoot
Write-Host "Uninstalling VYDRIX from $installDir ..." -ForegroundColor Cyan

# Check if VYDRIX processes are running
$running = Get-Process -Name "Vydrix", "vydrix-cli" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [System.StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    if (-not $Quiet) {
        Write-Warning "VYDRIX is currently running. Attempting graceful close..."
    }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# Remove Start Menu shortcut
$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "VYDRIX.lnk"
if (Test-Path $startMenu) {
    Remove-Item $startMenu -Force -ErrorAction SilentlyContinue
    Write-Host "Removed Start Menu shortcut."
}

# Remove Desktop shortcut
$desktop = Join-Path ([Environment]::GetFolderPath("Desktop")) "VYDRIX.lnk"
if (Test-Path $desktop) {
    Remove-Item $desktop -Force -ErrorAction SilentlyContinue
    Write-Host "Removed Desktop shortcut."
}

# Remove Run key if configured for startup
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$existingRun = Get-ItemProperty -Path $runKey -Name "VYDRIX" -ErrorAction SilentlyContinue
if ($existingRun) {
    Remove-ItemProperty -Path $runKey -Name "VYDRIX" -ErrorAction SilentlyContinue
    Write-Host "Removed startup auto-run registry entry."
}

# Remove HKCU Uninstall registry key
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VYDRIX"
if (Test-Path $key) {
    Remove-Item -Path $key -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed Windows uninstall entry."
}

# Purge user data if requested
if ($PurgeUserData) {
    $userData = Join-Path $env:LOCALAPPDATA "Vydrix"
    if (Test-Path $userData) {
        Write-Host "Purging user data directory: $userData" -ForegroundColor Yellow
        Remove-Item $userData -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "User data preserved in %LOCALAPPDATA%\Vydrix (pass -PurgeUserData to remove)." -ForegroundColor DarkGray
}

# Schedule program directory deletion after script exit
Write-Host "VYDRIX uninstalled successfully." -ForegroundColor Green
$cmd = "Start-Sleep -Seconds 2; Remove-Item -LiteralPath '$installDir' -Recurse -Force -ErrorAction SilentlyContinue"
Start-Process powershell.exe -ArgumentList "-NoProfile", "-WindowStyle", "Hidden", "-Command", $cmd
