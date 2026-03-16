using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MyLocalBackup.Core.Storage
{
    public static class HardLinkManager
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        // Track if we've already warned about hard link support for this drive (thread-safe)
        private static readonly ConcurrentDictionary<string, bool> _warnedDrives = new();

        /// <summary>
        /// Creates a hard link from lpFileName to lpExistingFileName.
        /// </summary>
        public static bool Create(string newFilePath, string existingFilePath)
        {
            if (!CreateHardLink(newFilePath, existingFilePath, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();

                // Only log once per drive/error combo to avoid log spam
                var driveRoot = Path.GetPathRoot(newFilePath) ?? "unknown";
                if (error == 1 && _warnedDrives.TryAdd(driveRoot, true)) // ERROR_INVALID_FUNCTION - file system doesn't support hard links
                {
                    Logger.Log($"Hard links not supported on drive {driveRoot} (likely exFAT/FAT32). Deduplication disabled, using full copies.");
                }
                else if (error == 17 && _warnedDrives.TryAdd($"{driveRoot}_cross", true)) // ERROR_NOT_SAME_DEVICE - cross-volume
                {
                    Logger.Log($"Hard links cannot span volumes (source and destination on different drives). Using full copies.");
                }

                return false;
            }
            return true;
        }
    }
}
