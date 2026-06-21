# MyLocalBackup - Project Context

## Overview

MyLocalBackup is a free versioned local backup tool for Windows (freeware). No activation, no trial, completely free to use. Source code is visible but not open-source: no rights to modify, redistribute, or sell.

## GitHub Account & Repository

- **Owner**: `gkaragioul`
- **Source code and releases repo**: `gkaragioul/My_Local_Backup`
- Updates are distributed through the main repo's GitHub Releases tab. The app opens the latest release page; it does not check, download, verify, or run installers itself.

## Release Process

Never do manual build steps. Always use the release build script.

When creating a new release:

1. Bump the version in `MyLocalBackup.UI/MyLocalBackup.UI.csproj` (`<Version>` tag).
2. Run the release build script: `powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z`
   - This handles publish, Files.wxs regeneration, MSI build, Burn bundle EXE build, and copying the EXE to Desktop.
   - The script prints both hashes for release verification.
3. Update `README.md` so features, tech stack, and descriptions reflect recent changes.
4. Commit and push:
   - `git add` changed files
   - `git commit`
   - `git push origin main`
5. Create the GitHub release in the main repo:
   - `gh release create vX.Y.Z --repo gkaragioul/My_Local_Backup ... Staging/MyLocalBackup_Setup/MyLocalBackupSetup.exe#MyLocalBackupSetup.exe Staging/MyLocalBackup_Setup/MyLocalBackupSetup.msi#MyLocalBackupSetup.msi`
6. Include EXE and MSI SHA256 hashes in release notes.
7. Update the repo description/about via `gh repo edit` if the release includes significant new functionality.

## Critical Rules

- Never install, uninstall, or run the MSI/EXE yourself. The user handles installer execution on their machine.
- Never run `msiexec` or installer commands. The build script copies the EXE to Desktop.
- If `Files.wxs` is stale, the release script auto-regenerates it.
- No obfuscation: source is publicly visible, but the project is freeware, not open-source.
- Do not add an in-app updater, background update checker, installer downloader, or hidden installer launcher.

## Version History

Versions follow the `0.X.Y` pattern. Check the latest tag or `.csproj` for current version.

- v0.9.15: Remove assisted in-app updater; updates are manual through GitHub Releases.
- v0.9.14: Test release for validating update discovery from v0.9.13.
- v0.9.13: Point updater and release links at `gkaragioul/My_Local_Backup`.
- v0.9.12: Assisted update test release with a real version-bumped installer.
- v0.9.11: Test release for validating main-repo auto-updates.
- v0.9.10: Switch in-app updates to the main repo Releases tab.
- v0.9.9: Fix tray "Backing up now" notification with no destination drive.
- v0.9.3: Fix duplicate shortcut (`COMMONDESKTOPFOLDER` SetProperty) and switch in-app updates to MSI.
- v0.9.2: Fix duplicate desktop shortcut and auto-relaunch after in-app update from v0.8.x.
- v0.9.1: Switched installer to per-user, fixing in-app update ".NET Desktop Runtime" error.
- v0.9.0: Parallel file processing, background retention, configurable exclusions.
- v0.8.x: Earlier releases with per-machine installer, superseded.

## Build Commands

- Full release build: `powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z`
- Dev build only: `dotnet build MyLocalBackup.UI/MyLocalBackup.UI.csproj`

## Project Structure

- `MyLocalBackup.Core/` - Backup engine, database, configuration, models
- `MyLocalBackup.UI/` - WPF desktop application
- `Staging/MyLocalBackup_Setup/` - WiX source files (`Package.wxs`, `Files.wxs`, `Bundle.wxs`)
- `release-build.ps1` - Automated release build script

## Key Technical Notes

- Target framework: .NET 9.0 (Windows)
- Database: SQLite via Microsoft.Data.Sqlite
- UI: WPF with dark-mode-only theme
- Installer: WiX v5 Burn bundle EXE wrapping MSI
- Install scope: per-user to `%LOCALAPPDATA%\Programs\MyLocalBackup`, no admin/UAC required
- Hard link deduplication for backup storage (NTFS only)
- Self-contained publish bundles the .NET runtime
- Updates are manual: users download the latest installer from GitHub Releases and run it.

## Installer Architecture - Per-User Only

The MSI must stay `Scope="perUser"`. Never change this to per-machine.

Per-user installs avoid UAC and keep the upgrade path predictable. Newer installers replace the existing per-user install through WiX `MajorUpgrade`.

Other invariants:

- `UpgradeCode` in `Package.wxs` is `D1E2F3A4-B5C6-4D7E-8F9A-B0C1D2E3F4A5`; never change it.
- Registry keys in shortcuts must use `Root="HKCU"`, not HKLM.
- Upload the EXE installer to the main repo GitHub release for users to download manually. Uploading the MSI as a secondary asset is acceptable for parity with older releases.

## Migration Note

Versions before v0.9.1 used a per-machine install to `%ProgramFiles%\MyLocalBackup`. The new per-user installer cannot auto-remove that old install. Users should uninstall the old version via Windows Settings > Apps > MyLocalBackup, then install the new version.

Older versions that still pointed at the removed secondary releases repo cannot discover new main-repo releases in-app. Those users need to install the latest GitHub Releases installer manually.
