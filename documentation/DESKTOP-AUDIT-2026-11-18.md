# Desktop UI audit — 2026-11-18

This audit compares the current Rust desktop preview with the current .NET desktop build across the three top-level pages: Sessions, Devices, and Settings. It records both visual evidence and focused functionality checks. The screenshots are local audit artifacts and are not release assets.

Builds reviewed:

- Rust desktop 0.1.1: `rust/target/release/pocketagent-desktop.exe`
- .NET desktop 0.3.13: `src/PocketAgent.Desktop/bin/Release/net10.0-windows/PocketAgent.Desktop.exe`

## Sessions

### Rust

[Sessions screenshot](../.local/audit-screens/rust-sessions.png) · [populated-session screenshot](../.local/rust-desktop-session.png)

The page renders with the compact PocketAgent header, machine/folder context, top-right navigation, title-only conversation tabs, session actions, bounded transcript, status row, composer, and model/reasoning controls. A populated session showed separate transcript rows without the earlier overlap problem. The header/model catalog is still cramped at the bottom edge, and the empty-state capture begins with no selected conversation.

The Rust smoke test confirmed navigation, Codex readiness, relay lifecycle, device host health, and Codex failure reporting. It does not yet perform a real send/steer turn.

### .NET

[Sessions screenshot](../.local/audit-screens/dotnet-sessions-foreground.png)

The live client showed a blank white content area. UI Automation exposed the navigation buttons but no rendered page content. This was traced to a page-local `TabControl` template in `MainWindow.xaml` that replaced the application template with a content-only presenter and omitted the item host. `SelectedContent` therefore remained empty.

## Devices

### Rust

[Devices screenshot](../.local/audit-screens/rust-devices.png)

The page renders the host API state, local endpoint, pairing code, renewal action, and paired-device access section. The focused smoke test verified pairing renewal and the host health endpoint.

Gaps versus .NET include QR rendering, copy-address, connectivity diagnostics, Android installer location, and the broader device setup flow. The usage line also overlaps the page heading because the compact header is too short for all of its context lines.

### .NET

[Devices screenshot](../.local/audit-screens/dotnet-devices.png)

The live page was blank, so QR, address copy, diagnostics, APK location, pairing renewal, and paired-device controls could not be exercised in the original build.

## Settings

### Rust

[Settings screenshot](../.local/audit-screens/rust-settings.png)

The page renders Codex repository/executable fields, connection state, usage, and optional relay start/stop/enrollment controls. The smoke test verified relay start/stop, persisted relay identity, and Codex connection-error display.

Gaps versus .NET include update controls, appearance scale, startup settings, provider permissions, listener/direct-internet setup, and certificate/Wi-Fi setup. The usage line overlaps the “Desktop setup” heading for the same compact-header reason.

### .NET

[Settings screenshot](../.local/audit-screens/dotnet-settings.png)

The live page was blank even though the source defines the full settings surface. The rendering defect prevented functional interaction with those controls.

## Validation

Before the fix, the old .NET smoke test failed immediately because it searched for removed controls such as `StartButton` and `ServerStatus`.

After the fix:

```text
dotnet build src/PocketAgent.Desktop/PocketAgent.Desktop.csproj --configuration Release
Build succeeded. 0 warnings, 0 errors.

tests/desktop-render-smoke.ps1
Desktop page rendering passed.

tests/desktop-smoke.ps1
Native desktop server lifecycle and page rendering passed.
```

## Summary and follow-up

Rust is the more functional preview, but it remains short of .NET feature and layout parity. The highest-priority Rust follow-up is fixing the compact-header overlap and adding an end-to-end send/steer smoke test.

The .NET blank-page defect is fixed in commit `f91ec19`:

- Removed the page-local content-only `TabControl` template so the shared template creates `TabItem` containers and selected content.
- Enabled WPF software rendering at process startup to avoid blank client surfaces on hardware-compositor/RDP/virtual-display paths.
- Added a page-render regression test and replaced the stale desktop smoke test with current navigation/server lifecycle coverage.

The current installed .NET application will not change until a new desktop build is packaged and installed.
