param([string]$BinaryDirectory)
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
$binary = if ($BinaryDirectory) { Join-Path $BinaryDirectory 'PocketAgent.Desktop.exe' } else { Join-Path $projectRoot "artifacts/releases/$version/desktop/PocketAgent.Desktop.exe" }
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
    Wait-For { $null -ne (Find-Name 'Create conversation') } 'new conversation options'
    Invoke-Element (Find-Name 'Create conversation')
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
    $unicodePrompt = [regex]::Unescape('unicode: \u201cHello\u201d \u2014 caf\u00e9 \u6f22\u5b57 \ud83d\ude00')
    $unicodeReply = [regex]::Unescape('It\u2019s ready \u2014 caf\u00e9 \u6f22\u5b57 \ud83d\ude00.')
    Set-Value (Find-Id 'Composer') $unicodePrompt
    Invoke-Element (Find-Id 'SendButton')
    Wait-For { $null -ne (Find-Name $unicodeReply) } 'intact Unicode assistant text'
    Wait-For { $null -ne (Find-Name $unicodePrompt) } 'intact Unicode prompt text'
    Invoke-Element (Find-Id 'StopButton')
    Wait-For { (Find-Id 'ServerStatus').Current.Name -eq 'Server stopped' } 'server stop'
    (Find-Name 'Settings').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Element (Find-Name ([regex]::Unescape('Set up direct internet (IP + port)\u2026')))
    Wait-For { $null -ne (Find-Name 'Detect public IP') } 'direct setup address lookup control'
    $lookupNotice = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Queries api.ipify.org for your IPv4 address. No chat, credentials or pairing code is sent. You can also enter the address manually.'))
    if ($null -eq $lookupNotice) { throw 'The address lookup must disclose its service and data boundary before use.' }
    (Find-Name 'Direct internet setup').GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-For { $desktop.Refresh(); $desktop.HasExited } 'window close'
    'Native desktop startup, nameless tabs, steering, Unicode, context placement, interruption, lookup setup disclosure and shutdown passed; no public IP lookup was performed.'
} finally {
    if ($desktop -and !$desktop.HasExited) { Stop-Process -Id $desktop.Id -ErrorAction SilentlyContinue }
    $env:POCKETAGENT_DESKTOP_DATA = $previousData
    $env:POCKETAGENT_FIXTURE_STATE = $previousFixture
}
