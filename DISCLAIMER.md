# Disclaimer

MyLocalBackup is a **free personal project**. Read this before you download,
build, install or run it.

## It can lose, overwrite or delete data

MyLocalBackup copies, hard-links, overwrites and deletes files. It deletes old
snapshots automatically, on a schedule and when the destination drive runs low
on space. Known bugs in versions 0.7.5 to 0.9.16 could delete real files outside
the backup and overwrite older versions of files inside it (see the
[README](README.md#known-data-loss-bugs-in-older-versions)). Other, unknown
problems are likely. It has not been independently tested or audited.

- Never use it as your only backup. Keep a second, independent copy of anything
  you can't afford to lose.
- Check regularly that you can open files from your snapshots.
- Double-check which source and destination folders you select.
- Don't edit, move or delete anything inside a snapshot folder: copy files out
  first.

## No warranty, no liability

MyLocalBackup is provided **"as is", without warranty of any kind**, express or
implied, including fitness for a particular purpose. In no event shall the
authors or copyright holders be liable for any claim, damages or other
liability, including loss of data, arising from or in connection with the
software or its use. The full terms are in the [MIT License](LICENSE).

## Your responsibility

By downloading, building, installing or running MyLocalBackup you accept that:

- you use it entirely at your own risk;
- you are solely responsible for how you use it, for protecting your data, and
  for any loss or damage that results;
- you are responsible for complying with the licences of MyLocalBackup and of
  its third-party components.

## No support

There is no support, no bug-fix commitment and no guarantee that issues or
requests will be answered. See [SUPPORT.md](SUPPORT.md).

## Not affiliated

MyLocalBackup is an independent, non-commercial project. It is not affiliated
with, endorsed by or sponsored by Microsoft. Windows and NTFS are trademarks of
Microsoft Corporation.
