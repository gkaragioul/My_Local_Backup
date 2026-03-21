using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MyLocalBackup.Core.Services
{
    public class UpdateInfo
    {
        public required string Version { get; set; }
        public required string DownloadUrl { get; set; }
        public required string ReleaseNotes { get; set; }
        public required string FileName { get; set; }
        /// <summary>Expected SHA256 hex digest from GitHub, or null if not provided.</summary>
        public string? ExpectedSha256 { get; set; }
    }

    public class UpdateService : IDisposable
    {
        private const string RepoOwner = "georgekgr12";
        private const string RepoName = "MyLocalBackup-releases";
        private const string GitHubApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyLocalBackup");
        private static readonly string DismissedVersionFile = Path.Combine(AppDataDir, "dismissed_update.txt");
        private static readonly string PendingUpdateFile = Path.Combine(AppDataDir, "pending_update.txt");
        private static readonly string PreviousVersionFile = Path.Combine(AppDataDir, "previous_version.txt");

        private static readonly string ETagFile = Path.Combine(AppDataDir, "github_etag.txt");

        private readonly HttpClient _httpClient;
        private readonly CancellationTokenSource _cts = new();
        private string? _cachedETag;
        private string? _cachedResponseBody;
        private bool _disposed;

        public UpdateService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.7.0";
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MyLocalBackup", version));
            LoadCachedETag();
            CleanupOrphanedTempScripts();
        }

        /// <summary>
        /// Removes orphaned mlb_relaunch_*.ps1 temp scripts from previous failed update attempts.
        /// </summary>
        private static void CleanupOrphanedTempScripts()
        {
            try
            {
                var tempDir = Path.GetTempPath();
                foreach (var file in Directory.EnumerateFiles(tempDir, "mlb_relaunch_*.ps1"))
                {
                    try { File.Delete(file); }
                    catch { /* File may be in use by a running update — skip */ }
                }
            }
            catch { /* Non-critical cleanup — ignore errors */ }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts.Cancel();
                _httpClient.Dispose();
                _cts.Dispose();
                _disposed = true;
            }
        }

        /// <summary>
        /// Compare only Major.Minor.Build so that 3-part (from GitHub tag)
        /// and 4-part (from assembly) versions are compared correctly.
        /// </summary>
        private static bool IsNewerVersion(Version latest, Version current)
        {
            return new Version(latest.Major, latest.Minor, latest.Build)
                 > new Version(current.Major, current.Minor, current.Build);
        }

        private void LoadCachedETag()
        {
            try
            {
                if (File.Exists(ETagFile))
                {
                    var lines = File.ReadAllLines(ETagFile);
                    if (lines.Length >= 2)
                    {
                        _cachedETag = lines[0];
                        _cachedResponseBody = string.Join('\n', lines.Skip(1));
                    }
                }
            }
            catch { /* Non-critical — will just do a full request */ }
        }

        private void SaveCachedETag(string etag, string body)
        {
            try
            {
                _cachedETag = etag;
                _cachedResponseBody = body;
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(ETagFile, $"{etag}\n{body}");
            }
            catch { /* Non-critical */ }
        }

        public async Task<UpdateInfo?> CheckForUpdatesAsync(bool isAuto = false)
        {
            if (_disposed) return null;

            // Use conditional request with ETag to avoid counting against GitHub rate limit
            var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
            if (!string.IsNullOrEmpty(_cachedETag))
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_cachedETag));

            using var response = await _httpClient.SendAsync(request, _cts.Token);

            string json;
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && _cachedResponseBody != null)
            {
                // 304 — release hasn't changed, use cached response (doesn't count against rate limit)
                json = _cachedResponseBody;
            }
            else if (response.IsSuccessStatusCode)
            {
                json = await response.Content.ReadAsStringAsync();
                // Cache the ETag and response for next time
                var etag = response.Headers.ETag?.Tag;
                if (!string.IsNullOrEmpty(etag))
                    SaveCachedETag(etag, json);
            }
            else
            {
                return null;
            }

            var release = JsonNode.Parse(json);

            var tagName = release?["tag_name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tagName)) return null;

            // Remove 'v' prefix if present
            var latestVersionStr = tagName.TrimStart('v');

            // Get current version
            var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version;
            if (currentVersion == null) return null;

            if (Version.TryParse(latestVersionStr, out var latestVersion))
            {
                if (IsNewerVersion(latestVersion, currentVersion))
                {
                    // For automatic checks, skip if the user already dismissed this version
                    if (isAuto && IsDismissed(tagName))
                        return null;

                    var assets = release?["assets"]?.AsArray();
                    var setupAsset = assets?.FirstOrDefault(a => a?["name"]?.GetValue<string>()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                                     ?? assets?.FirstOrDefault(a => a?["name"]?.GetValue<string>()?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true);

                    if (setupAsset != null)
                    {
                        var downloadUrl = setupAsset["browser_download_url"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(downloadUrl))
                            return null; // Asset exists but has no download URL — skip

                        // Try GitHub asset digest first, then parse from release notes body
                        string? sha256 = null;
                        var digest = setupAsset["digest"]?.GetValue<string>();
                        if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha256 = digest[7..];

                        // Fallback: parse SHA256 from release notes (format: "SHA256: <hex>")
                        if (sha256 == null)
                        {
                            var body = release?["body"]?.GetValue<string>() ?? "";
                            var match = System.Text.RegularExpressions.Regex.Match(body, @"SHA256:\s*([a-fA-F0-9]{64})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (match.Success)
                                sha256 = match.Groups[1].Value.ToLowerInvariant();
                        }

                        return new UpdateInfo
                        {
                            Version = tagName,
                            DownloadUrl = downloadUrl,
                            ReleaseNotes = release?["body"]?.GetValue<string>() ?? "No release notes.",
                            FileName = setupAsset["name"]?.GetValue<string>() ?? "setup.exe",
                            ExpectedSha256 = sha256
                        };
                    }
                }
            }

            return null;
        }

        /// <summary>Save the dismissed version so auto-checks won't re-prompt for it.</summary>
        public void DismissVersion(string version)
        {
            try { File.WriteAllText(DismissedVersionFile, version); }
            catch (Exception ex) { Logger.Log($"Warning: Could not save dismissed version: {ex.Message}"); }
        }

        private bool IsDismissed(string tagName)
        {
            try
            {
                if (!File.Exists(DismissedVersionFile)) return false;
                return File.ReadAllText(DismissedVersionFile).Trim() == tagName;
            }
            catch (Exception ex) { Logger.Log($"Warning: Could not read dismissed version file: {ex.Message}"); return false; }
        }

        /// <summary>
        /// Write a marker before starting the MSI install so the next launch
        /// can detect whether the update actually succeeded.
        /// </summary>
        public void WritePendingUpdateMarker(string expectedVersion)
        {
            try { File.WriteAllText(PendingUpdateFile, expectedVersion); }
            catch (Exception ex) { Logger.Log($"Warning: Could not write pending update marker: {ex.Message}"); }
        }

        /// <summary>
        /// On startup, check if a previous update was attempted but the version
        /// didn't change (MSI failed silently).
        /// </summary>
        public bool CheckPendingUpdateFailed(out string? expectedVersion)
        {
            expectedVersion = null;
            try
            {
                if (!File.Exists(PendingUpdateFile)) return false;

                expectedVersion = File.ReadAllText(PendingUpdateFile).Trim();
                // Clean up the marker regardless
                File.Delete(PendingUpdateFile);

                if (string.IsNullOrEmpty(expectedVersion)) return false;

                var expected = expectedVersion.TrimStart('v');
                var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version;
                if (currentVersion == null) return false;

                if (Version.TryParse(expected, out var expectedVer))
                {
                    // If still on older version, the update failed
                    return IsNewerVersion(expectedVer, currentVersion);
                }
            }
            catch (Exception ex) { Logger.Log($"Warning: Could not check pending update status: {ex.Message}"); }
            return false;
        }

        /// <summary>
        /// Save the current version and its download URL so users can roll back if an update breaks.
        /// Call this BEFORE starting the update.
        /// </summary>
        public void SavePreviousVersionInfo()
        {
            try
            {
                var currentVer = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
                var downloadUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/v{currentVer}/MyLocalBackupSetup.msi";
                File.WriteAllText(PreviousVersionFile, $"{currentVer}|{downloadUrl}");
            }
            catch (Exception ex) { Logger.Log($"Warning: Could not save previous version info: {ex.Message}"); }
        }

        /// <summary>
        /// Read the saved previous version info for rollback.
        /// Returns (version, downloadUrl) or null if not available.
        /// </summary>
        public static (string Version, string DownloadUrl)? GetPreviousVersionInfo()
        {
            try
            {
                if (!File.Exists(PreviousVersionFile)) return null;
                var text = File.ReadAllText(PreviousVersionFile).Trim();
                var parts = text.Split('|', 2);
                if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
                    return (parts[0], parts[1]);
            }
            catch (Exception ex) { Logger.Log($"Warning: Could not read previous version info: {ex.Message}"); }
            return null;
        }

        public async Task DownloadInstallerAsync(string downloadUrl, string destinationPath, IProgress<double> progress, string? expectedSha256 = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UpdateService));

            // Reject before downloading if no hash is available
            if (string.IsNullOrEmpty(expectedSha256))
                throw new InvalidOperationException(
                    "Update rejected: no SHA256 hash found in release notes.\n\nThis may indicate a tampered update source.");

            try
            {
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var canReportProgress = totalBytes != -1;

                using var sha256 = SHA256.Create();
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var totalRead = 0L;
                    var buffer = new byte[8192];

                    while (true)
                    {
                        var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            sha256.TransformFinalBlock(buffer, 0, 0);
                            break;
                        }

                        sha256.TransformBlock(buffer, 0, read, null, 0);
                        await fileStream.WriteAsync(buffer, 0, read);

                        totalRead += read;
                        if (canReportProgress)
                            progress.Report((double)totalRead / totalBytes * 100);
                    }
                }

                // Verify hash
                if (sha256.Hash == null)
                {
                    try { File.Delete(destinationPath); } catch { }
                    throw new InvalidOperationException("SHA256 hash computation failed. The downloaded file has been deleted for safety.");
                }
                var actualHash = BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(destinationPath); } catch { }
                    throw new InvalidOperationException(
                        $"Installer integrity check failed.\n\nExpected SHA256: {expectedSha256}\nActual SHA256: {actualHash}\n\nThe downloaded file has been deleted for safety.");
                }
            }
            catch
            {
                // Clean up any partial file. The hash-check blocks above already delete it before throwing,
                // so File.Delete on a missing file is silently swallowed by the inner try/catch.
                try { File.Delete(destinationPath); } catch { }
                throw;
            }
        }

        public void RunInstaller(string installerPath)
        {
            try
            {
                if (!File.Exists(installerPath))
                {
                    throw new FileNotFoundException("Installer file not found.", installerPath);
                }

                Process? started;
                if (installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    var logPath = Path.Combine(Path.GetTempPath(), "mlb_install.log");

                    // Use the current exe's location so this works regardless of install directory
                    var appExePath = Environment.ProcessPath ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "MyLocalBackup",
                        "MyLocalBackup.UI.exe"
                    );
                    var helperScript = Path.Combine(Path.GetTempPath(), $"mlb_relaunch_{Guid.NewGuid():N}.ps1");

                    // Use PowerShell: Start-Process -Wait blocks until msiexec finishes,
                    // then Start-Process (no -Wait) launches the app detached into its own
                    // window station so the GUI comes up correctly.
                    var scriptLines = new string[]
                    {
                        $"$msi = '{installerPath.Replace("'", "''")}'",
                        $"$log = '{logPath.Replace("'", "''")}'",
                        $"$app = '{appExePath.Replace("'", "''")}'",
                        "Start-Sleep -Seconds 2",
                        "$proc = Start-Process msiexec.exe -ArgumentList \"/i `\"$msi`\" /passive REBOOT=ReallySuppress /l*v `\"$log`\"\" -Wait -PassThru",
                        "if ($proc.ExitCode -ne 0) {",
                        "  Add-Content $log \"MSI exited with code $($proc.ExitCode)\"",
                        "}",
                        "if (Test-Path $app) { Start-Process $app }",
                        "Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue"
                    };

                    File.WriteAllLines(helperScript, scriptLines);

                    // UseShellExecute=true gives the child process its own desktop session,
                    // which is required for Start-Process to successfully launch a GUI app.
                    started = Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{helperScript}\"",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }
                else
                {
                    started = Process.Start(new ProcessStartInfo(installerPath)
                    {
                        UseShellExecute = true
                    });
                }

                if (started == null)
                    throw new InvalidOperationException("Failed to launch installer process.");

                // We don't wait for the installer — release the process handle immediately.
                started.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to run installer: {ex}");
                throw; // Rethrow to let UI handle it
            }
        }
    }
}
