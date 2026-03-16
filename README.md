# MyLocalBackup (v0.7)

A free, open-source, versioned local backup tool for Windows. Uses NTFS hard links for space-efficient incremental backups stored as full snapshots.

## Features

### Backup Engine
- **Versioned snapshots**: Each backup run creates a timestamped folder that looks like a complete copy, but unchanged files are hard-linked to previous snapshots — no duplicate data on disk.
- **Rename/move detection**: Files moved or renamed are matched via size + last-write-time, so they're hard-linked rather than re-copied.
- **Two-phase deletion**: Snapshots are marked `Deleting` before removal so a crash mid-delete leaves no orphans (cleaned up on next launch).
- **Retention management**: Automatically prunes old snapshots when disk space is low or count limits are reached. Pinned snapshots are never deleted.

### Scheduling
- **Interval mode**: Every N hours (1–168).
- **Daily fixed-time**: Runs once per day at a configured time (e.g. 2:00 AM).
- **Manual mode**: Run on demand only.
- **Background service**: Optional Windows Service for backups when the UI is not open.
- **Catch-up logic**: If a scheduled backup was missed (app closed), the next run fires soon rather than waiting a full interval.

### UI
- **Dashboard**: Live status, countdown to next backup, progress bar with per-file detail for large files, skipped-files tab.
- **Backup History**: Browse, open, or delete past snapshots.
- **System tray**: Run, pause, resume, or skip backups from the tray context menu.
- **Notifications**: Toast-style notification when a background backup starts (configurable).

### Updates
- Checks GitHub Releases every 12 hours.
- One-click download and install. SHA256 hash verified before installing.
- Rollback available if an update fails.

## Tech Stack

- **Framework**: .NET 9 / WPF (Windows)
- **Database**: SQLite via `Microsoft.Data.Sqlite` (WAL mode on local drives, exclusive mode on removable)
- **Installer**: WiX v4 MSI (per-user install to `%LocalAppData%\MyLocalBackup`)
- **Self-contained**: Bundles the .NET runtime — no separate .NET install required

## Requirements

- Windows 10 or 11 (64-bit)
- NTFS destination drive (hard links require NTFS)

## Building

```
dotnet build MyLocalBackup.UI/MyLocalBackup.UI.csproj
dotnet build MyLocalBackup.Service/MyLocalBackup.Service.csproj
```

## Release Build (MSI)

```
powershell -ExecutionPolicy Bypass -File release-build.ps1 -Version X.Y.Z
```

The script handles: publish → regenerate Files.wxs → MSI build → copy to Desktop. The SHA256 hash is printed for inclusion in release notes.

## Project Structure

```
MyLocalBackup.Core/       Backup engine, database, configuration, models, services
MyLocalBackup.UI/         WPF desktop application
MyLocalBackup.Service/    Windows background service
Staging/                  WiX MSI packaging
release-build.ps1         Automated release build script
```

## License

MIT — see [About dialog](MyLocalBackup.UI/MainWindow.xaml) in the app or the license text below.

Copyright © 2026 George Karagioules. Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files, to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
