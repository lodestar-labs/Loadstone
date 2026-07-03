using System.Data;
using Loadstone.Manifests;
using Loadstone.Pipeline;
using Loadstone.Records;
using Loadstone.Runtime;
using Loadstone.Writers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Loadstone.SqlServer;

/// <summary>
/// Set-based writer: batches of record trees are flattened per entity, bulk-copied into
/// session temp tables, and merged into the targets level by level — parents first, each
/// merge returning the database keys that become the children's foreign keys. One
/// transaction per batch, generated entirely from the manifest, identical for every
/// dataset and hierarchy depth.
/// </summary>
public sealed class SqlServerImportWriter(
    SqlConnectionFactory connectionFactory,
    IOptions<LoadstoneOptions> options,
    ILogger<SqlServerImportWriter> logger) : IImportWriter
{
    public async Task<WriteResult> WriteAsync(
        ImportContext context,
        IAsyncEnumerable<DataRecord> acceptedRoots,
        CancellationToken cancellationToken = default)
    {
        // Entities without a natural key insert unconditionally, so a retried job would
        // duplicate whatever earlier batches already committed. For those datasets the
        // whole job runs in one transaction — nothing is left behind for a retry to
        // duplicate. Keyed datasets keep the cheaper transaction-per-batch: their merges
        // are idempotent on re-import.
        var singleTransaction = context.Manifest.EnumerateEntities().Any(e => e.NaturalKey.Count == 0);
        return singleTransaction
            ? await WriteJobScopedAsync(context, acceptedRoots, cancellationToken)
            : await WriteBatchScopedAsync(context, acceptedRoots, cancellationToken);
    }

    private async Task<WriteResult> WriteBatchScopedAsync(
        ImportContext context,
        IAsyncEnumerable<DataRecord> acceptedRoots,
        CancellationToken cancellationToken)
    {
        var (batchSize, maxRecords) = BatchLimits();
        var batch = new List<DataRecord>(batchSize);
        var batchRecords = 0;
        long inserted = 0, updated = 0;

        async Task FlushAsync()
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var counts = await WriteBatchCoreAsync(connection, transaction, context.Manifest, batch, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                inserted += counts.Inserted;
                updated += counts.Updated;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }

            LogBatch(context, batch.Count);
            batch.Clear();
            batchRecords = 0;
        }

        await foreach (var root in acceptedRoots.WithCancellation(cancellationToken))
        {
            batch.Add(root);
            batchRecords += root.SelfAndDescendants().Count();
            if (batch.Count >= batchSize || batchRecords >= maxRecords)
            {
                await FlushAsync();
            }
        }

        if (batch.Count > 0)
        {
            await FlushAsync();
        }

        return new WriteResult(inserted, updated);
    }

    private async Task<WriteResult> WriteJobScopedAsync(
        ImportContext context,
        IAsyncEnumerable<DataRecord> acceptedRoots,
        CancellationToken cancellationToken)
    {
        var (batchSize, maxRecords) = BatchLimits();
        var batch = new List<DataRecord>(batchSize);
        var batchRecords = 0;
        long inserted = 0, updated = 0;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            async Task FlushAsync()
            {
                var counts = await WriteBatchCoreAsync(connection, transaction, context.Manifest, batch, cancellationToken);
                inserted += counts.Inserted;
                updated += counts.Updated;
                LogBatch(context, batch.Count);
                batch.Clear();
                batchRecords = 0;
            }

            await foreach (var root in acceptedRoots.WithCancellation(cancellationToken))
            {
                batch.Add(root);
                batchRecords += root.SelfAndDescendants().Count();
                if (batch.Count >= batchSize || batchRecords >= maxRecords)
                {
                    await FlushAsync();
                }
            }

            if (batch.Count > 0)
            {
                await FlushAsync();
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new WriteResult(inserted, updated);
    }

    private (int BatchSize, int MaxRecords) BatchLimits() => (
        Math.Max(1, options.Value.WriterBatchSize),
        Math.Max(1, options.Value.WriterBatchMaxRecords));

    private void LogBatch(ImportContext context, int rootCount) =>
        logger.LogDebug(
            "Batch of {RootCount} root records staged for dataset {Dataset}",
            rootCount, context.Manifest.Name);

    private static async Task<(long Inserted, long Updated)> WriteBatchCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DatasetManifest manifest,
        List<DataRecord> roots,
        CancellationToken cancellationToken)
    {
        long inserted = 0, updated = 0;
        var groups = GroupByEntity(manifest, roots);
        for (var ordinal = 0; ordinal < groups.Count; ordinal++)
        {
            var (entity, rows) = groups[ordinal];
            if (rows.Count == 0)
            {
                continue;
            }

            var counts = await MergeEntityAsync(connection, transaction, entity, ordinal, rows, cancellationToken);
            inserted += counts.Inserted;
            updated += counts.Updated;
        }

        return (inserted, updated);
    }

    /// <summary>Flattens the trees into per-entity row lists, parents before children.</summary>
    private static List<(EntityDefinition Entity, List<(DataRecord Record, DataRecord? Parent)> Rows)> GroupByEntity(
        DatasetManifest manifest,
        List<DataRecord> roots)
    {
        var groups = manifest.EnumerateEntities()
            .ToDictionary(e => e, _ => new List<(DataRecord, DataRecord?)>());

        void Walk(DataRecord record, DataRecord? parent)
        {
            groups[record.Entity].Add((record, parent));
            foreach (var child in record.Children)
            {
                Walk(child, record);
            }
        }

        foreach (var root in roots)
        {
            Walk(root, null);
        }

        return [.. manifest.EnumerateEntities().Select(e => (e, groups[e]))];
    }

    private static async Task<(long Inserted, long Updated)> MergeEntityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        EntityDefinition entity,
        int ordinal,
        List<(DataRecord Record, DataRecord? Parent)> rows,
        CancellationToken cancellationToken)
    {
        var hasParent = entity.ParentKeyColumn is not null;
        // Ordinal-based names: entity names that sanitize identically can never collide.
        var stageName = $"#ls_stage_{ordinal}";
        var outName = $"#ls_out_{ordinal}";

        await ExecuteAsync(connection, transaction, BuildStagingDdl(entity, stageName, outName, hasParent), cancellationToken);
        await BulkCopyAsync(connection, transaction, entity, rows, stageName, hasParent, cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, BuildMergeSql(entity, stageName, outName, hasParent), cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 8672)
        {
            throw new InvalidOperationException(
                $"The file contains multiple '{entity.Name}' records with the same natural key " +
                $"({string.Join(", ", entity.NaturalKey)}) under the same parent, which cannot be merged in one batch. " +
                "Deduplicate the source data or adjust the entity's natural key.",
                ex);
        }

        long inserted = 0, updated = 0;
        var readback = $"SELECT _Action, _DbKey, _StageId FROM {outName};";
        await using (var command = new SqlCommand(readback, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var action = reader.GetString(0);
                var dbKey = reader.GetInt64(1);
                var stageId = reader.GetInt32(2);
                rows[stageId].Record.DatabaseKey = dbKey;
                if (action == "INSERT")
                {
                    inserted++;
                }
                else
                {
                    updated++;
                }
            }
        }

        await ExecuteAsync(connection, transaction, $"DROP TABLE {stageName}; DROP TABLE {outName};", cancellationToken);
        return (inserted, updated);
    }

    internal static string BuildStagingDdl(EntityDefinition entity, string stageName, string outName, bool hasParent)
    {
        var columns = new List<string> { "_StageId int NOT NULL" };
        if (hasParent)
        {
            columns.Add("_ParentKey bigint NOT NULL");
        }

        columns.AddRange(entity.Fields.Select(f =>
            $"{SqlIdentifier.Quote(f.ColumnName)} {SqlTypeMap.SqlTypeFor(f)} NULL"));

        return $"""
            CREATE TABLE {stageName} ({string.Join(", ", columns)});
            CREATE TABLE {outName} (_Action nvarchar(10) NOT NULL, _DbKey bigint NOT NULL, _StageId int NOT NULL);
            """;
    }

    /// <summary>
    /// Generates the upsert. With a natural key, rows merge on parent key + natural key
    /// (null-safe); without one, every row inserts. The OUTPUT clause captures each row's
    /// database key against its staging id, which is how children learn their foreign keys.
    /// </summary>
    internal static string BuildMergeSql(EntityDefinition entity, string stageName, string outName, bool hasParent)
    {
        var target = entity.QualifiedTable;
        var naturalKey = new HashSet<string>(entity.NaturalKey, StringComparer.OrdinalIgnoreCase);
        var dataColumns = entity.Fields.Select(f => f.ColumnName).ToArray();

        var insertColumns = new List<string>();
        var insertValues = new List<string>();
        if (hasParent)
        {
            insertColumns.Add(SqlIdentifier.Quote(entity.ParentKeyColumn!));
            insertValues.Add("s._ParentKey");
        }

        insertColumns.AddRange(dataColumns.Select(SqlIdentifier.Quote));
        insertValues.AddRange(dataColumns.Select(c => $"s.{SqlIdentifier.Quote(c)}"));

        string onClause;
        string matchedClause = string.Empty;
        if (naturalKey.Count > 0)
        {
            var conditions = new List<string>();
            if (hasParent)
            {
                conditions.Add($"t.{SqlIdentifier.Quote(entity.ParentKeyColumn!)} = s._ParentKey");
            }

            conditions.AddRange(entity.NaturalKey.Select(k =>
            {
                var col = SqlIdentifier.Quote(k);
                var keyField = entity.Fields.FirstOrDefault(f =>
                    string.Equals(f.ColumnName, k, StringComparison.OrdinalIgnoreCase));
                // Null-safe comparison only where NULL can occur: the OR form defeats
                // index seeks, which dominates merge cost on large tables.
                return keyField?.Required == true
                    ? $"t.{col} = s.{col}"
                    : $"(t.{col} = s.{col} OR (t.{col} IS NULL AND s.{col} IS NULL))";
            }));

            onClause = string.Join(" AND ", conditions);

            var setColumns = dataColumns.Where(c => !naturalKey.Contains(c)).ToArray();
            if (setColumns.Length == 0)
            {
                // Nothing outside the key, but the UPDATE clause must exist so matched rows
                // still reach OUTPUT and hand their keys to child rows.
                setColumns = [entity.NaturalKey[0]];
            }

            matchedClause = $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", setColumns.Select(c => $"t.{SqlIdentifier.Quote(c)} = s.{SqlIdentifier.Quote(c)}"))}";
        }
        else
        {
            onClause = "1 = 0";
        }

        return $"""
            MERGE {target} WITH (HOLDLOCK) AS t
            USING {stageName} AS s
            ON {onClause}
            {matchedClause}
            WHEN NOT MATCHED THEN INSERT ({string.Join(", ", insertColumns)})
            VALUES ({string.Join(", ", insertValues)})
            OUTPUT $action, inserted.{SqlIdentifier.Quote(entity.KeyColumn)}, s._StageId INTO {outName} (_Action, _DbKey, _StageId);
            """;
    }

    private static async Task BulkCopyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        EntityDefinition entity,
        List<(DataRecord Record, DataRecord? Parent)> rows,
        string stageName,
        bool hasParent,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("_StageId", typeof(int));
        if (hasParent)
        {
            table.Columns.Add("_ParentKey", typeof(long));
        }

        foreach (var field in entity.Fields)
        {
            table.Columns.Add(field.ColumnName, SqlTypeMap.ClrTypeFor(field));
        }

        for (var stageId = 0; stageId < rows.Count; stageId++)
        {
            var (record, parent) = rows[stageId];
            var values = new object?[table.Columns.Count];
            var index = 0;
            values[index++] = stageId;
            if (hasParent)
            {
                if (parent?.DatabaseKey is null)
                {
                    throw new InvalidOperationException(
                        $"Record '{entity.Name}' at {record.Location} has no written parent row. " +
                        "This indicates a hierarchy ordering bug — please report it.");
                }

                values[index++] = Convert.ToInt64(parent.DatabaseKey);
            }

            foreach (var field in entity.Fields)
            {
                record.Values.TryGetValue(field.ColumnName, out var value);
                values[index++] = SqlTypeMap.ToStagingValue(value) ?? DBNull.Value;
            }

            table.Rows.Add(values);
        }

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = stageName,
            EnableStreaming = true,
            BulkCopyTimeout = 0,
        };
        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
