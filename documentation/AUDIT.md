# Code and flow audit

Audit date: 2026-11-11. Source baseline: `83aadb0`; published desktop/Android baseline: `v0.2.2`. This is a diagnostic review, not an implementation change or security certification.

## Scope and evidence

Reviewed pairing and authenticated transport, provider process lifecycle, conversation state and steering, schedules, native rendering, updater packaging/handoff/recovery, and release provenance. Reviewed source and the existing 0.2.2 package. No full test suite, live agent turn, phone test, installation replacement, public write, or new release was performed.

Implementation pass begun 2026-11-11: updater packaging/restart/rollback changes and release-source checks are now in the working tree. The focused solution build and a fresh desktop publish completed successfully; the packaged updater now launches far enough to validate its arguments. A full installed upgrade and failure-injection rollback remain to be tested on a disposable installation.

Two failures were independently verified:

- Launching `artifacts/releases/0.2.2/desktop/PocketAgent.Updater.exe` without arguments exits with `The application to execute does not exist: ...PocketAgent.Updater.dll`. This fails before updater code can validate arguments or create its diagnostic log.
- `git ls-remote origin refs/tags/v0.2.2 refs/heads/main` reports `c37e90fd83c247e2242cbffeaefc3a4c6ff0d832` for both remote refs. The local release commit is `d15b998`. The published tag therefore does not identify the source used for the new binaries.

Other findings below are confirmed source paths; timing, Windows file-lock behavior, and real-device performance have not been reproduced end to end.

## Flow map

| Flow | Current implementation | Main boundary or failure mode |
| --- | --- | --- |
| Pair | Desktop QR/string transfers origin, public-key pin and one-use ticket; Android verifies TLS before exchanging the ticket for a token | Authentication is per request. All paired devices act as the owner, not separate restricted users. |
| Read | Android polls list, selected snapshot, schedules, and usage; desktop calls these services locally | First reads hydrate complete provider history under a shared conversation gate. |
| Send | Client adds a pending bubble; server checks usage and current activity, then starts a turn | Unknown delivery must be reviewed. Pending UI records are not correlated to canonical messages by a request ID. |
| Steer | Client includes expected active-turn ID; server uses `turn/steer` | A stale turn is rejected instead of being silently converted into new work. |
| Schedule | Persist pending job; wait for idle/provider/usage; atomically claim; send; mark submitted | Restarted in-flight jobs become uncertain rather than being replayed. Unexpected worker exceptions are not contained per job. |
| Update | Check GitHub; launch bundled updater; close desktop; download/checksum/extract/replace/restart/health check | Packaging, restart health arguments, replacement recovery, and pre-exit readiness are incomplete. |
| Release | Optional local commit; build/sign/package; create GitHub release with notes and assets | Release creation neither pushes the commit nor binds the tag explicitly to it. Platform versions still share one release channel. |

## Findings

### A1 - High: packaged updater cannot execute

`scripts/Build.ps1:24` publishes the updater into `.updater`, copies only `PocketAgent.Updater.exe` to the desktop root, then removes the publish directory. The updater project does not enable single-file publishing. The exe is a .NET apphost; its application DLL, dependency manifest and runtime configuration are discarded. The packaged launch failure above reproduces this directly.

`UpdateService.Launch` checks only that the exe exists, and `MainWindow.InstallUpdateCore` closes the desktop as soon as a process object is returned. Process creation is not proof that the updater initialized. This explains the reported close-without-update behavior. The earlier shell-launch diagnosis and claim that 0.2.2 fixed it were insufficient.

Implementation status: the build now publishes the updater as a self-contained single file, the desktop launches a temporary copy outside the install directory, and the packaged executable has passed a launch smoke check. A real upgrade remains unverified.

### A2 - High: successful restart cannot satisfy the health check

`src/PocketAgent.Updater/Program.cs:42` starts the desktop with no arguments, then waits for the supplied health-file path. `MainWindow` only writes that file when it received `--health-file` at startup. After A1 is corrected, a healthy installation will still time out and attempt rollback.

Implementation status: the updater passes `--health-file` and `--update-version`; the desktop writes a version/process marker on startup and the updater waits for it. Startup health is intentionally a UI-start check, not proof that the server/provider is ready; its 30-second timeout still needs an installed-flow test.

### A3 - High: replacement failure does not reliably restore the old installation

`src/PocketAgent.Updater/Program.cs:40` moves the old installation to a backup, then moves the extracted replacement into place. The general exception handler only logs and exits. Rollback runs solely after a successful restart followed by a failed health check. Failure of the second move or initial restart leaves the old version in the backup without restoring it. Moving a staged directory from the system temp volume to a portable install on another volume is one concrete failure trigger.

The updater also runs from inside the installation being moved, with that folder as its working directory. Windows locks must be exercised in a packaged test. Download/checksum failures happen after the desktop has already closed and do not restart it.

Implementation status: the desktop launches a temp copy, staging is created beside the install root, backup names are unique, and replacement exceptions attempt restoration. A full installed-flow test is still required. Installer-owned files remain a separate packaging concern.

### A4 - High: release tag points at unrelated source

`scripts/Release.ps1:314` creates a release without an explicit target commit or verified tag. `-CommitChanges` commits locally but never pushes that commit. The observed `v0.2.2` tag resolves to `c37e90f`, not release commit `d15b998`. GitHub source downloads therefore omit the changes compiled into the release assets.

Implementation status: publish now requires a clean worktree whose branch is synchronized with `origin`, pushes the commit when `-CommitChanges` is used, passes the exact commit to `gh release create --target`, and verifies the resulting tag. Existing public tag repair remains a separate explicit decision.

### A5 - Medium: Android leaves rejected submissions visible as pending

`MainActivity.submit` adds an optimistic item before submitting. The exception path in `action` clears uncertain-delivery state on a definite 4xx rejection but never removes or marks that item as failed. It remains a pending bubble while the draft is still present. Users cannot reliably distinguish rejected input from queued/accepted input.

`mergeOptimistic` also reconciles by matching text anywhere in history. Repeating an earlier prompt can consume the new pending item immediately. Desktop uses a similar text/time heuristic. A client message ID and explicit delivery states would make acknowledgment reliable.

Implementation status: mobile definite 4xx rejections now remove the corresponding optimistic item immediately, while 5xx/network failures continue to be shown as unknown delivery. Mobile and desktop reconciliation now require matching text plus a bounded timestamp and do not reuse the same canonical item for multiple pending entries. A server-issued/client-correlated message ID remains a future hardening step.

### A6 - Medium: long conversations eventually cannot load on Android

`Conversations.Snapshot` includes every accumulated item and tool output. `ApiClient.java:107` rejects responses exceeding 8 MiB, while the server has no snapshot pagination or matching response bound. A sufficiently large command output or conversation permanently trips that limit for the selected history.

Before that limit, Android rebuilds the entire transcript widget tree on each changed snapshot. Desktop bounds its rendered view but still receives/serializes the complete snapshot. Implementation status: snapshots are now bounded to the latest 200 items, text/detail fields are capped before serialization, and both clients show when older history is hidden. Provider hydration still walks the full provider history, so true server-side pagination/incremental rendering remains future work.

### A7 - Medium: one slow conversation operation blocks all conversations

`Conversations` uses one semaphore for every conversation, provider event handling, list/read operations, and mutations. It holds that semaphore while awaiting provider requests and all hydration pages. A stalled request can hold it for the provider timeout (60 seconds per request), preventing unrelated reads, steering and interruption from advancing.

This is a responsiveness risk demonstrated by lock scope, not a reproduced diagnosis of the earlier desktop lockups. Implementation status: uncached conversation reads now hydrate provider history outside the host-wide state gate and deduplicate concurrent reads. Conversation mutations and provider events now serialize on a per-conversation gate, while the host-wide gate protects only the state registry and event dispatch bookkeeping. Conversation creation and some list bookkeeping still perform small host-wide database work; slow provider calls are no longer held under that gate.

### A8 - Medium: manual update checks are disabled with periodic checks

Both the timer and the manual Check button call `CheckForUpdatesCore(false)`. That method returns immediately when `CheckUpdatesPeriodically` is false. Turning off background checks therefore disables the explicitly requested manual check too.

Implementation status: manual checks now bypass the periodic-check preference. This is covered by the source change but still needs a desktop UI smoke test.

### A9 - Medium: unexpected scheduler failures can terminate the host

The worker in `ScheduledMessages.ExecuteAsync` catches only `ApiException`. Database I/O errors, malformed provider payloads, or other unexpected exceptions can escape the background service. The host uses the default background-service exception behavior, which stops the application. If a job was claimed first, its outcome remains dispatching until restart converts it to uncertain.

Implementation status: unexpected per-job exceptions are now logged and contained; unclaimed jobs retry, while jobs already marked dispatching become uncertain rather than being replayed. Persistence failures are logged separately. Worker health metrics/endpoint remain future work. Do not blindly retry a request whose acceptance is unknown.

## Requirements still incomplete

- Desktop/server and Android versions are not independently released. Android now ignores a newer release unless that release contains the matching APK asset, but it still uses the shared latest-release endpoint and tag; fully independent release channels remain open.
- Android now keeps the lower context bar to status/work information and moves the server machine plus conversation working directory into the header location line. Model/reasoning remain available through the model control rather than being repeated in the status bar.
- Android truncates combined release notes at 1,200 characters; an Android section after lengthy desktop notes can be hidden. It offers the full release page, but does not filter notes by app.
- Documentation was corrected in this pass. Compilation/readability checks still do not establish a complete installed upgrade.

## Recommended order

1. Run an isolated packaged upgrade and failure-injection rollback; then repair the already-published `v0.2.2` provenance only with an explicit release decision.
2. Establish independent desktop/Android release channels and filtered release notes.
3. Add server-issued/client-correlated message IDs and true paginated history/incremental rendering.
4. Finish the requested Android context-bar layout and expose worker health diagnostics.

This audit produced two local implementation commits after the initial report: `8f21d71` (updater, delivery, bounded snapshots, scheduler and release checks) and `21e8a42` (per-conversation serialization and read hydration). No public release, tag repair, or full test suite was run.
