param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $Executable).Path
$projectRoot = Split-Path $PSScriptRoot -Parent
$local = (New-Item -ItemType Directory -Path (Join-Path $projectRoot '.local') -Force).FullName
$previousEnvironment = @{}
foreach ($name in @('LOCALAPPDATA', 'POCKETAGENT_CODEX_EXECUTABLE', 'POCKETAGENT_FIXTURE_STATE', 'SLINT_BACKEND', 'POCKETAGENT_HOST_BIND')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$p = $null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$fixtureRoot = (New-Item -ItemType Directory -Path (Join-Path $local ('rust-sessions-fixture-' + [guid]::NewGuid().ToString('N')))).FullName
$fixtureBin = (New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'node_modules/@openai/codex/bin') -Force).FullName
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/codex.mjs') -Destination (Join-Path $fixtureBin 'codex.js')
function Await-Audit([scriptblock]$lookup, [string]$description) {
    for ($i = 0; $i -lt 65; $i++) {
        $result = & $lookup
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    }
    throw "Missing $description"
}

try {
  $env:LOCALAPPDATA = $fixtureRoot
  $env:POCKETAGENT_CODEX_EXECUTABLE = Join-Path $fixtureRoot 'codex.ps1'
  $env:POCKETAGENT_FIXTURE_STATE = Join-Path $fixtureRoot 'fixture-state.json'
  $env:SLINT_BACKEND = 'winit-software'
  $env:POCKETAGENT_HOST_BIND = '127.0.0.1:0'
  $p = Start-Process -FilePath $binary -WorkingDirectory $projectRoot -PassThru
  $processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
  $titleCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'PocketAgent Rust Desktop')
  $cond = [System.Windows.Automation.AndCondition]::new($processCondition, $titleCondition)
  $window = Await-Audit { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond) } 'fixture window'
  function Find-Audit([string]$label) {
    $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $label } | Select-Object -First 1
  }
  function Click-Audit([string]$label) {
    (Await-Audit { Find-Audit $label } $label).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  }
  $null=Await-Audit { Find-Audit 'Codex is ready' } 'fixture readiness'
  Click-Audit 'New conversation'
  Click-Audit 'Browse'
  Click-Audit 'Create'
  $null=Await-Audit { Find-Audit 'New conversation ready' } 'new fixture conversation'
  Click-Audit 'Pin conversation'
  $null=Await-Audit { Find-Audit 'Pinned*PocketAgent' } 'pinned tab'
  foreach ($actionName in @('New conversation', 'Rename conversation', 'Model and reasoning', 'Manage conversation access', 'Pin conversation', 'Archive conversation', 'Show archived conversations')) {
    $action = Await-Audit { Find-Audit $actionName } $actionName
    if ($action.Current.BoundingRectangle.Width -gt 70) {
      throw "Session action '$actionName' is still oversized."
    }
  }
  Click-Audit 'Rename conversation'
  $null=Await-Audit { Find-Audit 'Save name' } 'rename dialog'
  Click-Audit 'Cancel rename'
  Click-Audit 'Model and reasoning'
  $null=Await-Audit { Find-Audit 'Apply model' } 'model dialog'
  Click-Audit 'Cancel model changes'
  $sendButton = Await-Audit { Find-Audit 'Send' } 'send button'
  $scheduleButton = Await-Audit { Find-Audit 'Schedule message' } 'schedule button'
  $reviewButton = Await-Audit { Find-Audit 'Review scheduled messages' } 'scheduled review button'
  if ($sendButton.Current.BoundingRectangle.Width -lt 110 -or $sendButton.Current.BoundingRectangle.Width -gt 145) {
    throw 'Send button is not the .NET-matched action width.'
  }
  if ($scheduleButton.Current.BoundingRectangle.Width -gt 70 -or $reviewButton.Current.BoundingRectangle.Width -gt 70) {
    throw 'Schedule or scheduled-review action is still stretched.'
  }
  Click-Audit 'Review scheduled messages'
  $null=Await-Audit { Find-Audit 'Pending messages for this conversation' } 'pending job view'
  # The composer is the tall multiline edit. The delay is the last single-line edit.
  $editCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)
  $inputs=@($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,$editCondition) | Sort-Object { $_.Current.BoundingRectangle.Top },{ $_.Current.BoundingRectangle.Left })
  $composer=($inputs | Sort-Object { $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $delay=($inputs | Where-Object { $_.Current.BoundingRectangle.Height -lt 80 } | Sort-Object { $_.Current.BoundingRectangle.Top },{ $_.Current.BoundingRectangle.Left } | Select-Object -Last 1).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $composer.SetValue('queued fixture text')
  $delay.SetValue('bad')
  Click-Audit 'Schedule message'
  $null=Await-Audit { Find-Audit 'Codex error:*Schedule delay must be seconds*' } 'rejected schedule'
  if ($composer.Current.Value -ne 'queued fixture text') {
    $editInfo = ($inputs | ForEach-Object { "$($_.Current.Name)=$($_.Current.Value) h=$($_.Current.BoundingRectangle.Height)" }) -join ', '
    throw "Rejected schedule lost its draft: '$($composer.Current.Value)'. Edits: $editInfo"
  }
  $delay.SetValue('1800')
  Click-Audit 'Schedule message'
  $null=Await-Audit { Find-Audit 'Cancel scheduled message' } 'queued message'
  if ($composer.Current.Value) { throw 'Persisted schedule did not clear the draft' }
  Click-Audit 'Cancel scheduled message'
  $null=Await-Audit { -not (Find-Audit 'Cancel scheduled message') } 'canceled pending job'
  $composer.SetValue('hold')
  Click-Audit 'Send'
  $null=Await-Audit { -not $composer.Current.Value } 'accepted send acknowledgement'
  # Let the running turn cross a worker timer tick before steering it.
  Start-Sleep -Milliseconds 1200
  $composer.SetValue('steer fixture text')
  Click-Audit 'Send'
  $null=Await-Audit { -not $composer.Current.Value } 'accepted steer acknowledgement'
  Click-Audit 'Stop'
  $null=Await-Audit { Find-Audit 'Codex is ready*' } 'stopped turn'
  $history = Get-Content -LiteralPath $env:POCKETAGENT_FIXTURE_STATE -Raw | ConvertFrom-Json
  $turns = @($history.PSObject.Properties | ForEach-Object { $_.Value.turns })
  if ($turns.Count -ne 1 -or $turns[0].status -ne 'interrupted' -or $turns[0].items.Count -ne 2) {
    throw 'Send, steer, and Stop must operate on one provider turn.'
  }
  'Release fixture UI passed: browse, create, pin, queue, rejected schedule draft, cancel, send, steer, and stop.'
} finally {
    if ($p -and !$p.HasExited) {
        $p.CloseMainWindow() | Out-Null
        if (!$p.WaitForExit(1500)) { $p.Kill() }
    }
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
}
