param([string]$BinaryDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$projectRoot = Split-Path $PSScriptRoot -Parent
$runtimeDirectory = Join-Path $projectRoot ('.local/desktop-render-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
$previousData = $env:POCKETAGENT_DESKTOP_DATA
$env:POCKETAGENT_DESKTOP_DATA = $runtimeDirectory
$binary = if ($BinaryDirectory) { Join-Path $BinaryDirectory 'PocketAgent.Desktop.exe' } else { Join-Path $projectRoot 'src/PocketAgent.Desktop/bin/Release/net10.0-windows/PocketAgent.Desktop.exe' }
$desktop = $null

function Find-Id([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $Id)
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-Name([string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-For([scriptblock]$Condition, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw "The desktop did not render $Description."
}

function Invoke-Button($Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

try {
    $desktop = Start-Process -FilePath $binary -ArgumentList '--repository', $projectRoot -WorkingDirectory $projectRoot -PassThru
    Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $desktop.Id)
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
        $null -ne $script:window
    } 'the native window'

    Wait-For { $null -ne (Find-Name 'Desktop setup') } 'the Settings page'
    Invoke-Button (Find-Id 'DevicesNavButton')
    Wait-For { $null -ne (Find-Name 'Connect your phone') } 'the Devices page'
    Invoke-Button (Find-Id 'SessionsNavButton')
    Wait-For { $null -ne (Find-Name 'New conversation') } 'the Sessions page'
    Invoke-Button (Find-Id 'SettingsNavButton')
    Wait-For { $null -ne (Find-Name 'Desktop setup') } 'the Settings page after navigation'

    'Desktop page rendering passed.'
}
finally {
    if ($desktop -and !$desktop.HasExited) {
        $desktop.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 300
        if (!$desktop.HasExited) { $desktop.Kill() }
    }
    $env:POCKETAGENT_DESKTOP_DATA = $previousData
}
