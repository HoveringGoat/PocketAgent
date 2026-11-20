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
  # The transcript is rendered as custom Slint blocks and is not exposed as
  # a standalone automation Text node. The created tab is the stable UI
  # assertion that the provider thread was created and selected.
  $null=Await-Audit { Find-Audit 'PocketAgent' } 'new fixture conversation'
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
  if ($sendButton.Current.BoundingRectangle.Width -lt 110 -or $sendButton.Current.BoundingRectangle.Width -gt 145) {
    throw 'Send button is not the .NET-matched action width.'
  }
  if ($scheduleButton.Current.BoundingRectangle.Width -gt 70) {
    throw 'Schedule action is still stretched.'
  }
  # The composer is the tall multiline edit; scheduling is configured in a dialog.
  $editCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)
  $inputs=@($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,$editCondition) | Sort-Object { $_.Current.BoundingRectangle.Top },{ $_.Current.BoundingRectangle.Left })
  $composer=($inputs | Sort-Object { $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $composer.SetValue('queued fixture text')
  Click-Audit 'Schedule message'
  $null=Await-Audit { Find-Audit 'Schedule message' } 'schedule dialog'
  $null=Await-Audit { Find-Audit 'In 1 hour' } 'schedule choices'
  if ($composer.Current.Value -ne 'queued fixture text') {
    throw "Opening schedule dialog lost its draft: '$($composer.Current.Value)'."
  }
  Click-Audit 'In 1 hour'
  Click-Audit 'Confirm schedule'
  $null=Await-Audit { Find-Audit 'Cancel scheduled message' } 'queued message'
  if ($composer.Current.Value) { throw 'Persisted schedule did not clear the draft' }
  Click-Audit 'Cancel scheduled message'
  $null=Await-Audit { -not (Find-Audit 'Cancel scheduled message') } 'canceled pending job'
  $composer.SetValue('unicode:reply')
  Click-Audit 'Send'
  $reply = $null
  for ($attempt = 0; $attempt -lt 65 -and $null -eq $reply; $attempt++) {
    if (Test-Path -LiteralPath $env:POCKETAGENT_FIXTURE_STATE) {
      $history = Get-Content -LiteralPath $env:POCKETAGENT_FIXTURE_STATE -Raw | ConvertFrom-Json
      $completedTurns = @($history.PSObject.Properties | ForEach-Object { $_.Value.turns })
      $reply = $completedTurns |
        ForEach-Object { $_.items } |
        Where-Object { $_.type -eq 'agentMessage' -and $_.text -like '*ready*' } |
        Select-Object -First 1
    }
    if ($null -eq $reply) { Start-Sleep -Milliseconds 100 }
  }
  if ($null -eq $reply -or $reply.text -notlike '*It*ready*') {
    throw 'A completed Codex reply was not persisted by the desktop workflow.'
  }
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
  if ($turns.Count -ne 2 -or $turns[0].status -ne 'completed' -or $turns[1].status -ne 'interrupted' -or $turns[1].items.Count -ne 2) {
    throw 'Send, reply, steer, and Stop must operate on the expected provider turns.'
  }
  'Release fixture UI passed: browse, create, pin, schedule dialog, cancel, send, reply, steer, and stop.'
} finally {
    if ($p -and !$p.HasExited) {
        $p.CloseMainWindow() | Out-Null
        if (!$p.WaitForExit(1500)) { $p.Kill() }
    }
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
    $localRoot = [IO.Path]::GetFullPath($local)
    $fullFixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
    if (!$fullFixtureRoot.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean fixture path outside .local: $fullFixtureRoot"
    }
    if (Test-Path -LiteralPath $fullFixtureRoot) { Remove-Item -LiteralPath $fullFixtureRoot -Recurse -Force }
}
