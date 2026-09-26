namespace MyLocalBackup.Core.Engine
{
    /// <summary>
    /// Rules for where snapshots may be written relative to the folders being backed up.
    /// </summary>
    public static class BackupPathRules
    {
        /// <summary>Snapshots are written to &lt;destination&gt;\Backup Snapshots\&lt;timestamp&gt;.</summary>
        public const string SnapshotsFolderName = "Backup Snapshots";

        public static string GetSnapshotsRoot(string destinationRoot) =>
            Path.Combine(destinationRoot, SnapshotsFolderName);

        /// <summary>
        /// Returns a plain-language reason why this destination cannot be used with these source
        /// folders, or null when the combination is fine.
        ///
        /// Refused:
        /// - the snapshot folder would be inside a source folder (each backup would copy the
        ///   snapshots again, including the one being written, nesting until the disk is full);
        /// - a source folder is inside the snapshot folder (the app deletes old snapshots there).
        /// A source that is elsewhere in the destination (for example destination D:\ and source
        /// D:\Photos) is allowed: the app only writes into the "Backup Snapshots" folder.
        /// </summary>
        public static string? GetOverlapError(IEnumerable<string> sourceFolders, string? destinationRoot)
        {
            if (string.IsNullOrWhiteSpace(destinationRoot)) return null;

            string snapshotsRoot;
            try { snapshotsRoot = Normalize(GetSnapshotsRoot(destinationRoot)); }
            catch (Exception) { return null; } // invalid path: other checks report it

            foreach (var source in sourceFolders)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;

                string sourceRoot;
                try { sourceRoot = Normalize(source); }
                catch (Exception) { continue; }

                if (snapshotsRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return $"The backup destination \"{destinationRoot}\" is inside the folder you back up (\"{source}\").\n\n" +
                           "Every backup would copy the earlier backups again, over and over, until the drive is full.\n\n" +
                           "Please choose a destination outside your source folders, ideally on another drive.";
                }

                if (sourceRoot.StartsWith(snapshotsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return $"The folder \"{source}\" is inside the backup's own snapshot folder (\"{snapshotsRoot.TrimEnd(Path.DirectorySeparatorChar)}\").\n\n" +
                           "MyLocalBackup creates and deletes snapshots there, so it cannot also back that folder up.\n\n" +
                           "Please choose a different source folder or destination.";
                }
            }

            return null;
        }

        /// <summary>Full path with exactly one trailing separator, so "C:\Data" never matches "C:\Database".</summary>
        private static string Normalize(string path)
        {
            var full = Path.GetFullPath(path.Trim());
            full = Path.TrimEndingDirectorySeparator(full);
            return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        }
    }
}
