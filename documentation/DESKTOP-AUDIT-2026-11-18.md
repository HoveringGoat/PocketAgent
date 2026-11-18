# Desktop UI audit — 2026-11-18

This audit compares the current Rust desktop preview with the current .NET desktop build across the three top-level pages: Sessions, Devices, and Settings. It records both visual evidence and focused functionality checks. The screenshots are local audit artifacts and are not release assets.

Capture correction: the first .NET screenshot set was invalid because it captured the pre-fix blank client surface. The functioning screenshots linked below were captured from the fixed 0.3.13 build using native window capture after commit `f91ec19`.

Builds reviewed:

- Rust desktop 0.1.1: `rust/target/release/pocketagent-desktop.exe`
- .NET desktop 0.3.13: `src/PocketAgent.Desktop/bin/Release/net10.0-windows/PocketAgent.Desktop.exe`

## Sessions

### Rust

[Sessions screenshot](../.local/audit-screens/rust-sessions.png) · [populated-session screenshot](../.local/rust-desktop-session.png)

The page renders with the compact PocketAgent header, machine/folder context, top-right navigation, title-only conversation tabs, session actions, bounded transcript, status row, composer, and model/reasoning controls. A populated session showed separate transcript rows without the earlier overlap problem. The header/model catalog is still cramped at the bottom edge, and the empty-state capture begins with no selected conversation.

The Rust smoke test confirmed navigation, Codex readiness, relay lifecycle, device host health, and Codex failure reporting. It does not yet perform a real send/steer turn.

### .NET

[Functioning Sessions screenshot](../.local/audit-screens/dotnet-fixed-print-sessions.png) · [superseded blank capture](../.local/audit-screens/dotnet-sessions-foreground.png)

The fixed client renders the header, navigation, session action row, empty conversation surface, status row, composer, Send action, and timer control. The original blank capture was traced to a page-local `TabControl` template in `MainWindow.xaml` that replaced the application template with a content-only presenter and omitted the item host. `SelectedContent` therefore remained empty.

## Devices

### Rust

[Devices screenshot](../.local/audit-screens/rust-devices.png)

The page renders the host API state, local endpoint, pairing code, renewal action, and paired-device access section. The focused smoke test verified pairing renewal and the host health endpoint.

Gaps versus .NET include QR rendering, copy-address, connectivity diagnostics, Android installer location, and the broader device setup flow. The usage line also overlaps the page heading because the compact header is too short for all of its context lines.

### .NET

[Functioning Devices screenshot](../.local/audit-screens/dotnet-fixed-print-devices.png) · [superseded blank capture](../.local/audit-screens/dotnet-devices.png)

The fixed page renders the phone pairing instructions, address field, Copy address, Connection diagnostics, and Android installer controls. The server was stopped for this capture, so the address and QR/pairing values are empty; the page itself is functioning.

## Settings

### Rust

[Settings screenshot](../.local/audit-screens/rust-settings.png)

The page renders Codex repository/executable fields, connection state, usage, and optional relay start/stop/enrollment controls. The smoke test verified relay start/stop, persisted relay identity, and Codex connection-error display.

Gaps versus .NET include update controls, appearance scale, startup settings, provider permissions, listener/direct-internet setup, and certificate/Wi-Fi setup. The usage line overlaps the “Desktop setup” heading for the same compact-header reason.

### .NET

[Functioning Settings screenshot](../.local/audit-screens/dotnet-fixed-print-settings.png) · [superseded blank capture](../.local/audit-screens/dotnet-settings.png)

The fixed page renders the desktop setup, startup/update settings, appearance scale, repository and Codex fields, and update actions. The original live capture was blank even though the source defined this full settings surface; the rendering defect prevented functional interaction until the `TabControl` fix.

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

Rust remains short of .NET feature and layout parity, although both builds now render their top-level pages. The highest-priority Rust follow-up is fixing the compact-header overlap and adding an end-to-end send/steer smoke test.

The .NET blank-page defect is fixed in commit `f91ec19`:

- Removed the page-local content-only `TabControl` template so the shared template creates `TabItem` containers and selected content.
- Enabled WPF software rendering at process startup to avoid blank client surfaces on hardware-compositor/RDP/virtual-display paths.
- Added a page-render regression test and replaced the stale desktop smoke test with current navigation/server lifecycle coverage.

The current installed .NET application will not change until a new desktop build is packaged and installed.
