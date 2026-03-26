using Microsoft.Data.Sqlite;
using MyLocalBackup.Core.Models;

namespace MyLocalBackup.Core.Data
{
    public class DatabaseManager
    {
        private readonly string _connectionString;
        private readonly string _dbPath;
        private readonly bool _useExclusiveMode;

        public DatabaseManager(string dbPath)
        {
            _dbPath = dbPath;
            
            // Detect if this database is on a removable/non-NTFS drive
            // Only use aggressive locking for external drives (exFAT/FAT32)
            _useExclusiveMode = IsExternalDrive(dbPath);
            
            // Add default timeout (60s) to mitigate 'database is locked' errors
            _connectionString = $"Data Source={dbPath};Default Timeout=60;Pooling=True;Max Pool Size=5;";
            
            // Only clean up stale files for external drives
            if (_useExclusiveMode)
            {
                CleanupStaleWalFiles(dbPath);
            }
            
            InitializeDatabase();
        }
        
        private bool IsExternalDrive(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;
                
                var driveInfo = new DriveInfo(root);
                // Removable drives (USB sticks, SD cards) and network drives need special handling
                return driveInfo.DriveType == DriveType.Removable || 
                       driveInfo.DriveType == DriveType.Network;
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Could not determine drive type for {path}: {ex.Message}");
                return false; // Assume local drive if we can't determine
            }
        }
        
        private void CleanupStaleWalFiles(string dbPath)
        {
            try
            {
                var walPath = dbPath + "-wal";
                var shmPath = dbPath + "-shm";
                var journalPath = dbPath + "-journal";
                
                if (File.Exists(walPath)) File.Delete(walPath);
                if (File.Exists(shmPath)) File.Delete(shmPath);
                if (File.Exists(journalPath)) File.Delete(journalPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: WAL cleanup failed for {dbPath}: {ex.Message}");
            }
        }

        private void ApplyPragmas(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            if (_useExclusiveMode)
            {
                cmd.CommandText = @"
                    PRAGMA journal_mode=DELETE;
                    PRAGMA locking_mode=EXCLUSIVE;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA busy_timeout=60000;
                    PRAGMA foreign_keys=ON;
                    PRAGMA cache_size=-8000;
                    PRAGMA temp_store=MEMORY;
                ";
            }
            else
            {
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA busy_timeout=30000;
                    PRAGMA foreign_keys=ON;
                    PRAGMA cache_size=-8000;
                    PRAGMA temp_store=MEMORY;
                    PRAGMA wal_autocheckpoint=500;
                ";
            }
            cmd.ExecuteNonQuery();
        }

        private SqliteConnection OpenAdHocConnection()
        {
            var conn = new SqliteConnection(_connectionString);
            try
            {
                conn.Open();
                ApplyPragmas(conn);
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        public SqliteConnection GetConnection()
        {
            return OpenAdHocConnection();
        }

        private void InitializeDatabase()
        {
            using (var connection = OpenAdHocConnection())
            {
                using (var setupCmd = connection.CreateCommand())
                {
                    setupCmd.CommandText =
                    @"
                        CREATE TABLE IF NOT EXISTS RestorePoints (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Timestamp TEXT NOT NULL,
                            Path TEXT NOT NULL,
                            Status INTEGER NOT NULL,
                            IsPinned INTEGER NOT NULL DEFAULT 0,
                            TargetDestination TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS FileEntries (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            RestorePointId INTEGER NOT NULL,
                            RelativePath TEXT NOT NULL,
                            IsDirectory INTEGER NOT NULL,
                            Size INTEGER NOT NULL,
                            LastWriteTime TEXT NOT NULL,
                            Attributes INTEGER NOT NULL DEFAULT 0,
                            FOREIGN KEY(RestorePointId) REFERENCES RestorePoints(Id)
                        );

                        CREATE INDEX IF NOT EXISTS idx_file_path ON FileEntries(RelativePath);
                        CREATE INDEX IF NOT EXISTS idx_file_rpid ON FileEntries(RestorePointId);
                        CREATE INDEX IF NOT EXISTS idx_restore_timestamp ON RestorePoints(Timestamp);
                        CREATE INDEX IF NOT EXISTS idx_file_dedup ON FileEntries(Size, LastWriteTime);
                        CREATE INDEX IF NOT EXISTS idx_restore_destination ON RestorePoints(TargetDestination);
                        CREATE INDEX IF NOT EXISTS idx_restore_status_dest ON RestorePoints(Status, TargetDestination);

                        CREATE TABLE IF NOT EXISTS FailedFiles (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            RestorePointId INTEGER NOT NULL,
                            FilePath TEXT NOT NULL,
                            FOREIGN KEY(RestorePointId) REFERENCES RestorePoints(Id)
                        );

                        CREATE INDEX IF NOT EXISTS idx_failedfiles_rpid ON FailedFiles(RestorePointId);
                    ";
                    setupCmd.ExecuteNonQuery();
                }

                // Migration: Ensure new columns exist for existing databases
                MigrateSchema(connection);
            }
        }

        private void MigrateSchema(SqliteConnection connection)
        {
            // Each ALTER TABLE runs as its own operation with a column-exists check.
            // If a previous migration crashed after the ALTER but before completing,
            // the column already exists — so we guard with ColumnExists to be idempotent.
            TryAddColumn(connection, "FileEntries", "Attributes", "INTEGER NOT NULL DEFAULT 0");
            TryAddColumn(connection, "RestorePoints", "IsPinned", "INTEGER NOT NULL DEFAULT 0");
            TryAddColumn(connection, "RestorePoints", "TargetDestination", "TEXT NOT NULL DEFAULT ''");
            // Indexes are created by InitializeDatabase with IF NOT EXISTS and run on every startup,
            // so no index creation is needed here.
        }

        // Only these identifiers are valid for schema migration — prevents SQL injection in DDL statements
        private static readonly HashSet<string> AllowedIdentifiers = new(StringComparer.OrdinalIgnoreCase)
        {
            "RestorePoints", "FileEntries", "FailedFiles",
            "Attributes", "IsPinned", "TargetDestination",
            "Id", "Timestamp", "Path", "Status", "RestorePointId",
            "RelativePath", "IsDirectory", "Size", "LastWriteTime", "FilePath"
        };

        private static void ValidateIdentifier(string name)
        {
            if (!AllowedIdentifiers.Contains(name))
                throw new ArgumentException($"Unknown schema identifier: {name}");
        }

        private void TryAddColumn(SqliteConnection connection, string table, string column, string definition)
        {
            ValidateIdentifier(table);
            ValidateIdentifier(column);

            try
            {
                if (!ColumnExists(connection, table, column))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
                    cmd.ExecuteNonQuery();
                }
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Column was added by a previous crashed migration — safe to ignore
            }
        }

        private bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
        {
            ValidateIdentifier(tableName);
            ValidateIdentifier(columnName);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public void AddRestorePoint(RestorePoint rp, SqliteConnection? existingConnection = null)
        {
            bool ownsConnection = existingConnection == null;
            var connection = existingConnection ?? OpenAdHocConnection();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                @"
                    INSERT INTO RestorePoints (Timestamp, Path, Status, IsPinned, TargetDestination)
                    VALUES ($ts, $path, $status, $pinned, $dest);
                    SELECT last_insert_rowid();
                ";
                command.Parameters.AddWithValue("$ts", rp.Timestamp.ToString("O"));
                command.Parameters.AddWithValue("$path", rp.Path);
                command.Parameters.AddWithValue("$status", (int)rp.Status);
                command.Parameters.AddWithValue("$pinned", rp.IsPinned ? 1 : 0);
                command.Parameters.AddWithValue("$dest", rp.TargetDestination);

                var result = command.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                {
                    throw new InvalidOperationException("Failed to insert restore point - no ID returned");
                }
                rp.Id = Convert.ToInt32(result);
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
        }

        public void UpdateRestorePointStatus(int id, BackupStatus status, SqliteConnection? existingConnection = null)
        {
            bool ownsConnection = existingConnection == null;
            var connection = existingConnection ?? OpenAdHocConnection();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE RestorePoints SET Status = $status WHERE Id = $id";
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
        }

        public List<RestorePoint> GetRestorePoints(SqliteConnection? existingConnection = null)
        {
            var results = new List<RestorePoint>();
            bool ownsConnection = existingConnection == null;
            var connection = existingConnection ?? OpenAdHocConnection();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Timestamp, Path, Status, IsPinned, TargetDestination FROM RestorePoints ORDER BY Timestamp DESC";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadRestorePoint(reader));
                }
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
            return results;
        }

        public RestorePoint? GetLastSuccessfulRestorePoint()
        {
            using var connection = OpenAdHocConnection();
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Timestamp, Path, Status, IsPinned, TargetDestination FROM RestorePoints WHERE Status IN ($s1, $s2) ORDER BY Timestamp DESC LIMIT 1";
                command.Parameters.AddWithValue("$s1", (int)BackupStatus.Completed);
                command.Parameters.AddWithValue("$s2", (int)BackupStatus.CompletedWithErrors);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return ReadRestorePoint(reader);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error querying last successful restore point: {ex}");
                throw;
            }
            return null;
        }

        /// <summary>
        /// Maps a reader row (from explicit SELECT Id, Timestamp, Path, Status, IsPinned, TargetDestination)
        /// to a RestorePoint. Column order must match the SELECT.
        /// </summary>
        private static RestorePoint ReadRestorePoint(SqliteDataReader reader)
        {
            return new RestorePoint
            {
                Id = reader.GetInt32(0),
                Timestamp = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Path = reader.GetString(2),
                Status = (BackupStatus)reader.GetInt32(3),
                IsPinned = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
                TargetDestination = reader.IsDBNull(5) ? "" : reader.GetString(5)
            };
        }

        /// <summary>
        /// Batch insert file entries for much better performance with large backups.
        /// Uses a single transaction with prepared statements.
        /// </summary>
        public void AddFileEntriesBatch(IReadOnlyList<FileEntry> entries, SqliteConnection? existingConnection = null)
        {
            if (entries.Count == 0) return;

            bool ownsConnection = existingConnection == null;
            var connection = existingConnection ?? OpenAdHocConnection();

            try
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        INSERT INTO FileEntries (RestorePointId, RelativePath, IsDirectory, Size, LastWriteTime, Attributes)
                        VALUES ($rpId, $relPath, $isDir, $size, $lwt, $attr);
                    ";

                    var pRpId = command.Parameters.Add("$rpId", SqliteType.Integer);
                    var pRelPath = command.Parameters.Add("$relPath", SqliteType.Text);
                    var pIsDir = command.Parameters.Add("$isDir", SqliteType.Integer);
                    var pSize = command.Parameters.Add("$size", SqliteType.Integer);
                    var pLwt = command.Parameters.Add("$lwt", SqliteType.Text);
                    var pAttr = command.Parameters.Add("$attr", SqliteType.Integer);

                    foreach (var entry in entries)
                    {
                        pRpId.Value = entry.RestorePointId;
                        pRelPath.Value = entry.RelativePath;
                        pIsDir.Value = entry.IsDirectory ? 1 : 0;
                        pSize.Value = entry.Size;
                        pLwt.Value = entry.LastWriteTime.ToString("O");
                        pAttr.Value = entry.Attributes;
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    try { transaction.Rollback(); }
                    catch (Exception rollbackEx)
                    {
                        Logger.Log($"ERROR: Transaction rollback failed after batch insert error: {rollbackEx.Message}");
                        throw new AggregateException("Batch insert failed and rollback also failed", ex, rollbackEx);
                    }
                    throw;
                }
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
        }

        /// <summary>
        /// Gets file entries for deduplication lookup, indexed by (Size, LastWriteTime).
        /// Returns a dictionary for O(1) lookup during backup.
        /// Limited to 200k unique entries to prevent excessive memory usage on low-end machines.
        /// </summary>
        public Dictionary<(long size, string lwt), (int rpId, string relativePath)> GetDedupIndex(string destinationRoot, SqliteConnection? existingConnection = null, CancellationToken cancellationToken = default)
        {
            const int MaxDedupEntries = 200_000; // Reduced from 500K to limit memory (~60MB max)
            var result = new Dictionary<(long size, string lwt), (int rpId, string relativePath)>(MaxDedupEntries);

            bool ownsConnection = existingConnection == null;
            var connection = existingConnection ?? OpenAdHocConnection();

            try
            {
                // Two-step approach: first get valid restore point IDs (small table),
                // then query file entries using the indexed RestorePointId column.
                // This avoids a full-table JOIN + ORDER BY on millions of rows.
                using var rpCmd = connection.CreateCommand();
                rpCmd.CommandText = @"
                    SELECT Id FROM RestorePoints
                    WHERE Status IN ($s1, $s2) AND TargetDestination = $dest
                    ORDER BY Timestamp DESC";
                rpCmd.Parameters.AddWithValue("$s1", (int)BackupStatus.Completed);
                rpCmd.Parameters.AddWithValue("$s2", (int)BackupStatus.CompletedWithErrors);
                rpCmd.Parameters.AddWithValue("$dest", destinationRoot);

                var rpIds = new List<int>();
                using (var rpReader = rpCmd.ExecuteReader())
                {
                    while (rpReader.Read())
                        rpIds.Add(rpReader.GetInt32(0));
                }

                // Query file entries per restore point (most recent first) until we hit the limit
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Size, LastWriteTime, RestorePointId, RelativePath
                    FROM FileEntries
                    WHERE RestorePointId = $rpId AND IsDirectory = 0";
                command.Parameters.AddWithValue("$rpId", 0);

                int rowCount = 0;
                foreach (var rpId in rpIds)
                {
                    if (result.Count >= MaxDedupEntries) break;
                    cancellationToken.ThrowIfCancellationRequested();

                    command.Parameters["$rpId"].Value = rpId;
                    using var reader = command.ExecuteReader();
                    while (reader.Read() && result.Count < MaxDedupEntries)
                    {
                        if (++rowCount % 10_000 == 0)
                            cancellationToken.ThrowIfCancellationRequested();

                        var size = reader.GetInt64(0);
                        var lwt = reader.GetString(1);
                        var key = (size, lwt);

                        if (!result.ContainsKey(key))
                        {
                            result[key] = (reader.GetInt32(2), reader.GetString(3));
                        }
                    }
                }
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }

            return result;
        }

        public List<FileEntry> GetFilesForSnapshot(int restorePointId)
        {
            var results = new List<FileEntry>();
            using var connection = OpenAdHocConnection();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, RestorePointId, RelativePath, IsDirectory, Size, LastWriteTime, Attributes FROM FileEntries WHERE RestorePointId = $rpId";
                command.Parameters.AddWithValue("$rpId", restorePointId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new FileEntry
                    {
                        Id = reader.GetInt32(0),
                        RestorePointId = reader.GetInt32(1),
                        RelativePath = reader.GetString(2),
                        IsDirectory = reader.GetInt32(3) == 1,
                        Size = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        LastWriteTime = reader.IsDBNull(5)
                            ? DateTime.MinValue
                            : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        Attributes = reader.IsDBNull(6) ? 0 : (uint)(reader.GetInt64(6) & 0xFFFFFFFF)
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error reading file entries for restore point {restorePointId}: {ex}");
                throw;
            }
            return results;
        }

        public void SaveFailedFiles(int restorePointId, IReadOnlyList<string> failedFiles, SqliteConnection? existingConnection = null)
        {
            if (failedFiles.Count == 0) return;

            bool ownsConnection = existingConnection == null;
            var connection = existingConnection ?? OpenAdHocConnection();

            try
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    // Clear any existing failed files for this restore point
                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM FailedFiles WHERE RestorePointId = $rpId";
                    deleteCmd.Parameters.AddWithValue("$rpId", restorePointId);
                    deleteCmd.ExecuteNonQuery();

                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO FailedFiles (RestorePointId, FilePath) VALUES ($rpId, $path)";
                    var pRpId = insertCmd.Parameters.Add("$rpId", SqliteType.Integer);
                    var pPath = insertCmd.Parameters.Add("$path", SqliteType.Text);

                    // Cap at 500 to match UI limit
                    var limit = Math.Min(failedFiles.Count, 500);
                    for (int i = 0; i < limit; i++)
                    {
                        pRpId.Value = restorePointId;
                        pPath.Value = failedFiles[i];
                        insertCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
        }

        public List<string> GetFailedFiles(int restorePointId, SqliteConnection? existingConnection = null)
        {
            var results = new List<string>();
            bool ownsConnection = existingConnection == null;
            var connection = existingConnection ?? OpenAdHocConnection();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT FilePath FROM FailedFiles WHERE RestorePointId = $rpId";
                command.Parameters.AddWithValue("$rpId", restorePointId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(reader.GetString(0));
                }
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
            return results;
        }

        public void DeleteRestorePoint(int id, SqliteConnection? existingConnection = null)
        {
            var connection = existingConnection ?? OpenAdHocConnection();
            var ownsConnection = existingConnection == null;
            try
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var cmdFailed = connection.CreateCommand();
                    cmdFailed.Transaction = transaction;
                    cmdFailed.CommandText = "DELETE FROM FailedFiles WHERE RestorePointId = $id";
                    cmdFailed.Parameters.AddWithValue("$id", id);
                    cmdFailed.ExecuteNonQuery();

                    using var cmdFiles = connection.CreateCommand();
                    cmdFiles.Transaction = transaction;
                    cmdFiles.CommandText = "DELETE FROM FileEntries WHERE RestorePointId = $id";
                    cmdFiles.Parameters.AddWithValue("$id", id);
                    cmdFiles.ExecuteNonQuery();

                    using var cmdRp = connection.CreateCommand();
                    cmdRp.Transaction = transaction;
                    cmdRp.CommandText = "DELETE FROM RestorePoints WHERE Id = $id";
                    cmdRp.Parameters.AddWithValue("$id", id);
                    cmdRp.ExecuteNonQuery();

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
        }
    }
}
