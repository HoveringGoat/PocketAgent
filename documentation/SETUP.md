# Local setup and verification

## Use the packaged native apps

Version 0.1.4 replaces the old browser client. Opening the relay address in a browser returns service metadata, not chat. Use the native Windows server and Android client.

Local build artifacts:

The current package is 0.1.9. It fixes remaining white scrollbars/title bars and uses dark application confirmation dialogs. Direct setup also documents sharing public port 80 with an existing Caddy server. Existing Android clients remain compatible; no phone certificate installation is required for trusted HTTPS.

- `artifacts/PocketAgent-0.1.9-desktop.zip`: self-contained Windows x64 server app and setup documentation.
- `artifacts/PocketAgent-0.1.9-desktop/PocketAgent.Desktop.exe`: desktop/server executable.
- `artifacts/PocketAgent-0.1.9-android.apk`: signed Android client for Android 8 or later (existing clients remain compatible).
- Matching `.sha256` files verify artifact hashes. No public release has been uploaded.

Extract the Windows ZIP and run `PocketAgent.Desktop.exe`. No .NET SDK/runtime installation or Node.js is needed for the self-contained package. Copy the APK to the Android device (for example over USB), open it, and allow that trusted file source to install the app. The signed release is not a browser wrapper. Keep the original signing identity if rebuilding updates; changing signing keys requires uninstalling an existing Android installation and pairing again.

The older console server must stop before the desktop app uses the same port/runtime directory. Closing/stopping the server ends its provider process and active work. Pending schedules wait until the server runs again. Saved conversation references remain in runtime storage and Codex retains history. An update does not promise to preserve in-flight execution.

For an upgrade from 0.1.3, finish active work, close the old desktop app, extract 0.1.4 into a separate folder, and run its executable. Install the 0.1.4 APK over the existing app; the signing identity is unchanged. Six-digit pairing requires both updated apps for new connections. Existing paired-device credentials, folders and history remain available; no uninstall or database reset is required. Restarting a temporary tunnel changes its address and requires pairing at the new origin. GitHub Releases and built-in version checks are requested future work, not implemented update mechanisms.

## Desktop setup and permissions

1. Open **Settings**, choose an existing repository, and leave **Codex executable override** blank to prefer the installed VS Code Codex extension. The app shows which binary was discovered. It launches a separate PocketAgent-owned process; it does not attach to IDE chats or copy the binary into a release.
2. Choose **Use Codex settings**, **Ask for approval**, or **Full access**. Default is inheritance. Full access requests unrestricted filesystem/network execution with command approval policy never. Ask for approval requests workspace writes, restricted network access and on-request approvals. Managed restrictions remain authoritative.
3. Save while stopped and click **Start server**. The permission selector is locked while running; stop/save/start to change it. Model and reviewer are preserved. Global Codex configuration is not edited.
4. **Sessions** provides top conversation tabs. + opens working-folder and optional model selection, without asking for a name. Names default to the chosen folder, with suffixes for additional conversations. Browse or enter a different existing local folder for each session. Rename later. The context row above the composer shows the host, working directory and provider-reported sandbox/approval policy after start/resume. Reading an old transcript alone does not establish its current provider policy.
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

## Direct internet (IP and port)

Version 0.1.4 supports HTTPS directly from the Android client to the desktop through a manually opened TCP port. There is no third-party traffic relay or required domain. This version uses IPv4; your home router must have a public IPv4 address you can forward. A private/CGNAT WAN address needs ISP/router changes first.

1. Finish active work and stop the server. Open **Settings > Set up direct internet (IP + port)**.
2. Choose the desktop's private LAN address. **Detect public IP** optionally queries `https://api.ipify.org?format=json` without credentials or chat data. Alternatively enter the router WAN IPv4 manually. Choose an external chat TCP port (default 5147) and validation LAN port (default 5080). Reserve the desktop LAN IP in the router.
3. Manually forward **external TCP port → desktop LAN IP, internal TCP port** in the router. Allow the internal TCP listener through Windows Firewall on the appropriate network profile. The app does not create these rules. Double NAT requires both routers to be configured; an ISP's CGNAT generally needs a public IPv4 from the ISP. Detection identifies the outbound address, not whether that address belongs to a controllable router. A VPN can report its exit IP. If detection fails or reports the wrong route, enter the router WAN address manually.
4. Also forward **router external TCP 80 → desktop LAN IP, TCP 5080** (or the chosen validation port), and allow that LAN port through Windows Firewall. Public TCP 80 is required; remapping a different public port will not work. Accept the linked Let's Encrypt Subscriber Agreement, then click **Set up trusted HTTPS**. Setup first validates with staging, then requests a production IP certificate. A failure preserves your saved configuration. Both ACME stages can take a few minutes. No certificate needs transferring to Android.
5. Start the server. Open **Devices > Connection diagnostics**. A passing local result verifies the listener, HTTPS certificate/public-IP identity, exact Host and API, and displays the forwarding target. It explicitly does **not** certify router/firewall settings or outside access.
6. On the phone, add a server using `https://<public-IP>:<external-port>` (bare `IP:port` also selects HTTPS). **Turn Wi-Fi off** and tap **Test connection** before pairing. A successful cellular test verifies the outside route; some routers cannot reach their own WAN address from inside Wi-Fi. Enter the fresh six-digit code to pair.

Only the paired conversation API is exposed; permissions, executable configuration and device administration remain local. Closing a tunnel is unnecessary for normal direct operation because none is started. If you are switching from a temporary tunnel, stopping the old server also stops its connector. Phone credentials remain bound to their exact origin. A changed public IP needs direct setup and phone enrollment again; no dynamic DNS or address-update directory is required or implemented. Keep private runtime keys and databases when upgrading, and never include them in releases.

The server must run periodically to renew: IP certificates last about 160 hours and renewal starts halfway through validity. Keep the TCP 80 forward in place; its isolated listener runs only during validation. **Devices** shows expiry/renewal failures. Renewal reloads the certificate for new TLS connections without restarting agent work. After a failure the server retries in six hours; once expired, phone TLS fails closed. There is no Windows background service or renewal while the app is closed/asleep. Your public IP must stay the same; an IP change requires setup again and a new phone server address. The public certificate/IP appears in Certificate Transparency logs. Let's Encrypt receives certificate requests/validation traffic, never conversation traffic or private keys. TCP 80 blocked by the ISP makes this HTTP-01 setup unavailable; TLS-ALPN on 443 is not implemented.

The certificate covers the public IP only. On home Wi-Fi, connecting to that same public address needs router NAT loopback support; otherwise use cellular or the advanced manual Wi-Fi fallback. A private LAN IP will not match the public certificate.

Headless settings add `PocketAgent__DirectOrigin=https://<public-IP>:<external-port>`, `PocketAgent__AutomaticCertificate=true` and `PocketAgent__AcmePort=5080` alongside the private `ListenAddress`, internal `Port`, and the existing issued `CertificatePath`, with `PublicOrigin` empty. Initial trusted issuance is performed by the desktop wizard. `--setup` remains the manual private-CA fallback. `--diagnose` runs local HTTPS checks against a running relay. Old tunnel UI, connector process/helper and packaged cloudflared have been removed; no saved keys, certificates or databases are deleted.

### When an existing Caddy server already uses ports 80/443

Keep the existing router TCP 80/443 forwards to Caddy. Add an HTTP site for the public IP to the existing Caddyfile, with only the challenge path proxied to PocketAgent. Replace the example public IP `203.0.113.10` and desktop LAN IP `192.168.1.50` with real addresses:

```caddyfile
http://203.0.113.10 {
    @pocketagentChallenge {
        method GET
        path /.well-known/acme-challenge/*
    }
    handle @pocketagentChallenge {
        reverse_proxy http://192.168.1.50:5080 {
            header_up Host 203.0.113.10
        }
    }
    handle {
        respond 404
    }
}
```

This is an example to merge with the actual Caddy configuration, not a replacement Caddyfile. If an HTTP site already handles that exact IP, merge the challenge handler there instead of creating a duplicate. Use `handle`, not `handle_path`: the full challenge path must reach PocketAgent. The explicit `http://` site does not ask Caddy to obtain its own IP certificate. Existing domain sites and HTTPS stay configured as they are. Preserve the public-IP Host header; PocketAgent rejects the backend LAN address as Host. The Caddy machine/container needs network access to the desktop's validation port, and Windows Firewall should allow that source (5080 can be limited to the Caddy machine's LAN IP).

Validate the complete resulting file with `caddy validate --config /path/to/Caddyfile --adapter caddyfile` before reloading it. Reload through your existing Caddy deployment process. Then run PocketAgent's trusted HTTPS setup. A stopped challenge listener returns a proxy failure; that is expected outside issuance/renewal. Test successful validation during the wizard, not by expecting a permanent challenge page. This template has been checked against the [Caddy directive documentation](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy); it has not been applied to or verified on the user's home server.

Chat uses a separate router forward (for example external TCP 5147 to the desktop's TCP 5147), so its encrypted connection still goes directly to the desktop. Only public certificate-validation requests pass through the user's Caddy server. Caddy must also be reachable during renewals. Changing the public IP requires updating this IP-specific Caddy route as well as PocketAgent/phone configuration. No new public port 5080 forward is needed in this arrangement.

## Temporary internet trial

Historical instructions for 0.1.3–0.1.7 only; these controls and the connector were removed in 0.1.8. Use [direct internet](#direct-internet-ip-and-port) for current packages.

For a trial without buying/configuring a domain or creating a Cloudflare account:

1. Stop the server and choose **Settings > Prepare internet listener**. This saves loopback HTTP and clears direct-LAN certificate/public-origin settings.
2. Start the server, then click **Start temporary internet** and accept the desktop exposure confirmation.
3. In **Devices**, copy the generated `https://...trycloudflare.com` address. Enter it and a fresh pairing code in Android. This route is usable from Wi-Fi or cellular without installing a private CA or opening router ports.

The packaged connector creates an outbound tunnel. Cloudflare terminates public TLS and can process conversation traffic; provider credentials remain local. The URL changes whenever a new temporary tunnel is created and stops when the connector stops. A changed origin needs pairing again; saved credentials are never forwarded to a different origin. Quick Tunnels have no uptime guarantee and are for testing/development. Native polling avoids their SSE limitation. See [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/).

No public tunnel was started during automated verification. Actual internet/cellular access remains a user acceptance check.

## Stable internet connection

The tunnel instructions below are historical (0.1.3–0.1.7). Current desktop packages use [direct internet](#direct-internet-ip-and-port).

The default product direction is [direct IP and port](#direct-internet-ip-and-port), with no personal domain requirement, central PocketAgent service, or third-party traffic relay. The following named tunnel remains an optional setup for users who choose that intermediary.

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

Tap the server name or **⋮ > Servers** to switch saved machines or add a new one. Each origin has its own encrypted device token, tabs, draft and uncertain-delivery state. Adding a connection opens pairing; unpairing one server does not remove other saved servers. Long-press a conversation tab for Rename/Archive. + opens working-folder and optional model choices; it never asks for a name. **Browse folders** navigates the home machine's local drives; **Use this folder** selects the current directory. You can also enter an absolute desktop path. Paired owner devices can select existing local directories accessible to the desktop account; host permission settings remain desktop-owned. A conversation's folder stays fixed after creation. The desktop Settings repository remains the creation default. There is no sidebar or decorative footer.

Use **⋮ > Choose model**, or tap the context row above the composer, to select from that server's available models. Finish or stop work before changing it. The choice applies to this conversation's future messages, including queued messages when they dispatch. Other conversations and global Codex settings keep their choices. An omitted creation choice uses Codex settings; an unavailable model is never silently substituted. Desktop has the equivalent model button beside the title.

Use **⋮ > Archive conversation** to remove an idle conversation from the tab bar. The confirmation explains that pending scheduled messages are canceled. Active/unconfirmed work and in-flight schedule delivery must finish first. **⋮ > Archived conversations** offers Restore. History remains intact, and restoring does not resend canceled jobs. Desktop has Archive beside the title and Archived beside the tab bar. Provider transcripts are not permanently deleted.

Your messages use chat bubbles aligned to the trailing edge; assistant prose uses the plain transcript background. Messages use local timestamps, grouped by minute; older history without trustworthy timing remains untimestamped. Version 0.1.4 explicitly decodes Codex subprocess output as UTF-8, fixing corrupted punctuation, accented letters and emoji from Windows native launches. The bottom context row displays status, model, host/directory and elapsed/completed work time. A running conversation changes Send to Steer. New input augments that expected active turn; if the turn changed, the draft remains for explicit resubmission. Typing while a send is acknowledged is preserved. Closing the phone does not stop work. Returning to the app reconciles history and requests without replaying input.

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

Version 0.1.9 verification: `dotnet run --project tests/PocketAgent.DesktopChecks -c Release --no-restore` reproduced the native `#F0F0F0` scrollbar before the fix, then passed with dark tracks, rendered-screen checks, and themed confirmation Yes/dismiss behavior. Solution Release build and Android debug/release build/lint/signing passed. DWM requests dark native title bars on supported Windows versions; unsupported attributes fall back to OS behavior. Windows-owned file/folder pickers retain the OS theme. The existing-Caddy example is documentation only and requires validating the user's complete configuration before reload; no home-server configuration or router rule was changed.

Version 0.1.8 validation: warning-free solution Release build; focused `tests/PocketAgent.NetworkChecks` (local ACME fixture, challenge isolation, staging/failure preservation, renewal timing, public-trust rejection and real Kestrel certificate hot reload); `tests/PocketAgent.DesktopChecks` (native effective colors on all screens and dropdowns, transcript render budget, removed tunnel controls); focused `tests/direct-internet.test.mjs` using the Release server DLL; Android debug/release build, lint and signature verification. No full suite, live issuance, router changes or public deployment was performed. A first direct-network test invocation accidentally selected an older Debug DLL; selecting the built Release DLL passed both checks. A TLS regression exposed an incompatible selector/context combination and passed after using the per-handshake certificate context hook.

Build Release first and point focused checks at its DLL; an older running Debug console can lock Debug output.

```powershell
$env:POCKETAGENT_TEST_SERVER_DLL = (Resolve-Path src/PocketAgent.Server/bin/Release/net10.0/PocketAgent.Server.dll).Path
node --test --test-name-pattern='relay access and conversation lifecycle|pairing attempt limits|approval scopes|provider resolution' tests/relay.test.mjs
node --test --test-name-pattern='LAN certificate setup|HTTPS pairing uses generated certificate' tests/relay.test.mjs
node --test --test-name-pattern='native pairing naming|public HTTPS origin' tests/native.test.mjs
node --test tests/permissions.test.mjs
node --test tests/conversation-options.test.mjs
node --test tests/direct-internet.test.mjs
# Focused fake-HTTP lookup checks; no address service is contacted.
dotnet restore tests/PocketAgent.NetworkChecks/PocketAgent.NetworkChecks.csproj --locked-mode
dotnet run --project tests/PocketAgent.NetworkChecks/PocketAgent.NetworkChecks.csproj -c Release --no-restore
# Native WPF readability regression against isolated settings, without a provider/server startup.
dotnet restore tests/PocketAgent.DesktopChecks/PocketAgent.DesktopChecks.csproj --locked-mode
dotnet run --project tests/PocketAgent.DesktopChecks/PocketAgent.DesktopChecks.csproj -c Release --no-restore
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

Version 0.1.4 focused checks cover six-digit single-use pairing and saved-device survival, independent folders/models and workspace-write roots across resumes, legacy database migration, archive/cancel/restore without replay, and UTF-8 prompt/stream round trips. Direct-internet checks use documentation IPs with local sockets: they verify exact Host/Origin handling, public-IP SAN and CA validation, differing external/internal ports, rejected invalid/cleartext/mixed settings, and a failing certificate/IP mismatch. No public endpoint is contacted or opened by those checks. The native Windows Unicode regression failed before explicit process UTF-8 and passed afterward. The native Android focus regression failed before coordinate scrolling and passed afterward, preserving draft, focus and cursor while delayed assistant output arrived. Native Android also exercises folder/model selection, archive/restore, distinct bubbles and unpaired connection diagnostics. Windows/Android builds and lint pass; full test suites and live inference tests were not run.

The user reported physical Android installation and the earlier temporary tunnel connection working. Remaining acceptance for the new build: direct certificate installation/trust on the phone, manual firewall/router configuration, a successful cellular Test connection, and real-provider turns/steering/approval using the new conversation controls. `tests/live.test.mjs` remains opt-in and has not been run. The user's running 0.1.3 desktop is not silently restarted or replaced by the staged 0.1.4 build. A public IP is only needed when configuring an installation; it is not needed to build or distribute the packages.

An upgrade regression also reproduced schema migration before the runtime lease was checked. Host ownership is now acquired before constructing the database; the regression passes and confirms a rejected second version leaves an active older host's schema untouched. Stop the old host before starting the new one for the actual upgrade.

Version 0.1.5 lookup checks use a fake HTTP handler to verify the fixed HTTPS endpoint and absence of credential/chat headers or bodies; valid IPv4; rejected malformed/private/CGNAT/IPv6, oversized known/unknown-length, redirects and service failures; and cancellation. The native desktop check opens setup and verifies the Detect public IP control/disclosure without clicking it or making a real lookup. The live lookup service and router/cellular path remain runtime acceptance checks. The current 0.1.4 installation is left running while 0.1.5 is staged.

The 0.1.6 readability check reproduced an enabled transcript inheriting black text from the tab theme, then passed after setting the transcript's inherited foreground to `#C1DAA6`. It renders actual WPF user/assistant/tool/approval text and verifies that timestamps, errors and buttons retain their separate colors. The user's running 0.1.5 server is not interrupted by this isolated check or the build. No full suite or live inference is needed for this visual fix.
