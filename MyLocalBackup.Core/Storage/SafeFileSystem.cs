using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace MyLocalBackup.Core.Storage
{
    /// <summary>
    /// File-system operations for snapshot folders that protect data outside the backup.
    ///
    /// Two rules, applied everywhere the app deletes or writes inside a snapshot:
    /// 1. Never follow a reparse point (symbolic link, junction, mount point). Deleting a link
    ///    removes the link itself; whatever it points at is never entered, changed or deleted.
    /// 2. Never write through a hard link. Snapshot files share data with older snapshots, so an
    ///    existing file is replaced by renaming a fresh copy over it, never overwritten in place.
    /// </summary>
    public static class SafeFileSystem
    {
        /// <summary>Prefix of the temporary files used while copying into a snapshot folder.</summary>
        public const string TempFilePrefix = "~mlb";
        public const string TempFileSuffix = ".tmp";

        private static readonly EnumerationOptions SingleLevel = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,          // see hidden/system entries too, so nothing is left behind
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false
        };

        public sealed class DeleteResult
        {
            public long FilesDeleted { get; internal set; }
            public long LinksRemoved { get; internal set; }
            public int Failures { get; internal set; }
            public bool Cancelled { get; internal set; }
            public string? FirstError { get; internal set; }

            internal void Fail(string path, Exception ex)
            {
                Failures++;
                FirstError ??= $"{path}: {ex.Message}";
            }
        }

        /// <summary>True when the entry itself is a reparse point (checked without following it).</summary>
        public static bool IsReparsePoint(FileSystemInfo entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) != 0;

        /// <summary>
        /// True for a reparse point that redirects to another location: a symbolic link, a junction,
        /// a volume mount point or any other "name surrogate" reparse point. Other reparse points
        /// (for example OneDrive placeholders) are real local folders and are not links.
        /// When the kind cannot be read, the entry is treated as a link, so it is never entered.
        /// </summary>
        public static bool IsLink(FileSystemInfo entry)
        {
            if (!IsReparsePoint(entry)) return false;
            uint? tag = NativeMethods.TryGetReparseTag(entry.FullName);
            if (tag == null) return true;
            return (tag.Value & NativeMethods.ReparseTagNameSurrogateBit) != 0;
        }

        /// <summary>
        /// The reparse tag of an entry, read without following it: 0 for a normal file or folder,
        /// for example 0xA0000003 for a junction or 0xA000000C for a symbolic link; null if unreadable.
        /// </summary>
        public static uint? TryGetReparseTag(string path) => NativeMethods.TryGetReparseTag(path);

        /// <summary>
        /// Deletes a directory tree without following links. A link inside the tree (or the root
        /// itself, if it is a link) is removed as a link; the folder it points at is untouched.
        /// Best effort: entries that cannot be removed are counted in <see cref="DeleteResult.Failures"/>
        /// and the rest of the tree is still processed. Does not throw for I/O errors.
        /// </summary>
        /// <param name="onFileDeleted">Called after each file is deleted, with the running count.</param>
        public static DeleteResult DeleteDirectoryTree(string path, CancellationToken cancellationToken = default,
            Action<long>? onFileDeleted = null)
        {
            var result = new DeleteResult();
            var root = new DirectoryInfo(path);
            if (!root.Exists) return result;

            if (IsLink(root))
            {
                RemoveDirectoryEntry(root, result, isLink: true);
                return result;
            }

            // Iterative post-order walk: a directory is removed after everything inside it.
            // (No recursion, so very deep trees cannot overflow the stack.)
            var stack = new Stack<(DirectoryInfo Dir, bool ChildrenDone)>();
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    return result;
                }

                var (dir, childrenDone) = stack.Pop();
                if (childrenDone)
                {
                    RemoveDirectoryEntry(dir, result, isLink: false);
                    continue;
                }

                stack.Push((dir, true));

                List<FileSystemInfo> entries;
                try
                {
                    entries = dir.EnumerateFileSystemInfos("*", SingleLevel).ToList();
                }
                catch (Exception ex)
                {
                    result.Fail(dir.FullName, ex);
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        return result;
                    }

                    if (entry is DirectoryInfo subDir)
                    {
                        if (IsLink(subDir))
                            RemoveDirectoryEntry(subDir, result, isLink: true); // the link only, never its target
                        else
                            stack.Push((subDir, false)); // a real folder: empty it, then remove it
                    }
                    else if (entry is FileInfo file)
                    {
                        if (DeleteFileEntry(file, result))
                            onFileDeleted?.Invoke(result.FilesDeleted);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Deletes one file entry. For a file symbolic link this removes the link, never its target.
        /// A read-only flag is cleared only on real files (a link's flag is left alone).
        /// </summary>
        private static bool DeleteFileEntry(FileInfo file, DeleteResult result)
        {
            bool isLink = IsReparsePoint(file);
            try
            {
                try
                {
                    File.Delete(file.FullName);
                }
                catch (UnauthorizedAccessException) when (!isLink && (file.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    file.Attributes &= ~FileAttributes.ReadOnly;
                    File.Delete(file.FullName);
                }

                if (isLink) result.LinksRemoved++;
                else result.FilesDeleted++;
                return true;
            }
            catch (Exception ex)
            {
                result.Fail(file.FullName, ex);
                return false;
            }
        }

        /// <summary>
        /// Removes a single directory entry: an empty real folder, or a link (the link only).
        /// Directory.Delete(path, recursive: false) maps to RemoveDirectoryW, which removes a
        /// symbolic link or junction without touching its target.
        /// </summary>
        private static void RemoveDirectoryEntry(DirectoryInfo dir, DeleteResult result, bool isLink)
        {
            try
            {
                try
                {
                    Directory.Delete(dir.FullName, recursive: false);
                }
                catch (IOException) when (!isLink && (dir.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    dir.Attributes &= ~FileAttributes.ReadOnly;
                    Directory.Delete(dir.FullName, recursive: false);
                }
                catch (UnauthorizedAccessException) when (!isLink && (dir.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    dir.Attributes &= ~FileAttributes.ReadOnly;
                    Directory.Delete(dir.FullName, recursive: false);
                }

                if (isLink) result.LinksRemoved++;
            }
            catch (Exception ex)
            {
                result.Fail(dir.FullName, ex);
            }
        }

        /// <summary>
        /// Makes sure <paramref name="path"/> is a real directory before anything is written into it.
        /// If a link sits there (for example left in a resumed snapshot), the link is removed first,
        /// so files are never written into the folder it points at.
        /// </summary>
        public static void EnsureRealDirectory(string path)
        {
            var dir = new DirectoryInfo(path);
            if (dir.Exists && IsLink(dir))
            {
                Logger.Log($"Replacing a folder link inside the snapshot with a real folder (the link target is not touched): {path}");
                Directory.Delete(path, recursive: false); // removes the link only
            }
            Directory.CreateDirectory(path);
        }

        /// <summary>
        /// Removes whatever file entry exists at <paramref name="path"/> (a file, a hard link or a
        /// file symbolic link) so a new file can be put there. Only that directory entry goes away:
        /// other hard links keep their data and a link target is never touched. A folder link is
        /// removed as a link; a real folder is left alone. Returns true when the path is free.
        /// </summary>
        public static bool RemoveEntryIfPresent(string path)
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                if (!IsReparsePoint(file) && (file.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    // Clearing the flag on a hard-linked file also clears it on the other links, but it
                    // does not change their content. Needed so the stale entry can be removed.
                    file.Attributes &= ~FileAttributes.ReadOnly;
                }
                File.Delete(path);
                return true;
            }

            var dir = new DirectoryInfo(path);
            if (dir.Exists)
            {
                if (IsLink(dir))
                {
                    Directory.Delete(path, recursive: false);
                    return true;
                }
                return false; // a real folder: never removed here
            }

            return true;
        }

        /// <summary>A unique temporary file name in the same folder as <paramref name="targetPath"/>.</summary>
        public static string NewTempPathBeside(string targetPath)
        {
            var dir = Path.GetDirectoryName(targetPath)
                ?? throw new ArgumentException("Target path has no folder", nameof(targetPath));
            return Path.Combine(dir, TempFilePrefix + Guid.NewGuid().ToString("N") + TempFileSuffix);
        }

        public static bool IsTempFileName(string fileName) =>
            fileName.Length == TempFilePrefix.Length + 32 + TempFileSuffix.Length
            && fileName.StartsWith(TempFilePrefix, StringComparison.Ordinal)
            && fileName.EndsWith(TempFileSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Puts a finished temporary file in place of <paramref name="targetPath"/>. Renaming replaces
        /// the directory entry, so if the target was a hard link (or a symbolic link) only that entry
        /// is replaced; the data it shared with other snapshots, or the link target, stays as it was.
        /// </summary>
        public static void ReplaceWithTempFile(string tempPath, string targetPath)
        {
            var existing = new FileInfo(targetPath);
            if (existing.Exists && !IsReparsePoint(existing) && (existing.Attributes & FileAttributes.ReadOnly) != 0)
            {
                // MoveFileEx refuses to replace a read-only file. Clearing the flag changes no content.
                existing.Attributes &= ~FileAttributes.ReadOnly;
            }
            File.Move(tempPath, targetPath, overwrite: true);
        }

        /// <summary>
        /// Copies a file so that an existing file at the target is replaced, never written through:
        /// the copy goes to a temporary file in the target folder and is then renamed over the target.
        /// </summary>
        public static void CopyFileReplacing(string sourcePath, string targetPath)
        {
            var temp = NewTempPathBeside(targetPath);
            try
            {
                File.Copy(sourcePath, temp, overwrite: false);
                ReplaceWithTempFile(temp, targetPath);
            }
            catch
            {
                TryDeleteTempFile(temp);
                throw;
            }
        }

        public static void TryDeleteTempFile(string tempPath)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception ex) { Logger.Log($"Warning: Could not remove temporary file {tempPath}: {ex.Message}"); }
        }

        private static class NativeMethods
        {
            /// <summary>Microsoft's "name surrogate" bit: the reparse point names another file or folder.</summary>
            public const uint ReparseTagNameSurrogateBit = 0x20000000;

            private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
            private const int FindExInfoBasic = 1;
            private const int FindExSearchNameMatch = 0;
            private static readonly IntPtr InvalidHandle = new(-1);

            /// <summary>
            /// Reads the reparse tag of an entry without opening or following it (FindFirstFileEx reports
            /// the tag in dwReserved0). Returns 0 for a normal entry and null when it cannot be read.
            /// </summary>
            public static uint? TryGetReparseTag(string path)
            {
                try
                {
                    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    var extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
                        : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2)
                        : @"\\?\" + full;

                    IntPtr handle = FindFirstFileExW(extended, FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, 0);
                    if (handle == InvalidHandle) return null;
                    FindClose(handle);

                    return (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0 ? 0 : data.dwReserved0;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            [StructLayout(LayoutKind.Sequential,
                CharSet = CharSet.Unicode)]
            private struct WIN32_FIND_DATAW
            {
                public uint dwFileAttributes;
                public ComTypes.FILETIME ftCreationTime;
                public ComTypes.FILETIME ftLastAccessTime;
                public ComTypes.FILETIME ftLastWriteTime;
                public uint nFileSizeHigh;
                public uint nFileSizeLow;
                public uint dwReserved0;
                public uint dwReserved1;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string cFileName;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
                public string cAlternateFileName;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
                int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FindClose(IntPtr hFindFile);
        }
    }
}
