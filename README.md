

https://github.com/user-attachments/assets/7ac7e0a2-d48d-4335-8729-e0dc80d09bc0

<div align="center">

<h1>MyLocalBackup</h1>

<hr>

<p>
  <strong>Open-source versioned local backups for Windows. Space-efficient snapshots you can browse and copy back from in File Explorer.</strong><br>
  <em>MIT-licensed source code with NTFS hard-link deduplication to keep backup history compact.</em>
</p>

<p>
  <a href="https://github.com/gkaragioul/My_Local_Backup/releases/latest">Download</a> &bull;
  <a href="#features">Features</a> &bull;
  <a href="#restoring-files">Restoring files</a> &bull;
  <a href="#good-to-know">Good to know</a> &bull;
  <a href="#requirements">Requirements</a> &bull;
  <a href="#building">Building</a> &bull;
  <a href="#license">License</a>
</p>

<hr>

</div>

## Features

### Backup Engine
- **Versioned snapshots**: Each backup run creates a timestamped folder that looks like a complete copy, but unchanged files are hard-linked to previous snapshots — no duplicate data on disk. This means snapshots share those files; see [Good to know](#good-to-know).
- **Parallel file processing**: Files within each directory are processed up to 4 concurrently, significantly reducing backup time on large folders.
- **Rename/move detection**: Files moved or renamed are matched via size + last-write-time, so they're hard-linked rather than re-copied.
- **Background retention**: Old snapshot cleanup runs in the background after a backup completes — the UI never hangs at 100%.
- **Two-phase deletion**: Snapshots are marked `Deleting` before removal, so a delete that gets cut short (app closed, PC shut down) is finished on the next backup. Folder links inside a snapshot (symbolic links, junctions) are removed as links only; the folders they point to are never touched.
- **Retention management**: Old snapshots are thinned automatically: everything from the last 24 hours is kept, then one per day for 30 days, then one per week. The newest 5 are always kept. When the destination drive is low on space, older snapshots are deleted too (on by default, see [Good to know](#good-to-know)).
- **Backup resume**: If a backup is interrupted (app close, update, crash), the next run resumes from where it left off — files already copied are skipped. Files that changed in the meantime are copied fresh, and earlier snapshots keep their own versions.
- **Destination check**: A destination inside one of your source folders is refused, because every backup would copy the earlier backups again.
- **Configurable exclusions**: Add folder names to `ExcludedFolderNames` in `config.json` to skip regeneratable artifacts (e.g. `node_modules`, `bin`, `obj`) and speed up backups of developer workspaces. Empty by default — everything is backed up unless explicitly excluded.
- **Flush retry with bounded retries**: Batch database inserts retry up to 3 times on failure before dropping entries, preventing unbounded re-queuing.

### Scheduling
- **Interval mode**: Every N hours (1–168).
- **Daily fixed-time**: Runs once per day at a configured time (e.g. 2:00 AM).
- **Manual mode**: Run on demand only.
- **Launch at startup**: Optional registry-based auto-start so backups run whenever you're logged in.
- **Catch-up logic**: If a scheduled backup was missed (app closed), the next run fires soon rather than waiting a full interval.

### UI
- **Dashboard**: Live status, countdown to next backup, progress bar with per-file detail for large files, and a link to the Skipped Files list in Settings (persisted across restarts).
- **Backup History**: Browse past snapshots, open them in File Explorer, or delete them.
- **System tray**: Run, pause, resume, or skip backups from the tray context menu. Disable automatic backups directly from tray.
- **Notifications**: Toast-style notification when a background backup starts (configurable).

### Updates
- Updates are published through this repository's GitHub Releases tab.
- Download the latest `MyLocalBackupSetup.exe` installer and run it manually.
- Newer installers replace the existing per-user install when the version number increases.

## Restoring files

There's no restore button in the app. Every snapshot is a normal folder, so restoring is copy and paste:

1. Open **History**, right-click the backup you want and choose **Open in Explorer**. (Or browse to `<your destination>\Backup Snapshots\<date and time>` yourself.)
2. Find the files or folders you need.
3. **Copy** them to where you want them, for example your Desktop or their original folder, and open the copies.

Don't open and edit files directly inside a snapshot folder, and don't move, rename or delete things in there. The next section explains why.

## Good to know

Please read this before relying on MyLocalBackup.

- **Snapshots share unchanged files.** A file that didn't change between backups is stored once and appears in every snapshot through NTFS hard links. That's what keeps the backup small, but it also means that **editing a file inside a snapshot changes it in every snapshot that shares it**. Treat snapshot folders as read-only: copy files out first, then work on the copies.
- **Low-space cleanup is on by default.** When the destination drive has less than 20 GB free, each backup deletes the oldest snapshots (up to 10 per backup) until there's room again, always keeping the newest 5. On a nearly full drive with hourly backups, that can shrink your history to a few hours. You can turn this off in **Settings > Automatically delete old backups if disk is full**; then you'll have to free up space yourself. The normal thinning (one per day, then one per week) always runs.
- **No shadow copies (VSS).** Files that another program has locked are skipped and listed under **Settings > Skipped Files**. Files that are being written while the backup runs (open databases, mail stores, virtual machine disks) can be copied in a half-written state. Close those programs before a backup when you need a clean copy.
- **Folder links aren't followed.** Symbolic links and junctions inside your source folders are saved as links (this needs Windows Developer Mode) or listed under Skipped Files. The files they point to are only backed up if that folder is also one of your sources.
- **Keep the backup somewhere else.** Pick a destination outside your source folders, ideally on a separate drive. A backup on the same disk as the originals won't help if that disk fails.
- **Check the log now and then.** If a source folder is missing (for example on an unplugged drive) or a folder can't be read, it's skipped with a note in the activity log, and the backup still shows as completed.

## No warranty

MyLocalBackup is provided **as is, without warranty of any kind**, under the [MIT License](LICENSE). It's a free tool, not a guarantee. Keep a second backup of anything you can't afford to lose (another drive, or an off-site or cloud copy), and every so often check that you can actually open files from a snapshot.

## Tech Stack

- **Framework**: .NET 9 / WPF (Windows)
- **Database**: SQLite via `Microsoft.Data.Sqlite` (WAL mode on local drives, exclusive mode on removable)
- **Installer**: WiX v5 Burn bundle EXE wrapping MSI (per-user install to `%LOCALAPPDATA%\Programs\MyLocalBackup` — no admin required)
- **Self-contained**: Bundles the .NET runtime — no separate .NET install required

## Requirements

- Windows 10 or 11 (64-bit)
- NTFS destination drive (hard links require NTFS)

## Building

```
dotnet build MyLocalBackup.UI/MyLocalBackup.UI.csproj
```

Data-safety tests (they work in a throwaway folder under `%TEMP%` and never touch your real backups):

```
dotnet run -c Release --project tests/MyLocalBackup.SafetyTests
```

## Release Build (EXE Installer)

```
powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z
```

The script handles: publish, regenerate Files.wxs, MSI build, Burn bundle EXE build, and copy the installer to Desktop. Hashes are printed for release verification.

## Project Structure

```
MyLocalBackup.Core/       Backup engine, database, configuration, models
MyLocalBackup.UI/         WPF desktop application
tests/                    Data-safety regression tests
Staging/                  WiX MSI + Burn bundle EXE packaging
release-build.ps1         Automated release build script
PackagePortable.ps1       Portable ZIP packaging script
```

## License

MyLocalBackup is open source under the [MIT License](LICENSE).

You may use, copy, modify, merge, publish, distribute, sublicense, and sell copies of the software, provided the MIT copyright and permission notice are included in copies or substantial portions of the software.

Third-party dependencies and build tools remain under their own licenses. In particular, WiX Toolset extensions are external build dependencies and are not vendored in this repository.
