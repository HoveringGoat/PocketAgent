# Release convention for another repository

This is a small, reusable convention for projects that publish more than one
client or platform. It keeps release notes, versions, artifacts, and commits
consistent without making every source change an automatic commit.

## 1. Keep platform versions explicit

Choose one authoritative version source per deliverable. For example:

- .NET desktop/server: `Directory.Build.props` contains `<Version>0.3.1</Version>`
- Android: `app/build.gradle` contains `versionName` and a monotonically increasing
  `versionCode`

The release script should read these values and reject a requested version that
does not match the target platform. This prevents publishing an artifact with
the wrong version label.

Independent platforms may use different versions. Use the same version only
when both platforms are intentionally releasing together.

## 2. Use one changelog per platform

Keep durable changelogs in the repository, for example:

```text
CHANGELOG-desktop.md
CHANGELOG-android.md
```

Use a release section with an exact version heading:

```markdown
## 0.3.1 - 2026-11-11

- Restored the session metadata display.
- Grouped the mobile overflow menu into smaller sections.
```

An `Unreleased` section is fine while work is in progress. Before publishing,
rename it to the version and date. The release script should extract only the
matching section and use it to generate the GitHub release notes.

## 3. Make release commits opt-in and guarded

Use an explicit switch such as:

```powershell
.\scripts\Release.ps1 -Version 0.3.1 -Target Android -Publish -CommitChanges
```

`-CommitChanges` should:

1. inspect the worktree;
2. refuse private-looking files such as `.env`, keys, certificates, and
   keystores;
3. stage the intended release changes;
4. create a predictable commit such as `Release v0.3.1`;
5. push the current branch before publishing.

Without `-CommitChanges`, the script should never create a commit or push. A
release must be built from a clean worktree whose remote branch resolves to the
commit used for the artifacts.

This is repository tooling, not an agent-level instruction. It works for any
developer or CI runner that invokes the script.

## 4. Separate release channels with immutable tags

Give each platform a stable tag namespace so one platform's update check cannot
offer another platform's package:

```text
desktop-v0.3.1
android-v0.3.1
v0.3.1                 # coordinated all-platform release, if needed
```

The updater should query only its own tag namespace. Never move an existing
published tag to different source; publish a new patch version instead.

## 5. Publish versioned artifacts

Stage generated files under a versioned directory, for example:

```text
artifacts/releases/0.3.1/
  android/PocketAgent-0.3.1-android.apk
  PocketAgent-0.3.1-android.apk.sha256
  SHA256SUMS.txt
```

For each artifact, generate a SHA-256 checksum and include the checksums in the
GitHub release. Do not include runtime state, provider credentials, `.env`
files, private keys, or local databases in a distributable package.

## 6. Prerequisite and publish flow

Provide a read-only prerequisite check before a build:

```powershell
.\scripts\Release.ps1 -Version 0.3.1 -Target Android -Check
```

The check should validate the platform SDK/toolchain, signing setup, and (for
publishing) GitHub CLI authentication. A typical one-time GitHub setup is:

```powershell
gh auth login
gh auth status
```

Then use this sequence:

```powershell
.\scripts\Release.ps1 -Version 0.3.1 -Target Android -Check
.\scripts\Release.ps1 -Version 0.3.1 -Target Android -Publish -CommitChanges -Clean
```

The script should build, sign, checksum, create the platform tag/release, and
verify that the remote tag points at the release commit. Keep the final GitHub
release URL in the handoff notes.

## Minimal implementation checklist

- [ ] One authoritative version source per platform
- [ ] Platform changelog with exact version headings
- [ ] `-Check` prerequisite validation
- [ ] Opt-in guarded `-CommitChanges`
- [ ] Clean-worktree and remote-source verification
- [ ] Immutable platform-specific tags
- [ ] Versioned artifacts and SHA-256 manifests
- [ ] No secrets or runtime state in packages
- [ ] Release notes extracted from the matching changelog section
