using System.Globalization;
using Microsoft.Data.Sqlite;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Storage;

public sealed class ManifestDatabase : IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE metadata (
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        ) STRICT;

        CREATE TABLE sources (
            source_id TEXT PRIMARY KEY NOT NULL,
            root_path TEXT NOT NULL,
            display_name TEXT NOT NULL
        ) STRICT;

        CREATE TABLE entries (
            entry_id INTEGER PRIMARY KEY,
            source_id TEXT NOT NULL REFERENCES sources(source_id),
            relative_path TEXT NOT NULL COLLATE BINARY,
            kind INTEGER NOT NULL,
            object_algorithm TEXT NULL,
            object_hash TEXT NULL,
            object_length INTEGER NULL,
            last_write_utc TEXT NOT NULL,
            attributes INTEGER NOT NULL,
            link_target TEXT NULL,
            UNIQUE(source_id, relative_path),
            CHECK (kind BETWEEN 0 AND 3),
            CHECK (
                (kind = 0 AND object_algorithm IS NOT NULL AND object_hash IS NOT NULL AND object_length IS NOT NULL)
                OR
                (kind <> 0 AND object_algorithm IS NULL AND object_hash IS NULL AND object_length IS NULL)
            )
        ) STRICT;

        CREATE INDEX ix_entries_object_hash ON entries(object_hash) WHERE object_hash IS NOT NULL;
        """;

    private readonly SqliteConnection _connection;
    private bool _sealed;

    private ManifestDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static async Task<ManifestDatabase> CreateAsync(
        string path,
        Guid snapshotId,
        Guid repositoryId,
        IReadOnlyCollection<BackupSource> sources,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty || repositoryId == Guid.Empty)
        {
            throw new ArgumentException("Snapshot and repository IDs must be non-empty.");
        }

        if (sources.Count == 0)
        {
            throw new ArgumentException("A snapshot manifest requires at least one source.", nameof(sources));
        }

        var fullPath = PathRules.NormalizeAbsolutePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var connection = CreateConnection(fullPath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var manifest = new ManifestDatabase(connection);
        try
        {
            await manifest.ConfigureConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = Schema;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await manifest.SetMetadataAsync("format_version", "1", cancellationToken).ConfigureAwait(false);
            await manifest.SetMetadataAsync("snapshot_id", snapshotId.ToString("D"), cancellationToken).ConfigureAwait(false);
            await manifest.SetMetadataAsync("repository_id", repositoryId.ToString("D"), cancellationToken).ConfigureAwait(false);
            await manifest.SetMetadataAsync("created_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var source in sources)
            {
                await using var sourceCommand = connection.CreateCommand();
                sourceCommand.Transaction = (SqliteTransaction)transaction;
                sourceCommand.CommandText = "INSERT INTO sources(source_id, root_path, display_name) VALUES ($id, $root, $name);";
                sourceCommand.Parameters.AddWithValue("$id", source.Id.ToString("D"));
                sourceCommand.Parameters.AddWithValue("$root", source.RootPath);
                sourceCommand.Parameters.AddWithValue("$name", source.DisplayName);
                await sourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return manifest;
        }
        catch
        {
            await manifest.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task AddEntryAsync(ManifestEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_sealed)
        {
            throw new InvalidOperationException("A sealed manifest cannot be modified.");
        }

        ValidateEntry(entry);
        var relativePath = PathRules.NormalizeRelativePath(entry.RelativePath);

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO entries(
                source_id, relative_path, kind, object_algorithm, object_hash,
                object_length, last_write_utc, attributes, link_target)
            VALUES(
                $source, $path, $kind, $algorithm, $hash,
                $length, $lastWrite, $attributes, $linkTarget);
            """;
        command.Parameters.AddWithValue("$source", entry.SourceId.ToString("D"));
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$algorithm", (object?)entry.Content?.Algorithm ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)entry.Content?.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$length", (object?)entry.Content?.Length ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastWrite", entry.LastWriteUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$attributes", (long)entry.Attributes);
        command.Parameters.AddWithValue("$linkTarget", (object?)entry.LinkTarget ?? DBNull.Value);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ManifestConflictException(
                $"Manifest entry '{entry.SourceId:D}/{relativePath}' conflicts with an existing entry or source invariant.",
                exception);
        }
    }

    public async Task<IReadOnlyList<ManifestEntry>> ReadEntriesAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<ManifestEntry>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, relative_path, kind, object_algorithm, object_hash,
                   object_length, last_write_utc, attributes, link_target
            FROM entries
            ORDER BY source_id, relative_path;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var content = reader.IsDBNull(3)
                ? null
                : new StoredObject(reader.GetString(3), reader.GetString(4), reader.GetInt64(5));
            entries.Add(new ManifestEntry(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                (ManifestEntryKind)reader.GetInt32(2),
                content,
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                (FileAttributes)reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return entries;
    }

    public async Task SealAsync(CancellationToken cancellationToken = default)
    {
        var integrity = await VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
        if (!integrity.IsValid)
        {
            throw new RepositoryCorruptionException($"Manifest cannot be sealed: {integrity.Error}");
        }

        await SetMetadataAsync("sealed_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
        _sealed = true;
    }

    public async Task<ManifestIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return ManifestIntegrityResult.Invalid($"SQLite integrity check returned '{result}'.");
                }
            }

            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ManifestIntegrityResult.Invalid("The manifest contains a foreign-key violation.");
                }
            }

            return ManifestIntegrityResult.Valid;
        }
        catch (SqliteException exception)
        {
            return ManifestIntegrityResult.Invalid(exception.Message);
        }
    }

    public static async Task<ManifestIntegrityResult> VerifyFileAsync(
        string path,
        Guid expectedSnapshotId,
        Guid expectedRepositoryId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return ManifestIntegrityResult.Invalid("The manifest file is missing.");
        }

        try
        {
            await using var connection = CreateConnection(path, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var manifest = new ManifestDatabase(connection);
            await manifest.ConfigureConnectionAsync(cancellationToken).ConfigureAwait(false);
            var integrity = await manifest.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
            if (!integrity.IsValid)
            {
                return integrity;
            }

            var snapshotId = await manifest.GetMetadataAsync("snapshot_id", cancellationToken).ConfigureAwait(false);
            var repositoryId = await manifest.GetMetadataAsync("repository_id", cancellationToken).ConfigureAwait(false);
            var formatVersion = await manifest.GetMetadataAsync("format_version", cancellationToken).ConfigureAwait(false);
            var sealedUtc = await manifest.GetMetadataAsync("sealed_utc", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshotId, expectedSnapshotId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(repositoryId, expectedRepositoryId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(formatVersion, "1", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(sealedUtc))
            {
                return ManifestIntegrityResult.Invalid("Manifest identity or sealed state does not match the staged snapshot.");
            }

            return ManifestIntegrityResult.Valid;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or FormatException)
        {
            return ManifestIntegrityResult.Invalid(exception.Message);
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode)
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
    }

    private async Task ConfigureConnectionAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetMetadataAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetMetadataAsync(string key, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static void ValidateEntry(ManifestEntry entry)
    {
        if (entry.SourceId == Guid.Empty)
        {
            throw new ArgumentException("A manifest entry must have a source ID.", nameof(entry));
        }

        if (entry.Kind == ManifestEntryKind.File && entry.Content is null)
        {
            throw new ArgumentException("A file manifest entry must reference a content object.", nameof(entry));
        }

        if (entry.Kind != ManifestEntryKind.File && entry.Content is not null)
        {
            throw new ArgumentException("Only file manifest entries may reference a content object.", nameof(entry));
        }

        if (entry.Kind is ManifestEntryKind.SymbolicLink or ManifestEntryKind.Junction && string.IsNullOrEmpty(entry.LinkTarget))
        {
            throw new ArgumentException("A link manifest entry must preserve its link target.", nameof(entry));
        }
    }
}
