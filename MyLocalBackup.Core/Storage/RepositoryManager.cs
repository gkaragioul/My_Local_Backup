using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Storage;

public interface IVolumeIdentityProvider
{
    string GetIdentity(string path);
}

public sealed class DestinationVolumeMismatchException : IOException
{
    public DestinationVolumeMismatchException(string message)
        : base(message)
    {
    }
}

public sealed class RepositoryManager
{
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root;
    private readonly string _repositoryFile;
    private readonly string _snapshotStagingRoot;
    private readonly string _snapshotsRoot;
    private readonly IVolumeIdentityProvider _volumeIdentityProvider;
    private RepositoryDescriptor? _descriptor;

    public RepositoryManager(string repositoryRoot, IVolumeIdentityProvider? volumeIdentityProvider = null)
    {
        _root = PathRules.NormalizeAbsolutePath(repositoryRoot);
        _repositoryFile = Path.Combine(_root, "repository.json");
        _snapshotStagingRoot = Path.Combine(_root, "staging", "snapshots");
        _snapshotsRoot = Path.Combine(_root, "snapshots");
        _volumeIdentityProvider = volumeIdentityProvider ?? new WindowsVolumeIdentityProvider();
    }

    public async Task<RepositoryDescriptor> OpenOrCreateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_snapshotStagingRoot);
        Directory.CreateDirectory(_snapshotsRoot);
        var currentVolumeIdentity = _volumeIdentityProvider.GetIdentity(_root);

        if (File.Exists(_repositoryFile))
        {
            await using var stream = new FileStream(_repositoryFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var existing = await JsonSerializer.DeserializeAsync<RepositoryDescriptor>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RepositoryCorruptionException("repository.json is empty or invalid.");
            ValidateDescriptor(existing, currentVolumeIdentity);
            _descriptor = existing;
            return existing;
        }

        var descriptor = new RepositoryDescriptor(
            Guid.NewGuid(),
            CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            currentVolumeIdentity);
        await WriteJsonAtomicallyAsync(_repositoryFile, descriptor, cancellationToken).ConfigureAwait(false);
        _descriptor = descriptor;
        return descriptor;
    }

    public async Task<SnapshotStagingArea> BeginSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("Snapshot ID must be non-empty.", nameof(snapshotId));
        }

        await EnsureOpenedAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(_snapshotStagingRoot, snapshotId.ToString("N"));
        if (Directory.Exists(directory))
        {
            throw new IOException($"Snapshot staging directory '{directory}' already exists.");
        }

        Directory.CreateDirectory(directory);
        return new SnapshotStagingArea(snapshotId, directory, Path.Combine(directory, "manifest.db"));
    }

    public async Task<string> PromoteSnapshotAsync(
        SnapshotStagingArea staging,
        SnapshotSummary summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(summary);
        var repository = await EnsureOpenedAsync(cancellationToken).ConfigureAwait(false);
        RequireContainedSnapshotStaging(staging);
        if (summary.SnapshotId != staging.SnapshotId)
        {
            throw new ArgumentException("Snapshot summary ID does not match the staging area.", nameof(summary));
        }

        var integrity = await ManifestDatabase.VerifyFileAsync(
            staging.ManifestPath,
            staging.SnapshotId,
            repository.RepositoryId,
            cancellationToken).ConfigureAwait(false);
        if (!integrity.IsValid)
        {
            throw new RepositoryCorruptionException(
                $"Snapshot '{staging.SnapshotId:D}' cannot be promoted: {integrity.Error}");
        }

        await WriteJsonAtomicallyAsync(Path.Combine(staging.DirectoryPath, "summary.json"), summary, cancellationToken)
            .ConfigureAwait(false);
        var destination = Path.Combine(_snapshotsRoot, staging.SnapshotId.ToString("N"));
        if (Directory.Exists(destination))
        {
            throw new IOException($"Committed snapshot '{staging.SnapshotId:D}' already exists.");
        }

        Directory.Move(staging.DirectoryPath, destination);
        SetReadOnly(Path.Combine(destination, "manifest.db"));
        SetReadOnly(Path.Combine(destination, "summary.json"));
        return destination;
    }

    public async Task<int> RecoverAbandonedSnapshotStagingAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenedAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(_snapshotStagingRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PathRules.IsSameOrDescendant(directory, _snapshotStagingRoot))
            {
                throw new RepositoryCorruptionException("A staging recovery path escaped the repository.");
            }

            ClearReadOnlyFiles(directory);
            Directory.Delete(directory, recursive: true);
            removed++;
        }

        return removed;
    }

    private async Task<RepositoryDescriptor> EnsureOpenedAsync(CancellationToken cancellationToken)
    {
        return _descriptor ?? await OpenOrCreateAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireContainedSnapshotStaging(SnapshotStagingArea staging)
    {
        var expectedDirectory = Path.Combine(_snapshotStagingRoot, staging.SnapshotId.ToString("N"));
        if (!string.Equals(
                PathRules.NormalizeAbsolutePath(staging.DirectoryPath),
                PathRules.NormalizeAbsolutePath(expectedDirectory),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                PathRules.NormalizeAbsolutePath(staging.ManifestPath),
                PathRules.NormalizeAbsolutePath(Path.Combine(expectedDirectory, "manifest.db")),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RepositoryCorruptionException("The snapshot staging path is outside its repository-owned location.");
        }
    }

    private static void ValidateDescriptor(RepositoryDescriptor descriptor, string currentVolumeIdentity)
    {
        if (descriptor.RepositoryId == Guid.Empty || descriptor.FormatVersion != CurrentFormatVersion)
        {
            throw new RepositoryCorruptionException("repository.json has an invalid ID or unsupported format version.");
        }

        if (!string.Equals(descriptor.VolumeIdentity, currentVolumeIdentity, StringComparison.OrdinalIgnoreCase))
        {
            throw new DestinationVolumeMismatchException(
                $"The repository belongs to volume '{descriptor.VolumeIdentity}', but this path is now on volume '{currentVolumeIdentity}'.");
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destination,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ClearReadOnlyFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static void SetReadOnly(string path)
    {
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }
}

public sealed class WindowsVolumeIdentityProvider : IVolumeIdentityProvider
{
    public string GetIdentity(string path)
    {
        var volumePath = new StringBuilder(1024);
        if (!GetVolumePathName(path, volumePath, volumePath.Capacity))
        {
            throw new IOException($"Windows could not resolve the volume containing '{path}'.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        var volumeName = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(volumePath.ToString(), volumeName, volumeName.Capacity))
        {
            throw new IOException($"Windows could not read the identity of volume '{volumePath}'.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        return volumeName.ToString().TrimEnd('\\');
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int bufferLength);
}
