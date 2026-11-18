param(
    [Parameter(Mandatory = $true)][string]$Executable
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = Start-Process -FilePath (Resolve-Path -LiteralPath $Executable).Path -PassThru
try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'PocketAgent Rust Desktop')
    $window = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $window; $attempt++) {
        Start-Sleep -Milliseconds 200
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
    }
    if ($null -eq $window) { throw 'Rust desktop window did not appear.' }

    $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $textCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)

    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $start = $buttons | Where-Object { $_.Current.Name -eq 'Start relay' }
    if ($null -eq $start) { throw 'Start relay button was not exposed.' }
    $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) | ForEach-Object { $_.Current.Name }
    if (!($texts -contains 'Relay is running') -or !($texts | Where-Object { $_ -like 'ws://*' })) {
        throw 'Relay did not enter the running state with a visible endpoint.'
    }
    $hostEndpoint = $texts | Where-Object { $_ -like 'http://*' } | Select-Object -First 1
    if ($null -eq $hostEndpoint) { throw 'Desktop host endpoint was not exposed.' }
    $hostResponse = Invoke-WebRequest -Uri $hostEndpoint -UseBasicParsing
    if ($hostResponse.StatusCode -ne 200 -or $hostResponse.Content -notlike '*PocketAgent Rust Host*') {
        throw 'Desktop host endpoint did not return its health response.'
    }
    $relayConfigPath = Join-Path $env:LOCALAPPDATA 'PocketAgent-rust/relay.json'
    if (!(Test-Path -LiteralPath $relayConfigPath)) { throw 'Relay identity was not persisted.' }
    $firstRelayHost = (Get-Content -LiteralPath $relayConfigPath -Raw | ConvertFrom-Json).host_id
    if ([string]::IsNullOrWhiteSpace($firstRelayHost)) { throw 'Persisted relay identity was empty.' }

    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $stop = $buttons | Where-Object { $_.Current.Name -eq 'Stop' -and $_.Current.IsEnabled } | Select-Object -First 1
    if ($null -eq $stop) { throw 'Stop button was not exposed while running.' }
    $stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) | ForEach-Object { $_.Current.Name }
    if (!($texts -contains 'Relay is stopped')) { throw 'Relay did not stop cleanly.' }

    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    ($buttons | Where-Object { $_.Current.Name -eq 'Start relay' }).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    $secondRelayHost = (Get-Content -LiteralPath $relayConfigPath -Raw | ConvertFrom-Json).host_id
    if ($firstRelayHost -ne $secondRelayHost) { throw 'Relay identity changed across restart.' }
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    ($buttons | Where-Object { $_.Current.Name -eq 'Stop' -and $_.Current.IsEnabled } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300

    $relayConnect = $buttons | Where-Object { $_.Current.Name -eq 'Connect this desktop through relay' }
    if ($null -eq $relayConnect) { throw 'Outbound relay host connection control was not exposed.' }

    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue((Get-Location).Path)
    $edits[1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('missing-codex.exe')
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    ($buttons | Where-Object { $_.Current.Name -eq 'Connect Codex' }).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) | ForEach-Object { $_.Current.Name }
    if (!($texts | Where-Object { $_ -like 'Codex error:*' })) { throw 'Codex connection failure was not surfaced in the UI.' }
    Write-Output 'Rust desktop smoke passed.'
}
finally {
    Start-Sleep -Milliseconds 200
    if (!$process.HasExited) { $process.CloseMainWindow() | Out-Null }
    Start-Sleep -Milliseconds 300
    if (!$process.HasExited) { $process.Kill() }
}
