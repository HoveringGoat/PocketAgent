param([switch]$Publish, [switch]$SkipAndroid, [string]$Version, [ValidateSet('All','Desktop','Android')][string]$Target = 'All')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)
$dotnetCommand = if (Test-Path '.tools/dotnet/dotnet.exe') { (Resolve-Path '.tools/dotnet/dotnet.exe').Path } else { 'dotnet' }
$desktopVersion = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version.Trim()
$androidVersion = ([regex]::Match((Get-Content src/PocketAgent.Android/app/build.gradle -Raw), "versionName\s+.*\?:\s*'([^']+)'")).Groups[1].Value
if (!$Version) { $Version = if ($Target -eq 'Android') { $androidVersion } else { $desktopVersion } }
if (!$Version) { throw 'A project version could not be determined.' }
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.local/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($Target -ne 'Android') {
& $dotnetCommand restore PocketAgent.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw '.NET dependency restore failed.' }
& $dotnetCommand build PocketAgent.slnx --no-restore -c Release -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw '.NET build failed.' }
}
if (!$SkipAndroid -and $Target -ne 'Desktop') {
    & (Join-Path $PSScriptRoot 'Build-Android.ps1') -Version $Version
    if (!$?) { throw 'Android build failed.' }
}
if ($Publish -and $Target -ne 'Android') {
    $releaseDirectory = "artifacts/releases/$Version"
    $publishDirectory = "$releaseDirectory/desktop"
    & $dotnetCommand publish src/PocketAgent.Desktop -c Release -r win-x64 --self-contained true -p:Version=$Version -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $updaterDirectory = Join-Path $publishDirectory '.updater'
    & $dotnetCommand publish src/PocketAgent.Updater -c Release -r win-x64 --self-contained true -p:Version=$Version -p:RestoreLockedMode=false -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $updaterDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
    $updaterExecutable = Join-Path $updaterDirectory 'PocketAgent.Updater.exe'
    if (!(Test-Path -LiteralPath $updaterExecutable)) { throw 'The updater publish did not produce PocketAgent.Updater.exe.' }
    Copy-Item -LiteralPath $updaterExecutable -Destination (Join-Path $publishDirectory 'PocketAgent.Updater.exe') -Force
    Remove-Item -LiteralPath $updaterDirectory -Recurse -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory 'documentation') | Out-Null
    Copy-Item -LiteralPath documentation/SETUP.md,documentation/ARCHITECTURE.md,documentation/PROJECT.md -Destination (Join-Path $publishDirectory 'documentation')
    $archive = "$releaseDirectory/PocketAgent-$version-desktop.zip"
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath $archive -Force
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $archive -Leaf) | Set-Content -LiteralPath ($archive + '.sha256')
}
