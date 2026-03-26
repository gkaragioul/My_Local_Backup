# MyLocalBackup (v0.9.4)

A free versioned local backup tool for Windows. Uses NTFS hard links for space-efficient incremental backups stored as full snapshots.

## Features

### Backup Engine
- **Versioned snapshots**: Each backup run creates a timestamped folder that looks like a complete copy, but unchanged files are hard-linked to previous snapshots — no duplicate data on disk.
- **Parallel file processing**: Files within each directory are processed up to 4 concurrently, significantly reducing backup time on large folders.
- **Rename/move detection**: Files moved or renamed are matched via size + last-write-time, so they're hard-linked rather than re-copied.
- **Background retention**: Old snapshot cleanup runs in the background after a backup completes — the UI never hangs at 100%.
- **Two-phase deletion**: Snapshots are marked `Deleting` before removal so a crash mid-delete leaves no orphans (cleaned up on next launch).
- **Retention management**: Automatically prunes old snapshots when disk space is low or count limits are reached. Pinned snapshots are never deleted.
- **Backup resume**: If a backup is interrupted (app close, update, crash), the next run resumes from where it left off — files already copied are skipped.
- **Configurable exclusions**: Add folder names to `ExcludedFolderNames` in `config.json` to skip regeneratable artifacts (e.g. `node_modules`, `bin`, `obj`) and speed up backups of developer workspaces. Empty by default — everything is backed up unless explicitly excluded.
- **Flush retry with bounded retries**: Batch database inserts retry up to 3 times on failure before dropping entries, preventing unbounded re-queuing.

### Scheduling
- **Interval mode**: Every N hours (1–168).
- **Daily fixed-time**: Runs once per day at a configured time (e.g. 2:00 AM).
- **Manual mode**: Run on demand only.
- **Launch at startup**: Optional registry-based auto-start so backups run whenever you're logged in.
- **Catch-up logic**: If a scheduled backup was missed (app closed), the next run fires soon rather than waiting a full interval.

### UI
- **Dashboard**: Live status, countdown to next backup, progress bar with per-file detail for large files, skipped-files tab (persisted across restarts).
- **Backup History**: Browse, open, or delete past snapshots.
- **System tray**: Run, pause, resume, or skip backups from the tray context menu. Disable automatic backups directly from tray.
- **Notifications**: Toast-style notification when a background backup starts (configurable).

### Updates
- Checks GitHub Releases every 12 hours.
- One-click download and install. SHA256 hash verified before installing.
- Rollback available if an update fails.

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

## Release Build (EXE Installer)

```
powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z
```

The script handles: publish, regenerate Files.wxs, MSI build, Burn bundle EXE build, copy to Desktop. The SHA256 hash is printed for inclusion in release notes.

## Project Structure

```
MyLocalBackup.Core/       Backup engine, database, configuration, models, services
MyLocalBackup.UI/         WPF desktop application
Staging/                  WiX MSI + Burn bundle EXE packaging
release-build.ps1         Automated release build script
PackagePortable.ps1       Portable ZIP packaging script
```

## License

Freeware — see [About dialog](MyLocalBackup.UI/MainWindow.xaml) in the app or the license text below.

Copyright (c) 2026 George Karagioules. All rights reserved. This software is provided free of charge for personal and commercial use. You may NOT modify, reverse-engineer, redistribute, or sell this software or any portion of it without prior written permission from the author. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
