# Local setup and verification

## Use the packaged native apps

Version 0.1.3 replaces the old browser client. Opening the relay address in a browser returns service metadata, not chat. Use the native Windows server and Android client.

Local build artifacts:

- `artifacts/PocketAgent-0.1.3-desktop.zip`: self-contained Windows x64 server app, connector and setup documentation.
- `artifacts/PocketAgent-0.1.3-desktop/PocketAgent.Desktop.exe`: desktop/server executable.
- `artifacts/PocketAgent-0.1.3-android.apk`: signed Android client for Android 8 or later.
- Matching `.sha256` files verify artifact hashes. No public release has been uploaded.

Extract the Windows ZIP and run `PocketAgent.Desktop.exe`. No .NET SDK/runtime installation or Node.js is needed for the self-contained package. Copy the APK to the Android device (for example over USB), open it, and allow that trusted file source to install the app. The signed release is not a browser wrapper. Keep the original signing identity if rebuilding updates; changing signing keys requires uninstalling an existing Android installation and pairing again.

The older console server must stop before the desktop app uses the same port/runtime directory. Closing/stopping the server ends its provider process and active work. Pending schedules wait until the server runs again. Saved conversation references remain in runtime storage and Codex retains history. An update does not promise to preserve in-flight execution.

## Desktop setup and permissions

1. Open **Settings**, choose an existing repository, and leave **Codex executable override** blank to prefer the installed VS Code Codex extension. The app shows which binary was discovered. It launches a separate PocketAgent-owned process; it does not attach to IDE chats or copy the binary into a release.
2. Choose **Use Codex settings**, **Ask for approval**, or **Full access**. Default is inheritance. Full access requests unrestricted filesystem/network execution with command approval policy never. Ask for approval requests workspace writes, restricted network access and on-request approvals. Managed restrictions remain authoritative.
3. Save while stopped and click **Start server**. The permission selector is locked while running; stop/save/start to change it. Model and reviewer are preserved. Global Codex configuration is not edited.
4. **Sessions** provides top conversation tabs and a + action with no naming dialog. Names default to the repository folder, with suffixes for additional conversations. Rename later. The context row above the composer shows the host, working directory and provider-reported sandbox/approval policy after start/resume. Reading an old transcript alone does not establish its current provider policy.
5. **Devices** displays the connection address and expiring one-use code. Generate another code without restarting ongoing work. Revoke individual phones here. **Locate Android installer APK** selects the bundled client installer for transfer.

Settings/database/certificates remain under `%LOCALAPPDATA%/PocketAgent`, outside the package. `POCKETAGENT_DESKTOP_DATA` provides an isolated runtime directory for development/verification. Do not distribute `desktop.json`, `pocketagent.db`, PFX files, provider homes, signing keys or tunnel credential files.

## Check Codex sign-in and model compatibility

In desktop **Settings**, click **Check Codex sign-in** after starting the server. This uses the same Codex process as conversations and makes no model call. Desktop sign-in availability, catalog membership and successful inference are separate states. A saved sign-in does not prove every model is usable.

PocketAgent now automatically prefers the installed VS Code extension binary. The older npm CLI previously omitted the user's `gpt-6.1-sol` from its catalog while the newer extension binary included it. Preserve the desired model, inspect the executable/source displayed in Settings, and restart the relay when changing the executable. Updating a setting or extension installation does not replace an already running provider process. Use an explicit native executable override for a nonstandard extension location; do not point to a PowerShell/batch shim or copy provider authentication into PocketAgent.

If sign-in is missing, run Codex login in the desktop account used to launch the app and check that its existing `CODEX_HOME` is correct. Provider credentials stay there. To verify actual inference afterward, manually start a small conversation; the read-only check does not claim that inference succeeded. A provider disconnect requires restarting the relay. See [permissions](#desktop-setup-and-permissions) before choosing the desired execution mode.

## Connect over Wi-Fi

Loopback `127.0.0.1` is desktop-only. It is not a phone connection address.

1. Stop the server, then click **Settings > Set up Wi-Fi HTTPS**. Choose the private IPv4 interface shared with the phone. Setup generates a matching server certificate and saves the listener/certificate settings. It opens the certificate directory.
2. Transfer **only `pocketagent-root.cer`** from that directory to Android. Keep `server.pfx` on the desktop. Install the public root as a CA certificate in Android security/credential settings. Menu names vary by device. Trust only the CA generated on your own desktop.
3. Start the server. Allow PocketAgent's port on the Windows **private-network** firewall if required. The app does not silently create firewall rules, change trust stores or configure your router. Both devices must share a network allowing peer traffic; guest Wi-Fi isolation can block this.
4. In Android, enter the displayed `https://<desktop-private-IP>:5147`, optionally name it Work/Personal, enter a device name and the desktop code, then Connect.

The release app requires valid HTTPS with a matching hostname/IP. It supports system CAs and CAs explicitly installed by the phone owner. It never accepts arbitrary certificates or disables TLS checks. A changed IP requires a matching certificate; rerun Wi-Fi setup for the new address. Reserve a desktop IP if useful. The physical Android certificate/firewall path still needs real-device verification.

## Temporary internet trial

For a trial without buying/configuring a domain or creating a Cloudflare account:

1. Stop the server and choose **Settings > Prepare internet listener**. This saves loopback HTTP and clears direct-LAN certificate/public-origin settings.
2. Start the server, then click **Start temporary internet** and accept the desktop exposure confirmation.
3. In **Devices**, copy the generated `https://...trycloudflare.com` address. Enter it and a fresh pairing code in Android. This route is usable from Wi-Fi or cellular without installing a private CA or opening router ports.

The packaged connector creates an outbound tunnel. Cloudflare terminates public TLS and can process conversation traffic; provider credentials remain local. The URL changes whenever a new temporary tunnel is created and stops when the connector stops. A changed origin needs pairing again; saved credentials are never forwarded to a different origin. Quick Tunnels have no uptime guarantee and are for testing/development. Native polling avoids their SSE limitation. See [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/).

No public tunnel was started during automated verification. Actual internet/cellular access remains a user acceptance check.

## Stable internet connection

For regular remote use, provision a named tunnel and stable HTTPS hostname in your own Cloudflare account/domain. Account, DNS and any domain/service costs are user-owned prerequisites. PocketAgent does not create an account or publish DNS automatically.

Use the packaged `tools/cloudflared.exe` for the documented account login, tunnel creation and DNS route workflow from [Cloudflare's locally managed tunnel guide](https://developers.cloudflare.com/tunnel/features/locally-managed-tunnels/create-local-tunnel/). Keep generated credentials in a private local folder. In PocketAgent, stop the server, set **Public HTTPS address**, then use **Create named tunnel configuration** with the existing tunnel UUID and credential JSON. Save/start the server, then choose **Start internet connector**. The config forwards only the selected hostname to loopback with a catch-all 404.

Equivalent configuration (replace placeholders; do not commit credential contents):

```yaml
tunnel: <your-tunnel-uuid>
credentials-file: 'C:/Users/YourName/private/tunnel.json'
ingress:
  - hostname: pocket.example.com
    service: http://127.0.0.1:5147
    originRequest:
      httpHostHeader: pocket.example.com
  - service: http_status:404
```

The public address must match the configured origin; proxy mode requires a loopback listener. Use cloudflared's ingress validation tools when investigating routing. A running connector process alone is not proof that DNS, routing or remote access works. A user-managed Caddy/public HTTPS proxy is an alternative if you also manage the domain, trusted certificate and inbound connectivity. Do not expose an unauthenticated backend or relax Host/Origin checks to fix routing.

## Android servers and conversations

Tap the server name or **⋮ > Servers** to switch saved machines or add a new one. Each origin has its own encrypted device token, tabs, draft and uncertain-delivery state. Adding a connection opens pairing; unpairing one server does not remove other saved servers. Long-press a conversation tab to rename it. + creates a conversation immediately. There is no sidebar or decorative footer.

Messages use local timestamps, grouped by minute; older history without trustworthy timing remains untimestamped. The bottom context row displays status, host/directory and elapsed/completed work time. A running conversation changes Send to Steer. New input augments that expected active turn; if the turn changed, the draft remains for explicit resubmission. Typing while a send is acknowledged is preserved. Closing the phone does not stop work. Returning to the app reconciles history and requests without replaying input.

Approvals/questions remain explicit. Supported one-time approvals cannot broaden scope or answer stale requests. Unsupported host interactions are visible and can be interrupted; complete their setup on the desktop. Neither Full access nor steering changes these protocol checks.

## Usage and scheduled messages

The usage line shows provider-reported five-hour/weekly percentage remaining. Tap it (or **⋮ > Usage and resets**) for local reset times. Missing or unsupported windows say unavailable. This is the same desktop account/process used for conversations. PocketAgent does not purchase credits or consume reset credits.

If a known usage window is exhausted, Send acknowledges that the prompt is queued for after reset. With no reported reset time it says so and rechecks availability. A provider-reported quota failure before any known execution also queues the original prompt. Partial work and unknown delivery are not automatically repeated.

Write a prompt, then choose **Schedule message** in Android or **Schedule** on desktop. Select the local date/time. **Scheduled messages** / **Queue** lists pending, submitted, canceled, failed and uncertain entries; pending entries can be canceled. Dispatch waits for an idle, confirmed conversation, so the chosen time is an earliest eligible time. Existing active work and approvals take priority.

Schedules are persisted on the desktop and survive restarts. The desktop must be awake and the server running. Missed deadlines are reconsidered when it runs again; there is no wake timer or cloud execution. Revoking/expiring a device cancels its pending schedules. Submitted means Codex acknowledged receipt, not successful task completion. An uncertain dispatch is never replayed after restart: review history and explicitly create another message if needed.

## Build from source

Windows development prerequisites: .NET SDK 10.0.404 (repository-local `.tools/dotnet/dotnet.exe` supported), JDK 17/21, Android SDK platform 34 and build-tools 35.0.0. Gradle 8.11.1 is wrapped with its pinned distribution checksum. AGP 8.9.1 is pinned. Node.js 24 is test/format tooling, not a production app dependency. Install/sign in to Codex on the desktop separately.

```powershell
$env:JAVA_HOME = 'C:\Program Files\Java\jdk-21'
$env:ANDROID_HOME = 'C:\Users\YourName\AppData\Local\Android\Sdk'
.\scripts\Build.ps1 -Publish
```

`Build-Android.ps1` can build/sign Android alone (`-DebugOnly` skips release signing; `-GradleCommand` selects an existing matching Gradle installation). Release signing creates an ignored `.local/android-signing` key and DPAPI-protected password. Back up that identity appropriately; another Windows account cannot necessarily decrypt its password. The key/password are not included in the APK, desktop ZIP or repository. `Get-Connector.ps1` downloads a fixed release and verifies its pinned SHA-256 before packaging. Self-contained Windows publication includes required runtimes.

For focused server iteration, use `Build.ps1 -SkipAndroid` or a solution-level build. Launch via `scripts/Start-Desktop.ps1`; a published self-contained app is preferred, with repository-local runtime fallback for development. `scripts/Start.ps1 -Repository <path> -PermissionMode full-access` remains a headless option. Host settings have `PocketAgent__` environment equivalents (`RepositoryPath`, `ListenAddress`, `Port`, `DataDirectory`, `CertificatePath`, `PublicOrigin`, `CodexExecutable`, `CodexExtensionsDirectory`, `PermissionMode`).

## Verification commands and limits

Build Release first and point focused checks at its DLL; an older running Debug console can lock Debug output.

```powershell
$env:POCKETAGENT_TEST_SERVER_DLL = (Resolve-Path src/PocketAgent.Server/bin/Release/net10.0/PocketAgent.Server.dll).Path
node --test --test-name-pattern='relay access and conversation lifecycle|pairing attempt limits|approval scopes|provider resolution' tests/relay.test.mjs
node --test --test-name-pattern='LAN certificate setup|HTTPS pairing uses generated certificate' tests/relay.test.mjs
node --test --test-name-pattern='native pairing naming|public HTTPS origin' tests/native.test.mjs
node --test tests/permissions.test.mjs
node --test tests/scheduling.test.mjs
node --test --test-name-pattern='provider status' tests/provider.test.mjs
node --test --test-name-pattern='published native artifact' tests/publish.test.mjs
# Explicitly choose an isolated emulator. This check installs/clears only dev.pocketagent on that emulator.
$env:POCKETAGENT_TEST_ANDROID = 'emulator-5580'
node --test --test-name-pattern='native Android pairing' tests/android.test.mjs
.\tests\desktop-smoke.ps1
npm run format:check
dotnet format PocketAgent.slnx --no-restore --verify-no-changes
git diff --check
```

The native Android check covers pairing, creation without a name, steering, timestamps/usage, preserved drafts, bottom context placement, interruption, encrypted storage, switching two isolated server connections, and session/draft restoration after process restart. It saves a native screenshot under `.local/previews`. It uses the deterministic provider, not a real model. Debug-only instrumentation and loopback HTTP exceptions do not ship in the release APK. The Windows UI check launches the published app against an isolated deterministic provider/runtime directory and exercises startup, tabs, steering, context placement, interruption, stop and window close; it never starts a real Codex model or changes the user's running server.

The permission regressions were observed failing before implementation and passed afterward for inheritance and explicit modes on new/resumed conversations. Scheduler regressions were observed failing before implementation and passed for restart durability, cancellation, exhausted-window deferral, revocation, lost-acknowledgement non-replay and asynchronous provider quota failure. A native delivery-marker regression was observed failing before the fix: in-flight requests are now durably marked before sending and cleared after acknowledgement, protecting process-death recovery. The Windows UI check caught and verified the fix for reentrant window close after a clean shutdown. Android release signing was also checked with the existing signing identity after fixing newline handling in its DPAPI password read. A read-only real-provider probe through the VS Code binary reported desktop sign-in, configured `gpt-6.1-sol` catalog membership, and both expected usage windows, creating zero conversations or model turns.

Remaining acceptance checks: a successful real model response and steering/approval round trip, physical Android installation/CA/firewall/Wi-Fi access, and cellular/external access through an explicitly started tunnel. `tests/live.test.mjs` is opt-in and can incur usage; it has not been run. Full test suites were not run. The user's running 0.1.2 desktop is not silently restarted or replaced by a staged 0.1.3 build.
