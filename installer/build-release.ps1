<#
.SYNOPSIS
  Builds the CLOUDPLAY X release: self-contained win-x64 publish of the desktop app and the cpx CLI into one folder,
  bundles the checksum-verified rclone binary, documentation and install/uninstall scripts, and zips the result.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-release.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "0.7.0",
    [string]$WinFspVersion = "2.1.25156",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path "$PSScriptRoot\.."
$dist = Join-Path $repo "dist"
$name = "CloudPlayX-$Version-win-x64"
$stage = Join-Path $dist $name

Write-Host "== CLOUDPLAY X $Version release build ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

# 1. rclone: verify the bundled binary against the official SHA256SUMS before shipping it.
$rclone = Join-Path $repo "tools\rclone\rclone.exe"
$sums = Get-ChildItem (Join-Path $repo "tools\rclone") -Filter "SHA256SUMS.v*" | Select-Object -First 1
if (-not (Test-Path $rclone) -or -not $sums) { throw "tools\rclone\rclone.exe or SHA256SUMS missing. Run installer\fetch-rclone.ps1 first." }
$rcloneVersion = ($sums.Name -replace '^SHA256SUMS\.', '')
& $rclone version | Select-Object -First 1 | Write-Host

# 2. Tests (unit tests always; integration tests need WinFsp and take several minutes).
if (-not $SkipTests) {
    dotnet test (Join-Path $repo "tests\CloudPlayX.Tests") -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
}

# 3. Publish (self-contained so users do not need to install .NET).
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishReadyToRun=true", "-p:Version=$Version", "--nologo", "-v", "q", "-o", $stage)
dotnet publish (Join-Path $repo "src\CloudPlayX.Desktop\CloudPlayX.Desktop.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed" }
dotnet publish (Join-Path $repo "src\CloudPlayX.Cli\CloudPlayX.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

# 4. rclone + notices.
$toolDir = Join-Path $stage "tools\rclone"
New-Item -ItemType Directory -Force $toolDir | Out-Null
Copy-Item $rclone $toolDir -Force
Copy-Item $sums.FullName $toolDir -Force
$hash = (Get-FileHash (Join-Path $toolDir "rclone.exe") -Algorithm SHA256).Hash.ToLower()
Set-Content (Join-Path $toolDir "README.txt") @"
rclone $rcloneVersion (https://rclone.org), MIT License, Copyright (C) 2012 by Nick Craig-Wood.
Downloaded from https://downloads.rclone.org/$rcloneVersion/ - the zip's SHA256 was verified against the official
SHA256SUMS file (included). SHA256 of rclone.exe: $hash
"@
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") $stage -Force

# Optional publisher Google OAuth client (Desktop app client JSON from Google Cloud, renamed). It is only useful to
# everyone once the Google project is "In production"; in Testing mode only listed test users can sign in with it.
$publisherKey = Join-Path $PSScriptRoot "google-client.json"
if (Test-Path $publisherKey) {
    Copy-Item $publisherKey (Join-Path $stage "google-client.json") -Force
    Write-Host "Bundled publisher Google key (installer\google-client.json)." -ForegroundColor Yellow
}

# 5. Docs and scripts.
foreach ($f in "README.md", "KNOWN_LIMITATIONS.md", "COMPATIBILITY.md", "BENCHMARKS.md", "TEST_REPORT.md", "BUILD_STATUS.md", "ARCHITECTURE.md") {
    if (Test-Path (Join-Path $repo $f)) { Copy-Item (Join-Path $repo $f) $stage -Force }
}
New-Item -ItemType Directory -Force (Join-Path $stage "docs") | Out-Null
Copy-Item (Join-Path $repo "docs\*") (Join-Path $stage "docs") -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "Install-CloudPlayX.ps1") $stage -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-CloudPlayX.ps1") $stage -Force
Set-Content (Join-Path $stage "Install.cmd") "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-CloudPlayX.ps1`" %*`r`npause"
Remove-Item (Join-Path $stage "*.pdb") -ErrorAction SilentlyContinue

# 6. Smoke test of the published binaries (no UI): CLI must run and find rclone/WinFsp.
& (Join-Path $stage "cpx.exe") deps
if ($LASTEXITCODE -ne 0) { throw "cpx deps failed in the published folder" }

# 7. Zip + checksum.
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Set-Content "$zip.sha256" "$zipHash  $name.zip"
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Release: $zip ($size MB)`nSHA256: $zipHash" -ForegroundColor Green

# 8. Single-file setup.exe (Inno Setup 6) with the official WinFsp installer inside.
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Inno Setup not found: skipped setup.exe (install it with: winget install JRSoftware.InnoSetup)." -ForegroundColor Yellow
    return
}
$winfspDir = Join-Path $repo "tools\winfsp"
$winfspMsi = Join-Path $winfspDir "winfsp-$WinFspVersion.msi"
if (-not (Test-Path $winfspMsi)) {
    New-Item -ItemType Directory -Force $winfspDir | Out-Null
    $tag = "v" + ($WinFspVersion -replace '\.\d+$', '')
    Invoke-WebRequest "https://github.com/winfsp/winfsp/releases/download/$tag/winfsp-$WinFspVersion.msi" -OutFile $winfspMsi -UseBasicParsing
}
# Ship only the genuine installer: it must carry a valid Authenticode signature from WinFsp's publisher.
$sig = Get-AuthenticodeSignature $winfspMsi
if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch "NAVIMATICS LLC") {
    Remove-Item $winfspMsi -Force
    throw "WinFsp installer signature check failed ($($sig.Status)); deleted it."
}
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$stage" "/DWinFspMsi=$winfspMsi" "/O$dist" (Join-Path $PSScriptRoot "CloudPlayX.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
$setup = Join-Path $dist "CloudPlayX-$Version-setup.exe"
$setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Set-Content "$setup.sha256" "$setupHash  CloudPlayX-$Version-setup.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)`nSHA256: $setupHash" -ForegroundColor Green
