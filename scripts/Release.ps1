[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Publish,
    [switch]$SkipAndroid,
    [switch]$SkipInstaller,
    [switch]$Clean,
    [string]$Repository = 'HoveringGoat/PocketAgent',
    [string]$NotesFile
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot

function Get-ProjectVersion {
    $props = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
    $value = [string]$props.Project.PropertyGroup.Version
    if (!$value) { throw 'Directory.Build.props does not define a project version.' }
    return $value.Trim()
}

function Resolve-InnoSetup {
    $command = Get-Command iscc -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $knownPaths = @()
    if (${env:ProgramFiles(x86)}) { $knownPaths += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe') }
    if ($env:ProgramFiles) { $knownPaths += (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe') }
    foreach ($path in $knownPaths) {
        if ($path -and (Test-Path -LiteralPath $path)) { return $path }
    }
    return $null
}

function Assert-SemanticVersion([string]$value) {
    if ($value -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw "Version '$value' is not a supported semantic version."
    }
}

function Assert-PackageHasNoPrivateFiles([string]$directory) {
    $forbidden = '\\(?:\.env(?:\..*)?|.*\.(?:pem|key|pfx|p12|jks|keystore|sqlite|db)|password\.dpapi)$'
    $matches = @(Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {
            $_.FullName.Substring($directory.Length) -match $forbidden
        })
    if ($matches.Count -gt 0) {
        $names = ($matches | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Release package contains private/runtime files:`n$names"
    }
}

function Write-ChecksumManifest([string]$directory) {
    $assets = @(Get-ChildItem -LiteralPath $directory -File | Where-Object {
            $_.Name -notlike '*.sha256' -and $_.Name -ne 'SHA256SUMS.txt'
        } | Sort-Object Name)
    if ($assets.Count -eq 0) { throw "No release assets were found in $directory." }

    $lines = @(foreach ($asset in $assets) {
        "$((Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($asset.Name)"
    })
    $manifest = Join-Path $directory 'SHA256SUMS.txt'
    [IO.File]::WriteAllLines($manifest, $lines, [Text.UTF8Encoding]::new($false))
    return $manifest
}

function Invoke-Checked([string]$command, [string[]]$arguments, [string]$failureMessage) {
    & $command @arguments
    if ($LASTEXITCODE -ne 0) { throw $failureMessage }
}

$projectVersion = Get-ProjectVersion
if (!$Version) { $Version = $projectVersion }
Assert-SemanticVersion $Version
if ($Version -ne $projectVersion) {
    throw "Requested release version $Version does not match Directory.Build.props version $projectVersion. Update the version once, then rerun the release script."
}

$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts/releases/$Version"))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$artifactPrefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
if (!$releaseDirectory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output escaped the artifacts directory.'
}

if ($Clean -and (Test-Path -LiteralPath $releaseDirectory)) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
if ((Test-Path -LiteralPath $releaseDirectory) -and !$Clean) {
    throw "Release directory already exists: $releaseDirectory. Use -Clean only after confirming no active app is running from it."
}

$buildArguments = @('-Publish')
if ($SkipAndroid) { $buildArguments += '-SkipAndroid' }
& (Join-Path $PSScriptRoot 'Build.ps1') @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

$desktopDirectory = Join-Path $releaseDirectory 'desktop'
$desktopArchive = Join-Path $releaseDirectory "PocketAgent-$Version-desktop.zip"
if (!(Test-Path -LiteralPath $desktopDirectory)) { throw "Desktop publish directory was not created: $desktopDirectory" }
if (!(Test-Path -LiteralPath $desktopArchive)) { throw "Desktop archive was not created: $desktopArchive" }
Assert-PackageHasNoPrivateFiles $desktopDirectory

if (!$SkipInstaller) {
    $iscc = Resolve-InnoSetup
    if (!$iscc) {
        throw 'Inno Setup 6 (ISCC.exe) is required for the installer. Install it or rerun with -SkipInstaller to build the portable/Android assets only.'
    }
    $installerArguments = @(
        "/DAppVersion=$Version",
        "/DSourceDir=$desktopDirectory",
        "/DOutputDir=$releaseDirectory",
        "/DOutputBaseFilename=PocketAgent-$Version-Setup",
        "/DProjectRoot=$projectRoot",
        (Join-Path $projectRoot 'installer/PocketAgent.iss')
    )
    Invoke-Checked $iscc $installerArguments 'Inno Setup failed to create the desktop installer.'
    $installer = Join-Path $releaseDirectory "PocketAgent-$Version-Setup.exe"
    if (!(Test-Path -LiteralPath $installer)) { throw "Installer output was not created: $installer" }
}

$manifest = Write-ChecksumManifest $releaseDirectory
Write-Output "Release assets staged in $releaseDirectory"
Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name | ForEach-Object { Write-Output "  $($_.Name)" }

if ($Publish) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (!$gh) { throw 'GitHub CLI (gh) is required for -Publish. Authenticate with gh auth login, then rerun.' }
    Invoke-Checked $gh.Source @('auth', 'status', '--hostname', 'github.com') 'GitHub CLI is not authenticated. Run gh auth login, then rerun.'

    $tag = "v$Version"
    & $gh.Source 'release' 'view' $tag '--repo' $Repository *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "GitHub release $tag already exists. Refusing to overwrite a published release."
    }

    $assets = @(Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name | ForEach-Object { $_.FullName })
    $releaseArguments = @('release', 'create', $tag, '--repo', $Repository, '--title', "PocketAgent $Version")
    if ($NotesFile) {
        $notesPath = [IO.Path]::GetFullPath($NotesFile)
        if (!(Test-Path -LiteralPath $notesPath)) { throw "Release notes file was not found: $notesPath" }
        $releaseArguments += @('--notes-file', $notesPath)
    } else {
        $releaseArguments += '--generate-notes'
    }
    $releaseArguments += $assets
    Invoke-Checked $gh.Source $releaseArguments "GitHub release $tag could not be created."
    Write-Output "Published GitHub release $tag to $Repository"
}

