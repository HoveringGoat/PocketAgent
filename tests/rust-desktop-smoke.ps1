param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [switch]$RequireCodex
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$previousEnvironment = @{}
$testRoot = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) '.local') ('rust-desktop-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
foreach ($name in @('LOCALAPPDATA', 'POCKETAGENT_HOST_BIND', 'POCKETAGENT_CODEX_EXECUTABLE')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$env:LOCALAPPDATA = $testRoot
$env:POCKETAGENT_HOST_BIND = '127.0.0.1:0'
$env:POCKETAGENT_CODEX_EXECUTABLE = Join-Path $testRoot 'missing-codex.exe'

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

    function Await-Desktop([scriptblock]$lookup, [string]$description) {
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $result = & $lookup
            if ($result) { return $result }
            Start-Sleep -Milliseconds 100
        }
        throw "Missing $description"
    }

    $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $textCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)

    function Assert-VisibleControls([string]$sizeLabel) {
        $windowRect = $window.Current.BoundingRectangle
        $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        foreach ($button in $buttons) {
            $rect = $button.Current.BoundingRectangle
            if ($rect.Width -le 0 -or $rect.Height -le 0 -or
                $rect.Left -lt $windowRect.Left -or $rect.Top -lt $windowRect.Top -or
                ($rect.Left + $rect.Width) -gt ($windowRect.Left + $windowRect.Width) -or
                ($rect.Top + $rect.Height) -gt ($windowRect.Top + $windowRect.Height)) {
                throw "Rust desktop control '$($button.Current.Name)' is clipped or outside the window at $sizeLabel."
            }
        }

        $controlTypes = @(
            [System.Windows.Automation.ControlType]::Button,
            [System.Windows.Automation.ControlType]::Edit,
            [System.Windows.Automation.ControlType]::ComboBox
        )
        $controls = @($window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition
        ) | Where-Object { $controlTypes -contains $_.Current.ControlType })
        for ($leftIndex = 0; $leftIndex -lt $controls.Count; $leftIndex++) {
            $left = $controls[$leftIndex].Current.BoundingRectangle
            if ($left.Width -le 0 -or $left.Height -le 0) { continue }
            for ($rightIndex = $leftIndex + 1; $rightIndex -lt $controls.Count; $rightIndex++) {
                $right = $controls[$rightIndex].Current.BoundingRectangle
                if ($right.Width -le 0 -or $right.Height -le 0) { continue }
                $overlapWidth = [Math]::Min($left.Left + $left.Width, $right.Left + $right.Width) - [Math]::Max($left.Left, $right.Left)
                $overlapHeight = [Math]::Min($left.Top + $left.Height, $right.Top + $right.Height) - [Math]::Max($left.Top, $right.Top)
                if ($overlapWidth -gt 2 -and $overlapHeight -gt 2) {
                    throw "Rust desktop controls overlap at ${sizeLabel}: '$($controls[$leftIndex].Current.Name)' and '$($controls[$rightIndex].Current.Name)'."
                }
            }
        }
    }

    function Assert-CompactHeader([string]$sizeLabel) {
        $headerTextCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        $texts = @($window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $headerTextCondition))
        $appName = $texts | Where-Object { $_.Current.Name -eq 'PocketAgent' } |
            Sort-Object { $_.Current.BoundingRectangle.Top } | Select-Object -First 1
        $location = $texts | Where-Object {
            $_.Current.Name -match '\s\|\s' -or $_.Current.Name -like '*No folder selected*'
        } | Select-Object -First 1
        $usage = $texts | Where-Object {
            $_.Current.Name -eq 'Usage unavailable' -or
            $_.Current.Name -match '5-hour|weekly|usage'
        } | Select-Object -First 1
        if ($null -eq $appName -or $null -eq $location -or $null -eq $usage) {
            throw "Compact header fields were not exposed at $sizeLabel."
        }

        $appRect = $appName.Current.BoundingRectangle
        $locationRect = $location.Current.BoundingRectangle
        $usageRect = $usage.Current.BoundingRectangle
        $topDelta = [Math]::Max(
            [Math]::Abs($appRect.Top - $locationRect.Top),
            [Math]::Abs($appRect.Top - $usageRect.Top))
        if ($topDelta -gt 3 -or $locationRect.Left -le $appRect.Left -or $usageRect.Left -le $locationRect.Left) {
            throw "Compact header fields are not aligned left-to-right at $sizeLabel."
        }
    }

    function Assert-HeaderControls([string]$sizeLabel) {
        $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $buttons = @($window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $buttonCondition))
        $sessions = $buttons | Where-Object { $_.Current.Name -eq 'Sessions' } | Select-Object -First 1
        $devices = $buttons | Where-Object { $_.Current.Name -eq 'Devices' } | Select-Object -First 1
        $settings = $buttons | Where-Object { $_.Current.Name -eq 'Settings' } | Select-Object -First 1
        $power = $buttons | Where-Object { $_.Current.Name -in @('ON', 'OFF') } | Select-Object -First 1
        if ($null -eq $sessions -or $null -eq $devices -or $null -eq $settings -or $null -eq $power) {
            throw "Header controls were not exposed at $sizeLabel."
        }

        $navRects = @($sessions, $devices, $settings) | ForEach-Object { $_.Current.BoundingRectangle }
        $powerRect = $power.Current.BoundingRectangle
        foreach ($rect in $navRects) {
            if ($rect.Width -lt 110 -or $rect.Width -gt 130 -or $rect.Height -lt 40 -or $rect.Height -gt 52) {
                throw "Header navigation control has unexpected size at $sizeLabel."
            }
        }
        if ($powerRect.Width -lt 70 -or $powerRect.Width -gt 95 -or
            $powerRect.Height -lt 28 -or $powerRect.Height -ge $navRects[0].Height) {
            throw "Header power control is not the compact pill size at $sizeLabel."
        }
    }

    $transform = $window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $transform.Resize(800, 580)
    Start-Sleep -Milliseconds 300
    Assert-VisibleControls '800x580'
    Assert-CompactHeader '800x580'
    Assert-HeaderControls '800x580'
    $transform.Resize(1150, 820)
    Start-Sleep -Milliseconds 300
    Assert-VisibleControls '1150x820'
    Assert-CompactHeader '1150x820'
    Assert-HeaderControls '1150x820'

    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $power = $buttons | Where-Object { $_.Current.Name -eq 'ON' } | Select-Object -First 1
    if ($null -eq $power) { throw 'Running server power control was not exposed.' }
    $power.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $power = Await-Desktop {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition) |
            Where-Object { $_.Current.Name -eq 'OFF' } | Select-Object -First 1
    } 'stopped host state'
    if ($null -eq $power) { throw 'Stopped server power control was not exposed.' }
    $power.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Await-Desktop {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition) |
            Where-Object { $_.Current.Name -eq 'ON' } | Select-Object -First 1
    } 'restarted host state'

    if ($RequireCodex) {
        $codexReady = $false
        for ($attempt = 0; $attempt -lt 40 -and !$codexReady; $attempt++) {
            Start-Sleep -Milliseconds 250
            $statusTexts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) | ForEach-Object { $_.Current.Name }
            if ($statusTexts -contains 'Codex is ready') { $codexReady = $true; break }
            if ($statusTexts | Where-Object { $_ -like 'Codex error:*' }) { break }
        }
        if (!$codexReady) { throw 'Default Codex connection did not reach the ready state.' }
    }

    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $start = $buttons | Where-Object { $_.Current.Name -eq 'Start relay' }
    $settings = $buttons | Where-Object { $_.Current.Name -eq 'Settings' }
    $devices = $buttons | Where-Object { $_.Current.Name -eq 'Devices' }
    $sessions = $buttons | Where-Object { $_.Current.Name -eq 'Sessions' }
    if ($null -eq $settings -or $null -eq $devices -or $null -eq $sessions) {
        throw 'Top-level Sessions, Devices, and Settings navigation was not exposed.'
    }
    $settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    Assert-VisibleControls 'Settings'
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $start = $buttons | Where-Object { $_.Current.Name -eq 'Start relay' }
    if ($null -eq $start) { throw 'Start relay button was not exposed on Settings.' }
    $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) | ForEach-Object { $_.Current.Name }
    if (!($texts -contains 'Relay is running') -or !($texts | Where-Object { $_ -like 'ws://*' })) {
        throw 'Relay did not enter the running state with a visible endpoint.'
    }
    $relayConfigPath = Join-Path $env:LOCALAPPDATA 'PocketAgent-rust/relay.json'
    if (!(Test-Path -LiteralPath $relayConfigPath)) { throw 'Relay identity was not persisted.' }
    $firstRelayConfig = Get-Content -LiteralPath $relayConfigPath -Raw | ConvertFrom-Json
    $firstRelayHost = $firstRelayConfig.host_id
    if ([string]::IsNullOrWhiteSpace($firstRelayHost)) { throw 'Persisted relay identity was empty.' }
    if ([string]::IsNullOrWhiteSpace($firstRelayConfig.client_id) -or [string]::IsNullOrWhiteSpace($firstRelayConfig.session_key)) {
        throw 'Persisted relay enrollment bundle was incomplete.'
    }
    if (!($texts | Where-Object { $_ -like '*Session key: *' })) { throw 'Relay session key was not exposed for provisioning.' }

    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $stop = $buttons | Where-Object { $_.Current.Name -eq 'Stop' -and $_.Current.IsEnabled } | Select-Object -First 1
    if ($null -eq $stop) {
        $visibleNames = ($buttons | ForEach-Object { $_.Current.Name }) -join ', '
        $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $visibleElements = ($all | ForEach-Object { "$($_.Current.ControlType.ProgrammaticName)=$($_.Current.Name)" }) -join ', '
        throw "Stop button was not exposed while running. Visible buttons: $visibleNames. Elements: $visibleElements"
    }
    $stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) | ForEach-Object { $_.Current.Name }
    if (!($texts -contains 'Relay is stopped')) { throw 'Relay did not stop cleanly.' }

    $devices = $buttons | Where-Object { $_.Current.Name -eq 'Devices' }
    $devices.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    Assert-VisibleControls 'Devices'
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) | ForEach-Object { $_.Current.Name }
    if ($null -eq ($buttons | Where-Object { $_.Current.Name -eq 'Renew pairing' })) {
        throw 'Devices page did not expose pairing controls.'
    }
    $hostEndpoint = $texts | Where-Object { $_ -like 'http://*' } | Select-Object -First 1
    if ($null -eq $hostEndpoint) { throw 'Desktop host endpoint was not exposed.' }
    $hostResponse = Invoke-WebRequest -Uri $hostEndpoint -UseBasicParsing
    if ($hostResponse.StatusCode -ne 200 -or $hostResponse.Content -notlike '*PocketAgent Rust Host*') {
        throw 'Desktop host endpoint did not return its health response.'
    }

    ($buttons | Where-Object { $_.Current.Name -eq 'Settings' }).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    ($buttons | Where-Object { $_.Current.Name -eq 'Start relay' }).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    $secondRelayHost = (Get-Content -LiteralPath $relayConfigPath -Raw | ConvertFrom-Json).host_id
    if ($firstRelayHost -ne $secondRelayHost) { throw 'Relay identity changed across restart.' }
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    ($buttons | Where-Object { $_.Current.Name -eq 'Stop' -and $_.Current.IsEnabled } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300

    ($buttons | Where-Object { $_.Current.Name -eq 'Sessions' }).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    Assert-VisibleControls 'Sessions'
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    ($buttons | Where-Object { $_.Current.Name -eq 'Settings' }).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
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
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
    $localRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) '.local'))
    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (!$fullTestRoot.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean test path outside .local: $fullTestRoot"
    }
    if (Test-Path -LiteralPath $fullTestRoot) { Remove-Item -LiteralPath $fullTestRoot -Recurse -Force }
}
