# Local setup and verification

## Use the packaged native apps

Install the desktop/server package and Android APK from the same release. Extract the desktop ZIP and run the executable inside its desktop folder; the package is self-contained. Saved state remains under `%LOCALAPPDATA%/PocketAgent`, outside the release directory. Close the prior desktop before starting an upgrade so its provider process and listener are released.

## Desktop setup and permissions

Choose a default working folder in Settings. New conversations can select another existing local folder. Leave the Codex executable override empty to use the installed provider discovery order, or enter an explicit native executable path. Provider credentials remain on the desktop and are never packaged.

Permission mode is desktop-owned: inherit Codex settings, ask for workspace-write/on-request approvals, or full access. Stop, save and restart before changing the mode. The provider model picker uses the live catalog and never silently substitutes a model.

## Direct internet (IP and port)

Only the selected HTTPS chat port needs forwarding. No public CA, port 80/443, Caddy change or cloud service is required.

1. Stop the server and open Settings > Set up direct internet.
2. Select the desktop LAN address and router public IPv4. Public-IP lookup is explicit and credential-free.
3. Choose an external port and save. PocketAgent creates a persistent local certificate identity.
4. Forward that TCP port to the desktop and approve the one-time Windows Firewall rule named `PocketAgent inbound TCP <port>`.
5. Start the server, open Devices and pair the phone. Test cellular reachability with Wi-Fi disabled.

CGNAT and double NAT require router/ISP changes. Local diagnostics verify listener, TLS identity and API behavior; they cannot prove outside reachability. A changed public IP requires pairing at the new origin.

## Relay mode

Relay mode avoids an inbound firewall rule on a locked-down remote desktop. The unlocked desktop runs the relay node; the remote desktop enrolls with a one-time host identity/token and shared session key, then opens an outbound WebSocket to the relay. The relay routes encrypted request/response envelopes by host, device and request IDs and never sees conversation content or provider credentials.

Enrollment is intended to mirror device pairing:

1. The relay node generates a `pocketagent://relay` pairing string containing the relay WebSocket origin, remote-host identity/token and session key.
2. The remote server imports that string into its Relay settings and stores it in its local data directory.
3. The remote server connects outbound and appears as an attached host on the relay node.
4. Clients pair to the relay and select the attached remote host.

The current .NET prototype exposes `/relay`, accepts authenticated host/client WebSocket sessions, and has an outbound remote-host service configured through `PocketAgent__Relay__*` settings. The two-server loopback verification is:

```powershell
dotnet run --project tests/PocketAgent.RelayChecks
```

That check starts a relay node and a second remote HTTP server on separate loopback ports, sends two encrypted requests through the relay, and verifies both responses.

## Wi-Fi and device pairing

While stopped, choose a private interface under Wi-Fi setup and start the server. On Devices, generate a fresh QR or copy the complete pairing string. Android scans or pastes the payload; there is no second code. Each ticket is single-use and expires after ten minutes. Treat QR images and copied strings as temporary passwords. Revoke lost devices in Devices.

## Android servers and conversations

Android stores each server's origin, pin and bearer token in its encrypted vault. Servers keep separate tabs, drafts, usage and uncertain-delivery state. The selected conversation exposes Rename, Model/reasoning, Pin, Archive and archived-history actions. Devices are paired globally; conversation access is granted separately by the desktop. Ungranted devices cannot see a conversation.

Messages preserve user, assistant and activity roles. Commands and provider activity remain collapsed. History is bounded and cursor-paginated; refreshing does not replay a prompt. Images are fetched through the authenticated conversation image endpoint.

## Usage and scheduled messages

Usage shows the provider's five-hour and weekly windows with local reset times. If a known window is exhausted, Send queues the prompt for the reset instead of knowingly submitting a quota failure. Scheduled messages survive desktop restarts, but the desktop must be awake and running. Unknown delivery is never automatically replayed.

## Build from source

Use .NET SDK 10.0.404, JDK 17/21, Android SDK 34/build-tools 35.0.0 and the pinned Gradle wrapper. Node is test/format tooling only.

```powershell
.\scripts\Build.ps1 -Target Desktop -Publish
.\scripts\Build.ps1 -Target Android
.\scripts\Build.ps1 -Target All -Publish
```

For a release, run the platform-specific release script with an explicit version. The release gate restores/builds the solution and focused checks before packaging or publishing, then exercises the packaged updater against a disposable installation. A failed gate stops the release.

## Verification commands and limits

Focused relay and solution checks:

```powershell
dotnet build PocketAgent.slnx
dotnet run --project tests/PocketAgent.RelayChecks
dotnet run --project tests/PocketAgent.NetworkChecks -c Release
dotnet run --project tests/PocketAgent.DesktopChecks -c Release
node --test tests/relay.test.mjs
```

The relay check proves encrypted routing and two sequential requests on one machine. It does not prove router reachability, production certificate enrollment, Android relay support, or a real Codex inference turn. Those remain separate acceptance tests.

## Diagnostics

Desktop diagnostics are under `%LOCALAPPDATA%/PocketAgent/logs/desktop.log`; the embedded server writes `%LOCALAPPDATA%/PocketAgent/logs/server.log`. Logs record lifecycle, authentication outcomes, API failures, provider timing and access transitions while redacting tokens and omitting message bodies, request parameters and provider stderr. Updater handoff failures are recorded in `%TEMP%/PocketAgent-update.log`.
