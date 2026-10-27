using System.Globalization;
using ActivityRecordingService.Detection;
using Microsoft.Data.Sqlite;

namespace ActivityRecordingService.Recording;

public sealed class SqliteEventRepository : IEventRepository, IDisposable
{
    private readonly string _databasePath;
    private readonly int _defaultRetentionDays;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private int _disposed;

    public SqliteEventRepository(string databasePath, int defaultRetentionDays)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));

        if (defaultRetentionDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultRetentionDays));
        }

        _databasePath = databasePath;
        _defaultRetentionDays = defaultRetentionDays;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is null)
            {
                var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
                if (!string.IsNullOrEmpty(databaseDirectory))
                    Directory.CreateDirectory(databaseDirectory);

                _connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Pooling = false
                }.ToString());
                await _connection.OpenAsync(cancellationToken);
            }

            await ExecuteNonQueryAsync(
                _connection,
                """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """,
                cancellationToken);

            await ExecuteNonQueryAsync(
                _connection,
                """
                CREATE TABLE IF NOT EXISTS cameras (
                    name TEXT NOT NULL PRIMARY KEY,
                    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                    retention_days INTEGER NULL CHECK (retention_days IS NULL OR retention_days > 0)
                );

                CREATE TABLE IF NOT EXISTS events (
                    event_id TEXT NOT NULL PRIMARY KEY,
                    camera_name TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT NULL,
                    status TEXT NOT NULL CHECK (status IN ('Recording', 'Completed', 'Failed', 'Deleted')),
                    end_reason TEXT NULL,
                    FOREIGN KEY (camera_name) REFERENCES cameras(name)
                );

                CREATE TABLE IF NOT EXISTS recording_files (
                    file_id TEXT NOT NULL PRIMARY KEY,
                    event_id TEXT NOT NULL,
                    part_index INTEGER NOT NULL CHECK (part_index >= 0),
                    relative_path TEXT NOT NULL CHECK (relative_path <> ''),
                    bytes INTEGER NOT NULL CHECK (bytes >= 0),
                    status TEXT NOT NULL CHECK (status IN ('Reserved', 'Ready', 'Failed', 'Deleted')),
                    started_utc TEXT NULL,
                    ended_utc TEXT NULL,
                    FOREIGN KEY (event_id) REFERENCES events(event_id) ON DELETE CASCADE,
                    UNIQUE (event_id, part_index)
                );

                CREATE TABLE IF NOT EXISTS cleanup_audits (
                    cleanup_audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    created_utc TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    event_count INTEGER NOT NULL CHECK (event_count >= 0),
                    freed_bytes INTEGER NOT NULL CHECK (freed_bytes >= 0)
                );

                CREATE INDEX IF NOT EXISTS ix_events_camera_started
                    ON events(camera_name, started_utc);

                CREATE INDEX IF NOT EXISTS ix_events_status_ended
                    ON events(status, ended_utc);

                CREATE INDEX IF NOT EXISTS ix_recording_files_event_part
                    ON recording_files(event_id, part_index);

                CREATE INDEX IF NOT EXISTS ix_recording_files_status
                    ON recording_files(status);
                """,
                cancellationToken);

            await EnsureEndReasonColumnAsync(_connection, cancellationToken);
            await EnsureFileTimeColumnsAsync(_connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertCameraAsync(
        string cameraName,
        bool enabled,
        int? retentionDays,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cameraName))
            throw new ArgumentException("Camera name is required.", nameof(cameraName));

        if (retentionDays is <= 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        await ExecuteAsync(async (connection, token) =>
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO cameras(name, enabled, retention_days)
                VALUES ($name, $enabled, $retention_days)
                ON CONFLICT(name) DO UPDATE SET
                    enabled = excluded.enabled,
                    retention_days = excluded.retention_days
                """,
                token,
                new SqliteParameter("$name", cameraName),
                new SqliteParameter("$enabled", enabled ? 1L : 0L),
                new SqliteParameter("$retention_days", retentionDays.HasValue ? retentionDays.Value : (object)DBNull.Value));
            return 0;
        }, cancellationToken);
    }

    public async Task<Guid> CreateEventAsync(
        string cameraName,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cameraName))
            throw new ArgumentException("Camera name is required.", nameof(cameraName));

        var eventId = Guid.NewGuid();
        await ExecuteAsync(async (connection, token) =>
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO events(event_id, camera_name, started_utc, ended_utc, status)
                VALUES ($event_id, $camera_name, $started_utc, NULL, 'Recording')
                """,
                token,
                new SqliteParameter("$event_id", eventId.ToString()),
                new SqliteParameter("$camera_name", cameraName),
                new SqliteParameter("$started_utc", FormatUtc(startedUtc)));
            return 0;
        }, cancellationToken);

        return eventId;
    }

    public async Task CompleteEventAsync(
        Guid eventId,
        DateTimeOffset endedUtc,
        EventStatus status,
        CancellationToken cancellationToken,
        EventEndReason? endReason = null)
    {
        if (status is not (EventStatus.Completed or EventStatus.Failed))
        {
            throw new ArgumentException(
                "Only Completed or Failed events can be finalized.",
                nameof(status));
        }

        await ExecuteAsync(async (connection, token) =>
        {
            var rows = await ExecuteNonQueryAsync(
                connection,
                """
                UPDATE events
                SET ended_utc = $ended_utc, status = $status, end_reason = $end_reason
                WHERE event_id = $event_id
                  AND status = 'Recording'
                  AND started_utc <= $ended_utc
                """,
                token,
                new SqliteParameter("$event_id", eventId.ToString()),
                new SqliteParameter("$ended_utc", FormatUtc(endedUtc)),
                new SqliteParameter("$status", ToStorage(status)),
                new SqliteParameter(
                    "$end_reason",
                    MaybeValue(endReason.HasValue ? ToStorage(endReason.Value) : null)));
            if (rows == 0)
                throw new InvalidOperationException("Event is not an active event that can be finalized.");
            return 0;
        }, cancellationToken);
    }

    public async Task AddFileAsync(
        RecordingFileRecord file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.PartIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(file), "Part index must not be negative.");
        if (string.IsNullOrWhiteSpace(file.RelativePath))
            throw new ArgumentException("Relative path is required.", nameof(file));
        if (file.Bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(file), "Bytes must not be negative.");

        await ExecuteAsync(async (connection, token) =>
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO recording_files(
                    file_id, event_id, part_index, relative_path, bytes, status, started_utc, ended_utc)
                VALUES (
                    $file_id, $event_id, $part_index, $relative_path, $bytes, $status, $started_utc, $ended_utc)
                """,
                token,
                new SqliteParameter("$file_id", file.FileId.ToString()),
                new SqliteParameter("$event_id", file.EventId.ToString()),
                new SqliteParameter("$part_index", (long)file.PartIndex),
                new SqliteParameter("$relative_path", file.RelativePath),
                new SqliteParameter("$bytes", file.Bytes),
                new SqliteParameter("$status", ToStorage(file.Status)),
                new SqliteParameter(
                    "$started_utc",
                    MaybeValue(file.StartedUtc.HasValue ? FormatUtc(file.StartedUtc.Value) : null)),
                new SqliteParameter(
                    "$ended_utc",
                    MaybeValue(file.EndedUtc.HasValue ? FormatUtc(file.EndedUtc.Value) : null)));
            return 0;
        }, cancellationToken);
    }

    public async Task SetFileStatusAsync(
        Guid fileId,
        RecordingFileStatus status,
        CancellationToken cancellationToken)
    {
        _ = ToStorage(status);
        await ExecuteAsync(async (connection, token) =>
        {
            var rows = await ExecuteNonQueryAsync(
                connection,
                """
                UPDATE recording_files
                SET status = $status
                WHERE file_id = $file_id
                """,
                token,
                new SqliteParameter("$file_id", fileId.ToString()),
                new SqliteParameter("$status", ToStorage(status)));
            if (rows == 0)
                throw new InvalidOperationException("Recording file was not found.");
            return 0;
        }, cancellationToken);
    }

    public async Task SetFileReadyAsync(
        Guid fileId,
        long bytes,
        CancellationToken cancellationToken)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        await ExecuteAsync(async (connection, token) =>
        {
            var rows = await ExecuteNonQueryAsync(
                connection,
                """
                UPDATE recording_files
                SET status = 'Ready', bytes = $bytes
                WHERE file_id = $file_id
                """,
                token,
                new SqliteParameter("$file_id", fileId.ToString()),
                new SqliteParameter("$bytes", bytes));
            if (rows == 0)
                throw new InvalidOperationException("Recording file was not found.");
            return 0;
        }, cancellationToken);
    }

    public async Task<EventRecord?> GetEventAsync(
        Guid eventId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async (connection, token) =>
        {
            await using var command = CreateEventQuery(
                connection,
                """
                WHERE e.event_id = $event_id
                """,
                token,
                new SqliteParameter("$event_id", eventId.ToString()));
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? MapEvent(reader) : null;
        }, cancellationToken);

    public async Task<IReadOnlyList<RecordingFileRecord>> GetFilesAsync(
        Guid eventId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async (connection, token) =>
        {
            await using var command = CreateFileQuery(
                connection,
                """
                WHERE f.event_id = $event_id
                ORDER BY f.part_index
                """,
                token,
                new SqliteParameter("$event_id", eventId.ToString()));
            return await ReadFilesAsync(command, token);
        }, cancellationToken);

    public async Task<RecordingFileRecord?> GetReadyFileAsync(
        Guid fileId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async (connection, token) =>
        {
            await using var command = CreateFileQuery(
                connection,
                """
                WHERE f.file_id = $file_id AND f.status = 'Ready'
                """,
                token,
                new SqliteParameter("$file_id", fileId.ToString()));
            var files = await ReadFilesAsync(command, token);
            return files.Count == 1 ? files[0] : null;
        }, cancellationToken);

    public async Task<IReadOnlyList<EventRecord>> ListEventsAsync(
        string? cameraName,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
        {
            throw new ArgumentException("From time must not be later than to time.");
        }

        return await ExecuteAsync(async (connection, token) =>
        {
            var conditions = new List<string>();
            if (cameraName is not null)
                conditions.Add("e.camera_name = $camera_name");
            if (fromUtc.HasValue)
                conditions.Add("e.started_utc >= $from_utc");
            if (toUtc.HasValue)
                conditions.Add("e.started_utc <= $to_utc");

            var where = conditions.Count == 0
                ? string.Empty
                : "WHERE " + string.Join(" AND ", conditions);
            await using var command = CreateEventQuery(
                connection,
                $"""
                {where}
                ORDER BY e.started_utc DESC, e.event_id DESC
                LIMIT $limit OFFSET $offset
                """,
                token,
                new SqliteParameter("$camera_name", MaybeValue(cameraName)),
                new SqliteParameter(
                    "$from_utc",
                    MaybeValue(fromUtc.HasValue ? FormatUtc(fromUtc.Value) : null)),
                new SqliteParameter(
                    "$to_utc",
                    MaybeValue(toUtc.HasValue ? FormatUtc(toUtc.Value) : null)),
                new SqliteParameter("$limit", (long)limit),
                new SqliteParameter("$offset", (long)offset));
            return await ReadEventsAsync(command, token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<EventRecord>> ListEventsByStatusAsync(
        EventStatus status,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return await ExecuteAsync(async (connection, token) =>
        {
            await using var command = CreateEventQuery(
                connection,
                """
                WHERE e.status = $status
                ORDER BY e.started_utc, e.event_id
                LIMIT $limit
                """,
                token,
                new SqliteParameter("$status", ToStorage(status)),
                new SqliteParameter("$limit", (long)limit));
            return await ReadEventsAsync(command, token);
        }, cancellationToken);
    }

    public async Task<long> GetRecordingBytesAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(bytes), 0)
                FROM recording_files
                WHERE status = 'Ready'
                """;
            var result = await command.ExecuteScalarAsync(token);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }, cancellationToken);

    public async Task<IReadOnlyList<FileEventItem>> ListFileEventsAsync(
        string? cameraName,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
        {
            throw new ArgumentException("From time must not be later than to time.");
        }

        return await ExecuteAsync(async (connection, token) =>
        {
            // 与 /events 列表口径一致：删除的不可见，进行中（Recording）事件对回放无意义
            var conditions = new List<string>
            {
                "f.status != 'Deleted'",
                "e.status != 'Deleted'",
                "e.status != 'Recording'"
            };
            if (cameraName is not null)
                conditions.Add("e.camera_name = $camera_name");
            if (fromUtc.HasValue)
                conditions.Add("e.started_utc >= $from_utc");
            if (toUtc.HasValue)
                conditions.Add("e.started_utc <= $to_utc");

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                    f.file_id,
                    f.event_id,
                    e.camera_name,
                    f.part_index,
                    f.bytes,
                    f.status,
                    COALESCE(f.started_utc, e.started_utc) AS started_utc,
                    COALESCE(f.ended_utc, e.ended_utc) AS ended_utc,
                    e.status
                FROM recording_files AS f
                JOIN events AS e ON f.event_id = e.event_id
                WHERE {string.Join(" AND ", conditions)}
                ORDER BY e.started_utc DESC, f.part_index ASC
                LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddRange(
            [
                new SqliteParameter("$camera_name", MaybeValue(cameraName)),
                new SqliteParameter(
                    "$from_utc",
                    MaybeValue(fromUtc.HasValue ? FormatUtc(fromUtc.Value) : null)),
                new SqliteParameter(
                    "$to_utc",
                    MaybeValue(toUtc.HasValue ? FormatUtc(toUtc.Value) : null)),
                new SqliteParameter("$limit", (long)limit),
                new SqliteParameter("$offset", (long)offset)
            ]);
            var items = new List<FileEventItem>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new FileEventItem(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    checked((int)reader.GetInt64(3)),
                    reader.GetInt64(4),
                    ParseFileStatus(reader.GetString(5)),
                    NormalizeUtc(reader.GetDateTimeOffset(6)),
                    reader.IsDBNull(7) ? null : NormalizeUtc(reader.GetDateTimeOffset(7)),
                    ParseEventStatus(reader.GetString(8))));
            }

            return items;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<EventRecord>> ListDeletableEventsAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return await ExecuteAsync(async (connection, token) =>
        {
            await using var command = CreateEventQuery(
                connection,
                """
                WHERE e.status IN ('Completed', 'Failed')
                  AND datetime(
                      COALESCE(e.ended_utc, e.started_utc),
                      '+' || CAST(COALESCE(
                          (SELECT c.retention_days
                           FROM cameras AS c
                           WHERE c.name = e.camera_name),
                          $default_retention_days) AS INTEGER) || ' days')
                      <= datetime($now_utc)
                ORDER BY COALESCE(e.ended_utc, e.started_utc), e.event_id
                LIMIT $limit
                """,
                token,
                new SqliteParameter("$default_retention_days", (long)_defaultRetentionDays),
                new SqliteParameter("$now_utc", FormatUtc(nowUtc)),
                new SqliteParameter("$limit", (long)limit));
            return await ReadEventsAsync(command, token);
        }, cancellationToken);
    }

    public async Task AddCleanupAuditAsync(
        string reason,
        int eventCount,
        long freedBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required.", nameof(reason));
        if (eventCount < 0)
            throw new ArgumentOutOfRangeException(nameof(eventCount));
        if (freedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(freedBytes));

        await ExecuteAsync(async (connection, token) =>
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO cleanup_audits(
                    created_utc, reason, event_count, freed_bytes)
                VALUES (
                    $created_utc, $reason, $event_count, $freed_bytes)
                """,
                token,
                new SqliteParameter("$created_utc", FormatUtc(DateTimeOffset.UtcNow)),
                new SqliteParameter("$reason", reason),
                new SqliteParameter("$event_count", (long)eventCount),
                new SqliteParameter("$freed_bytes", freedBytes));
            return 0;
        }, cancellationToken);
    }

    public async Task DeleteEventAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction();
            await ExecuteNonQueryAsync(
                connection,
                "DELETE FROM recording_files WHERE event_id = $event_id",
                token,
                transaction: transaction,
                parameters: new SqliteParameter("$event_id", eventId.ToString()));
            var rows = await ExecuteNonQueryAsync(
                connection,
                """
                UPDATE events
                SET status = 'Deleted'
                WHERE event_id = $event_id
                """,
                token,
                transaction: transaction,
                parameters: new SqliteParameter("$event_id", eventId.ToString()));
            await transaction.CommitAsync(token);
            if (rows == 0)
                throw new InvalidOperationException("Event was not found.");
            return 0;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RecordingFileRecord>> ListFilesByStatusAsync(
        RecordingFileStatus status,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return await ExecuteAsync(async (connection, token) =>
        {
            await using var command = CreateFileQuery(
                connection,
                """
                WHERE f.status = $status
                ORDER BY f.event_id, f.part_index
                LIMIT $limit
                """,
                token,
                new SqliteParameter("$status", ToStorage(status)),
                new SqliteParameter("$limit", (long)limit));
            return await ReadFilesAsync(command, token);
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
                await _connection.DisposeAsync();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_connection is not null)
            _connection.Dispose();
        _connection = null;
        _gate.Dispose();
    }

    private async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is null)
            {
                throw new InvalidOperationException(
                    "The repository must be initialized before it can be used.");
            }

            return await operation(_connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Task<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params SqliteParameter[] parameters) =>
        ExecuteNonQueryAsync(
            connection,
            commandText,
            cancellationToken,
            transaction: null,
            parameters);

    private static async Task<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction,
        params SqliteParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureEndReasonColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columnNames = new List<string>();
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(events);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columnNames.Add(reader.GetString(1));

        if (!columnNames.Contains("end_reason", StringComparer.OrdinalIgnoreCase))
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE events ADD COLUMN end_reason TEXT NULL",
                cancellationToken);
        }
    }

    private static async Task EnsureFileTimeColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columnNames = new List<string>();
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(recording_files);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columnNames.Add(reader.GetString(1));

        foreach (var column in new[] { "started_utc", "ended_utc" })
        {
            if (!columnNames.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                await ExecuteNonQueryAsync(
                    connection,
                    $"ALTER TABLE recording_files ADD COLUMN {column} TEXT NULL",
                    cancellationToken);
            }
        }
    }

    private static SqliteCommand CreateEventQuery(
        SqliteConnection connection,
        string tail,
        CancellationToken cancellationToken,
        params SqliteParameter[] parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                e.event_id,
                e.camera_name,
                e.started_utc,
                e.ended_utc,
                e.status,
                e.end_reason,
                COALESCE(aggregated_files.file_bytes, 0) AS file_bytes,
                COALESCE(aggregated_files.file_count, 0) AS file_count
            FROM events AS e
            LEFT JOIN (
                SELECT
                    event_id,
                    SUM(bytes) AS file_bytes,
                    COUNT(*) AS file_count
                FROM recording_files
                WHERE status = 'Ready'
                GROUP BY event_id
            ) AS aggregated_files ON aggregated_files.event_id = e.event_id
            {tail}
            """;
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static SqliteCommand CreateFileQuery(
        SqliteConnection connection,
        string tail,
        CancellationToken cancellationToken,
        params SqliteParameter[] parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT file_id, event_id, part_index, relative_path, bytes, status, started_utc, ended_utc
            FROM recording_files AS f
            {tail}
            """;
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static async Task<IReadOnlyList<EventRecord>> ReadEventsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var events = new List<EventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(MapEvent(reader));
        return events;
    }

    private static async Task<IReadOnlyList<RecordingFileRecord>> ReadFilesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var files = new List<RecordingFileRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            files.Add(MapFile(reader));
        return files;
    }

    private static EventRecord MapEvent(SqliteDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        NormalizeUtc(reader.GetDateTimeOffset(2)),
        reader.IsDBNull(3) ? null : NormalizeUtc(reader.GetDateTimeOffset(3)),
        ParseEventStatus(reader.GetString(4)),
        reader.GetInt64(6),
        checked((int)reader.GetInt64(7)),
        reader.IsDBNull(5) ? null : ParseEndReason(reader.GetString(5)));

    private static RecordingFileRecord MapFile(SqliteDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        checked((int)reader.GetInt64(2)),
        reader.GetString(3),
        reader.GetInt64(4),
        ParseFileStatus(reader.GetString(5)),
        reader.IsDBNull(6) ? null : NormalizeUtc(reader.GetDateTimeOffset(6)),
        reader.IsDBNull(7) ? null : NormalizeUtc(reader.GetDateTimeOffset(7)));

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) =>
        value.ToUniversalTime();

    private static string FormatUtc(DateTimeOffset value) =>
        NormalizeUtc(value).ToString("O", CultureInfo.InvariantCulture);

    private static object MaybeValue(object? value) =>
        value ?? DBNull.Value;

    private static string ToStorage(EventStatus status) => status switch
    {
        EventStatus.Recording => "Recording",
        EventStatus.Completed => "Completed",
        EventStatus.Failed => "Failed",
        EventStatus.Deleted => "Deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static string ToStorage(EventEndReason reason) => reason switch
    {
        EventEndReason.None => "None",
        EventEndReason.NoPerson => "NoPerson",
        EventEndReason.MaxDuration => "MaxDuration",
        EventEndReason.StreamStopped => "StreamStopped",
        EventEndReason.ServiceStopping => "ServiceStopping",
        EventEndReason.StartupRecovered => "StartupRecovered",
        EventEndReason.StartupIncomplete => "StartupIncomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    private static EventEndReason ParseEndReason(string reason) => reason switch
    {
        "None" => EventEndReason.None,
        "NoPerson" => EventEndReason.NoPerson,
        "MaxDuration" => EventEndReason.MaxDuration,
        "StreamStopped" => EventEndReason.StreamStopped,
        "ServiceStopping" => EventEndReason.ServiceStopping,
        "StartupRecovered" => EventEndReason.StartupRecovered,
        "StartupIncomplete" => EventEndReason.StartupIncomplete,
        _ => throw new InvalidOperationException($"Unknown event end reason '{reason}'.")
    };

    private static EventStatus ParseEventStatus(string status) => status switch
    {
        "Recording" => EventStatus.Recording,
        "Completed" => EventStatus.Completed,
        "Failed" => EventStatus.Failed,
        "Deleted" => EventStatus.Deleted,
        _ => throw new InvalidOperationException($"Unknown event status '{status}'.")
    };

    private static string ToStorage(RecordingFileStatus status) => status switch
    {
        RecordingFileStatus.Reserved => "Reserved",
        RecordingFileStatus.Ready => "Ready",
        RecordingFileStatus.Failed => "Failed",
        RecordingFileStatus.Deleted => "Deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static RecordingFileStatus ParseFileStatus(string status) => status switch
    {
        "Reserved" => RecordingFileStatus.Reserved,
        "Ready" => RecordingFileStatus.Ready,
        "Failed" => RecordingFileStatus.Failed,
        "Deleted" => RecordingFileStatus.Deleted,
        _ => throw new InvalidOperationException($"Unknown recording file status '{status}'.")
    };
}
