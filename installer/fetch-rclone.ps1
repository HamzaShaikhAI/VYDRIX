# Downloads the pinned rclone release from downloads.rclone.org and verifies it against the official SHA256SUMS.
param([string]$Version = "v1.75.2")
$ErrorActionPreference = "Stop"
$dest = Join-Path $PSScriptRoot "..\tools\rclone"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("cpx-rclone-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp, $dest | Out-Null
$zip = "rclone-$Version-windows-amd64.zip"
Invoke-WebRequest -UseBasicParsing "https://downloads.rclone.org/$Version/$zip" -OutFile "$tmp\$zip"
Invoke-WebRequest -UseBasicParsing "https://downloads.rclone.org/$Version/SHA256SUMS" -OutFile "$tmp\SHA256SUMS"
$expected = ((Select-String -Path "$tmp\SHA256SUMS" -Pattern ([regex]::Escape($zip))).Line -split '\s+')[0]
$actual = (Get-FileHash "$tmp\$zip" -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { throw "Checksum mismatch for $zip (expected $expected, got $actual)" }
Expand-Archive "$tmp\$zip" -DestinationPath "$tmp\x" -Force
Copy-Item "$tmp\x\rclone-$Version-windows-amd64\rclone.exe" $dest -Force
Copy-Item "$tmp\SHA256SUMS" (Join-Path $dest "SHA256SUMS.$Version") -Force
Write-Host "rclone $Version verified ($actual) and installed to $dest"
