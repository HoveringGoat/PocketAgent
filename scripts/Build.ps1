param([switch]$Publish, [switch]$SkipAndroid)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)
$dotnetCommand = if (Test-Path '.tools/dotnet/dotnet.exe') { (Resolve-Path '.tools/dotnet/dotnet.exe').Path } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.local/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetCommand restore PocketAgent.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw '.NET dependency restore failed.' }
& $dotnetCommand build PocketAgent.slnx --no-restore -c Release
if ($LASTEXITCODE -ne 0) { throw '.NET build failed.' }
if (!$SkipAndroid) {
    & (Join-Path $PSScriptRoot 'Build-Android.ps1')
    if (!$?) { throw 'Android build failed.' }
}
if ($Publish) {
    $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $publishDirectory = "artifacts/PocketAgent-$version-desktop"
    & $dotnetCommand publish src/PocketAgent.Desktop -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory 'documentation') | Out-Null
    Copy-Item -LiteralPath documentation/SETUP.md,documentation/ARCHITECTURE.md,documentation/PROJECT.md -Destination (Join-Path $publishDirectory 'documentation')
    $androidArtifact = "artifacts/PocketAgent-$version-android.apk"
    if (Test-Path -LiteralPath $androidArtifact) { Copy-Item -LiteralPath $androidArtifact -Destination $publishDirectory }
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath ($publishDirectory + '.zip') -Force
    (Get-FileHash -LiteralPath ($publishDirectory + '.zip') -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path ($publishDirectory + '.zip') -Leaf) | Set-Content -LiteralPath ($publishDirectory + '.zip.sha256')
}
