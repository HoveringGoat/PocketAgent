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

Relay mode lets a locked-down desktop reach the relay node over an outbound WebSocket. Use a network address the remote desktop can reach for the relay node. For LAN use, configure HTTPS under **Settings > Advanced certificate settings > Set up Wi-Fi connection**. For internet use, follow the [direct internet](#direct-internet-ip-and-port) setup on the relay node and forward its HTTPS port. A loopback listener is useful when testing on one computer.

1. On the relay desktop, stop the server, open **Settings > Relay**, check **Enable relay node on this desktop**, and save. Turn the server on. **Relay node listening** shows the WebSocket address.
2. Select **Copy host enrollment** and transfer the complete string privately to the remote desktop. It contains the host credential, encryption key and, for a directly hosted HTTPS endpoint, the relay certificate pin.
3. On the remote desktop, stop the server, paste the string into **Upstream relay enrollment**, and save. Keep the default loopback listener for relay-only access. Turn the server on and look for **Upstream relay connected**. If it shows **Retrying**, check reachability to the displayed node address and confirm the enrollment came from that node.
4. For a .NET relay client, select **Copy client setup** on the node. Pass the endpoint, target host ID, client ID/token, session key and optional certificate pin to `RelayApplicationClient.ConnectAsync`.
5. On the connected remote desktop, select **Copy device pairing ticket**. Submit the ticket as `code`, with a `deviceName`, in a JSON `POST /api/devices/pair` over the relay. The ticket expires after ten minutes or one use. Use the returned token in the `Authorization: Bearer` header for protected API operations. Android currently uses direct pairing.

Each node provisions one reusable host/client enrollment. Save the settings with the relay checkbox cleared to disable the node on its next start. Clear **Upstream relay enrollment** and save on the remote desktop to disable its outbound connection on its next start.

The server can also be configured through `PocketAgent__Relay__*` settings. A network relay uses `wss://`. Without a configured pin, the client uses platform certificate trust. Plain `ws://` is supported for loopback. Two focused checks cover desktop and transport behavior:

```powershell
dotnet run --project tests/PocketAgent.DesktopChecks -- --relay-setup
dotnet run --project tests/PocketAgent.RelayChecks
```

The desktop check starts the real relay node and outbound service over pinned WSS on separate loopback ports. It verifies the remote API, restart and reconnection, and Settings at minimum and larger window sizes. The transport check exercises two sequential encrypted requests through the relay. Test reachability from the remote network after configuring a public endpoint.

## Wi-Fi and device pairing

While stopped, choose a private interface under Wi-Fi setup and start the server. On Devices, generate a fresh QR or copy the complete pairing string. Android scans or pastes the payload; there is no second code. Each ticket is single-use and expires after ten minutes. Treat QR images and copied strings as temporary passwords. Revoke lost devices in Devices.

## Android servers and conversations

Android stores each server's origin, pin and bearer token in its encrypted vault. Servers keep separate tabs, drafts, usage and uncertain-delivery state. The selected conversation exposes Rename, Model/reasoning, Pin, Archive and archived-history actions. Devices are paired globally; conversation access is granted separately by the desktop. Ungranted devices cannot see a conversation.

Messages preserve user, assistant and activity roles. Commands and provider activity remain collapsed. History is bounded and cursor-paginated; refreshing does not replay a prompt. Images are fetched through the authenticated conversation image endpoint.

## Desktop conversations

Open **Edit conversation** from the pencil icon beside the conversation tabs to rename or pin the conversation, or manage device access. The status bar shows elapsed work, model, and reasoning. The Stop icon is available during an active turn.

Scroll up to read earlier messages while work continues and select **Jump to latest** to follow new output again. Expand an activity group to see the individual actions. Agent replies use Markdown. Select an image thumbnail to open the image at a larger size.

The focused desktop conversation check uses the native WPF window and captures layouts at 800×580 and 1150×820 with Large scaling.

```powershell
dotnet run --project tests/PocketAgent.DesktopChecks -c Release -- --conversation-ui
```

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
