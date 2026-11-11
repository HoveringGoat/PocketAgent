[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Publish,
    [switch]$SkipAndroid,
    [switch]$SkipInstaller,
    [switch]$Clean,
    [switch]$Check,
    [switch]$InstallPrerequisites,
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
    if (${env:LOCALAPPDATA}) { $knownPaths += (Join-Path ${env:LOCALAPPDATA} 'Programs/Inno Setup 6/ISCC.exe') }
    foreach ($path in $knownPaths) {
        if ($path -and (Test-Path -LiteralPath $path)) { return $path }
    }
    return $null
}

function Resolve-GitHubCli {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $knownPaths = @()
    if ($env:ProgramFiles) { $knownPaths += (Join-Path $env:ProgramFiles 'GitHub CLI/gh.exe') }
    if (${env:LOCALAPPDATA}) { $knownPaths += (Join-Path ${env:LOCALAPPDATA} 'Microsoft/WinGet/Links/gh.exe') }
    foreach ($path in $knownPaths) {
        if ($path -and (Test-Path -LiteralPath $path)) { return $path }
    }
    return $null
}

function Assert-Tool([string]$name, [string]$path, [string]$remediation) {
    if (!$path) { throw "$name is required before the release build. $remediation" }
}

function Test-AndroidPrerequisites {
    $javaPath = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/java.exe' } else { 'C:/Program Files/Java/jdk-21/bin/java.exe' }
    $androidSdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
    $gradle = Join-Path $projectRoot 'src/PocketAgent.Android/gradlew.bat'
    if (!(Test-Path -LiteralPath $javaPath)) {
        throw "Java JDK 17 or 21 is required before the Android release build. Set JAVA_HOME or install JDK 21 at C:\Program Files\Java\jdk-21."
    }
    if (!(Test-Path -LiteralPath $androidSdk)) {
        throw "Android SDK was not found at $androidSdk. Set ANDROID_HOME or install the Android SDK."
    }
    if (!(Test-Path -LiteralPath (Join-Path $androidSdk 'build-tools/35.0.0'))) {
        throw "Android build-tools 35.0.0 were not found under $androidSdk. Install that SDK component before releasing."
    }
    if (!(Test-Path -LiteralPath $gradle)) {
        throw "The Android Gradle wrapper was not found: $gradle"
    }
}

function Test-ReleasePrerequisites {
    $dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) {
        $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
        if (!$dotnetCommand) {
            throw 'The .NET SDK is required before the release build. Restore .tools/dotnet or install the SDK listed in global.json.'
        }
    }
    if (!$SkipAndroid) { Test-AndroidPrerequisites }

    $missingInno = !$SkipInstaller -and !(Resolve-InnoSetup)
    $missingGh = $Publish -and !(Resolve-GitHubCli)
    if ($missingInno -or $missingGh) {
        if (!$InstallPrerequisites) {
            $missing = @()
            if ($missingInno) { $missing += 'Inno Setup 6' }
            if ($missingGh) { $missing += 'GitHub CLI' }
            $names = $missing -join ' and '
            $verb = if ($missing.Count -eq 1) { 'is' } else { 'are' }
            throw "$names $verb missing. Run scripts/Release.ps1 -Check -InstallPrerequisites, or install them manually before retrying."
        }
        $installParameters = @{}
        if ($missingInno) { $installParameters.InnoSetup = $true }
        if ($missingGh) { $installParameters.GitHubCli = $true }
        & (Join-Path $PSScriptRoot 'Install-ReleasePrerequisites.ps1') @installParameters
        if ($LASTEXITCODE -ne 0) { throw 'Release prerequisite installation failed.' }
    }

    if (!$SkipInstaller) { Assert-Tool 'Inno Setup 6' (Resolve-InnoSetup) 'Run scripts/Release.ps1 -Check -InstallPrerequisites.' }
    if ($Publish) { Assert-Tool 'GitHub CLI' (Resolve-GitHubCli) 'Run scripts/Release.ps1 -Check -InstallPrerequisites.' }
}

function Assert-GitHubAuthentication([string]$ghPath) {
    Invoke-Checked $ghPath @('auth', 'status', '--hostname', 'github.com') 'GitHub CLI is not authenticated. Run gh auth login, then rerun.'
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

Test-ReleasePrerequisites
if ($Publish) { Assert-GitHubAuthentication (Resolve-GitHubCli) }
if ($Check) {
    Write-Output "Release prerequisites are ready for PocketAgent $Version."
    exit 0
}

if ($Clean -and (Test-Path -LiteralPath $releaseDirectory)) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
if ((Test-Path -LiteralPath $releaseDirectory) -and !$Clean) {
    throw "Release directory already exists: $releaseDirectory. Use -Clean only after confirming no active app is running from it."
}

$buildParameters = @{ Publish = $true }
if ($SkipAndroid) { $buildParameters.SkipAndroid = $true }
& (Join-Path $PSScriptRoot 'Build.ps1') @buildParameters
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
    $ghPath = Resolve-GitHubCli

    $tag = "v$Version"
    & $ghPath 'release' 'view' $tag '--repo' $Repository *> $null
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
    Invoke-Checked $ghPath $releaseArguments "GitHub release $tag could not be created."
    Write-Output "Published GitHub release $tag to $Repository"
}

