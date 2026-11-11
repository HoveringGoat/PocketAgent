# Runtime architecture

## Components and ownership

PocketAgent 0.1.3 has a native Windows WPF desktop/server app and a native Android client built with Android platform widgets. There is no browser client or WebView. The desktop embeds an ASP.NET Core relay, owns one local Codex app-server subprocess over standard input/output, and stores local metadata in SQLite. Android uses foreground HTTPS snapshot polling. The retained SignalR API is compatible with earlier relay consumers but is not required by either native UI.

Only conversations created by PocketAgent and registered for the configured repository are exposed. Existing IDE-owned sessions are not attached. One repository is configured per desktop host; a phone can pair with several different desktop hosts. Provider execution, repository access, provider credentials, and provider-owned transcript history remain on the desktop.

Desktop administration calls local services in-process. The network API exposes paired conversation operations, usage, and schedules, not permission settings, arbitrary repository paths, executable settings, pairing-code generation, or device administration. All paired devices act as the same desktop owner across the host's PocketAgent conversations; separate user roles are not implemented.

## Provider discovery and permissions

`CodexExecutableResolver` prioritizes an explicit executable override, then the highest installed stable VS Code `openai.chatgpt-*` extension's architecture-matching native binary, then a native CLI on PATH/npm locations. The default extension root is the current user's `.vscode/extensions`; `CodexExtensionsDirectory` can override it. No extension files are copied, changed, or redistributed. An already running provider process keeps its selected executable until restart. The model is never silently replaced.

`HostSettings.PermissionMode` is a desktop-owned startup setting:

| Value | Behavior |
| --- | --- |
| `inherit` | Omit policy overrides and use Codex settings. Default. |
| `ask` | Request workspace-write with restricted network access and on-request approvals. |
| `full-access` | Request danger-full-access and approval policy never. |

`CodexPermissions` applies an explicit selection to thread start/resume and each new turn. Steering retains the active turn's policy. Global Codex configuration, model, and reviewer are not rewritten. Managed provider restrictions remain authoritative. Provider rejection is surfaced without falling back to a weaker mode. The conversation snapshot distinguishes the selected mode from sandbox/approval fields reported by the provider. Old transcript reads do not resume execution or establish current permissions.

The native desktop selector is disabled while the host runs. Stop, save, and restart to change the mode. This changes PocketAgent-owned conversations, not a separate parent agent, IDE, or terminal session. Permissions determine command access; repository instruction files can still require a conversational planning approval.

## Authentication and device access

A desktop-generated eight-digit code is valid for ten minutes and consumed once. It can be renewed locally without stopping work. Pairing is rate-limited per source address and globally; a proxy cannot evade the global cap by changing forwarded headers. Native `/api/devices/pair` returns a unique random bearer token. Only its SHA-256 hash is stored in SQLite. Device sessions expire after thirty days and can be individually revoked on the desktop.

All protected operations authenticate on every request. Native mutations require `X-PocketAgent: 1`. Any supplied browser Origin must exactly match a configured origin; CORS is not enabled. The legacy cookie pairing/logout path remains for API compatibility checks, with HttpOnly, SameSite Strict and HTTPS Secure cookies. There is no browser UI behind it.

Android stores saved server names, origins and tokens in an AES-GCM encrypted vault using a non-exportable Android Keystore key. Tokens are tied to their exact origin; changing or adding an address requires pairing at that address. Redirects are rejected before credentials can be forwarded. Each host has separate selection, draft and uncertain-delivery state. Backups and device transfers exclude app data. Draft text is private app-local state and is not packaged. A revoked server returns to pairing without deleting other saved server connections.

## Network exposure and trust

The initial listener is loopback HTTP at `127.0.0.1:5147`. It is reachable only on the desktop. Loopback is the origin for local development and internet connectors; cleartext LAN listening is rejected. Host validation accepts only the exact listener authority and, when configured, the exact HTTPS `PublicOrigin` authority. Forwarded headers are not used to authorize identities or weaken transport checks.

### Direct Wi-Fi

Desktop **Set up Wi-Fi HTTPS** enumerates private IPv4 interfaces and creates a CA/server certificate for the selected address. The PFX remains under the private runtime directory; only the public CA certificate goes to the phone. Android accepts platform-trusted certificates and CAs explicitly installed by its owner. It never disables hostname or chain verification. This deliberate local-CA support is documented beside the narrow Android lint suppression.

The setup action saves the listener/certificate settings and opens the certificate directory. It does not modify firewall rules, trust stores, or routers. Windows private-network firewall access may be needed; guest-network peer isolation can still prevent access. A changed IP requires a matching certificate. On Windows the HTTPS loader uses the current user's key store for Schannel compatibility.

### Internet access

The desktop package includes checksum-verified cloudflared 2026.10.7 and its license. The connector is a supervised hidden child process started by an explicit desktop action. Builds and installation do not publish anything. Credentials/configuration remain outside the package. Public start requires a desktop confirmation. Stopping the server stops its supervised connector.

**Named tunnel:** the default durable deployment is a user-owned Cloudflare account/domain and named outbound tunnel with a stable HTTPS hostname. Its ingress forwards to the loopback relay. PocketAgent can generate the local config from an existing tunnel UUID and credential file, but does not provision DNS, create accounts or copy secrets into artifacts. Cloudflare owns the external edge; the user owns tunnel/DNS configuration and account costs; this repository owns application code, packaging and connector lifecycle.

**Temporary trial:** Quick Tunnel needs no account/domain and produces a temporary HTTPS `trycloudflare.com` address. The connector parser accepts only that exact authority, never wildcard hosts. The runtime public origin updates to that address and is cleared on connector stop. The address changes on restart and has no uptime guarantee; saved clients must pair at a new origin. Foreground polling avoids Quick Tunnel's SSE limitation. This is a documented development exception, not the stable hosting model.

Cloudflare terminates public TLS and can process relayed conversation data. This is not end-to-end encryption through the edge. Provider credentials never cross it. A user-managed HTTPS reverse proxy such as Caddy is an alternative for users who also manage DNS, inbound reachability and certificates. Public-origin proxy mode requires a loopback backend. Do not add a public listener or trust arbitrary proxy headers to make a connection work.

## Conversations, steering and recovery

`Conversations` serializes state mutations. Exactly one provider turn runs per conversation. Idle input starts a new turn; additional active input uses `turn/steer` with the sender's expected turn ID. A stale ID is rejected and never silently converted into a new turn. An unscoped second turn is rejected. Steering does not answer an approval, modify policy, or cancel work.

Both native clients leave the composer editable during submission and clear only the exact acknowledged draft. Typing after Send is preserved. Neither prompts nor approval decisions are automatically replayed. Uncertain native delivery is persisted per origin/conversation and requires the user to review before resubmitting. Reconnection refreshes authoritative state; phone disconnection does not stop desktop execution. Provider disconnect clears pending controls and requires a host restart.

Supported command/file approvals are restricted to available one-time decisions. Broader grants, unsupported host interactions and unknown provider fields are not automatically approved. Questions require explicit answers. Pending requests retain their original JSON-RPC and conversation identity; resolved requests cannot be answered again.

Codex owns transcripts. SQLite stores PocketAgent references, device hashes, timing metadata and schedules. Live message timestamps are persisted on first observation and restored from that metadata. Provider turn times support work durations, but are not invented as individual message times for older history. Unknown historical message times remain unknown. Turn starts/completions/durations are retained separately and surfaced as active elapsed time or completed work duration. Clients group adjacent chat times by local minute. Status/context controls remain between the scrolling transcript and composer. Session navigation uses top tabs.

## Usage and scheduled messages

`ProviderUsage` reads `account/rateLimits/read` through the same provider connection and accepts rate-limit updates. It exposes only normalized remaining percentage, duration, reset time and availability. Account IDs, plan/credit details and upsell payloads stay private. Five-hour and weekly labels require reported 300-minute and 10080-minute windows; unsupported/missing data is shown as unavailable. Read results are briefly cached. No credits or earned resets are purchased/consumed by PocketAgent.

`ScheduledMessages.Send` checks known exhausted windows. A blocked prompt is persisted and acknowledged as queued, with the reported reset time or an explicit unknown-reset state. The latest reset among exhausted windows is used. A later provider `usageLimitExceeded` failure also defers an accepted prompt only when no work is known to have executed. It never automatically repeats partial work or an uncertain submission.

Manual schedules accept an explicit UTC timestamp converted from the client's chosen local date/time. Jobs have requested time, next attempt, reason and status. The worker waits for host readiness and an idle, confirmed conversation. Active turns and unanswered approvals postpone dispatch; schedules never become implicit steering or approval decisions. Jobs for other repositories remain dormant until that repository is hosted. Expired/revoked devices cancel their pending jobs; desktop-created jobs have no device expiry dependency.

Dispatch claims a pending job atomically in SQLite before contacting Codex. A confirmed acceptance marks it submitted; this is not a promise of successful agent completion. Known provider rejections become failed/pending as appropriate. Lost acknowledgements become uncertain. A host restart converts in-flight dispatching jobs to uncertain and never automatically replays them. A per-data-directory host lease prevents two new hosts from dispatching the same queue. Missed due times are reconsidered when the desktop runs again. There are no OS wake timers or cloud workers: a sleeping/stopped desktop cannot dispatch.

## Build, distribution and change points

`scripts/Build.ps1 -Publish` produces a self-contained Windows x64 desktop directory/ZIP, with compiled relay, connector, setup docs and the signed Android APK. Users do not need a .NET SDK, Node.js or a source checkout. `Build-Android.ps1` builds/lints Android and signs using a local ignored key; its private key/password never enter either package. Preserve that signing identity for upgrade compatibility. No installer registration, automatic startup, code-signing service, cloud deployment or automatic update service is implemented.

Runtime state remains in the user's LocalAppData directory across artifact replacement. Artifacts are versioned; do not deploy an unqualified latest tag. Application CI is not implemented; any future CI must use short-lived/federated authentication. External tunnel provisioning/DNS remain external infrastructure configuration, not application secrets.

- `src/PocketAgent.Server`: API, provider protocol, policy mapping, authentication, SQLite, timing and scheduler.
- `src/PocketAgent.Desktop`: native control/chat UI, Wi-Fi setup and connector lifecycle.
- `src/PocketAgent.Android`: native multi-server client, encrypted vault, transport and debug-only instrumentation.
- `scripts`: repeatable builds, signing, publishing, connector acquisition and local launch.
- `tests`: isolated deterministic protocol/security/scheduler checks and emulator tests. Live inference requires explicit opt-in.

See [setup and verification](SETUP.md) for commands, artifacts and remaining acceptance checks.
