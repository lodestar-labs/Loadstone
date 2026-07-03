using System.Text;
using Loadstone.Manifests;
using Loadstone.Writers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Loadstone.SqlServer;

/// <summary>
/// Creates Loadstone's system tables and derives target-table DDL from dataset manifests:
/// identity keys, parent foreign keys, typed columns, and a unique index over the natural
/// key — the same definitions the writer's merge relies on, from the same source of truth.
/// </summary>
public sealed class SqlServerSchemaManager(SqlConnectionFactory connectionFactory, ILogger<SqlServerSchemaManager> logger)
    : ISchemaManager
{
    public async Task EnsureSystemObjectsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseExistsAsync(cancellationToken);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        foreach (var statement in SystemSchema.Statements)
        {
            await using var command = new SqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogDebug("Loadstone system objects verified");
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = await connectionFactory.OpenAsync(cancellationToken);
            return;
        }
        catch (SqlException ex) when (ex.Number == 4060)
        {
            // Database doesn't exist yet; create it so a fresh server works out of the box.
        }

        var database = connectionFactory.DatabaseName;
        logger.LogInformation("Database {Database} does not exist; creating it", database);
        await using var master = await connectionFactory.OpenMasterAsync(cancellationToken);
        var sql = $"IF DB_ID(N'{database.Replace("'", "''")}') IS NULL CREATE DATABASE {SqlIdentifier.Quote(database)};";
        await using var command = new SqlCommand(sql, master);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public string GenerateTargetSchemaScript(DatasetManifest manifest)
    {
        var script = new StringBuilder();
        var parents = new Dictionary<EntityDefinition, EntityDefinition>();
        foreach (var entity in manifest.EnumerateEntities())
        {
            foreach (var child in entity.Children)
            {
                parents[child] = entity;
            }
        }

        foreach (var entity in manifest.EnumerateEntities())
        {
            AppendEntityTable(script, entity, parents.GetValueOrDefault(entity));
        }

        return script.ToString();
    }

    public async Task ApplyTargetSchemaAsync(DatasetManifest manifest, CancellationToken cancellationToken = default)
    {
        var script = GenerateTargetSchemaScript(manifest);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        foreach (var batch in SplitBatches(script))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("Target schema verified for dataset {Dataset}", manifest.Name);
    }

    private static IEnumerable<string> SplitBatches(string script)
    {
        var batch = new StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.Length > 0)
                {
                    yield return batch.ToString();
                    batch.Clear();
                }

                continue;
            }

            batch.AppendLine(line.TrimEnd('\r'));
        }

        if (batch.ToString().Trim().Length > 0)
        {
            yield return batch.ToString();
        }
    }

    private static void AppendEntityTable(StringBuilder script, EntityDefinition entity, EntityDefinition? parent)
    {
        var table = SqlIdentifier.Quote(entity.Schema, entity.Table);
        var constraintBase = $"{entity.Schema}_{entity.Table}".Replace("]", string.Empty).Replace("[", string.Empty);

        script.AppendLine($"IF OBJECT_ID(N'{table.Replace("'", "''")}') IS NULL");
        script.AppendLine("BEGIN");
        script.AppendLine($"CREATE TABLE {table} (");
        script.AppendLine($"    {SqlIdentifier.Quote(entity.KeyColumn)} bigint IDENTITY(1,1) NOT NULL CONSTRAINT {SqlIdentifier.Quote($"PK_{constraintBase}")} PRIMARY KEY,");

        if (parent is not null && entity.ParentKeyColumn is { } parentKeyColumn)
        {
            script.AppendLine(
                $"    {SqlIdentifier.Quote(parentKeyColumn)} bigint NOT NULL CONSTRAINT {SqlIdentifier.Quote($"FK_{constraintBase}_parent")} " +
                $"REFERENCES {SqlIdentifier.Quote(parent.Schema, parent.Table)} ({SqlIdentifier.Quote(parent.KeyColumn)}),");
        }

        var isNaturalKeyColumn = new HashSet<string>(entity.NaturalKey, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entity.Fields.Count; i++)
        {
            var field = entity.Fields[i];
            var sqlType = SqlTypeMap.SqlTypeFor(field, indexable: isNaturalKeyColumn.Contains(field.ColumnName));
            var nullability = field.Required ? "NOT NULL" : "NULL";
            var separator = i < entity.Fields.Count - 1 ? "," : string.Empty;
            script.AppendLine($"    {SqlIdentifier.Quote(field.ColumnName)} {sqlType} {nullability}{separator}");
        }

        script.AppendLine(");");

        if (entity.NaturalKey.Count > 0)
        {
            var keyColumns = new List<string>();
            if (entity.ParentKeyColumn is { } parentKey && parent is not null)
            {
                keyColumns.Add(SqlIdentifier.Quote(parentKey));
            }

            keyColumns.AddRange(entity.NaturalKey.Select(SqlIdentifier.Quote));
            script.AppendLine(
                $"CREATE UNIQUE INDEX {SqlIdentifier.Quote($"UX_{constraintBase}_natural")} ON {table} ({string.Join(", ", keyColumns)});");
        }

        script.AppendLine("END");
        script.AppendLine("GO");
        script.AppendLine();
    }
}
