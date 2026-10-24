<#
.SYNOPSIS
  Installs VYDRIX for the current Windows user (no administrator rights needed).
.DESCRIPTION
  Copies the application to %LOCALAPPDATA%\Programs\VYDRIX, creates a Start menu shortcut (and optionally a
  desktop shortcut), and registers an uninstall entry under "Installed apps". WinFsp is required for the virtual
  drive; if it is missing this script advises where to obtain it.
.PARAMETER DesktopShortcut
  Also create a desktop shortcut.
.PARAMETER InstallDir
  Custom installation folder (defaults to %LOCALAPPDATA%\Programs\VYDRIX).
.PARAMETER Quiet
  Suppress interactive prompts.
#>
param(
    [switch]$DesktopShortcut,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\VYDRIX"),
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src "Vydrix.exe"))) {
    throw "Run this script from the extracted VYDRIX release folder containing Vydrix.exe."
}

# Check for running VYDRIX processes from target folder
$running = Get-Process -Name "Vydrix", "vydrix-cli" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [System.StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "VYDRIX is currently running from $InstallDir. Please exit VYDRIX before upgrading or reinstalling."
}

Write-Host "Installing VYDRIX to $InstallDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $InstallDir | Out-Null

# Copy program files only (sanitized clean distribution; user settings/cache in %LOCALAPPDATA%\Vydrix are never overwritten)
Get-ChildItem $src -Force | Where-Object { $_.Name -notin @("Install.cmd") } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $InstallDir $_.Name) -Recurse -Force
}

$exe = Join-Path $InstallDir "Vydrix.exe"
$cli = Join-Path $InstallDir "vydrix-cli.exe"

# Create Start Menu and Desktop shortcuts
$shell = New-Object -ComObject WScript.Shell
function New-VydrixShortcut($path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "VYDRIX - Cloud-to-SSD Game Storage Engine"
    $lnk.Save()
}

$programsDir = [Environment]::GetFolderPath("Programs")
$startMenu = Join-Path $programsDir "VYDRIX.lnk"
New-VydrixShortcut $startMenu

if ($DesktopShortcut) {
    $desktopDir = [Environment]::GetFolderPath("Desktop")
    New-VydrixShortcut (Join-Path $desktopDir "VYDRIX.lnk")
}

# Register Windows Uninstall entry in HKCU (unprivileged)
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VYDRIX"
New-Item -Path $key -Force | Out-Null
$version = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $version) { $version = "1.1.0" }

Set-ItemProperty $key DisplayName "VYDRIX - Cloud-to-SSD Engine"
Set-ItemProperty $key DisplayVersion $version
Set-ItemProperty $key Publisher "VYDRIX"
Set-ItemProperty $key DisplayIcon "$exe,0"
Set-ItemProperty $key InstallLocation $InstallDir
Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\Uninstall-Vydrix.ps1`""
Set-ItemProperty $key NoModify 1
Set-ItemProperty $key NoRepair 1

# Check WinFsp prerequisite
$winfspFound = (Test-Path "$env:ProgramFiles\WinFsp\bin\winfsp-x64.dll") -or
               (Test-Path "${env:ProgramFiles(x86)}\WinFsp\bin\winfsp-x64.dll") -or
               (Get-ItemProperty "HKLM:\SOFTWARE\WinFsp" -ErrorAction SilentlyContinue)
if (-not $winfspFound) {
    Write-Host ""
    Write-Host "[NOTE] WinFsp was not detected on this system." -ForegroundColor Yellow
    Write-Host "       WinFsp is required to mount cloud storage as a local Windows drive letter."
    Write-Host "       Download the free installer from: https://winfsp.dev" -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "VYDRIX $version installed successfully!" -ForegroundColor Green
Write-Host "Start VYDRIX from your Start Menu or by running: $exe"
