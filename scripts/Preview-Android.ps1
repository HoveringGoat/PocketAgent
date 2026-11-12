[CmdletBinding()]
param(
    [string]$AvdName = 'PuzzleApp_Pixel6_API36',
    [string]$Serial,
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdkDirectory = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
$adb = Join-Path $sdkDirectory 'platform-tools/adb.exe'
$emulator = Join-Path $sdkDirectory 'emulator/emulator.exe'
$apk = Join-Path $projectRoot 'src/PocketAgent.Android/app/build/outputs/apk/debug/app-debug.apk'
$previewDirectory = Join-Path $projectRoot '.local/previews/android'
$capture = Join-Path $previewDirectory ('conversation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.png')

if (!(Test-Path -LiteralPath $adb)) { throw "Android adb was not found at $adb." }
if (!(Test-Path -LiteralPath $emulator)) { throw "Android emulator was not found at $emulator." }

& (Join-Path $PSScriptRoot 'Build-Android.ps1') -DebugOnly
if ($LASTEXITCODE -ne 0) { throw 'The Android debug build failed.' }

if (!$Serial) {
    $devices = @(& $adb devices | Where-Object { $_ -match '^emulator-\d+\s+device$' })
    if ($devices.Count -gt 0) {
        $Serial = ($devices[0] -split '\s+')[0]
    } else {
        $process = Start-Process -FilePath $emulator -ArgumentList @('-avd', $AvdName, '-no-snapshot', '-no-boot-anim') -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddMinutes(3)
        do {
            Start-Sleep -Seconds 2
            $Serial = @(& $adb devices | Where-Object { $_ -match '^emulator-\d+\s+device$' } | ForEach-Object { ($_ -split '\s+')[0] }) | Select-Object -First 1
        } while (!$Serial -and (Get-Date) -lt $deadline)
        if (!$Serial) { throw "The emulator did not become available. Process id: $($process.Id)." }
    }
}

$bootDeadline = (Get-Date).AddMinutes(3)
do {
    $booted = (& $adb -s $Serial shell getprop sys.boot_completed 2>$null).Trim()
    if ($booted -eq '1') { break }
    Start-Sleep -Seconds 2
} while ((Get-Date) -lt $bootDeadline)
if ($booted -ne '1') { throw "Android emulator $Serial did not finish booting." }

& $adb -s $Serial install -r $apk | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The debug APK could not be installed.' }
& $adb -s $Serial shell am force-stop dev.pocketagent
& $adb -s $Serial shell am start -n dev.pocketagent/.PreviewActivity | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The Android preview activity could not be launched.' }
$activityDeadline = (Get-Date).AddSeconds(30)
do {
    $focusedActivity = (& $adb -s $Serial shell dumpsys activity activities 2>$null | Out-String)
    if ($focusedActivity -match 'PreviewActivity') { break }
    Start-Sleep -Seconds 1
} while ((Get-Date) -lt $activityDeadline)
if ($focusedActivity -notmatch 'PreviewActivity') { throw 'PreviewActivity did not become the focused Android activity.' }
Start-Sleep -Seconds 1

New-Item -ItemType Directory -Force -Path $previewDirectory | Out-Null
$quotedAdb = '"' + $adb + '"'
$quotedCapture = '"' + $capture + '"'
& cmd.exe /c "$quotedAdb -s $Serial exec-out screencap -p > $quotedCapture"
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $capture)) { throw 'The Android preview screenshot could not be captured.' }
Write-Output "Android preview captured: $capture"
if ($Open) { Start-Process -FilePath $capture }
