param(
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)

$cargoManifestPath = Join-Path (Get-Location) 'rust/pocketagent-desktop/Cargo.toml'
$cargoManifest = Get-Content -LiteralPath $cargoManifestPath -Raw
$versionMatch = [regex]::Match($cargoManifest, '(?m)^version\s*=\s*"([^\"]+)"')
if (!$versionMatch.Success) { throw 'The Rust desktop Cargo.toml does not define a package version.' }
$version = $versionMatch.Groups[1].Value
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) 'artifacts'))
$output = Join-Path $artifactRoot "rust-desktop-$version"
$archive = Join-Path $artifactRoot "rust-desktop-$version.zip"
$installerOutput = Join-Path $artifactRoot "rust-desktop-$version-setup.exe"
$executableName = if ($env:OS -eq 'Windows_NT') { 'pocketagent-desktop.exe' } else { 'pocketagent-desktop' }
$releaseDirectory = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) 'rust/target/release'))
$releaseExecutable = Join-Path $releaseDirectory $executableName

# The Rust desktop is routinely left open while it is being tested. Stop only
# the process running the canonical release binary; never invent another target
# directory or artifact name to work around its file lock.
if (Test-Path -LiteralPath $releaseExecutable) {
    $runningReleaseProcesses = Get-Process -Name 'pocketagent-desktop' -ErrorAction SilentlyContinue |
        Where-Object {
            try { [System.IO.Path]::GetFullPath($_.Path) -ieq $releaseExecutable }
            catch { $false }
        }
    foreach ($process in $runningReleaseProcesses) {
        Write-Host "Stopping running test process $($process.Id): $releaseExecutable"
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
    }
}

# A rebuild of the same version replaces the exact version directory and artifacts.
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

# Packaging is gated on the same workspace checks used for implementation.
# A failed test or warning must stop before any release artifact is written.
& cargo test --manifest-path rust/Cargo.toml --workspace --all-targets
if ($LASTEXITCODE -ne 0) { throw 'Rust workspace test gate failed.' }
& cargo clippy --manifest-path rust/Cargo.toml --workspace --all-targets -- -D warnings
if ($LASTEXITCODE -ne 0) { throw 'Rust workspace clippy gate failed.' }
& cargo run --manifest-path rust/Cargo.toml -p pocketagent-relay --bin pocketagent-relay --quiet -- demo
if ($LASTEXITCODE -ne 0) { throw 'Rust relay process smoke gate failed.' }

& cargo build --manifest-path rust/Cargo.toml -p pocketagent-desktop --release
if ($LASTEXITCODE -ne 0) { throw 'Rust desktop build failed.' }

New-Item -ItemType Directory -Force -Path $output | Out-Null
$executable = $releaseExecutable
if (!(Test-Path -LiteralPath $executable)) { throw "Rust desktop executable was not produced: $executable" }
Copy-Item -LiteralPath $executable -Destination (Join-Path $output $executableName) -Force
Copy-Item -LiteralPath documentation/SETUP.md,documentation/ARCHITECTURE.md -Destination $output -Force
& (Join-Path (Get-Location) 'tests/rust-desktop-smoke.ps1') -Executable (Join-Path $output $executableName)
if ($LASTEXITCODE -ne 0) { throw 'Rust desktop smoke test failed.' }

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value "$hash  $(Split-Path $archive -Leaf)"
Write-Host "Rust desktop artifact: $output"
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
