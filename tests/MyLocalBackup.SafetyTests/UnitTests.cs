using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;
using MyLocalBackup.Core.Models;
using MyLocalBackup.Core.Storage;

namespace MyLocalBackup.SafetyTests
{
    internal static class UnitTests
    {
        public static IEnumerable<(string, Action)> All => new (string, Action)[]
        {
            ("Links: junctions are recognised by their reparse tag, also on paths over 260 characters", ReparseTagsAreRead),
            ("Delete: a normal tree is removed, including read-only and hidden files", DeleteRemovesNormalTree),
            ("Delete: a junction deep inside the tree is removed as a link, its target survives", DeleteNestedJunction),
            ("Delete: a tree whose root is a junction removes only the link", DeleteRootJunction),
            ("Delete: symbolic links (file and folder) are removed as links, targets survive", DeleteSymlinks),
            ("Delete: cancellation stops without touching anything outside", DeleteCancellation),
            ("Copy: replacing a hard-linked file leaves the other link's content alone", CopyReplacingKeepsOtherLink),
            ("Restore: restoring over an existing hard-linked file leaves the other link alone", RestoreDoesNotWriteThroughHardLink),
            ("Restore: overwrite=false still refuses to replace an existing file", RestoreRespectsNoOverwrite),
            ("Paths: destination inside a source is refused", DestinationInsideSourceRefused),
            ("Paths: source inside the snapshot folder is refused", SourceInsideSnapshotsRefused),
            ("Paths: separate folders, drive-root destination and look-alike names are allowed", SeparateFoldersAllowed),
            ("Engine: a backup into its own source folder is refused before anything is written", BackupIntoSourceRefused),
            ("Engine: unchanged files are still hard-linked between snapshots", UnchangedFilesStillHardLinked),
            ("Engine: moved files are still hard-linked to the earlier copy", MovedFilesStillHardLinked),
            ("Engine: no temporary copy files are left after a backup or a resume", NoTempFilesLeft),
        };

        private const string Canary = "precious user data - must survive";

        // ---------------------------------------------------------------- link detection

        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003; // junctions use this tag

        private static void ReparseTagsAreRead()
        {
            using var sb = new Sandbox("tags");
            var target = sb.Dir("user", "Music");
            var normalDir = sb.Dir("normal");
            var junction = Path.Combine(sb.Root, "junction");
            TestFs.CreateJunction(junction, target);

            Check.Equal<uint?>(0, SafeFileSystem.TryGetReparseTag(normalDir), "tag of a normal folder");
            Check.Equal<uint?>(IO_REPARSE_TAG_MOUNT_POINT, SafeFileSystem.TryGetReparseTag(junction), "tag of a junction");
            Check.True(SafeFileSystem.IsLink(new DirectoryInfo(junction)), "a junction is not seen as a link");
            Check.True(!SafeFileSystem.IsLink(new DirectoryInfo(normalDir)), "a normal folder is seen as a link");

            // Move the junction (the link itself) into a folder chain longer than MAX_PATH
            var deep = sb.Root;
            while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
            Directory.CreateDirectory(deep);
            var deepJunction = Path.Combine(deep, "junction");
            Directory.Move(junction, deepJunction);

            Check.True(deepJunction.Length > 260, "test path is not long enough");
            Check.Equal<uint?>(IO_REPARSE_TAG_MOUNT_POINT, SafeFileSystem.TryGetReparseTag(deepJunction), "tag of a junction on a long path");

            var canary = Path.Combine(target, "song.txt");
            Sandbox.Write(canary, Canary);
            var result = SafeFileSystem.DeleteDirectoryTree(Path.Combine(sb.Root, new string('d', 40)));
            Check.Equal(Canary, File.ReadAllText(canary), "file behind the long-path junction");
            Check.Equal(1L, result.LinksRemoved, "links removed");
            Check.Equal(0, result.Failures, "failures");
        }

        // ---------------------------------------------------------------- delete

        private static void DeleteRemovesNormalTree()
        {
            using var sb = new Sandbox("del-normal");
            var tree = sb.Dir("snapshot");
            Sandbox.Write(Path.Combine(tree, "a.txt"), "a");
            var ro = Path.Combine(tree, "sub", "readonly.txt");
            Sandbox.Write(ro, "ro");
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            var hidden = Path.Combine(tree, "sub", "deeper", "hidden.txt");
            Sandbox.Write(hidden, "h");
            File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

            var result = SafeFileSystem.DeleteDirectoryTree(tree);

            Check.Equal(0, result.Failures, "failures");
            Check.Equal(3L, result.FilesDeleted, "files deleted");
            Check.True(!Directory.Exists(tree), "the tree still exists");
        }

        private static void DeleteNestedJunction()
        {
            using var sb = new Sandbox("del-nested");
            var target = sb.Dir("user", "Music");
            var canary = Path.Combine(target, "sub", "song.txt");
            Sandbox.Write(canary, Canary);
            var tree = sb.Dir("snapshot");
            Sandbox.Write(Path.Combine(tree, "a", "b", "file.txt"), "x");
            TestFs.CreateJunction(Path.Combine(tree, "a", "b", "My Music"), target);

            var result = SafeFileSystem.DeleteDirectoryTree(tree);

            Check.Equal(Canary, File.ReadAllText(canary), "file behind the junction");
            Check.Equal(0, result.Failures, "failures");
            Check.Equal(1L, result.LinksRemoved, "links removed");
            Check.True(!Directory.Exists(tree), "the tree still exists");
        }

        private static void DeleteRootJunction()
        {
            using var sb = new Sandbox("del-root");
            var target = sb.Dir("user", "Pictures");
            var canary = Path.Combine(target, "photo.txt");
            Sandbox.Write(canary, Canary);
            var link = Path.Combine(sb.Dir("snapshots"), "2000-01-01 00.00.00");
            TestFs.CreateJunction(link, target);

            var result = SafeFileSystem.DeleteDirectoryTree(link);

            Check.Equal(Canary, File.ReadAllText(canary), "file behind the junction");
            Check.True(!Directory.Exists(link), "the link still exists");
            Check.True(Directory.Exists(target), "the link target folder was removed");
            Check.Equal(0L, result.FilesDeleted, "files deleted");
        }

        private static void DeleteSymlinks()
        {
            using var sb = new Sandbox("del-symlink");
            var target = sb.Dir("user", "Docs");
            var canary = Path.Combine(target, "doc.txt");
            Sandbox.Write(canary, Canary);
            var tree = sb.Dir("snapshot");
            if (!TestFs.TryCreateDirectorySymlink(Path.Combine(tree, "dirlink"), target) ||
                !TestFs.TryCreateFileSymlink(Path.Combine(tree, "filelink.txt"), canary))
                throw new SkipTestException("symbolic links need Developer Mode or admin on this machine");

            var result = SafeFileSystem.DeleteDirectoryTree(tree);

            Check.Equal(Canary, File.ReadAllText(canary), "file behind the links");
            Check.Equal(2L, result.LinksRemoved, "links removed");
            Check.True(!Directory.Exists(tree), "the tree still exists");
        }

        private static void DeleteCancellation()
        {
            using var sb = new Sandbox("del-cancel");
            var target = sb.Dir("user", "Music");
            var canary = Path.Combine(target, "song.txt");
            Sandbox.Write(canary, Canary);
            var tree = sb.Dir("snapshot");
            for (int i = 0; i < 20; i++) Sandbox.Write(Path.Combine(tree, $"f{i}.txt"), "x");
            TestFs.CreateJunction(Path.Combine(tree, "My Music"), target);

            using var cts = new CancellationTokenSource();
            var result = SafeFileSystem.DeleteDirectoryTree(tree, cts.Token, deleted => { if (deleted >= 5) cts.Cancel(); });

            Check.True(result.Cancelled, "the delete did not report cancellation");
            Check.True(Directory.Exists(tree), "the tree was removed despite cancellation");
            Check.Equal(Canary, File.ReadAllText(canary), "file behind the junction");

            var finish = SafeFileSystem.DeleteDirectoryTree(tree); // resume later, as the app does
            Check.True(!Directory.Exists(tree) && !finish.Cancelled, "the resumed delete did not finish");
            Check.Equal(Canary, File.ReadAllText(canary), "file behind the junction after finishing");
        }

        // ---------------------------------------------------------------- copy / restore

        private static void CopyReplacingKeepsOtherLink()
        {
            using var sb = new Sandbox("copy");
            var older = Path.Combine(sb.Dir("snapA"), "f.txt");
            Sandbox.Write(older, "old version");
            var newer = Path.Combine(sb.Dir("snapB"), "f.txt");
            CreateHardLinkOrSkip(newer, older);
            var source = Path.Combine(sb.Root, "source.txt");
            Sandbox.Write(source, "new version from source");

            SafeFileSystem.CopyFileReplacing(source, newer);

            Check.Equal("old version", File.ReadAllText(older), "older snapshot's copy");
            Check.Equal("new version from source", File.ReadAllText(newer), "replaced copy");
            Check.True(!TestFs.SameFile(older, newer), "the two copies still share one file");
            Check.True(!Directory.EnumerateFiles(Path.GetDirectoryName(newer)!).Any(f => SafeFileSystem.IsTempFileName(Path.GetFileName(f))),
                "a temporary file was left behind");
        }

        private static void RestoreDoesNotWriteThroughHardLink()
        {
            using var sb = new Sandbox("restore");
            var snapshotFile = Path.Combine(sb.Dir("snapshot"), "report.txt");
            Sandbox.Write(snapshotFile, "restored content");
            var otherSnapshotFile = Path.Combine(sb.Dir("other-snapshot"), "report.txt");
            Sandbox.Write(otherSnapshotFile, "other snapshot's version");
            var restoreTarget = Path.Combine(sb.Dir("restore-here"), "report.txt");
            CreateHardLinkOrSkip(restoreTarget, otherSnapshotFile);

            RestorationManager.RestoreFile(snapshotFile, restoreTarget, overwrite: true);

            Check.Equal("restored content", File.ReadAllText(restoreTarget), "restored file");
            Check.Equal("other snapshot's version", File.ReadAllText(otherSnapshotFile), "other hard link");
        }

        private static void RestoreRespectsNoOverwrite()
        {
            using var sb = new Sandbox("restore-noover");
            var snapshotFile = Path.Combine(sb.Dir("snapshot"), "a.txt");
            Sandbox.Write(snapshotFile, "from snapshot");
            var existing = Path.Combine(sb.Dir("target"), "a.txt");
            Sandbox.Write(existing, "user's current file");

            bool threw = false;
            try { RestorationManager.RestoreFile(snapshotFile, existing, overwrite: false); }
            catch (IOException) { threw = true; }

            Check.True(threw, "restore with overwrite=false did not refuse");
            Check.Equal("user's current file", File.ReadAllText(existing), "existing file");
        }

        // ---------------------------------------------------------------- path rules

        private static void DestinationInsideSourceRefused()
        {
            Check.True(BackupPathRules.GetOverlapError(new[] { @"C:\Users\me\Documents" }, @"C:\Users\me\Documents\Backups") != null,
                "destination below a source was allowed");
            Check.True(BackupPathRules.GetOverlapError(new[] { @"C:\Users\me\Documents" }, @"C:\Users\me\Documents") != null,
                "destination equal to a source was allowed");
            Check.True(BackupPathRules.GetOverlapError(new[] { @"D:\" }, @"D:\Backups") != null,
                "destination on a drive that is itself a source was allowed");
            Check.True(BackupPathRules.GetOverlapError(new[] { @"c:\users\ME\documents\" }, @"C:\Users\me\Documents\Backups") != null,
                "case and trailing-slash differences slipped through");
        }

        private static void SourceInsideSnapshotsRefused()
        {
            Check.True(BackupPathRules.GetOverlapError(new[] { @"E:\Backups\Backup Snapshots" }, @"E:\Backups") != null,
                "the snapshot folder as a source was allowed");
            Check.True(BackupPathRules.GetOverlapError(new[] { @"E:\Backups\Backup Snapshots\2026-01-01 10.00.00\Docs" }, @"E:\Backups") != null,
                "a folder inside a snapshot as a source was allowed");
        }

        private static void SeparateFoldersAllowed()
        {
            Check.True(BackupPathRules.GetOverlapError(new[] { @"C:\Users\me\Documents", @"C:\Users\me\Pictures" }, @"E:\") == null,
                "sources on another drive were refused");
            Check.True(BackupPathRules.GetOverlapError(new[] { @"D:\Photos" }, @"D:\") == null,
                "destination at a drive root with a source elsewhere on it was refused");
            Check.True(BackupPathRules.GetOverlapError(new[] { @"C:\Data" }, @"C:\Database") == null,
                "a look-alike folder name (C:\\Data vs C:\\Database) was refused");
            Check.True(BackupPathRules.GetOverlapError(new[] { @"E:\Backups\Other" }, @"E:\Backups") == null,
                "a source beside the snapshot folder was refused");
        }

        private static void BackupIntoSourceRefused()
        {
            using var sb = new Sandbox("overlap");
            var source = sb.Dir("src");
            Sandbox.Write(Path.Combine(source, "a.txt"), "a");
            var destination = Path.Combine(source, "Backups");
            var db = new DatabaseManager(Path.Combine(sb.Root, "metadata.db"));
            var config = new BackupConfig
            {
                SourceFolders = { source },
                Destination = new BackupDestination { Name = "Sandbox", RootPath = destination },
                Schedule = ScheduleType.Manual
            };

            bool refused = false;
            try { new BackupJob(config, db, destination).Execute(); }
            catch (InvalidOperationException) { refused = true; }

            Check.True(refused, "BackupJob accepted a destination inside its source");
            Check.True(!Directory.Exists(destination), "the job created the destination folder anyway");
            Check.Equal(0, db.GetRestorePoints().Count, "restore points created");

            // The scheduler reports it as an error instead of starting
            var scheduler = new BackupScheduler(config, db);
            (bool success, string? error, IReadOnlyList<string>? failed) outcome = default;
            scheduler.BackupCompleted += (_, e) => outcome = e;
            scheduler.RunBackup().GetAwaiter().GetResult();
            Check.True(!outcome.success && outcome.error != null && outcome.error.Contains("inside the folder you back up"),
                $"scheduler did not refuse clearly (error: {outcome.error})");
            Check.True(!Directory.Exists(destination), "the scheduler created the destination folder anyway");
        }

        // ---------------------------------------------------------------- space saving still works

        private static (DatabaseManager, BackupConfig) NewEngine(Sandbox sb, string source)
        {
            var db = new DatabaseManager(Path.Combine(sb.Root, "metadata.db"));
            var config = new BackupConfig
            {
                SourceFolders = { source },
                Destination = new BackupDestination { Name = "Sandbox", RootPath = sb.Dir("dest") },
                Schedule = ScheduleType.Manual,
                AutoDeleteOldBackups = false
            };
            return (db, config);
        }

        private static RestorePoint RunNewBackup(DatabaseManager db, BackupConfig config)
        {
            var before = db.GetRestorePoints().Select(r => r.Id).ToHashSet();
            Thread.Sleep(1100); // snapshot folder names have one-second resolution
            new BackupJob(config, db, config.Destination!.RootPath).Execute();
            return db.GetRestorePoints().Single(r => !before.Contains(r.Id));
        }

        private static void UnchangedFilesStillHardLinked()
        {
            using var sb = new Sandbox("dedup");
            var source = sb.Dir("src");
            Sandbox.Write(Path.Combine(source, "sub", "big.txt"), new string('x', 10_000));
            var (db, config) = NewEngine(sb, source);

            var first = RunNewBackup(db, config);
            var second = RunNewBackup(db, config);

            Check.True(TestFs.SameFile(Path.Combine(first.Path, "src", "sub", "big.txt"), Path.Combine(second.Path, "src", "sub", "big.txt")),
                "an unchanged file was copied instead of hard-linked");
        }

        private static void MovedFilesStillHardLinked()
        {
            using var sb = new Sandbox("moved");
            var source = sb.Dir("src");
            Sandbox.Write(Path.Combine(source, "old-name.txt"), "content that moves");
            var (db, config) = NewEngine(sb, source);

            var first = RunNewBackup(db, config);
            Directory.CreateDirectory(Path.Combine(source, "moved"));
            File.Move(Path.Combine(source, "old-name.txt"), Path.Combine(source, "moved", "new-name.txt"));
            var second = RunNewBackup(db, config);

            Check.True(TestFs.SameFile(Path.Combine(first.Path, "src", "old-name.txt"), Path.Combine(second.Path, "src", "moved", "new-name.txt")),
                "a moved file was copied instead of hard-linked");
        }

        private static void NoTempFilesLeft()
        {
            using var sb = new Sandbox("tempfiles");
            var source = sb.Dir("src");
            Sandbox.Write(Path.Combine(source, "a.txt"), "a");
            Sandbox.Write(Path.Combine(source, "d", "b.txt"), "b");
            var (db, config) = NewEngine(sb, source);

            var snapshot = RunNewBackup(db, config);
            db.UpdateRestorePointStatus(snapshot.Id, BackupStatus.Interrupted);
            // A crash mid-copy leaves a temporary file behind; the resume must clean it up
            Sandbox.Write(Path.Combine(snapshot.Path, "src", "d", SafeFileSystem.TempFilePrefix + Guid.NewGuid().ToString("N") + SafeFileSystem.TempFileSuffix), "partial");
            Sandbox.Write(Path.Combine(source, "d", "b.txt"), "b changed");
            Thread.Sleep(1100);
            new BackupJob(config, db, config.Destination!.RootPath).Execute();

            var leftovers = Directory.EnumerateFiles(config.Destination!.RootPath, "*", SearchOption.AllDirectories)
                .Where(f => SafeFileSystem.IsTempFileName(Path.GetFileName(f))).ToList();
            Check.Equal(0, leftovers.Count, "temporary files left in the snapshots");
            Check.Equal("b changed", File.ReadAllText(Path.Combine(snapshot.Path, "src", "d", "b.txt")), "resumed copy");
        }

        // ---------------------------------------------------------------- helpers

        private static void CreateHardLinkOrSkip(string newPath, string existingPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            if (!HardLinkManager.Create(newPath, existingPath))
                throw new SkipTestException("hard links are not supported on the sandbox volume");
        }
    }
}
