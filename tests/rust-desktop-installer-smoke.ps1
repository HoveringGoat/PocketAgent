param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [string]$ExecutableName = 'pocketagent-desktop.exe'
)

$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$root = Join-Path $env:TEMP ('PocketAgent-rust-installer-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$installed = Join-Path $root 'installed'
New-Item -ItemType Directory -Path $installed -Force | Out-Null
$uninstaller = Join-Path $installed 'unins000.exe'

try {
    $install = Start-Process -FilePath $installerPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installed") -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Rust desktop installer exited with code $($install.ExitCode)." }
    $executable = Join-Path $installed $ExecutableName
    if (!(Test-Path -LiteralPath $executable)) { throw "Installed Rust desktop executable was not found: $executable" }
    & (Join-Path $PSScriptRoot 'rust-desktop-smoke.ps1') -Executable $executable
    if (!(Test-Path -LiteralPath $uninstaller)) { throw 'Rust desktop uninstaller was not created.' }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Rust desktop uninstaller exited with code $($uninstall.ExitCode)." }
    Write-Output 'Rust desktop installer smoke passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}
