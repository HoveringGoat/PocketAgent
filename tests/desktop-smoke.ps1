$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$projectRoot = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$runtimeDirectory = Join-Path $projectRoot ('.local/desktop-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$settings = @{ Host = @{ RepositoryPath = $projectRoot; ListenAddress = '127.0.0.1'; Port = $port; PermissionMode = 'inherit'; CodexExecutable = (Get-Command node.exe).Source; CodexArguments = @((Join-Path $projectRoot 'tests/fixtures/codex.mjs')) } }
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'desktop.json')
$previousData = $env:POCKETAGENT_DESKTOP_DATA
$previousFixture = $env:POCKETAGENT_FIXTURE_STATE
$env:POCKETAGENT_DESKTOP_DATA = $runtimeDirectory
$env:POCKETAGENT_FIXTURE_STATE = Join-Path $runtimeDirectory 'fixture-history.json'
$binary = Join-Path $projectRoot "artifacts/PocketAgent-$version-desktop/PocketAgent.Desktop.exe"
$desktop = $null
function Wait-For($Condition, [string]$Description = 'expected state') {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) { try { if (& $Condition) { return } } catch { }; Start-Sleep -Milliseconds 100 }
    $detail = if ($script:window) { (Find-Id 'ErrorDisplay').Current.Name } else { 'No automation window found.' }
    throw "The native desktop did not reach $Description. $detail"
}
function Find-Id([string]$Id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Find-Name([string]$Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$Name)
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke-Element($Element) { $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Value($Element,[string]$Value) { $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value) }
try {
    $desktop = Start-Process -FilePath $binary -WorkingDirectory $projectRoot -PassThru
    Wait-For {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$desktop.Id)
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
        $null -ne $script:window
    } 'the native window'
    Invoke-Element (Find-Id 'StartButton')
    Wait-For { (Find-Id 'ServerStatus').Current.Name.StartsWith('Running') } 'server startup'
    Invoke-Element (Find-Name 'New conversation')
    Wait-For { (Find-Id 'ConversationTitle').Current.Name -eq 'PocketAgent' } 'new conversation'
    Set-Value (Find-Id 'Composer') 'hold'
    Invoke-Element (Find-Id 'SendButton')
    Wait-For { (Find-Id 'SendButton').Current.Name -eq 'Steer' } 'active turn'
    Set-Value (Find-Id 'Composer') 'Native desktop steering'
    Invoke-Element (Find-Id 'SendButton')
    Wait-For { $null -ne (Find-Name 'Native desktop steering') } 'steering acknowledgement'
    if ((Find-Id 'ConversationMeta').Current.BoundingRectangle.Bottom -gt (Find-Id 'Composer').Current.BoundingRectangle.Top) { throw 'Desktop context row must be above the composer.' }
    Invoke-Element (Find-Id 'InterruptButton')
    Wait-For { (Find-Id 'ConversationMeta').Current.Name.Contains('interrupted') } 'interruption'
    Invoke-Element (Find-Id 'StopButton')
    Wait-For { (Find-Id 'ServerStatus').Current.Name -eq 'Server stopped' } 'server stop'
    $script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-For { $desktop.Refresh(); $desktop.HasExited } 'window close'
    'Native desktop startup, nameless tabs, steering, context placement, interruption and shutdown passed.'
} finally {
    if ($desktop -and !$desktop.HasExited) { Stop-Process -Id $desktop.Id -ErrorAction SilentlyContinue }
    $env:POCKETAGENT_DESKTOP_DATA = $previousData
    $env:POCKETAGENT_FIXTURE_STATE = $previousFixture
}
