# MyLocalBackup - Project Context

## GitHub Account & Repositories

- **Owner**: `georgekgr12`
- **Source code repo**: `georgekgr12/MyLocalBackup` - contains all source code, committed and pushed from this workspace
- **Releases repo**: `georgekgr12/MyLocalBackup-releases` - contains only release tags with MSI installer assets. The in-app update checker (`UpdateService.cs`) queries THIS repo at `https://api.github.com/repos/georgekgr12/MyLocalBackup-releases/releases/latest`

## Release Process (MANDATORY — follow exactly)

**NEVER do manual build steps. Always use the release build script.**

When creating a new release:

1. **Bump version** in `MyLocalBackup.UI/MyLocalBackup.UI.csproj` (`<Version>` tag)
2. **Run the release build script**: `powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z`
   - This handles: publish → regenerate Files.wxs → MSI build → copy to Desktop
   - The script outputs the MSI SHA256 hash — include it in the GitHub release notes
3. **User tests the MSI** from their Desktop before any GitHub release
4. **Only after user confirms it works**, commit and push:
   - `git add` changed files, `git commit`, `git push origin main`
   - `gh release create vX.Y.Z --repo georgekgr12/MyLocalBackup ...`
   - `gh release create vX.Y.Z --repo georgekgr12/MyLocalBackup-releases ... Staging/MyLocalBackup_Setup/MyLocalBackupSetup.msi#MyLocalBackupSetup.msi`
5. **Include SHA256 in release notes**: Format: `SHA256: <hash>` (the update checker parses this)

Both releases (source + releases repo) must be created. The app will only detect updates from the releases repo.

### Critical Rules

- **NEVER push to GitHub before user tests the MSI on their machine**
- **If Files.wxs is stale** (DLL count mismatch after .NET SDK update), the script auto-regenerates it from the publish output — this is handled automatically
- **UI.dll and Core.dll CANNOT be obfuscated** — no Licensing.dll exists anymore; there is no obfuscation step

## Version History

Versions follow `2.0.X` pattern (v2.0.0 was the open-source conversion). Check last commit message or `.csproj` for current version.

## Build Commands

- **Full release build (USE THIS)**: `powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z`
- **Dev build only**: `dotnet build MyLocalBackup.UI/MyLocalBackup.UI.csproj`
- **Build Service**: `dotnet build MyLocalBackup.Service/MyLocalBackup.Service.csproj`

## Project Structure

- `MyLocalBackup.Core/` - Backup engine, database, configuration, models, services
- `MyLocalBackup.UI/` - WPF desktop application (main entry point)
- `MyLocalBackup.Service/` - Windows background service
- `Staging/` - Build artifacts and MSI packaging (WiX v4)
- `Staging/MyLocalBackup_Setup/` - WiX source files (Package.wxs, Files.wxs, License.rtf)
- `C:/MLB_Staging/` - Local staging directory for MSI source files (populated by build script)
- `release-build.ps1` - Automated release build script

## Key Technical Notes

- Target framework: .NET 9.0 (Windows)
- Database: SQLite via Microsoft.Data.Sqlite (WAL mode on local drives, exclusive mode on removable drives)
- UI: WPF with dark-mode-only theme
- Installer: WiX v4 MSI (per-user install to `%LocalAppData%\MyLocalBackup`)
- Hard link deduplication for backup storage (NTFS only)
- Self-contained publish (bundles .NET runtime — users don't need .NET installed)
- Update security: SHA256 hash required (parsed from release notes body `SHA256: <hex>`), rollback mechanism

## Update Checker Notes

- `UpdateService.cs` compares only Major.Minor.Build (3-part) to handle GitHub 3-part tags vs assembly 4-part versions
- SHA256 is extracted from the release notes body using regex `SHA256:\s*([a-fA-F0-9]{64})`
- If no SHA256 is found in the release notes, the download is rejected entirely
