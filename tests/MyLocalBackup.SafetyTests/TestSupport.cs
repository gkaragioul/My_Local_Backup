using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace MyLocalBackup.SafetyTests
{
    internal sealed class SkipTestException : Exception
    {
        public SkipTestException(string reason) : base(reason) { }
    }

    internal static class Check
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what}: expected <{expected}> but was <{actual}>");
        }
    }

    /// <summary>
    /// A throwaway folder under %TEMP%\mlb-safety-tests. Every file a test creates, including the
    /// "user data" canaries that must survive, lives inside it, so a failing test can only damage
    /// its own sandbox.
    /// </summary>
    internal sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), "mlb-safety-tests", $"{name}-{Guid.NewGuid():N}"[..(name.Length + 9)]);
            Directory.CreateDirectory(Root);
        }

        public string Dir(params string[] parts)
        {
            var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        public static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { TestFs.RemoveTreeWithoutFollowingLinks(Root); }
            catch (Exception ex) { Console.Error.WriteLine($"(sandbox cleanup warning for {Root}: {ex.Message})"); }
        }
    }

    internal static class TestFs
    {
        public static void CreateJunction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0 || !IsReparsePoint(link))
                throw new Exception($"Could not create junction {link} -> {target}: {p.StandardError.ReadToEnd()}");
        }

        /// <summary>Directory symbolic links need Developer Mode or admin; returns false when unavailable.</summary>
        public static bool TryCreateDirectorySymlink(string link, string target)
        {
            try { Directory.CreateSymbolicLink(link, target); return true; }
            catch (Exception) { return false; }
        }

        public static bool TryCreateFileSymlink(string link, string target)
        {
            try { File.CreateSymbolicLink(link, target); return true; }
            catch (Exception) { return false; }
        }

        public static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        /// <summary>Removes a folder tree created by a test. Links are removed, never entered.</summary>
        public static void RemoveTreeWithoutFollowingLinks(string path)
        {
            if (!Directory.Exists(path)) return;
            if (IsReparsePoint(path)) { Directory.Delete(path, recursive: false); return; }

            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                bool isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                if (entry is DirectoryInfo)
                {
                    if (isLink) Directory.Delete(entry.FullName, recursive: false);
                    else RemoveTreeWithoutFollowingLinks(entry.FullName);
                }
                else
                {
                    if (!isLink && (entry.Attributes & FileAttributes.ReadOnly) != 0)
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    File.Delete(entry.FullName);
                }
            }
            Directory.Delete(path, recursive: false);
        }

        /// <summary>
        /// NTFS identity of a file (volume serial + file index) and its hard-link count.
        /// Two paths with the same identity are hard links to the same data.
        /// </summary>
        public static (uint Volume, ulong Index, uint Links) FileIdentity(string path)
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info))
                throw new IOException($"GetFileInformationByHandle failed ({Marshal.GetLastWin32Error()}) for {path}");
            return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
        }

        public static bool SameFile(string a, string b)
        {
            var ia = FileIdentity(a);
            var ib = FileIdentity(b);
            return ia.Volume == ib.Volume && ia.Index == ib.Index;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
    }

    internal static class TestRunner
    {
        public static int Run(IReadOnlyList<(string Name, Action Body)> tests, string[] args)
        {
            var filter = args.FirstOrDefault();
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            int passed = 0, failed = 0, skipped = 0;

            foreach (var (name, body) in tests)
            {
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                // The engine logs to the console; keep it and show it only when a test fails.
                var captured = new StringWriter();
                Console.SetOut(captured);
                Console.SetError(captured);
                var sw = Stopwatch.StartNew();
                string outcome;
                string? detail = null;
                try
                {
                    body();
                    outcome = "PASS";
                    passed++;
                }
                catch (SkipTestException skip)
                {
                    outcome = "SKIP";
                    detail = skip.Message;
                    skipped++;
                }
                catch (Exception ex)
                {
                    outcome = "FAIL";
                    detail = ex.Message;
                    failed++;
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }

                Console.WriteLine($"{outcome}  {name} ({sw.ElapsedMilliseconds} ms){(detail != null ? " - " + detail : "")}");
                if (outcome == "FAIL")
                {
                    var lines = captured.ToString().Split('\n');
                    foreach (var line in lines.TakeLast(25))
                        Console.WriteLine("      | " + line.TrimEnd());
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped");
            return failed == 0 ? 0 : 1;
        }
    }
}
