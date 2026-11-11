param(
    [string]$SdkDirectory = $env:ANDROID_HOME,
    [string]$JavaDirectory = $env:JAVA_HOME,
    [string]$GradleCommand,
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
$tasks = if ($DebugOnly) { @(':app:assembleDebug', ':app:lintDebug') } else { @(':app:assembleDebug', ':app:assembleRelease', ':app:lintDebug', ':app:lintRelease') }
& $GradleCommand -p $androidProject --no-daemon @tasks
if ($LASTEXITCODE -ne 0) { throw 'Android build or lint failed.' }
if ($DebugOnly) { return }
$version = ([xml](Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$signingDirectory = Join-Path $projectRoot '.local/android-signing'
New-Item -ItemType Directory -Force -Path $signingDirectory | Out-Null
$keystore = Join-Path $signingDirectory 'pocketagent.jks'
$passwordFile = Join-Path $signingDirectory 'password.dpapi'
if (!(Test-Path -LiteralPath $keystore)) {
    if (Test-Path -LiteralPath $passwordFile) { throw 'Signing password exists without its key. Recover the original key before generating another identity.' }
    $passwordBytes = New-Object byte[] 32
    $passwordGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
    $passwordGenerator.GetBytes($passwordBytes)
    $passwordGenerator.Dispose()
    $password = [Convert]::ToBase64String($passwordBytes)
    $password | ConvertTo-SecureString -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordFile
    $env:POCKETAGENT_SIGNING_PASSWORD = $password
    & (Join-Path $JavaDirectory 'bin/keytool.exe') -genkeypair -keystore $keystore -storepass:env POCKETAGENT_SIGNING_PASSWORD -keypass:env POCKETAGENT_SIGNING_PASSWORD -alias pocketagent -keyalg RSA -keysize 3072 -validity 7300 -dname 'CN=PocketAgent Local Distribution' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the local Android signing identity.' }
} else {
    if (!(Test-Path -LiteralPath $passwordFile)) { throw 'Restore the password file for the existing Android signing key.' }
    $securePassword = (Get-Content -LiteralPath $passwordFile -Raw).Trim() | ConvertTo-SecureString
    $env:POCKETAGENT_SIGNING_PASSWORD = [System.Net.NetworkCredential]::new('', $securePassword).Password
}
try {
    $outputDirectory = Join-Path $projectRoot 'artifacts'
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $unsigned = Join-Path $androidProject 'app/build/outputs/apk/release/app-release-unsigned.apk'
    $aligned = Join-Path $projectRoot '.local/android-signing/aligned.apk'
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
