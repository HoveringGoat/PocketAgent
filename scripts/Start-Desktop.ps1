param([string]$Repository = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$desktopDirectory = Join-Path $projectRoot "artifacts/releases/$version/desktop"
$desktopDll = Join-Path $desktopDirectory 'PocketAgent.Desktop.dll'
if (Test-Path -LiteralPath (Join-Path $desktopDirectory 'hostfxr.dll')) {
    & (Join-Path $desktopDirectory 'PocketAgent.Desktop.exe') --repository $Repository
    return
}
if (!(Test-Path -LiteralPath $desktopDll)) {
    $desktopDll = Join-Path $projectRoot 'src/PocketAgent.Desktop/bin/Release/net10.0-windows/PocketAgent.Desktop.dll'
}
if (!(Test-Path -LiteralPath $desktopDll)) { throw 'Build the Release desktop project first.' }
$localRuntime = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$runtime = if (Test-Path -LiteralPath $localRuntime) { $localRuntime } else { 'dotnet' }
& $runtime $desktopDll --repository $Repository
