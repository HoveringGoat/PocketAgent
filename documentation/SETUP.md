# Local setup and verification

## Use the packaged native apps

Current release: **0.2.2**. Install the 0.2.2 Android app and desktop app for QR/pairing-string enrollment. Existing saved connections remain available, but new pairing uses the full string rather than a six-digit entry.

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

Extract the ZIP and run the executable inside its desktop folder. The Windows package is self-contained; no .NET runtime, Node or source checkout is required. Install the signed APK over the existing phone app using the same signing identity; no uninstall/data reset is needed. Desktop Settings displays the running version and executable path so shortcuts to old builds can be identified. This release includes a pocket/terminal icon.

Finish active work and close the old desktop before starting the new version. Saved state is under LocalAppData/PocketAgent, outside release folders. Closing a server ends its provider process; pending schedules wait for startup. An upgrade does not preserve in-flight execution. The running 0.1.9 artifact folder was left at its original path because it is in use; older artifacts were grouped without deletion. GitHub Releases and built-in update checks remain future work.

## Desktop setup and permissions

Choose a default working folder in Settings; new conversations can choose another folder. Leave the Codex executable override empty to discover the installed VS Code native binary, or enter an explicit native path. The binary is launched locally and never bundled in releases.

Choose Use Codex settings (inherit), Ask for approval (workspace writes and on-request approvals), or Full access (unrestricted filesystem/network and no command prompts, subject to managed restrictions). Stop, save and start to apply permission changes. Provider sign-in stays on the desktop.

### Check Codex sign-in and model compatibility

Start the server and click Check Codex sign-in. Diagnostics read account/config/model metadata through the same local process; they do not generate model responses. Catalog presence is not inference verification. The model picker changes the conversation model. When the installed Codex app-server advertises supported reasoning efforts, the conversation model dialog also offers a per-conversation reasoning setting; “Use model default” clears the override.

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

Current navigation uses the Settings gear for a full page listing all paired servers uniformly, per-server actions, add-server access, and inline application information. Usage and reset details are opened from the relevant server card, so Personal and Work quotas remain separate. Paired servers remain connected and poll independently; switching the visible workspace does not disconnect another machine. Conversation tabs visually join the open pane; its title strip contains rename, model/reasoning, archive, and archived-history actions. Scheduling is created only with the timer button beside Send. Pending schedules appear below Send and open the review/cancel dialog when selected.

Open the gear to view the full Settings page, then use the unified server list to switch saved machines or add a new one. Each origin has its own encrypted device token, tabs, draft and uncertain-delivery state. Adding a connection opens pairing; unpairing one server does not remove other saved servers. The selected conversation pane contains compact actions for Rename, Model/reasoning, Archive, and archived-history. + opens working-folder and optional model choices; it never asks for a name. **Browse folders** navigates the home machine's local drives; **Use this folder** selects the current directory. You can also enter an absolute desktop path. Paired owner devices can select existing local directories accessible to the desktop account; host permission settings remain desktop-owned. A conversation's folder stays fixed after creation. The desktop Settings repository remains the creation default. There is no sidebar or decorative footer.

Use **⋮ > Choose model**, or tap the context row above the composer, to select from that server's available models. Finish or stop work before changing it. The choice applies to this conversation's future messages, including queued messages when they dispatch. Other conversations and global Codex settings keep their choices. An omitted creation choice uses Codex settings; an unavailable model is never silently substituted. Desktop has the equivalent model button beside the title.

Use **⋮ > Archive conversation** to remove an idle conversation from the tab bar. The confirmation explains that pending scheduled messages are canceled. Active/unconfirmed work and in-flight schedule delivery must finish first. **⋮ > Archived conversations** offers Restore. History remains intact, and restoring does not resend canceled jobs. Desktop has Archive beside the title and Archived beside the tab bar. Provider transcripts are not permanently deleted.

Your messages use chat bubbles aligned to the trailing edge; assistant prose uses the plain transcript background. Messages use local timestamps, grouped by minute; older history without trustworthy timing remains untimestamped. Version 0.1.4 explicitly decodes Codex subprocess output as UTF-8, fixing corrupted punctuation, accented letters and emoji from Windows native launches. The bottom context row displays status, model, host/directory and elapsed/completed work time. A running conversation changes Send to Steer. New input augments that expected active turn; if the turn changed, the draft remains for explicit resubmission. Typing while a send is acknowledged is preserved. Closing the phone does not stop work. Returning to the app reconciles history and requests without replaying input.

Approvals/questions remain explicit. Supported one-time approvals cannot broaden scope or answer stale requests. Unsupported host interactions are visible and can be interrupted; complete their setup on the desktop. Neither Full access nor steering changes these protocol checks.

## Per-conversation reasoning

The model picker is per conversation. When the installed Codex app-server advertises supported reasoning efforts, the desktop model dialog and Android connection menu expose those levels plus **Use model default**. The selected value is stored with that conversation and sent on future turns; changing models clears an unsupported override. If the provider does not advertise levels, PocketAgent reports that instead of guessing.

## 0.2.1 release status

The 0.2.2 release includes the direct desktop updater handoff, updater diagnostics, desktop release-note display, startup/update preferences, the fixed clickable scheduling control and the public GitHub release channel. Android checks the latest published release once on app startup and offers to open its release page when a newer version exists. APK installation remains user-confirmed.

## Usage and scheduled messages

Desktop summarizes usage compactly, for example `5hr: 80% 8:14 PM | weekly: 20% 11/17/26`; hover the line for full local reset timestamps. Android's usage details remain available from its usage control/menu.

The usage line shows provider-reported five-hour/weekly percentage remaining. Tap it (or **⋮ > Usage and resets**) for local reset times. Missing or unsupported windows say unavailable. This is the same desktop account/process used for conversations. PocketAgent does not purchase credits or consume reset credits.

If a known usage window is exhausted, Send acknowledges that the prompt is queued for after reset. With no reported reset time it says so and rechecks availability. A provider-reported quota failure before any known execution also queues the original prompt. Partial work and unknown delivery are not automatically repeated.

Write a prompt, then choose **Schedule message** in Android or **Schedule** on desktop. Select the local date/time. **Scheduled messages** / **Queue** lists pending, submitted, canceled, failed and uncertain entries; pending entries can be canceled. Dispatch waits for an idle, confirmed conversation, so the chosen time is an earliest eligible time. Existing active work and approvals take priority.

Schedules are persisted on the desktop and survive restarts. The desktop must be awake and the server running. Missed deadlines are reconsidered when it runs again; there is no wake timer or cloud execution. Revoking/expiring a device cancels its pending schedules. Submitted means Codex acknowledged receipt, not successful task completion. An uncertain dispatch is never replayed after restart: review history and explicitly create another message if needed.


## Build from source

Use .NET SDK 10.0.404, JDK 17/21, Android SDK 34/build-tools 35.0.0 and the pinned Gradle wrapper. Node is test/format tooling only. Native QR uses QRCoder 1.8.0 on desktop and ZXing core 3.5.4 with Android camera2; no WebView, external scanner service or cloud image processing.

```powershell
.\scripts\Build.ps1 -Target Desktop -Publish
.\scripts\Build.ps1 -Target Android
.\scripts\Build.ps1 -Target All -Publish
.\scripts\Start-Desktop.ps1
```

Build-Android.ps1 accepts -GradleCommand for an existing matching installation and -DebugOnly to skip signing. Signing keys and the DPAPI-protected password stay under ignored .local/android-signing; preserve them for upgrades. The APK and ZIP are separate sibling artifacts, each with checksums. Build.ps1 -SkipAndroid remains supported for desktop iteration. Generate-Icon.ps1 regenerates the committed desktop icon asset.

For a distributable release, first run `scripts/Release.ps1 -Version 0.2.2 -Check`. This validates the .NET, Android, installer and (when publishing) GitHub CLI prerequisites without compiling. Add `-InstallPrerequisites` to let Windows Package Manager install missing Inno Setup 6 and/or GitHub CLI. Then run `scripts/Release.ps1 -Version 0.2.2`; the script performs the same preflight before compiling, stages the portable desktop ZIP and signed Android APK, creates a per-user Windows installer from `installer/PocketAgent.iss`, rejects private/runtime files in the desktop package, includes the nested APK in the GitHub asset/checksum set, and writes an aggregate `SHA256SUMS.txt`. Release notes come from the matching sections in `CHANGELOG-desktop.md` and `CHANGELOG-android.md`, and packaged clients show those notes when an update is available. Release directories are versioned and are not reused unless `-Clean` is supplied. `-Publish` is an explicit second step: it requires an authenticated GitHub CLI session and creates `v<version>` in the configured repository. If a release was created with missing assets, rerun with `-UpdateExisting` to upload the corrected asset set and refresh its notes. Use `-CommitChanges` only when you explicitly want the script to stage and commit outstanding changes as `Release v<version>` before building; private-looking files are rejected and commits never happen by default. The script never embeds provider credentials, pairing data, signing keys or local paths in an artifact.

The desktop updater runs as a separate bundled process. If an update closes the desktop app without restarting it, inspect `%TEMP%\PocketAgent-update.log`; the updater records download, checksum, replacement, restart and rollback failures there.

Organize-Artifacts.ps1 moves historical flat artifacts into release folders without deletion/overwrites. It skips running executables, checks targets remain under artifacts, and uses an atomic directory move. -WhatIf previews operations. A running old folder must remain until its application is closed. No runtime databases or keys are relocated.

## Verification commands and limits

Focused checks for this change:

```powershell
$env:POCKETAGENT_TEST_NODE = (Get-Command node.exe).Source
dotnet run --project tests/PocketAgent.NetworkChecks -c Release
dotnet run --project tests/PocketAgent.DesktopChecks -c Release
$env:POCKETAGENT_TEST_SERVER_DLL = (Resolve-Path src/PocketAgent.Server/bin/Release/net10.0/PocketAgent.Server.dll).Path
node --test tests/direct-internet.test.mjs
.\tests\desktop-smoke.ps1
```

NetworkChecks verifies ticket single use/rotation/expiry, same-key certificate renewal, real HTTPS enrollment and replay rejection, protected API access and certificate hot reload. DesktopChecks verifies effective colors, rendered surfaces, confirmation Yes/dismiss behavior and render limits; it generates a desktop QR fixture. PairingInstrumentation on an isolated Android emulator decodes both ZXing-generated and actual desktop-generated QR images, exercises manual-paste UI enrollment, encrypted pin restoration/server switching and real TLS. Wrong pins, wrong hostnames and expired certificates fail before HTTP delivery. The chat smoke regression seeds its isolated debug connection separately so steering/drafts/folder/model/archive checks remain independent of camera input. Debug instrumentation/HTTP exceptions do not ship in the signed APK.

Build/lint/signature checks and focused native/protocol tests are distinguished from physical acceptance: scanning a real screen with the phone camera and connecting through the actual router/cellular route still require user verification. No public endpoint, router/Caddy configuration, live model turn or full test suite was used. Native title-bar attributes can fall back to Windows behavior; OS-owned folder pickers retain the OS theme. A prior theme claim was too broad: brush/render tests do not prove every OS theme state matches the user's display.
