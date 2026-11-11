[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
if (!(Test-Path -LiteralPath $artifactRoot)) { return }
$prefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
foreach ($item in Get-ChildItem -LiteralPath $artifactRoot) {
    if ($item.Name -notmatch '^PocketAgent-(\d+\.\d+\.\d+)(.*)$') { continue }
    $version = $Matches[1]
    $suffix = $Matches[2]
    $release = Join-Path $artifactRoot "releases/$version"
    if ($item.PSIsContainer) {
        $inUse = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($item.FullName + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }
        if ($inUse) { Write-Warning "Preserving running artifact folder at $($item.FullName). Close that app before organizing it."; continue }
        $destination = if ($suffix -eq '-desktop') { Join-Path $release 'desktop' } else { Join-Path $release "legacy/$($item.Name)" }
    } elseif ($suffix -like '-android.apk*') { $destination = Join-Path $release "android/$($item.Name)" }
    elseif ($suffix -like '-desktop.zip*') { $destination = Join-Path $release $item.Name }
    else { continue }
    $source = [IO.Path]::GetFullPath($item.FullName)
    $target = [IO.Path]::GetFullPath($destination)
    if (!$source.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or !$target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact move escaped the artifacts directory.' }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to move linked artifact: $source" }
    if (Test-Path -LiteralPath $target) { throw "Destination already exists; nothing overwritten: $target" }
    if ($PSCmdlet.ShouldProcess($source, "Move to $target")) {
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        try { if ($item.PSIsContainer) { [IO.Directory]::Move($source, $target) } else { Move-Item -LiteralPath $source -Destination $target } }
        catch { Write-Warning "Could not move $source (possibly still in use); preserved at its original path. $($_.Exception.Message)" }
    }
}
