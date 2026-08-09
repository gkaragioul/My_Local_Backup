using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Storage;

public static class PathRules
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new BackupConfigurationException("A filesystem path cannot be empty.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(fullPath);

            if (!string.Equals(fullPath, root, PathComparison))
            {
                fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            }

            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BackupConfigurationException($"The path '{path}' is not a valid absolute filesystem path.", exception);
        }
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new BackupConfigurationException($"The path '{relativePath}' must be relative to its backup source.");
        }

        var segments = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var normalized = new Stack<string>();
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (!normalized.TryPop(out _))
                {
                    throw new BackupConfigurationException($"The relative path '{relativePath}' escapes its backup source.");
                }

                continue;
            }

            normalized.Push(segment);
        }

        return string.Join(Path.DirectorySeparatorChar, normalized.Reverse());
    }

    public static bool IsSameOrDescendant(string candidatePath, string rootPath)
    {
        var candidate = NormalizeAbsolutePath(candidatePath);
        var root = NormalizeAbsolutePath(rootPath);

        if (string.Equals(candidate, root, PathComparison))
        {
            return true;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootWithSeparator, PathComparison);
    }

    public static void EnsureNoSourceDestinationOverlap(IEnumerable<BackupSource> sources, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var destination = NormalizeAbsolutePath(destinationPath);

        foreach (var source in sources)
        {
            if (IsSameOrDescendant(destination, source.RootPath) ||
                IsSameOrDescendant(source.RootPath, destination))
            {
                throw new BackupConfigurationException(
                    $"Backup source '{source.RootPath}' and destination '{destination}' overlap. " +
                    "Choose independent locations to prevent recursive or self-destructive backups.");
            }
        }
    }
}
