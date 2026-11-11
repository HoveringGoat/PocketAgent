[CmdletBinding()]
param(
    [switch]$InnoSetup,
    [switch]$GitHubCli
)

$ErrorActionPreference = 'Stop'

if (!$InnoSetup -and !$GitHubCli) {
    $InnoSetup = $true
    $GitHubCli = $true
}

$winget = Get-Command winget -ErrorAction SilentlyContinue
if (!$winget) {
    throw 'Windows Package Manager (winget) is required for automatic prerequisite installation. Install App Installer from Microsoft, then rerun this script.'
}

$packages = @()
if ($InnoSetup) { $packages += @{ Id = 'JRSoftware.InnoSetup'; Name = 'Inno Setup 6' } }
if ($GitHubCli) { $packages += @{ Id = 'GitHub.cli'; Name = 'GitHub CLI' } }

foreach ($package in $packages) {
    Write-Output "Installing or updating $($package.Name) ($($package.Id))..."
    & $winget.Source install --id $package.Id --exact --source winget --force --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget could not install or repair $($package.Name). If it reports a stale package registration, rerun this command with --force or uninstall the package from Windows Apps first."
    }
}

Write-Output 'Release prerequisites are installed. Open a new PowerShell window if PATH changes are not visible.'
