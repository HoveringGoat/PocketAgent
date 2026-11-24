# Local setup and verification

## Use the packaged native apps

Install the desktop/server package and Android APK from the same release. The desktop package is self-contained and stores saved state under `%LOCALAPPDATA%/PocketAgent`, outside the release directory. Close the prior desktop before starting an upgrade so its provider process and listener are released.

## Desktop setup and permissions

Choose a default working folder in Settings. New conversations can select another existing local folder. Leave the Codex executable override empty to use the installed provider discovery order, or enter an explicit native executable path. The resolver prefers the newest stable VS Code Codex extension before a native CLI on `PATH`, so use the override when the CLI is the current installation you intend to run. Provider credentials remain on the desktop and are never packaged.

Settings includes **Codex Platform API key**. Paste a replacement while the server is stopped, choose the environment-variable name used by the provider (normally `OPENAI_API_KEY`), and select **Save API key**. PocketAgent stores the value in Windows Credential Manager and injects it only when launching the selected Codex executable. It is never saved in `desktop.json`, logged, copied, or included in relay/mobile data. **Remove saved key** disables the PocketAgent override and returns to the inherited environment or Codex-managed sign-in store. After changing the key, start the server and use **Check Codex connection** followed by **Test inference access**.

The connection check identifies the running executable, authentication mode, credential source, configured provider/model and model catalog without running inference. **Test inference access** performs one real provider request and may consume usage; it is the only check that proves the key's project can use the selected model. If it reports that the project tied to the API key is unauthorized, grant the project model access or replace the key with one from the intended project, restart Codex and run the test again. PocketAgent cannot change OpenAI project permissions. Usage limits may be shown as unavailable for Platform API-key authentication because the ChatGPT-only rate-limit method is not applicable; PocketAgent stops polling that method and offers a manual retry.

The **Update the VS Code Codex extension when PocketAgent starts** setting is off by default. When enabled, PocketAgent checks for an extension update before starting its local server so Codex can launch directly on the new executable. `CodexExtensionUpdateService` queries the VS Code Marketplace by extension name and product target, reads version and pre-release metadata, and selects a stable version for the host's Windows architecture. The **Check for Codex extension updates now** button performs the same install check manually regardless of the startup setting. Both update checks immediately show an animated progress bar and disable their check button. The Codex status shows the target version during installation. Empty or malformed Marketplace responses appear in Settings and `desktop.log`. Installation is deferred while a conversation is active, and explicit Codex executable overrides take precedence.

When opening Settings or finishing an extension check, PocketAgent compares the running Codex executable with the version available on disk. If a newer one is installed, Settings shows both versions and **Restart using updated Codex version**, including for extensions updated directly through VS Code. With startup auto-update enabled, PocketAgent also attempts this switch if the server is already running. The switch restarts the local server with its current configuration. It reserves the idle server before stopping so new conversation turns can retry once the server returns. If work is active, the button becomes available after it finishes. The status confirms the replacement executable after Codex initializes.

Permission mode is desktop-owned: inherit Codex settings, ask for workspace-write/on-request approvals, or full access. Stop, save and restart before changing the mode. The provider model picker uses the live catalog and never silently substitutes a model.

## Direct internet (IP and port)

Only the selected HTTPS chat port needs forwarding. No public CA, port 80/443, Caddy change or cloud service is required.

1. Stop the server and open Settings > Set up direct internet.
2. Select the desktop LAN address and router public IPv4. Public-IP lookup is explicit and credential-free.
3. Confirm the selected listener port; PocketAgent uses this same port for the public address and creates a persistent local certificate identity.
4. Forward that TCP port to the desktop and approve the one-time Windows Firewall rule named `PocketAgent inbound TCP <port>`.
5. Start the server, open Devices and pair the phone. Test cellular reachability with Wi-Fi disabled.

CGNAT and double NAT require router/ISP changes. Local diagnostics verify listener, TLS identity and API behavior; they cannot prove outside reachability. A changed public IP requires pairing at the new origin.

## Relay mode

Relay mode lets a locked-down desktop reach the relay node over an outbound WebSocket. Use a network address the remote desktop can reach for the relay node. For LAN use, configure HTTPS under **Settings > Advanced certificate settings > Set up Wi-Fi connection**. For internet use, follow the [direct internet](#direct-internet-ip-and-port) setup on the relay node and forward its HTTPS port. A loopback listener is useful when testing on one computer.

1. On the relay desktop, turn the server **OFF** and choose **Host relay node** under **Settings > Relay mode**. Save settings, then turn the server **ON**. The host panel shows the WebSocket address and warns when that address works only on the same computer.
2. Select **Copy client enrollment** and transfer the complete string privately to the desktop that runs Codex. It contains the host credential, encryption key, and the certificate pin for directly hosted HTTPS.
3. On the Codex desktop, turn the server **OFF** and choose **Connect to relay**. Paste the string and select **Save enrollment**. Turn the server **ON** and look for **Connected to relay**. Client mode uses a loopback listener and an outbound relay connection, so it starts without an inbound firewall prompt even if LAN or direct internet settings are saved. If it shows **Retrying**, check reachability to the node address and confirm the enrollment came from that node.
4. On the connected Codex desktop, open **Devices** to show the relay pairing QR. In Android PocketAgent, open **Add server**, scan the QR, and tap **Connect**. You can also copy and paste the full pairing string. Use desktop 0.3.19 or later on both desktops and Android 0.4.6 or later. The phone reaches the relay node's HTTPS address. The connected desktop remains responsible for device access and conversation permissions.
5. For custom .NET clients, the node still offers **Copy .NET client credentials** and the connected desktop offers **Copy device pairing ticket** in their respective advanced panels. Submit the ticket as `code`, with a `deviceName`, in a JSON `POST /api/devices/pair`. The ticket expires after ten minutes or one use. The returned token authorizes protected API operations.

**Off** is the default and hides both setup panels. Saving **Off** pauses relay use and retains saved credentials. To change or remove a client enrollment, stop the server and choose **Replace enrollment** or **Unenroll** in **Connect to relay**. Unenrolling removes the saved upstream credentials and leaves the client ready to enroll again. Each node provisions one reusable host/client enrollment.

The server can also be configured through `PocketAgent__Relay__*` settings. A network relay uses `wss://`. Without a configured pin, the client uses platform certificate trust. Plain `ws://` is supported for loopback. Two focused checks cover desktop and transport behavior:

```powershell
dotnet run --project tests/PocketAgent.DesktopChecks -- --relay-setup
dotnet run --project tests/PocketAgent.RelayChecks
```

The desktop check starts the real relay node and outbound service over pinned WSS on separate loopback ports. It verifies the remote API, restart and reconnection, and Settings at minimum and larger window sizes. The transport check exercises two sequential encrypted requests through the relay. Test reachability from the remote network after configuring a public endpoint.

To verify phone pairing on an isolated emulator, install the Android debug APK, set `POCKETAGENT_TEST_ANDROID` to the emulator serial, and run:

```powershell
dotnet run --project tests/PocketAgent.DesktopChecks -- --relay-setup --android-relay
```

The check uses ADB reverse, decodes the Windows-generated QR in the Android app, pairs through Add server, reloads the stored connection, reconnects, and checks device revocation.

## Wi-Fi and device pairing

While stopped, choose a private interface under Wi-Fi setup and start the server. On Devices, generate a fresh QR or copy the complete pairing string. Android scans or pastes the payload; there is no second code. Each ticket is single-use and expires after ten minutes. Treat QR images and copied strings as temporary passwords. Revoke lost devices in Devices.

## Android servers and conversations

Android stores each server's origin, pin and bearer token in its encrypted vault. Servers keep separate tabs, drafts, usage and uncertain-delivery state. The selected conversation exposes Rename, Model/reasoning, Pin, Archive, Reset and archived-history actions. Reset confirms before creating a clean conversation with the same name, folder, model, reasoning, pin state and device access; the previous transcript is archived rather than deleted. Devices are paired globally; conversation access is granted separately by the desktop. Ungranted devices cannot see a conversation.

Messages preserve user, assistant and activity roles. Commands and provider activity remain collapsed. History is bounded and cursor-paginated; refreshing does not replay a prompt. Images are fetched through the authenticated conversation image endpoint.

## Desktop conversations

Open **Edit conversation** from the pencil icon beside the conversation tabs to rename or pin the conversation, or manage device access. The **Reset conversation** icon starts a clean Codex thread with the same name, folder, model, reasoning, pin state and device access after confirmation, then archives the old transcript. Reset is unavailable while work or scheduled delivery is active. The status bar shows elapsed work, model, and reasoning. The Stop icon is available during an active turn.

When the desktop starts its local server, it opens the Sessions page and restores the last conversation selected on that desktop after the session list loads. If that conversation was archived, removed, or unavailable during configuration migration, the desktop opens the non-archived conversation with the newest activity instead. The desktop remembers its normal window position and size, restores maximized state, and ignores saved bounds that are no longer visible on any monitor.

On desktop or Android, open **Model and reasoning** on the conversation. The two dropdowns show the effective model and reasoning and update the available efforts when you choose another model. Choose **OK** to save both settings for future turns and scheduled work. Close the desktop dialog or use Android Back to leave the saved settings as they were. The conversation status shows the actual values when Codex reports them and **Unavailable** when a value cannot be resolved. Android 0.4.10 uses the combined save endpoint on desktop/server 0.3.42 or later.

Scroll up to read earlier messages while work continues and select **Jump to latest** to follow new output again. Expand an activity group to see the individual actions. Select text in messages, formatted agent replies, activity details, or conversation errors and press **Ctrl+C** to copy it. Select an image thumbnail to open the image at a larger size.

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

For a release, run the platform-specific release script with an explicit version. The release gate restores/builds the solution and focused checks before packaging or publishing, then exercises the packaged updater against a disposable installation. The test installer is compiled from the same setup script and payload with a separate app ID, no Windows uninstall registration, and no shortcuts. The release uploads the production installer and checksums. A failed gate stops the release.

Android release signing uses `%APPDATA%\PocketAgent\secrets\android-signing\pocketagent.jks` and its `password.dpapi` file by default. Set `POCKETAGENT_ANDROID_SIGNING_DIRECTORY` to use another durable location. The keystore contains the Android app's long-lived private signing identity; back up both files securely. The release build refuses to generate a replacement key when either file is missing, because a different identity prevents Android upgrades and requires users to uninstall the previous app.

With **Check for updates on startup** enabled, a newer desktop release with a checksum is offered when conversations become idle. **Install update** opens the download window. **Later** and closing the prompt leave the update available in Settings and suppress repeat offers for that version until the next launch. With automatic installation enabled, the download starts once conversations are idle. A minimized window defers manual approval until it is restored. Manual and periodic checks continue to report availability in Settings.

Desktop updates keep the main window open under a modal while the installer downloads and its SHA-256 checksum is verified. The modal shows a percentage when the download server supplies a content length. `UpdateDownloadDialog` receives progress over a per-update, current-user named pipe. The separately launched updater displays its own window before signaling readiness. The desktop checks for active work before downloading and again before authorizing installation and closing. An accepted startup offer stays queued if work begins during the download. The updater window stays visible during silent installation and restart, then closes after the launched desktop writes its version-matched startup confirmation. Preparation failures leave the desktop open. Install and startup failures leave a dismissible updater window with the log location documented below.

## Verification commands and limits

Focused relay and solution checks:

```powershell
dotnet build PocketAgent.slnx
dotnet run --project tests/PocketAgent.RelayChecks
dotnet run --project tests/PocketAgent.NetworkChecks -c Release
dotnet run --project tests/PocketAgent.DesktopChecks -c Release
node --test tests/relay.test.mjs
```

The relay checks verify encrypted routing, managed certificate trust, and Android pairing on an isolated emulator. Router reachability and a real Codex inference turn require separate acceptance tests.

## Diagnostics

Desktop diagnostics are under `%LOCALAPPDATA%/PocketAgent/logs/desktop.log`; the embedded server writes `%LOCALAPPDATA%/PocketAgent/logs/server.log`. Logs record lifecycle, authentication outcomes, API failures, provider timing and access transitions. Desktop/server 0.3.25 adds the resolved Codex executable source/path, `CODEX_HOME` source/path/presence, API-key environment-variable source/presence, and a comparison-only credential fingerprint to the Codex startup entry. Diagnostic output is not a credential boundary: do not place tokens or other secrets in provider messages. Updater handoff failures are recorded in `%TEMP%/PocketAgent-update.log`.
