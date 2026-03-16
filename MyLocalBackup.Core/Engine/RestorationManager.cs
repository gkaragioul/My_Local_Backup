namespace MyLocalBackup.Core.Engine
{
    public static class RestorationManager
    {
        public static void RestoreFile(string snapshotFilePath, string targetPath, bool overwrite = false)
        {
            if (!File.Exists(snapshotFilePath))
                throw new FileNotFoundException("Snapshot file not found.", snapshotFilePath);

            // Path traversal protection
            var safeTargetPath = Path.GetFullPath(targetPath);
            var targetDir = Path.GetDirectoryName(safeTargetPath);
            if (string.IsNullOrEmpty(targetDir))
                throw new ArgumentException("Invalid target path", nameof(targetPath));

            Directory.CreateDirectory(targetDir);

            File.Copy(snapshotFilePath, safeTargetPath, overwrite);
        }

        public static void RestoreFolder(string snapshotFolderPath, string targetFolderPath)
        {
            if (!Directory.Exists(snapshotFolderPath))
                throw new DirectoryNotFoundException($"Snapshot folder not found: {snapshotFolderPath}");

            var fullTargetPath = Path.GetFullPath(targetFolderPath);
            // Ensure trailing separator so StartsWith can't match partial directory names
            // e.g. prevents "C:\Restore" matching "C:\RestoreEvil\file.txt"
            if (!fullTargetPath.EndsWith(Path.DirectorySeparatorChar))
                fullTargetPath += Path.DirectorySeparatorChar;
            Directory.CreateDirectory(fullTargetPath);

            foreach (var file in Directory.GetFiles(snapshotFolderPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(snapshotFolderPath, file);

                // Path traversal protection: ensure relative path doesn't escape target
                if (relativePath.Contains(".."))
                    throw new InvalidOperationException($"Path traversal detected in snapshot: {relativePath}");

                var targetFile = Path.GetFullPath(Path.Combine(fullTargetPath, relativePath));
                if (!targetFile.StartsWith(fullTargetPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Path traversal detected: {relativePath}");

                var targetDir = Path.GetDirectoryName(targetFile);
                if (targetDir != null) Directory.CreateDirectory(targetDir);

                File.Copy(file, targetFile, true);
            }
        }

        public static string RestoreToStaging(string snapshotPath, bool isFolder)
        {
            var stagingRoot = Path.Combine(Path.GetTempPath(), "MyLocalBackup_RestoreStaging_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);

            try
            {
                if (isFolder)
                {
                    RestoreFolder(snapshotPath, stagingRoot);
                }
                else
                {
                    var targetFile = Path.Combine(stagingRoot, Path.GetFileName(snapshotPath));
                    RestoreFile(snapshotPath, targetFile, true);
                }
            }
            catch
            {
                // Clean up staging directory on failure to prevent temp space leaks
                try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); } catch { }
                throw;
            }

            return stagingRoot;
        }

        public static void FinalizeRestore(string stagingPath, string finalDestination)
        {
            // stagingPath is always a directory created by RestoreToStaging
            try
            {
                RestoreFolder(stagingPath, finalDestination);
            }
            finally
            {
                try { Directory.Delete(stagingPath, true); } catch { }
            }
        }
    }
}
