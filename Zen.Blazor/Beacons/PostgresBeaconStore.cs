using System.Text.Json;
using Dapper;
using Npgsql;

namespace Zen.Blazor.Beacons;

public sealed class PostgresBeaconStore(NpgsqlDataSource dataSource, ILogger<PostgresBeaconStore> logger)
    : IBeaconStore, IBeaconMaintenance
{
    private const long SchemaLockId = 7_243_019_001;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS beacon (
            id uuid PRIMARY KEY,
            key text NOT NULL,
            received_at timestamptz NOT NULL,
            method text NOT NULL,
            path text NOT NULL,
            query text NOT NULL,
            referer text NOT NULL,
            content_type text,
            client text,
            user_agent text,
            headers jsonb NOT NULL,
            body text NOT NULL,
            body_encoding text NOT NULL,
            size_bytes integer NOT NULL
        ) WITH (autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.05);
        CREATE INDEX IF NOT EXISTS beacon_key_id ON beacon (key, id);
        CREATE INDEX IF NOT EXISTS beacon_id_size ON beacon (id) INCLUDE (size_bytes);
        """;

    private const string InsertSql = """
        INSERT INTO beacon (id, key, received_at, method, path, query, referer, content_type, client, user_agent, headers, body, body_encoding, size_bytes)
        VALUES (@Id, @Key, @ReceivedAt, @Method, @Path, @Query, @Referer, @ContentType, @Client, @UserAgent, CAST(@Headers AS jsonb), @Body, @BodyEncoding, @Size)
        ON CONFLICT (id) DO NOTHING
        """;

    private const string GetSql = """
        SELECT id AS Id, key AS Key, received_at AS ReceivedAt, method AS Method, path AS Path, query AS Query,
               referer AS Referer, content_type AS ContentType, client AS Client, user_agent AS UserAgent,
               headers::text AS Headers, body AS Body, body_encoding AS BodyEncoding, size_bytes AS Size
        FROM beacon
        WHERE id = @id
        """;

    private const int PreviewLength = 160;

    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    public bool IsConfigured => true;

    public async Task InsertAsync(IReadOnlyList<BeaconEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var rows = entries.Select(e => new
        {
            e.Id,
            e.Key,
            e.ReceivedAt,
            e.Method,
            e.Path,
            e.Query,
            e.Referer,
            e.ContentType,
            e.Client,
            e.UserAgent,
            Headers = JsonSerializer.Serialize(e.Headers, BeaconJsonContext.Default.IReadOnlyDictionaryStringString),
            e.Body,
            e.BodyEncoding,
            e.Size,
        }).ToList();

        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(InsertSql, rows, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        catch (PostgresException ex) when (IsDataError(ex))
        {
            logger.LogWarning(ex, "Beacon batch rejected; retrying rows one at a time");
        }

        foreach (var row in rows)
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(InsertSql, row, cancellationToken: cancellationToken));
            }
            catch (PostgresException ex) when (IsDataError(ex))
            {
                logger.LogWarning(ex, "Discarded beacon {Id} rejected by the database", row.Id);
            }
        }
    }

    private static bool IsDataError(PostgresException ex) =>
        ex.SqlState.StartsWith("22", StringComparison.Ordinal) || ex.SqlState.StartsWith("23", StringComparison.Ordinal);

    public async Task<BeaconEntry?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<EntryRow>(
            new CommandDefinition(GetSql, new { id }, cancellationToken: cancellationToken));
        return row?.ToEntry();
    }

    public async Task<IReadOnlyList<BeaconItem>> ListAsync(string? key, string? referer, Guid? before, int limit, CancellationToken cancellationToken)
    {
        var filters = new List<string>(3);
        if (key is not null)
        {
            filters.Add("key = @key");
        }

        if (referer is not null)
        {
            filters.Add("referer = @referer");
        }

        if (before is not null)
        {
            filters.Add("id < @before");
        }

        var where = filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters);
        var sql = $"""
            SELECT id AS Id, key AS Key, received_at AS ReceivedAt, referer AS Referer, content_type AS ContentType,
                   size_bytes AS Size, left(body, {PreviewLength}) AS Preview
            FROM beacon
            {where}
            ORDER BY id DESC
            LIMIT @limit
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ItemRow>(
            new CommandDefinition(sql, new { key, referer, before, limit }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToItem())];
    }

    public async Task<BeaconTotals> SummarizeAsync(string key, string? referer, DateTimeOffset hourlySince, CancellationToken cancellationToken)
    {
        var where = referer is null ? "WHERE key = @key" : "WHERE key = @key AND referer = @referer";
        var sql = $"""
            SELECT count(*) AS Total, coalesce(sum(size_bytes), 0)::bigint AS Bytes,
                   min(received_at) AS FirstAt, max(received_at) AS LastAt
            FROM beacon {where};

            SELECT referer AS Referer, count(*) AS Count, max(received_at) AS LastAt
            FROM beacon {where}
            GROUP BY referer
            ORDER BY count(*) DESC, referer
            LIMIT {BeaconTotals.MaxReferers};

            SELECT date_trunc('hour', received_at, 'UTC') AS Hour, count(*) AS Count
            FROM beacon {where} AND received_at >= @since
            GROUP BY 1
            ORDER BY 1;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, new { key, referer, since = hourlySince }, cancellationToken: cancellationToken));

        var totals = await grid.ReadSingleAsync<TotalsRow>();
        var referers = await grid.ReadAsync<RefererRow>();
        var hourly = await grid.ReadAsync<HourRow>();

        return new BeaconTotals(
            totals.Total,
            totals.Bytes,
            Utc(totals.FirstAt),
            Utc(totals.LastAt),
            [.. referers.Select(r => new RefererCount(r.Referer, r.Count, Utc(r.LastAt)))],
            [.. hourly.Select(h => new HourCount(Utc(h.Hour), h.Count))]);
    }

    public async Task<IReadOnlyList<KeyCount>> TopKeysAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT key AS Key, count(*) AS Count
            FROM beacon
            GROUP BY key
            ORDER BY count(*) DESC, key
            LIMIT @limit
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<KeyRow>(new CommandDefinition(sql, new { limit }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new KeyCount(r.Key, r.Count))];
    }

    public async Task<IReadOnlyDictionary<string, long>> CountAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<string, long>();
        }

        const string sql = """
            SELECT key AS Key, count(*) AS Count
            FROM beacon
            WHERE key = ANY(@keys)
            GROUP BY key
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<KeyRow>(
            new CommandDefinition(sql, new { keys = keys.ToArray() }, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
    }

    public async Task<PruneResult> PruneAsync(DateTimeOffset olderThan, long budgetBytes, long targetBytes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var removed = (long)await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM beacon WHERE id < @cutoff",
            new { cutoff = BeaconIds.Floor(olderThan) },
            cancellationToken: cancellationToken));

        var stored = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT coalesce(sum(size_bytes), 0)::bigint FROM beacon",
            cancellationToken: cancellationToken));

        if (stored > budgetBytes)
        {
            const string cutoffSql = """
                SELECT id
                FROM (SELECT id, sum(size_bytes) OVER (ORDER BY id DESC) AS running FROM beacon) ranked
                WHERE running > @targetBytes
                ORDER BY id DESC
                LIMIT 1
                """;

            var cutoff = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                cutoffSql, new { targetBytes }, cancellationToken: cancellationToken));

            if (cutoff is { } id)
            {
                var trimmed = await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM beacon WHERE id <= @id", new { id }, cancellationToken: cancellationToken));
                removed += trimmed;
                logger.LogInformation("Trimmed {Count} beacons to stay under {Budget} bytes", trimmed, budgetBytes);

                stored = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT coalesce(sum(size_bytes), 0)::bigint FROM beacon",
                    cancellationToken: cancellationToken));
            }
        }

        var databaseBytes = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT pg_database_size(current_database())",
            cancellationToken: cancellationToken));

        return new PruneResult(removed, stored, databaseBytes);
    }

    public async Task VacuumAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "VACUUM (ANALYZE) beacon", commandTimeout: 300, cancellationToken: cancellationToken));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (_schemaReady)
        {
            return connection;
        }

        try
        {
            await EnsureSchemaAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task EnsureSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_xact_lock(@id)", new { id = SchemaLockId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(SchemaSql, transaction: transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            _schemaReady = true;
            logger.LogInformation("Beacon schema ready");
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private sealed class EntryRow
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public string Query { get; set; } = "";
        public string Referer { get; set; } = "";
        public string? ContentType { get; set; }
        public string? Client { get; set; }
        public string? UserAgent { get; set; }
        public string Headers { get; set; } = "{}";
        public string Body { get; set; } = "";
        public string BodyEncoding { get; set; } = "";
        public int Size { get; set; }

        public BeaconEntry ToEntry() => new(
            Id,
            Key,
            Utc(ReceivedAt),
            Method,
            Path,
            Query,
            Referer,
            ContentType,
            Client,
            UserAgent,
            JsonSerializer.Deserialize(Headers, BeaconJsonContext.Default.DictionaryStringString) ?? new Dictionary<string, string>(),
            Body,
            BodyEncoding,
            Size);
    }

    private sealed class ItemRow
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public string Referer { get; set; } = "";
        public string? ContentType { get; set; }
        public int Size { get; set; }
        public string Preview { get; set; } = "";

        public BeaconItem ToItem() => new(Id, Key, Utc(ReceivedAt), Referer, ContentType, Size, Preview);
    }

    private sealed class TotalsRow
    {
        public long Total { get; set; }
        public long Bytes { get; set; }
        public DateTime? FirstAt { get; set; }
        public DateTime? LastAt { get; set; }
    }

    private sealed class RefererRow
    {
        public string Referer { get; set; } = "";
        public long Count { get; set; }
        public DateTime LastAt { get; set; }
    }

    private sealed class HourRow
    {
        public DateTime Hour { get; set; }
        public long Count { get; set; }
    }

    private sealed class KeyRow
    {
        public string Key { get; set; } = "";
        public long Count { get; set; }
    }
}
