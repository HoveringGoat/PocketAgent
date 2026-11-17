param(
    [string]$OutputDirectory = "artifacts/rust-desktop-preview"
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)

& cargo build --manifest-path rust/Cargo.toml -p pocketagent-desktop --release
if ($LASTEXITCODE -ne 0) { throw 'Rust desktop build failed.' }

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$executableName = if ($env:OS -eq 'Windows_NT') { 'pocketagent-desktop.exe' } else { 'pocketagent-desktop' }
$executable = Join-Path (Join-Path (Get-Location) 'rust/target/release') $executableName
if (!(Test-Path -LiteralPath $executable)) { throw "Rust desktop executable was not produced: $executable" }
Copy-Item -LiteralPath $executable -Destination (Join-Path $output $executableName) -Force
Copy-Item -LiteralPath documentation/SETUP.md,documentation/ARCHITECTURE.md -Destination $output -Force

$archive = Join-Path (Split-Path $output -Parent) 'PocketAgent-RustDesktopPreview.zip'
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value "$hash  $(Split-Path $archive -Leaf)"
Write-Host "Rust desktop preview: $output"
Write-Host "Archive: $archive"
