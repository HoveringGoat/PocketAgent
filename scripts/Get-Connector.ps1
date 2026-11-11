param([string]$Destination)
$ErrorActionPreference = 'Stop'
if (!$Destination) { $Destination = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/cloudflared/2026.10.7' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$binary = Join-Path $Destination 'cloudflared.exe'
$expected = '2493dbc168923bb3e59e5e7716f71deced92992eea9acd6e9d74f84ac7751343'
if (!(Test-Path -LiteralPath $binary)) {
    Invoke-WebRequest -Uri 'https://github.com/cloudflare/cloudflared/releases/download/2026.10.7/cloudflared-windows-amd64.exe' -OutFile ($binary + '.download') -UseBasicParsing
    if ((Get-FileHash -LiteralPath ($binary + '.download') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Connector checksum did not match the pinned release.' }
    Move-Item -LiteralPath ($binary + '.download') -Destination $binary
}
if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Connector checksum mismatch.' }
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/cloudflare/cloudflared/2026.10.7/LICENSE' -OutFile (Join-Path $Destination 'cloudflared-LICENSE') -UseBasicParsing
Set-Content -LiteralPath (Join-Path $Destination 'cloudflared-VERSION.txt') -Value 'cloudflared 2026.10.7; Apache-2.0; https://github.com/cloudflare/cloudflared/releases/tag/2026.10.7'
Write-Output $binary
