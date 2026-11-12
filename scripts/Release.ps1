[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Publish,
    [switch]$SkipAndroid,
    [switch]$SkipInstaller,
    [switch]$Clean,
    [switch]$Check,
    [switch]$InstallPrerequisites,
    [switch]$UpdateExisting,
    [switch]$CommitChanges,
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

function Invoke-ReleaseCommit([string]$version) {
    $status = @(git status --short)
    if ($LASTEXITCODE -ne 0) { throw 'Git status failed; the release commit cannot be created.' }
    if ($status.Count -eq 0) {
        Write-Output 'No outstanding repository changes need a release commit.'
        return
    }

    $privatePathPattern = '(?i)(^|[\\/])(?:\.env(?:\..*)?|secrets|.*\.(?:pem|key|pfx|p12|jks|keystore)|password\.dpapi)(?:$|[\\/])'
    $privateChanges = @($status | Where-Object {
            $path = $_.Substring([Math]::Min(3, $_.Length)).Trim()
            $path -match $privatePathPattern
        })
    if ($privateChanges.Count -gt 0) {
        throw "Refusing to create a release commit because private-looking files are outstanding:`n$($privateChanges -join [Environment]::NewLine)"
    }

    Invoke-Checked 'git' @('add', '--all') 'Git could not stage the release changes.'
    $staged = @(git diff --cached --name-only)
    if ($LASTEXITCODE -ne 0 -or $staged.Count -eq 0) { throw 'Git did not stage any release changes.' }
    Invoke-Checked 'git' @('commit', '-m', "Release v$version") "Git could not create the release commit for v$version."
    Write-Output "Created release commit for v$version ($($staged.Count) path(s))."
}

function Get-CurrentCommit {
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or !$commit) { throw 'Git could not resolve the release commit.' }
    return $commit
}

function Get-CurrentBranch {
    $branch = (git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or !$branch) { throw 'A published release must be created from a named branch, not a detached HEAD.' }
    return $branch
}

function Get-RemoteCommit([string]$reference) {
    $line = @(git ls-remote origin $reference | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0) { throw "Git could not resolve remote reference $reference." }
    if ($line.Count -eq 0 -and $reference.EndsWith('^{}', [StringComparison]::Ordinal)) {
        $line = @(git ls-remote origin $reference.Substring(0, $reference.Length - 3) | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0) { throw "Git could not resolve remote reference $reference." }
    }
    if ($line.Count -eq 0) { return $null }
    return ($line[0] -split '\s+')[0]
}

function Assert-ReleaseSource([string]$commit) {
    if (!$Publish) { return }
    $status = @(git status --short)
    if ($LASTEXITCODE -ne 0) { throw 'Git status failed before publishing the release.' }
    if ($status.Count -gt 0) { throw 'The worktree is still dirty after release preparation. Refusing to publish uncommitted source.' }

    $branch = Get-CurrentBranch
    if ($CommitChanges) {
        Invoke-Checked 'git' @('push', 'origin', $branch) "Git could not push release commit $commit to origin/$branch."
    }

    $remoteCommit = Get-RemoteCommit "refs/heads/$branch"
    if ($remoteCommit -ne $commit) {
        throw "origin/$branch resolves to $remoteCommit, but the release was built from $commit. Push the intended commit before publishing."
    }
}

function Get-ChangelogSection([string]$path, [string]$version, [string]$label) {
    if (!(Test-Path -LiteralPath $path)) { throw "$label changelog was not found: $path" }
    $content = Get-Content -LiteralPath $path -Raw
    $pattern = '(?ms)^##\s+' + [regex]::Escape($version) + '(?:\s[^\r\n]*)?\r?\n.*?(?=^##\s|\z)'
    $match = [regex]::Match($content, $pattern)
    if (!$match.Success) { throw "$label changelog does not contain a section for version ${version}: $path" }
    return $match.Value.Trim()
}

function New-ReleaseNotes([string]$version) {
    $desktop = Get-ChangelogSection (Join-Path $projectRoot 'CHANGELOG-desktop.md') $version 'Desktop/server'
    $android = Get-ChangelogSection (Join-Path $projectRoot 'CHANGELOG-android.md') $version 'Android'
    $desktop = [regex]::Replace($desktop, '^##[^\r\n]*\r?\n', '', 1)
    $android = [regex]::Replace($android, '^##[^\r\n]*\r?\n', '', 1)
    $notes = "# PocketAgent $version`r`n`r`n## Desktop and server`r`n`r`n$desktop`r`n`r`n## Android`r`n`r`n$android`r`n"
    $path = Join-Path ([IO.Path]::GetTempPath()) ("PocketAgent-$version-release-notes-" + [Guid]::NewGuid().ToString('N') + '.md')
    [IO.File]::WriteAllText($path, $notes, [Text.UTF8Encoding]::new($false))
    return $path
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

function Write-ChecksumManifest([string]$directory, [IO.FileInfo[]]$additionalAssets) {
    $assets = @(Get-ChildItem -LiteralPath $directory -File | Where-Object {
            $_.Name -notlike '*.sha256' -and $_.Name -ne 'SHA256SUMS.txt'
        } | Sort-Object Name)
    if ($additionalAssets) {
        $assets += @($additionalAssets | Where-Object { $_.Name -notlike '*.sha256' })
    }
    if ($assets.Count -eq 0) { throw "No release assets were found in $directory." }

    $lines = @(foreach ($asset in $assets) {
        "$((Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($asset.Name)"
    })
    $manifest = Join-Path $directory 'SHA256SUMS.txt'
    [IO.File]::WriteAllLines($manifest, $lines, [Text.UTF8Encoding]::new($false))
    return $manifest
}

function Invoke-Checked([string]$command, [string[]]$arguments, [string]$failureMessage) {
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $command @arguments
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($exitCode -ne 0) { throw $failureMessage }
}

$projectVersion = Get-ProjectVersion
if (!$Version) { $Version = $projectVersion }
Assert-SemanticVersion $Version
if ($Version -ne $projectVersion) {
    throw "Requested release version $Version does not match Directory.Build.props version $projectVersion. Update the version once, then rerun the release script."
}
if ($CommitChanges -and $Check) { throw '-CommitChanges cannot be used with -Check.' }

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

if ($CommitChanges) { Invoke-ReleaseCommit $Version }
$releaseCommit = Get-CurrentCommit
Assert-ReleaseSource $releaseCommit

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

$androidAssets = @()
if (!$SkipAndroid) {
    $androidDirectory = Join-Path $releaseDirectory 'android'
    if (!(Test-Path -LiteralPath $androidDirectory)) { throw "Android release directory was not created: $androidDirectory" }
    $androidAssets = @(Get-ChildItem -LiteralPath $androidDirectory -File | Where-Object {
            $_.Name -eq "PocketAgent-$Version-android.apk" -or $_.Name -eq "PocketAgent-$Version-android.apk.sha256"
        })
    if (!($androidAssets | Where-Object { $_.Name -eq "PocketAgent-$Version-android.apk" })) {
        throw "Android APK was not created under $androidDirectory."
    }
}

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

$manifest = Write-ChecksumManifest $releaseDirectory $androidAssets
Write-Output "Release assets staged in $releaseDirectory"
$stagedFiles = @(
    @(Get-ChildItem -LiteralPath $releaseDirectory -File)
    @($androidAssets)
)
$stagedFiles | Sort-Object Name | ForEach-Object { Write-Output "  $($_.Name)" }

if ($Publish) {
    $ghPath = Resolve-GitHubCli

    $tag = "v$Version"
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $ghPath 'release' 'view' $tag '--repo' $Repository *> $null
        $releaseViewExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    $releaseExists = $releaseViewExitCode -eq 0
    if ($releaseExists -and !$UpdateExisting) {
        throw "GitHub release $tag already exists. Refusing to overwrite a published release."
    }

    $assets = @(
        @(Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name | ForEach-Object { $_.FullName })
        @($androidAssets | Sort-Object Name | ForEach-Object { $_.FullName })
    )
    $generatedNotesFile = $null
    $notesPath = $null
    if ($NotesFile) {
        $notesPath = [IO.Path]::GetFullPath($NotesFile)
        if (!(Test-Path -LiteralPath $notesPath)) { throw "Release notes file was not found: $notesPath" }
    } else {
        $generatedNotesFile = New-ReleaseNotes $Version
        $notesPath = $generatedNotesFile
    }

    if ($releaseExists) {
        $tagCommit = Get-RemoteCommit "refs/tags/$tag^{}"
        if ($tagCommit -and $tagCommit -ne $releaseCommit) {
            throw "GitHub tag $tag already points to $tagCommit, not release commit $releaseCommit. Refusing to attach new assets to a different source."
        }
        $releaseArguments = @('release', 'upload', $tag, '--repo', $Repository, '--clobber')
    } else {
        $releaseArguments = @('release', 'create', $tag, '--repo', $Repository, '--target', $releaseCommit, '--title', "PocketAgent $Version")
        $releaseArguments += @('--notes-file', $notesPath)
    }
    $releaseArguments += $assets
    $operation = if ($releaseExists) { 'updated' } else { 'created' }
    try {
        Invoke-Checked $ghPath $releaseArguments "GitHub release $tag could not be $operation."
        if ($releaseExists) {
            Invoke-Checked $ghPath @('release', 'edit', $tag, '--repo', $Repository, '--notes-file', $notesPath) "GitHub release $tag assets were updated, but its release notes could not be updated."
        }
    } finally {
        if ($generatedNotesFile -and (Test-Path -LiteralPath $generatedNotesFile)) { Remove-Item -LiteralPath $generatedNotesFile -Force }
    }
    $publishedTagCommit = Get-RemoteCommit "refs/tags/$tag^{}"
    if ($publishedTagCommit -ne $releaseCommit) {
        throw "GitHub tag $tag resolves to $publishedTagCommit after publishing, expected $releaseCommit."
    }
    Write-Output "Published/updated GitHub release $tag on $Repository"
}

