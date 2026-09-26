using MyLocalBackup.Core.Data;
using MyLocalBackup.Core.Engine;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.SafetyTests
{
    /// <summary>
    /// End-to-end tests that drive the real backup engine (BackupJob, RetentionManager) against a
    /// sandbox source, destination and metadata database.
    /// </summary>
    internal static class EngineTests
    {
        public static IEnumerable<(string, Action)> All => new (string, Action)[]
        {
            ("Engine: finishing an interrupted snapshot delete never deletes files behind a junction", InterruptedDeleteDoesNotFollowJunction),
            ("Engine: finishing an interrupted snapshot delete never deletes files behind a symlink", InterruptedDeleteDoesNotFollowSymlink),
            ("Engine: retention delete removes a snapshot with a junction and leaves the target untouched", RetentionDeleteDoesNotFollowJunction),
            ("Engine: resuming a backup never rewrites a file shared with an older snapshot", ResumeDoesNotWriteThroughHardLink),
            ("Engine: resuming a backup never writes through a folder link left in the snapshot", ResumeDoesNotWriteThroughFolderLink),
            ("Engine: hard links are never made to files reached through a link in an older snapshot", NoHardLinkThroughLinkInOlderSnapshot),
        };

        private const string Canary = "precious user data - must survive";

        private static (DatabaseManager Db, BackupConfig Config) NewEngine(Sandbox sb, string source, string destination)
        {
            var db = new DatabaseManager(Path.Combine(sb.Root, "metadata.db"));
            var config = new BackupConfig
            {
                SourceFolders = { source },
                Destination = new BackupDestination { Name = "Sandbox", RootPath = destination },
                Schedule = ScheduleType.Manual,
                AutoDeleteOldBackups = false
            };
            return (db, config);
        }

        /// <summary>Runs one backup. Snapshot folder names have one-second resolution, so wait first.</summary>
        private static void RunBackup(DatabaseManager db, BackupConfig config)
        {
            Thread.Sleep(1100);
            new BackupJob(config, db, config.Destination!.RootPath).Execute();
        }

        private static RestorePoint RunNewBackup(DatabaseManager db, BackupConfig config)
        {
            var before = db.GetRestorePoints().Select(r => r.Id).ToHashSet();
            RunBackup(db, config);
            return db.GetRestorePoints().Single(r => !before.Contains(r.Id));
        }

        private static RestorePoint Get(DatabaseManager db, int id) => db.GetRestorePoints().Single(r => r.Id == id);

        private static string SnapshotsRoot(BackupConfig config) =>
            Path.Combine(config.Destination!.RootPath, "Backup Snapshots");

        private static RestorePoint AddFakeSnapshot(DatabaseManager db, BackupConfig config, string folderName, BackupStatus status)
        {
            var rp = new RestorePoint
            {
                Timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Path = Path.Combine(SnapshotsRoot(config), folderName),
                Status = status,
                TargetDestination = config.Destination!.RootPath
            };
            Directory.CreateDirectory(rp.Path);
            db.AddRestorePoint(rp);
            return rp;
        }

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(100);
            }
            return condition();
        }

        // ---------------------------------------------------------------------------------
        // 1. Deleting snapshots must never follow links
        // ---------------------------------------------------------------------------------

        private static void InterruptedDeleteDoesNotFollowJunction() =>
            InterruptedDeleteDoesNotFollowLink((link, target) => { TestFs.CreateJunction(link, target); return true; });

        private static void InterruptedDeleteDoesNotFollowSymlink() =>
            InterruptedDeleteDoesNotFollowLink(TestFs.TryCreateDirectorySymlink);

        /// <summary>
        /// Real-world trigger: Documents contains Windows' hidden "My Music" junction. With Developer
        /// Mode on, a snapshot keeps it as a link to C:\Users\name\Music. If the app is closed while
        /// that snapshot is being deleted, the next backup finishes the delete in the background.
        /// </summary>
        private static void InterruptedDeleteDoesNotFollowLink(Func<string, string, bool> makeLink)
        {
            using var sb = new Sandbox("bgdelete");
            var source = sb.Dir("src");
            Sandbox.Write(Path.Combine(source, "a.txt"), "hello");
            var userMusic = sb.Dir("user", "Music");
            var canaryFile = Path.Combine(userMusic, "song.txt");
            Sandbox.Write(canaryFile, Canary);

            var (db, config) = NewEngine(sb, source, sb.Dir("dest"));
            RunNewBackup(db, config);

            // A snapshot whose deletion was cut off: still marked Deleting, link still inside.
            var stuck = AddFakeSnapshot(db, config, "2000-01-01 00.00.00", BackupStatus.Deleting);
            Sandbox.Write(Path.Combine(stuck.Path, "src", "notes.txt"), "old snapshot file");
            if (!makeLink(Path.Combine(stuck.Path, "src", "My Music"), userMusic))
                throw new SkipTestException("directory symbolic links need Developer Mode or admin on this machine");

            RunBackup(db, config); // queues the background cleanup of the stuck snapshot

            bool snapshotGone = WaitUntil(() => !Directory.Exists(stuck.Path), TimeSpan.FromSeconds(15));

            Check.True(File.Exists(canaryFile), "the file behind the link was deleted");
            Check.Equal(Canary, File.ReadAllText(canaryFile), "content of the file behind the link");
            Check.True(snapshotGone, "the stuck snapshot folder was not removed");
        }

        private static void RetentionDeleteDoesNotFollowJunction()
        {
            using var sb = new Sandbox("retention");
            var source = sb.Dir("src");
            Sandbox.Write(Path.Combine(source, "a.txt"), "hello");
            var userPictures = sb.Dir("user", "Pictures");
            var canaryFile = Path.Combine(userPictures, "photo.txt");
            Sandbox.Write(canaryFile, Canary);
            File.SetAttributes(canaryFile, FileAttributes.ReadOnly);

            var (db, config) = NewEngine(sb, source, sb.Dir("dest"));
            RunNewBackup(db, config);

            var old = AddFakeSnapshot(db, config, "2000-01-01 00.00.00", BackupStatus.Completed);
            var oldFile = Path.Combine(old.Path, "src", "old.txt");
            Sandbox.Write(oldFile, "old snapshot file");
            File.SetAttributes(oldFile, FileAttributes.ReadOnly); // snapshot copies of read-only sources stay read-only
            TestFs.CreateJunction(Path.Combine(old.Path, "src", "My Pictures"), userPictures);

            new RetentionManager(db, config).DeleteSnapshot(old);

            Check.True(File.Exists(canaryFile), "the file behind the junction was deleted");
            Check.Equal(Canary, File.ReadAllText(canaryFile), "content of the file behind the junction");
            Check.True((File.GetAttributes(canaryFile) & FileAttributes.ReadOnly) != 0,
                "the read-only flag of the file behind the junction was changed");
            Check.True(!Directory.Exists(old.Path), "the snapshot folder was not removed");
            Check.True(db.GetRestorePoints().All(r => r.Id != old.Id), "the snapshot record was not removed");
        }

        // ---------------------------------------------------------------------------------
        // 2. Writing into an existing (resumed) snapshot must never write through a link
        // ---------------------------------------------------------------------------------

        private static void ResumeDoesNotWriteThroughHardLink()
        {
            using var sb = new Sandbox("resume");
            var source = sb.Dir("src");
            var sourceFile = Path.Combine(source, "report.txt");
            Sandbox.Write(sourceFile, "version 1");
            Sandbox.Write(Path.Combine(source, "other.txt"), "unchanged");

            var (db, config) = NewEngine(sb, source, sb.Dir("dest"));
            var first = RunNewBackup(db, config);
            var second = RunNewBackup(db, config);

            var firstCopy = Path.Combine(first.Path, "src", "report.txt");
            var secondCopy = Path.Combine(second.Path, "src", "report.txt");
            if (!TestFs.SameFile(firstCopy, secondCopy))
                throw new SkipTestException("the sandbox volume did not hard-link unchanged files (not NTFS?)");

            // Pretend the second backup was interrupted, then the user edits the file.
            db.UpdateRestorePointStatus(second.Id, BackupStatus.Interrupted);
            Sandbox.Write(sourceFile, "version 2, edited after the interruption");

            RunBackup(db, config); // resumes the second snapshot

            Check.Equal(BackupStatus.Completed, Get(db, second.Id).Status, "status of the resumed snapshot");
            Check.Equal("version 1", File.ReadAllText(firstCopy), "older snapshot's copy after the resume");
            Check.Equal("version 2, edited after the interruption", File.ReadAllText(secondCopy), "resumed snapshot's copy");
            Check.True(!TestFs.SameFile(firstCopy, secondCopy), "the two versions still share one file");
            Check.Equal(File.GetLastWriteTimeUtc(sourceFile), File.GetLastWriteTimeUtc(secondCopy), "timestamp of the resumed copy");
            Check.True(Directory.EnumerateFiles(second.Path, "*.tmp", SearchOption.AllDirectories).All(f => !Path.GetFileName(f).StartsWith("~mlb")),
                "a temporary copy file was left in the snapshot");
        }

        private static void ResumeDoesNotWriteThroughFolderLink()
        {
            using var sb = new Sandbox("resumelink");
            var source = sb.Dir("src");
            Sandbox.Write(Path.Combine(source, "docs", "x.txt"), "current source content");

            var (db, config) = NewEngine(sb, source, sb.Dir("dest"));
            var snapshot = RunNewBackup(db, config);
            db.UpdateRestorePointStatus(snapshot.Id, BackupStatus.Interrupted);

            // The interrupted snapshot holds a folder link where the source now has a real folder.
            var elsewhere = sb.Dir("user", "elsewhere");
            var canaryFile = Path.Combine(elsewhere, "x.txt");
            Sandbox.Write(canaryFile, Canary);
            var docsInSnapshot = Path.Combine(snapshot.Path, "src", "docs");
            TestFs.RemoveTreeWithoutFollowingLinks(docsInSnapshot);
            TestFs.CreateJunction(docsInSnapshot, elsewhere);

            RunBackup(db, config); // resumes the snapshot

            Check.Equal(Canary, File.ReadAllText(canaryFile), "content of the file behind the folder link");
            Check.True(Directory.Exists(docsInSnapshot) && !TestFs.IsReparsePoint(docsInSnapshot),
                "the snapshot folder is still a link instead of a real folder");
            Check.Equal("current source content", File.ReadAllText(Path.Combine(docsInSnapshot, "x.txt")), "resumed snapshot's copy");
        }

        private static void NoHardLinkThroughLinkInOlderSnapshot()
        {
            using var sb = new Sandbox("linksource");
            var source = sb.Dir("src");
            var sourceFile = Path.Combine(source, "d", "f.txt");
            Sandbox.Write(sourceFile, "same bytes");

            var (db, config) = NewEngine(sb, source, sb.Dir("dest"));
            var first = RunNewBackup(db, config);

            // The older snapshot holds a link at "d" pointing at the user's live folder, which
            // contains a file with the same size and timestamp as the source file.
            var live = sb.Dir("user", "live");
            var liveFile = Path.Combine(live, "f.txt");
            File.Copy(sourceFile, liveFile);
            File.SetLastWriteTimeUtc(liveFile, File.GetLastWriteTimeUtc(sourceFile));
            var linkInFirst = Path.Combine(first.Path, "src", "d");
            TestFs.RemoveTreeWithoutFollowingLinks(linkInFirst);
            TestFs.CreateJunction(linkInFirst, live);

            var second = RunNewBackup(db, config);
            var secondCopy = Path.Combine(second.Path, "src", "d", "f.txt");

            Check.True(File.Exists(secondCopy), "the new snapshot is missing the file");
            Check.True(!TestFs.SameFile(secondCopy, liveFile), "the new snapshot's copy is hard-linked to the user's live file");
            Check.Equal("same bytes", File.ReadAllText(secondCopy), "new snapshot's copy");
        }
    }
}
