[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$releaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$archive = Join-Path $releaseDirectory "PocketAgent-$Version-desktop.zip"
$checksum = "$archive.sha256"
$packageDirectory = Join-Path $releaseDirectory 'desktop'
$updaterPath = Join-Path $packageDirectory 'PocketAgent.Updater.exe'
foreach ($path in @($archive, $checksum, $packageDirectory, $updaterPath)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Updater artifact check input was not found: $path" }
}

$root = Join-Path $projectRoot ('.local/updater-check-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'install'
$server = $null
$parent = $null
$updater = $null
function Quote-Argument([string]$value) { return '"' + $value.Replace('"', '\"') + '"' }
function Set-ProcessArguments([Diagnostics.ProcessStartInfo]$info, [string[]]$arguments) {
    # The release test runs under Windows PowerShell 5.1, whose ProcessStartInfo
    # does not expose ArgumentList. Keep the test compatible with the host shell.
    $info.Arguments = (($arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
}
function Wait-ForFile([string]$path, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $path) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}
try {
    New-Item -ItemType Directory -Force -Path $install | Out-Null
    Get-ChildItem -LiteralPath $packageDirectory -Force | Copy-Item -Destination $install -Recurse -Force

    $portListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portListener.Start(); $port = ([Net.IPEndPoint]$portListener.LocalEndpoint).Port; $portListener.Stop()
    $serverInfo = [Diagnostics.ProcessStartInfo]::new('python.exe')
    Set-ProcessArguments $serverInfo @('-m', 'http.server', $port.ToString([Globalization.CultureInfo]::InvariantCulture), '--bind', '127.0.0.1')
    $serverInfo.WorkingDirectory = $releaseDirectory
    $serverInfo.UseShellExecute = $false; $serverInfo.CreateNoWindow = $true
    $server = [Diagnostics.Process]::Start($serverInfo)
    Start-Sleep -Milliseconds 500

    $parentInfo = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
    Set-ProcessArguments $parentInfo @('-NoProfile', '-Command', 'Start-Sleep -Seconds 3')
    $parentInfo.UseShellExecute = $false; $parentInfo.CreateNoWindow = $true
    $parent = [Diagnostics.Process]::Start($parentInfo)

    $health = Join-Path $root 'health.txt'
    $log = Join-Path $root 'update.log'
    $assetUrl = "http://127.0.0.1:$port/PocketAgent-$Version-desktop.zip"
    $checksumUrl = "http://127.0.0.1:$port/PocketAgent-$Version-desktop.zip.sha256"
    $arguments = @(
        '--asset-url', $assetUrl, '--sha256-url', $checksumUrl,
        '--install-root', $install, '--restart-path', (Join-Path $install 'PocketAgent.Desktop.exe'),
        '--wait-pid', $parent.Id.ToString([Globalization.CultureInfo]::InvariantCulture), '--version', $Version,
        '--health-file', $health, '--log-file', $log
    ) | ForEach-Object { Quote-Argument $_ }
    $updaterInfo = [Diagnostics.ProcessStartInfo]::new($updaterPath)
    $updaterInfo.Arguments = $arguments -join ' '
    $updaterInfo.WorkingDirectory = Split-Path $updaterPath
    $updaterInfo.UseShellExecute = $true; $updaterInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $updater = [Diagnostics.Process]::Start($updaterInfo)
    if (!$updater.WaitForExit(120000)) { throw 'The updater did not exit within 120 seconds.' }
    $logText = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
    if ($updater.ExitCode -ne 0) { throw "The updater exited with code $($updater.ExitCode).`n$logText" }
    if ($logText.IndexOf('Update completed successfully.', [StringComparison]::Ordinal) -lt 0) {
        throw "The updater did not complete its startup health check.`n$logText"
    }
    $updatedDesktop = @(Get-Process -Name PocketAgent.Desktop -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq (Join-Path $install 'PocketAgent.Desktop.exe') } catch { $false }
    })
    if ($updatedDesktop.Count -eq 0) { throw "The updated desktop was not running after the health check.`n$logText" }
    $staging = @(Get-ChildItem (Split-Path $install) -Directory -Filter '.PocketAgent-update-*' -ErrorAction SilentlyContinue)
    if ($staging.Count -gt 0) { throw "Updater staging directories were left behind: $($staging.Name -join ', ')`n$logText" }
    if (!(Test-Path -LiteralPath (Join-Path $install 'PocketAgent.Desktop.exe'))) { throw 'The updated desktop executable is missing after replacement.' }
    Write-Output "PASS updater artifact handoff, replacement, restart health check, and staging cleanup ($Version)."
}
finally {
    if ($updater -and !$updater.HasExited) { $updater.Kill() }
    if ($parent -and !$parent.HasExited) { $parent.Kill() }
    if ($server -and !$server.HasExited) { $server.Kill() }
    $desktopPath = Join-Path $install 'PocketAgent.Desktop.exe'
    Get-Process -Name PocketAgent.Desktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $desktopPath } | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
