# Third-party notices

MyLocalBackup is released under the [MIT License](LICENSE). The installers
(`MyLocalBackupSetup.exe` and `MyLocalBackupSetup.msi`) and the portable build
also contain the third-party components listed below. Each component remains
under its own license; the license texts are available at the links given.

This file is installed next to `MyLocalBackup.UI.exe` together with the
MyLocalBackup license (`LICENSE.txt`).

## .NET runtime (self-contained)

The app is published self-contained, so the installer ships the .NET 9 runtime
(Microsoft.NETCore.App) and the Windows Desktop runtime
(Microsoft.WindowsDesktop.App: WPF and Windows Forms).

- License: MIT
- Copyright (c) .NET Foundation and Contributors
- License: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and
  https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- The runtime's own third-party notices:
  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and
  https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT

## NuGet packages

| Package | Version | License | Copyright |
|---|---|---|---|
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 9.0.0 | MIT | (c) Microsoft Corporation / .NET Foundation and Contributors |
| Microsoft.Win32.SystemEvents | 9.0.0 | MIT | (c) Microsoft Corporation / .NET Foundation and Contributors |
| System.Drawing.Common | 9.0.0 | MIT | (c) Microsoft Corporation / .NET Foundation and Contributors |
| System.Memory | 4.5.3 | MIT | (c) Microsoft Corporation / .NET Foundation and Contributors |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.provider.e_sqlite3, SQLitePCLRaw.lib.e_sqlite3 | 2.1.10 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| H.NotifyIcon, H.NotifyIcon.Wpf, H.GeneratedIcons.System.Drawing | 2.2.0 | MIT | Copyright (c) havendv and contributors |

- Microsoft.Data.Sqlite: https://github.com/dotnet/efcore (MIT,
  https://github.com/dotnet/efcore/blob/main/LICENSE.txt)
- Microsoft.Win32.SystemEvents, System.Drawing.Common, System.Memory:
  https://github.com/dotnet/runtime and https://github.com/dotnet/winforms
  (MIT)
- SQLitePCLRaw: https://github.com/ericsink/SQLitePCL.raw (Apache License 2.0,
  https://www.apache.org/licenses/LICENSE-2.0)
- H.NotifyIcon: https://github.com/HavenDV/H.NotifyIcon (MIT,
  https://licenses.nuget.org/MIT). H.NotifyIcon continues
  https://github.com/hardcodet/wpf-notifyicon.

## SQLite

`e_sqlite3.dll` (shipped by SQLitePCLRaw.lib.e_sqlite3) is a build of SQLite.
SQLite is in the public domain: https://www.sqlite.org/copyright.html

## WiX Toolset

The installers are built with WiX Toolset 5.0.2. `MyLocalBackupSetup.exe`
contains the WiX Burn engine and the WiX Standard Bootstrapper Application, and
`MyLocalBackupSetup.msi` contains the WiX UI dialogs and images.

- License: Microsoft Reciprocal License (MS-RL)
- Copyright (c) .NET Foundation and contributors
- Source and license: https://github.com/wixtoolset/wix and
  https://github.com/wixtoolset/wix/blob/main/LICENSE.TXT

## MIT License text (for the MIT-licensed components above)

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
