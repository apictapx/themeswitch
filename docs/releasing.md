# Releasing

Releases are built by GitHub Actions from a git tag — never from someone's PC — so every download
can be traced to a commit and rebuilt.

## Versioning

[Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`.

| Bump | When | Example |
|---|---|---|
| PATCH | Bug fix, no behaviour change you'd notice otherwise | wrong sunset in a time zone |
| MINOR | New feature or setting, old settings files still work | a new tray menu item |
| MAJOR | Something users must act on | settings format changes incompatibly |

While the version is `0.x`, minor bumps may still change behaviour. Pre-releases use a suffix:
`v0.2.0-beta.1` (published as a GitHub *pre-release*).

The version lives only in the tag. `Directory.Build.props` holds a `-dev` placeholder for local
builds; the release workflow passes the tag's version to `build.ps1`, which stamps it into the exe.

## Changelog

[`CHANGELOG.md`](../CHANGELOG.md) follows [Keep a Changelog](https://keepachangelog.com). Add entries
under `## [Unreleased]` as you go. The release workflow copies the section for the tagged version into
the GitHub release notes, and **fails if that section doesn't exist** — so release notes can't be
forgotten.

## Cutting a release

1. Make sure `main` is green in CI.
2. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [1.2.3] - YYYY-MM-DD`, add a fresh empty
   `## [Unreleased]` above it, and update the compare links at the bottom.
3. Commit: `git commit -am "Release v1.2.3"`
4. Tag and push:

   ```bash
   git tag -a v1.2.3 -m "ThemeSwitch v1.2.3"
   git push origin main v1.2.3
   ```

5. Watch the **Release** workflow (`gh run watch`). It:
   - checks the tag looks like `v1.2.3` / `v1.2.3-suffix`,
   - extracts the changelog section,
   - runs all tests (unit + end-to-end) on `windows-latest`,
   - publishes the framework-dependent single-file exe,
   - creates the GitHub release with `ThemeSwitch-v1.2.3-win-x64.zip` and its `.sha256`.

You can rehearse step 5 locally: `./build.ps1 -Version 1.2.3` and `./tools/changelog-section.ps1 -Version 1.2.3`.

## If something went wrong

- **Workflow failed before publishing:** fix on `main`, then move the tag:
  `git tag -d v1.2.3 && git push origin :refs/tags/v1.2.3`, re-tag and push.
- **A bad release is already out:** don't reuse the version. Ship `v1.2.4` with the fix and mark the
  bad release as such in its notes (or delete it with `gh release delete v1.2.3`).

## What's in a release

| File | |
|---|---|
| `ThemeSwitch-vX.Y.Z-win-x64.zip` | `ThemeSwitch.exe` (~200 KB, needs .NET 10 Desktop Runtime), README, LICENSE, CHANGELOG |
| `ThemeSwitch-vX.Y.Z-win-x64.zip.sha256` | SHA-256 of the zip, `sha256sum -c` format |

The exe isn't code-signed; see the README for the SmartScreen note. Signing (e.g. Azure Trusted
Signing) can be added to the workflow later without changing anything else.
