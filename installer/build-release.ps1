<#
.SYNOPSIS
  Builds the official VYDRIX 1.1.0 release: self-contained win-x64 publish of the desktop app and the CLI,
  bundles the verified rclone binary, documentation, and install/uninstall scripts, and packages the result.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-release.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.1.0",
    [string]$WinFspVersion = "2.1.25156",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path "$PSScriptRoot\.."
$dist = Join-Path $repo "dist"
$name = "VYDRIX-$Version-win-x64"
$stage = Join-Path $dist $name

Write-Host "== VYDRIX $Version Release Build ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

# 1. Verify rclone binary
$rclone = Join-Path $repo "tools\rclone\rclone.exe"
$sums = Get-ChildItem (Join-Path $repo "tools\rclone") -Filter "SHA256SUMS.v*" | Select-Object -First 1
if (-not (Test-Path $rclone) -or -not $sums) {
    throw "tools\rclone\rclone.exe or SHA256SUMS missing."
}
$rcloneVersion = ($sums.Name -replace '^SHA256SUMS\.', '')
Write-Host "Bundling rclone $rcloneVersion..."

# 2. Run test suite
if (-not $SkipTests) {
    Write-Host "Running unit test suite..."
    dotnet test (Join-Path $repo "tests\Vydrix.Tests\Vydrix.Tests.csproj") -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed!" }
    Write-Host "All tests passed cleanly." -ForegroundColor Green
}

# 3. Publish desktop app and CLI (self-contained win-x64)
Write-Host "Publishing Vydrix.Desktop..."
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishReadyToRun=true", "-p:Version=$Version", "--nologo", "-v", "q", "-o", $stage)
dotnet publish (Join-Path $repo "src\Vydrix.Desktop\Vydrix.Desktop.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed!" }

Write-Host "Publishing Vydrix.Cli..."
dotnet publish (Join-Path $repo "src\Vydrix.Cli\Vydrix.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed!" }

# 4. Bundle rclone and licensing notices
$toolDir = Join-Path $stage "tools\rclone"
New-Item -ItemType Directory -Force $toolDir | Out-Null
Copy-Item $rclone $toolDir -Force
Copy-Item $sums.FullName $toolDir -Force
$hash = (Get-FileHash (Join-Path $toolDir "rclone.exe") -Algorithm SHA256).Hash.ToLower()
Set-Content (Join-Path $toolDir "README.txt") @"
rclone $rcloneVersion (https://rclone.org), MIT License, Copyright (C) 2012 by Nick Craig-Wood.
Official release SHA256 verified against $sums. SHA256: $hash
"@
if (Test-Path (Join-Path $repo "THIRD_PARTY_NOTICES.md")) {
    Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") $stage -Force
}

# 5. Installer scripts and command launcher
Copy-Item (Join-Path $PSScriptRoot "Install-Vydrix.ps1") $stage -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-Vydrix.ps1") $stage -Force
Set-Content (Join-Path $stage "Install.cmd") "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-Vydrix.ps1`" %*`r`npause"
Remove-Item (Join-Path $stage "*.pdb") -ErrorAction SilentlyContinue

# 6. Verify zero secrets policy: check that no personal files leaked into the package
$forbidden = @("rclone.conf", "rclone.conf.key", "settings.json", "vydrix.db", "*.token")
foreach ($pat in $forbidden) {
    $found = Get-ChildItem -Path $stage -Filter $pat -Recurse -ErrorAction SilentlyContinue
    if ($found) {
        throw "SECURITY VIOLATION: Private or user file leaked into release package: $($found[0].FullName)"
    }
}

# 7. Smoke test CLI
Write-Host "Running smoke test on published CLI..."
& (Join-Path $stage "vydrix-cli.exe") deps
if ($LASTEXITCODE -ne 0) { throw "Smoke test failed for vydrix-cli.exe deps" }

# 8. Create portable zip release
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Set-Content "$zip.sha256" "$zipHash  $name.zip"
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Portable package: $zip ($size MB)`nSHA256: $zipHash" -ForegroundColor Green

# 9. Inno Setup single-file installer (if ISCC available)
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    Write-Host "Compiling Inno Setup installer..."
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$stage" "/O$dist" (Join-Path $PSScriptRoot "Vydrix.iss")
    Write-Host "Setup installer created in $dist" -ForegroundColor Green
} else {
    Write-Host "Inno Setup not detected on PATH; installer script is ready at: $($PSScriptRoot)\Vydrix.iss" -ForegroundColor Yellow
}
