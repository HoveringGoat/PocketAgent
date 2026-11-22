param(
    [string]$SdkDirectory = $env:ANDROID_HOME,
    [string]$JavaDirectory = $env:JAVA_HOME,
    [string]$GradleCommand,
    [string]$Version,
    [string]$SigningDirectory = $env:POCKETAGENT_ANDROID_SIGNING_DIRECTORY,
    [switch]$DebugOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$SdkDirectory) { $SdkDirectory = Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
if (!$JavaDirectory -and (Test-Path -LiteralPath 'C:/Program Files/Java/jdk-21')) { $JavaDirectory = 'C:/Program Files/Java/jdk-21' }
if (!$JavaDirectory) { throw 'Set JAVA_HOME or pass -JavaDirectory for JDK 17 or 21.' }
$env:JAVA_HOME = $JavaDirectory
$env:ANDROID_HOME = $SdkDirectory
$env:GRADLE_USER_HOME = Join-Path $projectRoot '.local/gradle'
if (!$GradleCommand) { $GradleCommand = Join-Path $projectRoot 'src/PocketAgent.Android/gradlew.bat' }
$androidProject = Join-Path $projectRoot 'src/PocketAgent.Android'
$gradleFile = Join-Path $androidProject 'app/build.gradle'
if (!$Version) { $Version = ([regex]::Match((Get-Content $gradleFile -Raw), "versionName\s+.*\?:\s*'([^']+)'")).Groups[1].Value }
if (!$Version) { throw 'Android versionName could not be determined.' }
$tasks = if ($DebugOnly) { @(':app:assembleDebug', ':app:lintDebug') } else { @(':app:assembleDebug', ':app:assembleRelease', ':app:lintDebug', ':app:lintRelease') }
& $GradleCommand -p $androidProject --no-daemon "-PpocketAgentVersion=$Version" @tasks
if ($LASTEXITCODE -ne 0) { throw 'Android build or lint failed.' }
if ($DebugOnly) { return }
$version = $Version
$signingDirectory = if ($SigningDirectory) {
    [IO.Path]::GetFullPath($SigningDirectory)
} elseif ($env:APPDATA) {
    Join-Path $env:APPDATA 'PocketAgent/secrets/android-signing'
} else {
    throw 'APPDATA is unavailable. Set POCKETAGENT_ANDROID_SIGNING_DIRECTORY to the durable Android signing directory.'
}
$keystore = Join-Path $signingDirectory 'pocketagent.jks'
$passwordFile = Join-Path $signingDirectory 'password.dpapi'
if (!(Test-Path -LiteralPath $keystore)) {
    throw "Android signing keystore was not found at $keystore. Restore the original pocketagent.jks or set POCKETAGENT_ANDROID_SIGNING_DIRECTORY; a replacement identity will not be generated automatically."
}
if (!(Test-Path -LiteralPath $passwordFile)) {
    throw "Android signing password was not found at $passwordFile. Restore the DPAPI-protected password for the original key."
}
$securePassword = (Get-Content -LiteralPath $passwordFile -Raw).Trim() | ConvertTo-SecureString
$env:POCKETAGENT_SIGNING_PASSWORD = [System.Net.NetworkCredential]::new('', $securePassword).Password
try {
    $outputDirectory = Join-Path $projectRoot "artifacts/releases/$version/android"
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $unsigned = Join-Path $androidProject 'app/build/outputs/apk/release/app-release-unsigned.apk'
    $aligned = Join-Path $signingDirectory 'aligned.apk'
    $apk = Join-Path $outputDirectory "PocketAgent-$version-android.apk"
    $tools = Join-Path $SdkDirectory 'build-tools/35.0.0'
    & (Join-Path $tools 'zipalign.exe') -f 4 $unsigned $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed.' }
    & (Join-Path $tools 'apksigner.bat') sign --ks $keystore --ks-key-alias pocketagent --ks-pass env:POCKETAGENT_SIGNING_PASSWORD --key-pass env:POCKETAGENT_SIGNING_PASSWORD --out $apk $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }
    & (Join-Path $tools 'apksigner.bat') verify $apk
    if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
    (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $apk -Leaf) | Set-Content -LiteralPath ($apk + '.sha256')
    Write-Output "Signed Android app: $apk"
} finally {
    Remove-Item Env:POCKETAGENT_SIGNING_PASSWORD -ErrorAction SilentlyContinue
    $password = $null
}
