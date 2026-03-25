# MyLocalBackup - Project Context

## Overview

MyLocalBackup is a **free** versioned local backup tool for Windows (freeware). No activation, no trial — completely free to use. Source code is visible but not open-source (no rights to modify, redistribute, or sell).

## GitHub Account & Repositories

- **Owner**: `georgekgr12`
- **Source code repo**: `georgekgr12/MyLocalBackup` - contains all source code
- **Releases repo**: `georgekgr12/MyLocalBackup-releases` - contains only release tags with EXE installer assets (Burn bundle wrapping MSI). The in-app update checker (`UpdateService.cs`) queries THIS repo at `https://api.github.com/repos/georgekgr12/MyLocalBackup-releases/releases/latest`

## Release Process (MANDATORY — follow exactly)

**NEVER do manual build steps. Always use the release build script.**

When creating a new release:

1. **Bump version** in `MyLocalBackup.UI/MyLocalBackup.UI.csproj` (`<Version>` tag)
2. **Run the release build script**: `powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z`
   - This handles: publish, regenerate Files.wxs, MSI build, Burn bundle EXE build, copy to Desktop
   - The script outputs the EXE SHA256 hash — include it in the GitHub release notes
3. **Commit and push**:
   - `git add` changed files, `git commit`, `git push origin main`
   - `gh release create vX.Y.Z --repo georgekgr12/MyLocalBackup ...`
   - `gh release create vX.Y.Z --repo georgekgr12/MyLocalBackup-releases ... Staging/MyLocalBackup_Setup/MyLocalBackupSetup.exe#MyLocalBackupSetup.exe`
4. **Include SHA256 in release notes**: Format: `SHA256: <hash>` (the update checker parses this)
5. **Update README.md**: Update the version in the heading (`# MyLocalBackup (vX.Y.Z)`) and ensure the feature list, tech stack, and descriptions reflect any recent changes. Commit and push the README update.
6. **Update repo descriptions** if the release includes significant new functionality: update the GitHub repo description/about via `gh repo edit` if appropriate.

Both releases (source + releases repo) must be created. The app will only detect updates from the releases repo.

### Critical Rules

- **NEVER install, uninstall, or run the MSI/EXE yourself** — only the user installs and tests on their machine
- **NEVER run msiexec or any installer commands** — the build script copies the EXE to the user's Desktop; they handle it from there
- **If Files.wxs is stale** (DLL count mismatch after .NET SDK update), the script auto-regenerates it
- **No obfuscation** — source is publicly visible (freeware, not open-source)

## Version History

Versions follow `0.X.Y` pattern. Check last commit message or `.csproj` for current version.
- v0.9.1: Switched installer to per-user (fixes in-app update ".NET Desktop Runtime" error)
- v0.9.0: Parallel file processing, background retention, configurable exclusions
- v0.8.x: Earlier releases (per-machine installer — superseded)

## Build Commands

- **Full release build (USE THIS)**: `powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z`
- **Dev build only**: `dotnet build MyLocalBackup.UI/MyLocalBackup.UI.csproj`

## Project Structure

- `MyLocalBackup.Core/` - Backup engine, database, configuration, models, services
- `MyLocalBackup.UI/` - WPF desktop application (main entry point)
- `Staging/MyLocalBackup_Setup/` - WiX source files (Package.wxs, Files.wxs, Bundle.wxs)
- `release-build.ps1` - Automated release build script

## Key Technical Notes

- Target framework: .NET 9.0 (Windows)
- Database: SQLite via Microsoft.Data.Sqlite
- UI: WPF with dark-mode-only theme
- Installer: WiX v5 Burn bundle EXE wrapping MSI — **per-user install** to `%LOCALAPPDATA%\Programs\MyLocalBackup` (NO admin/UAC required)
- Hard link deduplication for backup storage (NTFS only)
- Self-contained publish (bundles .NET runtime — no separate .NET install required)
- Update security: SHA256 hash required in release notes, verified before install

## CRITICAL: Installer Architecture — Why Per-User (NEVER Change This)

The MSI **must** stay `Scope="perUser"` (installs to `%LOCALAPPDATA%\Programs\MyLocalBackup`). **Never change this to per-machine.**

**Why**: Per-machine installs require UAC elevation to write to `%ProgramFiles%`. The in-app updater runs the Burn bundle EXE from a hidden, non-interactive PowerShell process. When the bundle needs elevation, Windows must show a UAC prompt from that hidden context — this combination is unreliable and causes the installer to fail silently. The old app files get removed by MajorUpgrade but the new ones are never laid down, leaving the install broken. Result: "You must install .NET Desktop Runtime" error on relaunch.

Per-user install eliminates ALL of this — no elevation, no UAC, `msiexec` and the Burn bundle run reliably in the background.

**Other invariants that must not change:**
- `UpgradeCode` in `Package.wxs` is `D1E2F3A4-B5C6-4D7E-8F9A-B0C1D2E3F4A5` — never change this (breaks upgrades)
- Registry keys in shortcuts must use `Root="HKCU"` (per-user, not HKLM)
- The EXE hash (not MSI hash) goes in GitHub release notes — the build script computes and prints the EXE hash
- Upload the **EXE** (Burn bundle) to GitHub releases — not the MSI
- The relaunch path fallback in `UpdateService.cs` must use `LocalApplicationData\Programs\MyLocalBackup` (not ProgramFiles)

## Migration Note (Users Upgrading from v0.9.0 or Earlier)

Versions before v0.9.1 used a per-machine install to `%ProgramFiles%\MyLocalBackup`. The new per-user installer cannot auto-remove that old install (different scope). Users should:
1. Uninstall the old version via Windows Settings → Apps → MyLocalBackup
2. Install the new version

The in-app updater handles this gracefully: after installing the new per-user version, if the old exe path no longer exists, the relaunch falls back to `%LOCALAPPDATA%\Programs\MyLocalBackup\MyLocalBackup.UI.exe`.

## Update Checker Notes

- `UpdateService.cs` compares only Major.Minor.Build (3-part) to handle GitHub 3-part tags vs assembly 4-part versions
- SHA256 is extracted from the release notes body using regex `SHA256:\s*([a-fA-F0-9]{64})`
- If no SHA256 is found in the release notes, the download is rejected entirely
