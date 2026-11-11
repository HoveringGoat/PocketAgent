param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [string]$ListenAddress = '127.0.0.1',
    [int]$Port = 5147,
    [string]$DataDirectory,
    [string]$CertificatePath,
    [string]$CodexExecutable,
    [ValidateSet('inherit', 'ask', 'full-access')][string]$PermissionMode = 'inherit',
    [switch]$SetupCertificate,
    [switch]$RevokeDevices
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)
$dotnetCommand = if (Test-Path '.tools/dotnet/dotnet.exe') { (Resolve-Path '.tools/dotnet/dotnet.exe').Path } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.local/nuget'
$env:PocketAgent__RepositoryPath = (Resolve-Path -LiteralPath $Repository).Path
$env:PocketAgent__ListenAddress = $ListenAddress
$env:PocketAgent__Port = $Port.ToString()
$env:PocketAgent__DataDirectory = $DataDirectory
$env:PocketAgent__CertificatePath = $CertificatePath
$env:PocketAgent__CodexExecutable = $CodexExecutable
$env:PocketAgent__PermissionMode = $PermissionMode
$extraArguments = @()
if ($SetupCertificate) { $extraArguments += '--setup' }
if ($RevokeDevices) { $extraArguments += '--revoke-devices' }
& $dotnetCommand run --project src/PocketAgent.Server --no-launch-profile -- @extraArguments
if ($LASTEXITCODE -ne 0) { throw 'PocketAgent stopped with an error.' }
