<#
.SYNOPSIS
  Uninstalls CLOUDPLAY X for the current user.
.DESCRIPTION
  Stops CLOUDPLAY X and the rclone mounts it started (pending uploads are given time to finish), removes the program
  files, shortcuts and uninstall entry. Settings, game library, profiles and the cache are KEPT unless you choose to
  remove them. Cloud files are never touched. Cache folders are only deleted if they carry the CLOUDPLAY X marker.
.PARAMETER RemoveData
  Remove settings, logs, profiles and the saved (encrypted) cloud login.
.PARAMETER RemoveCache
  Remove cached game data (only folders marked as CLOUDPLAY X caches).
#>
param([switch]$RemoveData, [switch]$RemoveCache, [switch]$Quiet)
$ErrorActionPreference = "Continue"
$installDir = $PSScriptRoot
$dataRoot = Join-Path $env:LOCALAPPDATA "CloudPlayX"

if (-not $Quiet -and -not $PSBoundParameters.ContainsKey('RemoveData')) {
    $a = Read-Host "Also delete CLOUDPLAY X settings, game library, profiles and saved cloud login? [y/N]"
    $RemoveData = $a -match '^[yY]'
}
if (-not $Quiet -and -not $PSBoundParameters.ContainsKey('RemoveCache')) {
    $a = Read-Host "Also delete cached game data from your SSD (cloud files are not affected)? [y/N]"
    $RemoveCache = $a -match '^[yY]'
}

# 1. Stop the app, then rclone mounts started from this installation (graceful first via rc is done by the app; here we
#    give rclone a chance to finish uploads by closing the app normally).
$ours = { Get-Process CloudPlayX -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, 'OrdinalIgnoreCase') } }
& $ours | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(60000) | Out-Null }
& $ours | Stop-Process -Force
$rclones = Get-Process rclone -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, 'OrdinalIgnoreCase') }
foreach ($r in $rclones) {
    Write-Host "Stopping rclone mount (pid $($r.Id))..."
    $r | Stop-Process -Force
}
Start-Sleep 2

# 2. Cache (only marked folders).
$settingsFile = Join-Path $dataRoot "settings.json"
$cacheRoot = Join-Path $dataRoot "cache"
if (Test-Path $settingsFile) {
    try { $s = Get-Content $settingsFile -Raw | ConvertFrom-Json; if ($s.Cache.CacheRoot) { $cacheRoot = $s.Cache.CacheRoot } } catch { }
}
if ($RemoveCache -and (Test-Path $cacheRoot)) {
    Get-ChildItem $cacheRoot -Directory -Filter "m-*" | ForEach-Object {
        if (Test-Path (Join-Path $_.FullName ".cloudplayx-cache")) {
            Write-Host "Removing cache $($_.FullName)"
            Remove-Item $_.FullName -Recurse -Force
        } else { Write-Host "Skipped $($_.FullName) (no CLOUDPLAY X marker)" }
    }
}

# 3. Data.
if ($RemoveData -and (Test-Path $dataRoot)) {
    foreach ($item in "settings.json", "cloudplayx.db", "cloudplayx.db-wal", "cloudplayx.db-shm", "rclone.conf", "rclone.conf.key", "mounts.state.json", "instance.lock") {
        Remove-Item (Join-Path $dataRoot $item) -Force -ErrorAction SilentlyContinue
    }
    Remove-Item (Join-Path $dataRoot "logs"), (Join-Path $dataRoot "diagnostics") -Recurse -Force -ErrorAction SilentlyContinue
}

# 4. Shortcuts, uninstall entry, program files.
Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "CLOUDPLAY X.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("Desktop")) "CLOUDPLAY X.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudPlayX" -Recurse -Force -ErrorAction SilentlyContinue
$removedFiles = $false
if ((Test-Path (Join-Path $installDir "CloudPlayX.exe")) -and $installDir -like "*CloudPlayX*") {
    Set-Location $env:TEMP
    Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$installDir`"" -WindowStyle Hidden
    $removedFiles = $true
} else {
    Write-Host "Program files were left in $installDir (folder name does not contain 'CloudPlayX'; delete it manually)." -ForegroundColor Yellow
}
Write-Host "CLOUDPLAY X uninstalled$(if (-not $removedFiles) { ' (shortcuts and registration removed)' }).$(if (-not $RemoveData) { " Your settings and library were kept in $dataRoot." })"
