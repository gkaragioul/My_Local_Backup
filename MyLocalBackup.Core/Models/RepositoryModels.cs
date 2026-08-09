using MyLocalBackup.Core.Storage;

namespace MyLocalBackup.Core.Models;

public sealed class BackupConfigurationException : Exception
{
    public BackupConfigurationException(string message)
        : base(message)
    {
    }

    public BackupConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record BackupSource
{
    private BackupSource(Guid id, string rootPath, string displayName)
    {
        Id = id;
        RootPath = rootPath;
        DisplayName = displayName;
    }

    public Guid Id { get; }

    public string RootPath { get; }

    public string DisplayName { get; }

    public static BackupSource Create(Guid id, string rootPath, string? displayName = null)
    {
        if (id == Guid.Empty)
        {
            throw new BackupConfigurationException("A backup source must have a non-empty immutable ID.");
        }

        var normalizedRoot = PathRules.NormalizeAbsolutePath(rootPath);
        var effectiveName = string.IsNullOrWhiteSpace(displayName)
            ? GetDefaultDisplayName(normalizedRoot)
            : displayName.Trim();

        return new BackupSource(id, normalizedRoot, effectiveName);
    }

    public string ManifestKey(string relativePath)
    {
        var normalized = PathRules.NormalizeRelativePath(relativePath)
            .Replace(Path.DirectorySeparatorChar, '/');

        return $"{Id:N}/{normalized}";
    }

    private static string GetDefaultDisplayName(string normalizedRoot)
    {
        var root = Path.GetPathRoot(normalizedRoot);
        if (string.Equals(root?.TrimEnd(Path.DirectorySeparatorChar), normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return normalizedRoot.TrimEnd(Path.DirectorySeparatorChar).TrimEnd(Path.VolumeSeparatorChar);
        }

        return Path.GetFileName(normalizedRoot);
    }
}

public sealed record RepositoryDescriptor(
    Guid RepositoryId,
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string VolumeIdentity);
