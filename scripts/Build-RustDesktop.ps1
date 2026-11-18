param(
    [string]$OutputDirectory = "artifacts/rust-desktop-preview",
    [switch]$Installer
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
& (Join-Path (Get-Location) 'tests/rust-desktop-smoke.ps1') -Executable (Join-Path $output $executableName)
if ($LASTEXITCODE -ne 0) { throw 'Rust desktop smoke test failed.' }

$archive = Join-Path (Split-Path $output -Parent) 'PocketAgent-RustDesktopPreview.zip'
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value "$hash  $(Split-Path $archive -Leaf)"
Write-Host "Rust desktop preview: $output"
Write-Host "Archive: $archive"

if ($Installer) {
    $iscc = Get-Command iscc -ErrorAction SilentlyContinue
    if ($iscc) {
        $isccPath = $iscc.Source
    } else {
        $knownPaths = @()
        if (${env:ProgramFiles(x86)}) { $knownPaths += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe') }
        if ($env:ProgramFiles) { $knownPaths += (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe') }
        if (${env:LOCALAPPDATA}) { $knownPaths += (Join-Path ${env:LOCALAPPDATA} 'Programs/Inno Setup 6/ISCC.exe') }
        $isccPath = $knownPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (!$isccPath) { throw 'Inno Setup 6 was not found. Install it or omit -Installer.' }
    $installerOutput = Join-Path (Split-Path $output -Parent) 'PocketAgent-RustDesktop-Setup.exe'
    $cargoManifest = Get-Content -LiteralPath (Join-Path (Get-Location) 'rust/pocketagent-desktop/Cargo.toml') -Raw
    $versionMatch = [regex]::Match($cargoManifest, '(?m)^version\s*=\s*"([^"]+)"')
    if (!$versionMatch.Success) { throw 'The Rust desktop Cargo.toml does not define a package version.' }
    $version = $versionMatch.Groups[1].Value
    $installerBaseName = [IO.Path]::GetFileNameWithoutExtension($installerOutput)
    & $isccPath "/DAppVersion=$version" "/DSourceDir=$output" "/DOutputDir=$(Split-Path $installerOutput -Parent)" "/DOutputBaseFilename=$installerBaseName" "/DProjectRoot=$(Get-Location)" (Join-Path (Get-Location) 'installer/PocketAgentRustDesktop.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Rust desktop installer build failed.' }
    if (!(Test-Path -LiteralPath $installerOutput)) { throw "Rust desktop installer was not produced: $installerOutput" }
    & (Join-Path (Get-Location) 'tests/rust-desktop-installer-smoke.ps1') -Installer $installerOutput
    if ($LASTEXITCODE -ne 0) { throw 'Rust desktop installer smoke test failed.' }
    $installerHash = (Get-FileHash -LiteralPath $installerOutput -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($installerOutput + '.sha256') -Value "$installerHash  $(Split-Path $installerOutput -Leaf)"
    Write-Host "Installer: $installerOutput"
}
