param([string]$BinaryDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$projectRoot = Split-Path $PSScriptRoot -Parent
$runtimeDirectory = Join-Path $projectRoot ('.local/desktop-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$settings = @{
    Host = @{
        RepositoryPath = $projectRoot
        ListenAddress = '127.0.0.1'
        Port = $port
        PermissionMode = 'inherit'
        CodexExecutable = (Get-Command node.exe).Source
        CodexArguments = @((Join-Path $projectRoot 'tests/fixtures/codex.mjs'))
    }
}
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'desktop.json')
$previousData = $env:POCKETAGENT_DESKTOP_DATA
$previousFixture = $env:POCKETAGENT_FIXTURE_STATE
$env:POCKETAGENT_DESKTOP_DATA = $runtimeDirectory
$env:POCKETAGENT_FIXTURE_STATE = Join-Path $runtimeDirectory 'fixture-history.json'
$binary = if ($BinaryDirectory) { Join-Path $BinaryDirectory 'PocketAgent.Desktop.exe' } else { Join-Path $projectRoot 'artifacts/releases/desktop/PocketAgent.Desktop.exe' }
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
    throw "The native desktop did not reach $Description."
}

function Invoke-Button($Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-ToggleState($Element) {
    return $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
}

try {
    $desktop = Start-Process -FilePath $binary -WorkingDirectory $projectRoot -PassThru
    Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $desktop.Id)
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
        $null -ne $script:window
    } 'the native window'

    $toggle = Find-Id 'ServerToggle'
    $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Wait-For { (Get-ToggleState $toggle) -eq 'On' } 'server startup'
    Wait-For { $null -ne (Find-Name 'New conversation') } 'the rendered Sessions page'

    Invoke-Button (Find-Id 'DevicesNavButton')
    Wait-For { $null -ne (Find-Name 'Connect your phone') } 'the rendered Devices page'
    Invoke-Button (Find-Id 'SettingsNavButton')
    Wait-For { $null -ne (Find-Name 'Desktop setup') } 'the rendered Settings page'
    Invoke-Button (Find-Id 'SessionsNavButton')
    Wait-For { $null -ne (Find-Name 'New conversation') } 'the rendered Sessions page after navigation'

    $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Wait-For { (Get-ToggleState $toggle) -eq 'Off' } 'server shutdown'
    'Native desktop server lifecycle and page rendering passed.'
}
finally {
    if ($desktop -and !$desktop.HasExited) { Stop-Process -Id $desktop.Id -Force -ErrorAction SilentlyContinue }
    $env:POCKETAGENT_DESKTOP_DATA = $previousData
    $env:POCKETAGENT_FIXTURE_STATE = $previousFixture
}
