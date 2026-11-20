# Local setup and verification

## Use the packaged native apps

Current release: **Android 0.4.3 / desktop-server 0.3.11**. Install the 0.4.3 Android app and 0.3.11 desktop app for QR/pairing-string enrollment. Existing saved connections remain available, but new pairing uses the full string rather than a six-digit entry.

Release files are grouped together:

```text
artifacts/releases/0.2.2/
  desktop/PocketAgent.Desktop.exe
PocketAgent-0.2.2-desktop.zip
PocketAgent-0.2.2-desktop.zip.sha256
PocketAgent-0.2.2-Setup.exe
android/PocketAgent-0.2.2-android.apk
android/PocketAgent-0.2.2-android.apk.sha256
```

Extract the ZIP and run the executable inside its desktop folder. The Windows package is self-contained; no .NET runtime, Node or source checkout is required. Install the signed APK over the existing phone app using the same signing identity; no uninstall/data reset is needed. Desktop Settings displays the running version and executable path so shortcuts to old builds can be identified. The Rust executable and installer use the committed PocketAgent pocket/terminal icon. The Rust client uses the live Codex model catalog and falls back to a supported catalog model when the local Codex configuration names a model unavailable to the signed-in account.

Finish active work and close the old desktop before starting the new version. Saved state is under LocalAppData/PocketAgent, outside release folders. Closing a server ends its provider process; pending schedules wait for startup. An upgrade does not preserve in-flight execution. The running 0.1.9 artifact folder was left at its original path because it is in use; older artifacts were grouped without deletion. GitHub Releases and built-in update checks remain future work.

## Desktop setup and permissions

Choose a default working folder in Settings; new conversations can choose another folder. Leave the Codex executable override empty to discover the installed VS Code native binary, or enter an explicit native path. The binary is launched locally and never bundled in releases.

Choose Use Codex settings (inherit), Ask for approval (workspace writes and on-request approvals), or Full access (unrestricted filesystem/network and no command prompts, subject to managed restrictions). Stop, save and start to apply permission changes. Provider sign-in stays on the desktop.

### Check Codex sign-in and model compatibility

Start the server and click Check Codex sign-in. Diagnostics read account/config/model metadata through the same local process; they do not generate model responses. Catalog presence is not inference verification. The desktop model picker searches readable model names and shows the technical model ID beneath each choice. When the installed Codex app-server advertises supported reasoning efforts, the conversation model dialog also offers a per-conversation reasoning setting with friendly labels; "Use model default" clears the override.

## Direct internet (IP and port)

Only one chosen chat port needs forwarding. **No public CA, port 80/443, Caddy edits, domain or relay is needed.**

1. Finish active work and stop the server. Open Settings > Set up direct internet (IP + port).
2. Choose the desktop LAN address and your router public IPv4. Detect public IP is optional: only clicking it queries api.ipify.org, without chat, credentials or pairing information. A VPN may report its exit address; compare with the router WAN address.
3. Choose the external chat port (for example 5147) and save. Setup generates a local server certificate with a persistent private identity key. It does not contact a certificate authority.
4. Forward that external TCP port to the desktop LAN address and internal listener port. Allow that listener through Windows Firewall. Reserve the LAN address in the router. Do not move your existing 80/443 forwards.
5. Start the server, open Devices, then pair the phone as below. To test internet reachability, turn phone Wi-Fi off before connecting.

On the first start after choosing a non-loopback listener, PocketAgent offers to create one Windows Firewall rule for the configured chat port. Approve it once with the Windows elevation prompt. The rule is named `PocketAgent inbound TCP <port>` and is tied to the port rather than the release executable, so future desktop versions do not require another exception. Declining leaves the server available for local-only use, but Windows Firewall or the router may block phone connections. If you change the listener port, approve the new port rule once.

A CGNAT/private WAN address needs ISP/router changes to permit direct inbound access. Double NAT needs both routers configured. Local diagnostics check the listener, certificate identity, exact Host and API; they cannot certify outside reachability. No silent relay fallback exists.

Public-IP changes require setup again and a new phone origin; the desktop key is preserved, but saved tokens are never forwarded to a changed origin. On home Wi-Fi, using the public address requires router NAT loopback support; otherwise use cellular or configure a separate LAN connection.

### Relay mode status

The planned relay mode uses an unlocked PocketAgent server as a relay node. A locked-down remote desktop connects outbound to that relay; the phone connects to the relay and selects the attached remote host. This removes the inbound firewall requirement from the remote desktop, but the relay node itself must remain reachable. The Rust workspace now has authenticated WebSocket transport, including a native Rustls `wss://` loopback round trip. The Rust desktop persists one relay host/client enrollment bundle and shared application-session key under `%LOCALAPPDATA%/PocketAgent-rust/relay.json`, and displays that bundle while the relay is running so another host can be provisioned without opening the secrets file. Native Android/WPF relay clients and multi-enrollment management are not available in the current release.

## Connect over Wi-Fi

While stopped, expand Advanced certificate settings and choose Set up Wi-Fi connection. Choose the desktop's private interface, save, and start. Pair using its QR/string. No CA certificate installation is required. Both devices must share a network that permits connections; guest isolation can block them. Configure direct internet instead when cellular access is required.

## Pair by QR or pairing string

On desktop Devices, generate a fresh pairing QR. In the Android app, choose Scan pairing QR and grant camera access only if using the scanner. Alternatively click Copy pairing string on desktop and paste the entire string into the phone's Pairing string field. Both methods use exactly the same payload.

Optionally name the server (Work/Personal), review the device name and tap Connect. **There is no additional code to type.** A QR/string is a ten-minute, single-use enrollment credential. Regenerating it invalidates the prior one; after successful use, generate another for the next device. Treat screenshots and copied strings as temporary passwords and clear the clipboard after transferring. Obtain the payload from your own desktop, not an untrusted website or message.

The payload includes the exact HTTPS origin, a SHA-256 public-key fingerprint and a random 256-bit pairing ticket. The app verifies that key, certificate validity, server-auth purpose and IP/hostname before sending the ticket. It then stores the origin-bound pin and bearer token in its Android Keystore-encrypted vault. A different key is rejected even if it has a publicly trusted certificate. Redirects are rejected. No fingerprint is silently learned over the network.

The managed certificate renews locally with the same key, so ordinary renewal does not require re-pairing. If the private identity key is lost/replaced, restore the key or scan a new QR on that desktop. A missing identity key beside an existing managed certificate is an error, not permission to silently create another identity. Expired certificates fail closed. Legacy saved connections without pins keep their former system/owner-installed trust until re-paired; new pinned connections never fall back to it.

The desktop key and server.pfx remain private. Do not export them to the phone or include them in a release. Revoke lost devices in desktop Devices. QR pairing does not broaden the Codex agent's approval/permission boundaries.

## Android servers and conversations

Appearance settings are available from the main Settings page on Android and desktop. Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) presets are stored locally and apply to fonts and primary controls without changing server or conversation data.

Current navigation uses the Settings gear for a full page listing all paired servers uniformly, per-server actions, add-server access, and inline application information. Usage and reset details are opened from the relevant server card, so Personal and Work quotas remain separate. Paired servers remain connected and poll independently; switching the visible workspace does not disconnect another machine. Conversation tabs visually join the open pane; its title strip contains rename, model/reasoning, archive, and archived-history actions. The active tab has the outlined top/side boundary while inactive tabs keep a lower divider. The status/model row, composer, Send, and timer controls share one full-width bottom control panel. Scheduling is created or reviewed from the timer button beside Send; its pending count appears beside the timer, and tapping it with an empty composer opens the review/cancel dialog. When a conversation is selected, the **Access** action appears beside the other conversation actions and opens that conversation's device checklist; the Devices page is for pairing and device-wide revocation. Ungranted devices do not see that conversation.

Open the gear to view the full Settings page, then use the unified server list to switch saved machines or add a new one. Each origin has its own encrypted device token, tabs, draft and uncertain-delivery state. Adding a connection opens pairing; unpairing one server does not remove other saved servers. The selected conversation pane contains compact actions for Rename, Model/reasoning, Pin/unpin, Archive, and archived-history immediately beside the new-conversation button; the global header shows the server machine and selected working directory. + opens working-folder and optional model choices; it never asks for a name. **Browse folders** navigates the home machine's local drives; **Use this folder** selects the current directory. You can also enter an absolute desktop path. Paired owner devices can select existing local directories accessible to the desktop account; host permission settings remain desktop-owned. A conversation's folder stays fixed after creation. The desktop Settings repository remains the creation default. There is no sidebar or decorative footer.

Use **⋮ > Choose model**, or tap the context row above the composer, to select from that server's available models. Finish or stop work before changing it. The choice applies to this conversation's future messages, including queued messages when they dispatch. Other conversations and global Codex settings keep their choices. An omitted creation choice uses Codex settings; an unavailable model is never silently substituted. Desktop has the equivalent model button beside the title.

Use **⋮ > Archive conversation** to remove an idle conversation from the tab bar. The confirmation explains that pending scheduled messages are canceled. Active/unconfirmed work and in-flight schedule delivery must finish first. **⋮ > Archived conversations** offers Restore. History remains intact, and restoring does not resend canceled jobs. Desktop has Archive beside the title and Archived beside the tab bar. Provider transcripts are not permanently deleted.

Your messages use chat bubbles aligned to the trailing edge; assistant prose uses the plain transcript background. Messages use local timestamps, grouped by minute; older history without trustworthy timing remains untimestamped. Version 0.1.4 explicitly decodes Codex subprocess output as UTF-8, fixing corrupted punctuation, accented letters and emoji from Windows native launches. The bottom context row displays status, model, host/directory and elapsed/completed work time. The Send label stays unchanged while a conversation is running; new input augments that expected active turn. If the turn changed, the draft remains for explicit resubmission. Typing while a send is acknowledged is preserved. Closing the phone does not stop work. Returning to the app reconciles history and requests without replaying input.

Approvals/questions remain explicit. Supported one-time approvals cannot broaden scope or answer stale requests. Unsupported host interactions are visible and can be interrupted; complete their setup on the desktop. Neither Full access nor steering changes these protocol checks.

## Per-conversation reasoning

The model picker is per conversation. When the installed Codex app-server advertises supported reasoning efforts, the desktop model dialog and Android connection menu expose those levels plus **Use model default**. The selected value is stored with that conversation and sent on future turns; changing models clears an unsupported override. If the provider does not advertise levels, PocketAgent reports that instead of guessing.

## 0.2.1 release status

The 0.2.2 release includes the direct desktop updater handoff, updater diagnostics, desktop release-note display, startup/update preferences, the fixed clickable scheduling control and the public GitHub release channel. Android checks the latest published release once on app startup and offers to open its release page when a newer version exists. APK installation remains user-confirmed.

## Usage and scheduled messages

Desktop and Android summarize usage compactly, for example `5hr: 80% 8:14 PM | weekly: 20% 11/17/26`; hover or tap the line for full local reset timestamps.

The usage line shows provider-reported five-hour/weekly percentage remaining. Tap it (or **⋮ > Usage and resets**) for local reset times. Missing or unsupported windows say unavailable. This is the same desktop account/process used for conversations. PocketAgent does not purchase credits or consume reset credits.

If a known usage window is exhausted, Send acknowledges that the prompt is queued for after reset. With no reported reset time it says so and rechecks availability. A provider-reported quota failure before any known execution also queues the original prompt. Partial work and unknown delivery are not automatically repeated.

Write a prompt, then choose the timer button beside Send in Android or **Schedule** on desktop. Select the local date/time. When pending jobs exist, the Android timer shows their count; tapping it with no draft opens the review/cancel dialog. **Scheduled messages** / **Queue** lists pending, submitted, canceled, failed and uncertain entries; pending entries can be canceled. Dispatch waits for an idle, confirmed conversation, so the chosen time is an earliest eligible time. Existing active work and approvals take priority.

Schedules are persisted on the desktop and survive restarts. The desktop must be awake and the server running. Missed deadlines are reconsidered when it runs again; there is no wake timer or cloud execution. Revoking/expiring a device cancels its pending schedules. Submitted means Codex acknowledged receipt, not successful task completion. An uncertain dispatch is never replayed after restart: review history and explicitly create another message if needed.


After a connected provider turn fails, the Rust desktop keeps the composer available so the user can inspect the error and explicitly retry; connection and disconnect failures still disable provider actions. Revoking a device refreshes the access checklist for the conversation currently selected instead of clearing that conversation context.

## Build from source

Use .NET SDK 10.0.404, JDK 17/21, Android SDK 34/build-tools 35.0.0 and the pinned Gradle wrapper. Node is test/format tooling only. Native QR uses QRCoder 1.8.0 on desktop and ZXing core 3.5.4 with Android camera2; no WebView, external scanner service or cloud image processing.

```powershell
.\scripts\Build.ps1 -Target Desktop -Publish
.\scripts\Build.ps1 -Target Android
.\scripts\Build.ps1 -Target All -Publish
.\scripts\Start-Desktop.ps1
```

Build-Android.ps1 accepts -GradleCommand for an existing matching installation and -DebugOnly to skip signing. Signing keys and the DPAPI-protected password stay under ignored .local/android-signing; preserve them for upgrades. The APK and ZIP are separate sibling artifacts, each with checksums. Build.ps1 -SkipAndroid remains supported for desktop iteration. Generate-Icon.ps1 regenerates the committed desktop icon asset.

For a distributable release, run the release script separately for Desktop and Android. For example, first run `scripts/Release.ps1 -Version 0.3.5 -Target Desktop -Check` and `scripts/Release.ps1 -Version 0.3.5 -Target Android -Check`. This validates the .NET, Android, installer and (when publishing) GitHub CLI prerequisites without compiling. Add `-InstallPrerequisites` to let Windows Package Manager install missing Inno Setup 6 and/or GitHub CLI. Then publish Desktop with `scripts/Release.ps1 -Version 0.3.5 -Target Desktop -Publish -CommitChanges -Clean`, followed by Android with `scripts/Release.ps1 -Version 0.3.5 -Target Android -Publish -Clean`. Each command stages its own platform assets, checksums, release notes, and immutable `desktop-v<version>` or `android-v<version>` tag. `Target All` remains valid for local builds but is rejected for publishing. Release notes come from the matching platform changelog, and packaged clients show those notes when an update is available. Release directories are versioned and are not reused unless `-Clean` is supplied. Use `-CommitChanges` only when you explicitly want the script to stage and commit outstanding changes as `Release v<version>` before building; private-looking files are rejected and commits never happen by default. The script never embeds provider credentials, pairing data, signing keys or local paths in an artifact.

The desktop writes a rotating diagnostic log under `%LOCALAPPDATA%\PocketAgent\logs\desktop.log`. The embedded relay writes `%LOCALAPPDATA%\PocketAgent\logs\server.log` in the same directory. The server log records startup/shutdown, pairing/authentication outcomes, API failures, Codex process/RPC timing, heartbeat filtering decisions, schedule failures, and conversation/access transitions. Token-like values are redacted; message bodies, request parameters, pairing credentials, and provider stderr contents are not logged. Provider stderr is represented only by line-arrival and length records. The desktop updater runs as a separate bundled process. If an update closes the desktop app without restarting it, inspect `%TEMP%\PocketAgent-update.log`; the updater records download, checksum, replacement, restart and rollback failures there. Every non-`-Check` release run now executes a mandatory release test gate before release commits, packaging, or publication. It restores/builds the solution and focused checks, runs the conversation/session regressions, NetworkChecks, and DesktopChecks, then launches the packaged updater against a disposable installed-app directory served from a local HTTP endpoint. That installed-flow check verifies replacement, restart health, staging cleanup, rejects hidden directories in the package, and seeds a stale hidden transaction in the install to prove it is ignored; any failure stops the release and there is no bypass.

If a client reports `codex: thread not loaded`, the desktop still serves the session list and usage and marks that conversation disconnected. The local conversation metadata is retained. Do not delete the PocketAgent database or automatically resend work: first make sure the same Codex executable and `CODEX_HOME` are being used, because the original thread may still be recoverable there. If the thread was actually deleted from Codex storage, PocketAgent cannot reconstruct its transcript from its metadata-only local record. An idle conversation can still be archived or restored from its local metadata while the Codex thread is unavailable.

For a manual desktop image check, run `dotnet run --project tests/PocketAgent.DesktopChecks/PocketAgent.DesktopChecks.csproj --no-build -c Release -- --interactive-image` after building the checks. The preview opens the current native desktop renderer with the repository's `deadspace.png` as a file-backed conversation image; click `View image: deadspace.png` and close the window when finished. Android and desktop use the same `images[].source` schema, but file-backed sources are resolved through the server conversation image endpoint before desktop decoding.

Organize-Artifacts.ps1 moves historical flat artifacts into release folders without deletion/overwrites. It skips running executables, checks targets remain under artifacts, and uses an atomic directory move. -WhatIf previews operations. A running old folder must remain until its application is closed. No runtime databases or keys are relocated.

## Android visual preview

The repository includes a debug-only conversation preview with mocked servers,
tabs, messages, running status, usage, and scheduled messages. It uses the same
native Android renderer intended for the client; it is not a browser or
TypeScript clone and is not included in release APKs.

Live conversation tabs use this same shared renderer. The preview is therefore
also a regression check for the active-tab divider, overflow chevrons, server
selector, pin action, and centered composer actions, not only a static mockup.

From PowerShell, run:

```powershell
.\scripts\Preview-Android.ps1 -Open
```

The script builds and installs the debug APK, starts the `PuzzleApp_Pixel6_API36`
AVD when necessary, launches the preview activity, and saves a screenshot under
`.local/previews/android/`. Pass `-Serial emulator-5554` to use a specific
already-running emulator. The fixture is for visual review only; network,
pairing, provider credentials, and conversation state are not used.

## Verification commands and limits

### Rust desktop MVP preview

Rust 0.1.15 keeps a newly granted conversation visible during the short interval before the Codex worker finishes its metadata refresh. The Android client can therefore begin its authenticated read immediately; the placeholder title is replaced by the saved desktop title on the next host sync. Rust diagnostics now include device grant/revoke outcomes and authenticated `/api/conversations` counts (host snapshot, authorized grants, and visible results), without logging bearer tokens or message contents.

Rust desktop diagnostics are appended to `%LOCALAPPDATA%/PocketAgent-rust/desktop.log` (or the XDG data equivalent). Entries record lifecycle and failure categories without message bodies, tokens, or provider stderr.

On startup and every ten minutes while the worker is running, the Rust local store applies the same seven-day idle archive rule as the server: unpinned conversations with no recent activity are moved to Archived; pinned conversations and recent activity are preserved. This is local metadata only and never deletes Codex history.

Rust metadata writes use a temporary file plus a recoverable backup replacement, so repeated saves work on Windows even when the previous JSON file exists; a backup is read if a power loss leaves the main file missing.

The Rust composer also accepts a delay in seconds for a scheduled prompt and shows persisted entries in a compact Scheduled messages panel with per-entry Cancel actions. Scheduled entries are persisted with their target conversation and dispatched through the normal Codex turn path after restart; canceling a pending entry is rechecked at the dispatch boundary so a sleeping timer cannot send it later. A failed dispatch is recorded in the local schedule state rather than silently retried.

Before a Rust turn is dispatched, the provider rate-limit response is checked. If a known usage window is exhausted, the original prompt is persisted as a pending schedule until the reported reset (or a five-minute retry when no reset is reported); it is not sent into a known quota failure.

The Rust rewrite now has a runnable native desktop shell. It defaults to local Codex mode: the repository defaults to saved local state, `POCKETAGENT_REPOSITORY`, or the launch directory, and Windows detects `%APPDATA%/npm/codex.ps1` when present. It connects to Codex on startup, persists settings, thread metadata, per-conversation working directories, and per-conversation model/reasoning values under the user data directory, lists the provider model catalog and sanitized rate-limit usage summary, lists saved threads, creates a new conversation (using an optional working-folder field or the saved repository by default), resumes a selected thread, sends prompts, keeps status beside the transcript composer, steers an active turn with its observed turn ID, rejects remote steering whose expected turn no longer matches the active turn or is omitted, keeps an active turn's model/reasoning policy fixed while steering, disables model/reasoning edits during active work, preserves the composer draft, renders headings, bullets, code blocks, command output, file changes, and local `imageView` results as Markdown-styled transcript blocks, resolves relative image paths against the selected conversation folder, accepts structured image URL/data fields, surfaces failed provider turns instead of treating them as successful, and presents supported Codex approval/user-input requests in the conversation view. Archiving an active conversation is rejected until the work is stopped or completed. The host UI exposes per-conversation grants, a device-wide **Revoke** action that invalidates the device token and removes its conversation access, and **Renew pairing** to replace an expired or consumed one-time code without restarting the app. The **Optional relay** panel starts/stops the authenticated relay and accepts an explicit bind address (it defaults to loopback). Its host/client identity and shared application-session key are persisted under `%LOCALAPPDATA%/PocketAgent-rust/relay.json`; the running panel displays that bundle for local provisioning, and the file must still be treated as secret. Local conversation use does not require the relay. The Rust desktop now starts an authenticated HTTPS host on `0.0.0.0:8788` by default, waits for the HTTPS listener to be confirmed before showing it as running, and uses the detected router public IPv4 address in the Android pairing QR. Set `POCKETAGENT_HOST_ADVERTISE` to provide the public address manually. The Devices page's **Connection diagnostics** checks local TCP, HTTPS/API, TLS identity files, and reports the external-routing checks that still require a phone test. Forward the external port to the desktop and allow it through the host firewall. If lookup fails, pairing reports the failure instead of advertising a private LAN address. Pairing state is persisted under `%LOCALAPPDATA%/PocketAgent-rust/devices.json`, bearer tokens are stored only as SHA-256 digests, and the host UI manages per-conversation device access. The Android-compatible routes cover pairing, session discovery, authorized conversation listing, directory browsing, per-folder conversation creation, send, archive/restore, pin, rename, history, images, and schedules; every remote action is forwarded to the same local Codex worker. The relay crate now has an encrypted host dispatch loop; native WPF/Android relay clients and multi-enrollment management remain open.

On Windows, the Rust app does not prompt during initial launch. When you enable a stopped non-loopback host or open **Show pairing**, it first displays an in-app explanation and then invokes the standard UAC prompt to create the stable `PocketAgent inbound TCP <port>` rule. Canceling leaves the host stopped or pairing closed; loopback smoke runs do not prompt.
The Devices **Connection diagnostics** report includes the rule's PASS/FAIL state without changing firewall configuration.

The Rust host certificate is a persisted self-signed leaf with SANs for the listener and advertised addresses and the TLS `serverAuth` extended-key usage required by Android's pinned-trust verifier. A newly generated certificate is loaded on host startup; restart the desktop after changing certificate files.

The Rust Android-compatible API returns the same structured usage windows, model options, and absolute local-folder entries as the .NET host. Conversation history reads use Codex's non-writing thread-read path, so a phone can mirror a conversation while another client owns its active writer lease; returned transcript items preserve user, agent, and activity roles.

The Rust desktop status row shows live elapsed work time and the duration of the last completed turn; host snapshots expose the corresponding active-start and completed-duration fields.

The Rust desktop uses persistent top-level **Sessions**, **Devices**, and **Settings** pages. Its native layout follows the packaged desktop shell: a compact server/context header, right-aligned navigation and lifecycle indicator, title-only conversation tabs, adjacent conversation actions, a bounded transcript surface, status row, and composer/action panel. Sessions owns conversation selection and the transcript/composer; Devices owns pairing renewal, per-conversation grants, and device revocation; Settings owns Codex connection and relay configuration. Each page is scrollable so controls below the initial viewport remain reachable, and transcript rows size themselves to their Markdown content so blocks do not overlap.

The relay panel now displays the persisted host/client enrollment bundle and shared application-session key while the relay is running. This is sufficient to provision one attached host/device pair locally; native Android/WPF relay clients and multi-enrollment management remain open.

The Rust host binds all interfaces with HTTPS on port 8788 by default. The QR origin uses the detected router public IPv4 address; set `POCKETAGENT_HOST_ADVERTISE` when the address must be entered manually. `POCKETAGENT_HOST_BIND` remains available for an explicit listener address and port. The host now also exposes provider status/models/usage, authenticated session logout/revocation, conversation creation and model/reasoning/interrupt controls, bounded conversation snapshots and cursor-paginated history through `/api/conversations/{id}` and `/api/conversations/{id}/history`, owner-authenticated non-hidden directory browsing through `/api/directories`, repository-scoped images through `/api/conversations/{id}/images?path=...`, and persisted schedules through `/api/scheduled` and `/api/conversations/{id}/scheduled`. During an active turn, an authenticated non-paginated conversation read can return the last complete snapshot immediately with the current `working` status and active turn ID; paginated history remains provider-backed. Relay application transport and replacement of the packaged WPF/ASP.NET desktop remain open.

For a development remote-host connection, fill the **Connect this desktop through relay** fields in the Rust desktop's Optional relay panel, or set `POCKETAGENT_RELAY_HOST_URL`, `POCKETAGENT_RELAY_HOST_ID`, `POCKETAGENT_RELAY_HOST_TOKEN`, and `POCKETAGENT_RELAY_SESSION_KEY` (`<uuid>:<64 hex characters>`) before launching. The desktop opens an outbound authenticated relay-host connection and dispatches encrypted HTTP-like requests to the same local host router. The panel's **Disconnect outbound host** action closes that connection before another relay target is configured. The relay session key and transport token are secrets; the form does not persist them, and this enrollment remains a development hook rather than production provisioning. The Android client does not yet use the relay transport.

```powershell
cargo run --manifest-path rust/Cargo.toml -p pocketagent-desktop
```

To produce a portable preview directory and ZIP, run:

```powershell
.\scripts\Build-RustDesktop.ps1
```

The build first runs the Rust workspace test, warnings-as-errors clippy, and authenticated relay process-demo gates; a failure stops before packaging. It then runs `tests/rust-desktop-smoke.ps1` against the packaged executable before writing the ZIP. The output is under `artifacts/rust-desktop-<version>/`, with the matching `rust-desktop-<version>.zip` beside it. Rebuilding without changing the package version replaces that exact directory, ZIP, and checksum; bumping the version creates a new immutable artifact name. To also build and install-test the per-user Rust Windows installer, run:

```powershell
.\scripts\Build-RustDesktop.ps1 -Installer
```

That command compiles `installer/PocketAgentRustDesktop.iss`, installs the resulting setup into an isolated temporary directory, launches the installed executable through the same UI smoke test, and uninstalls it. The Rust preview artifacts are separate from the current .NET/WPF release channel and are not publicly uploaded by this local build.

Focused checks for this change:

```powershell
$env:POCKETAGENT_TEST_NODE = (Get-Command node.exe).Source
dotnet run --project tests/PocketAgent.NetworkChecks -c Release
dotnet run --project tests/PocketAgent.DesktopChecks -c Release
$env:POCKETAGENT_TEST_SERVER_DLL = (Resolve-Path src/PocketAgent.Server/bin/Release/net10.0/PocketAgent.Server.dll).Path
node --test tests/direct-internet.test.mjs
.\tests\desktop-smoke.ps1
cargo run --manifest-path rust/Cargo.toml -p pocketagent-relay --bin pocketagent-relay -- demo
```

The Rust relay foundation is checked with `cargo test --manifest-path rust/Cargo.toml --workspace`; it covers protocol serialization, AES-GCM envelope round trips, tamper/wrong-key rejection, encrypted application request/response route binding, authenticated loopback WebSocket routing, native Rustls WSS routing, invalid-auth rejection, opaque payload preservation, outbound-host routing and device/session mismatch rejection. The `pocketagent-relay demo` command runs the same authenticated encrypted request/response flow as a real process-level smoke check. These checks do not yet prove production certificate enrollment or a real two-desktop application deployment.

For a three-process routing probe, start the relay with explicit test identities and tokens, then run one host and one client in separate terminals:

```powershell
cargo run --manifest-path rust/Cargo.toml -p pocketagent-relay --bin pocketagent-relay -- serve 127.0.0.1:8787 11111111-1111-1111-1111-111111111111 host-token 22222222-2222-2222-2222-222222222222 client-token
cargo run --manifest-path rust/Cargo.toml -p pocketagent-relay --bin pocketagent-relay -- host ws://127.0.0.1:8787 11111111-1111-1111-1111-111111111111 host-token
cargo run --manifest-path rust/Cargo.toml -p pocketagent-relay --bin pocketagent-relay -- client ws://127.0.0.1:8787 11111111-1111-1111-1111-111111111111 22222222-2222-2222-2222-222222222222 client-token
```

The host probe echoes the client's opaque envelope and the client verifies the request ID and envelope. These commands use loopback `ws://`, in-memory authentication and a deliberately non-provider payload; they are development probes, not a production service or a replacement for device enrollment.

To keep probe identities across restarts, create a local config once and start from it:

```powershell
cargo run --manifest-path rust/Cargo.toml -p pocketagent-relay --bin pocketagent-relay -- init-config .local/relay-config.json
cargo run --manifest-path rust/Cargo.toml -p pocketagent-relay --bin pocketagent-relay -- serve-config 127.0.0.1:8787 .local/relay-config.json
```

The generated file contains bearer tokens and must stay local/ignored. This format is intentionally plain JSON for the MVP; it is not encrypted secret storage and must not be used for production deployment.

NetworkChecks verifies ticket single use/rotation/expiry, same-key certificate renewal, real HTTPS enrollment and replay rejection, protected API access and certificate hot reload. DesktopChecks verifies effective colors, rendered surfaces, confirmation Yes/dismiss behavior and render limits; it generates a desktop QR fixture. PairingInstrumentation on an isolated Android emulator decodes both ZXing-generated and actual desktop-generated QR images, exercises manual-paste UI enrollment, encrypted pin restoration/server switching and real TLS. Wrong pins, wrong hostnames and expired certificates fail before HTTP delivery. The chat smoke regression seeds its isolated debug connection separately so steering/drafts/folder/model/archive checks remain independent of camera input. Debug instrumentation/HTTP exceptions do not ship in the signed APK.

Build/lint/signature checks and focused native/protocol tests are distinguished from physical acceptance: scanning a real screen with the phone camera and connecting through the actual router/cellular route still require user verification. No public endpoint, router/Caddy configuration, live model turn or full test suite was used. Native title-bar attributes can fall back to Windows behavior; OS-owned folder pickers retain the OS theme. A prior theme claim was too broad: brush/render tests do not prove every OS theme state matches the user's display.

For previews of the actual desktop server switch, run `dotnet run --project tests/PocketAgent.DesktopChecks -- --toggle-preview`. This captures OFF and ON from the real WPF template at twice normal resolution into `.local/previews/desktop/server-switch-off.png` and `server-switch-on.png`, plus full-window views with dummy conversations in `desktop-switch-off.png` and `desktop-switch-on.png`. The fixture changes only the displayed state and does not start a server. These visual captures do not verify real server start/stop failure recovery.

The Rust 0.1.4 preview uses the same Codex executable discovery priority as the .NET desktop (latest stable VS Code extension native binary, then native PATH installation, then local npm fallback). Its Windows executable and provider child use GUI/no-console process flags. Session tabs scroll horizontally, every actionable control has an accessible icon and tooltip, active/primary controls use the filled-green treatment, machine/directory and usage occupy separate header lines, model/reasoning controls sit above the composer, and command/tool/file provider activity is omitted from the normal conversation transcript. Build and smoke verification must inspect the minimum and resized layouts for clipping or overlap.
