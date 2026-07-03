using System.Data;
using Loadstone.Jobs;
using Microsoft.Data.SqlClient;

namespace Loadstone.SqlServer;

/// <summary>Job read model and audit trail (timeline events and rejected rows) in SQL Server.</summary>
public sealed class SqlImportJobStore(SqlConnectionFactory connectionFactory) : IImportJobStore
{
    public async Task<ImportJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT * FROM loadstone.Jobs WHERE Id = @Id;", connection);
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? JobMapper.Read(reader) : null;
    }

    public async Task<IReadOnlyList<ImportJob>> ListAsync(
        string? dataset = null,
        ImportJobStatus? status = null,
        int top = 100,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Top) *
            FROM loadstone.Jobs
            WHERE (@Dataset IS NULL OR Dataset = @Dataset)
              AND (@Status IS NULL OR Status = @Status)
            ORDER BY CreatedAt DESC;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Top", Math.Clamp(top, 1, 1000));
        command.Parameters.AddWithValue("@Dataset", (object?)dataset ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", (object?)status?.ToString() ?? DBNull.Value);

        var jobs = new List<ImportJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(JobMapper.Read(reader));
        }

        return jobs;
    }

    public async Task AddEventAsync(JobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO loadstone.JobEvents (JobId, At, Stage, Message, ElapsedMs)
            VALUES (@JobId, @At, @Stage, @Message, @ElapsedMs);
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@JobId", jobEvent.JobId);
        command.Parameters.AddWithValue("@At", jobEvent.At);
        command.Parameters.AddWithValue("@Stage", jobEvent.Stage);
        command.Parameters.AddWithValue("@Message", jobEvent.Message);
        command.Parameters.AddWithValue("@ElapsedMs", (object?)jobEvent.ElapsedMs ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT JobId, At, Stage, Message, ElapsedMs FROM loadstone.JobEvents WHERE JobId = @JobId ORDER BY At, Id;";
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@JobId", jobId);

        var events = new List<JobEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new JobEvent(
                reader.GetGuid(0),
                reader.GetDateTimeOffset(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }

        return events;
    }

    public async Task AddRejectedRowsAsync(IReadOnlyList<RejectedRow> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var table = new DataTable();
        table.Columns.Add("JobId", typeof(Guid));
        table.Columns.Add("Entity", typeof(string));
        table.Columns.Add("SourceLine", typeof(long));
        table.Columns.Add("SourcePath", typeof(string));
        table.Columns.Add("Field", typeof(string));
        table.Columns.Add("Reason", typeof(string));
        table.Columns.Add("RawValue", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.JobId,
                Truncate(row.Entity, 200),
                (object?)row.SourceLine ?? DBNull.Value,
                (object?)Truncate(row.SourcePath, 400) ?? DBNull.Value,
                Truncate(row.Field, 200),
                Truncate(row.Reason, 2000),
                (object?)Truncate(row.RawValue, 2000) ?? DBNull.Value);
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        using var bulkCopy = new SqlBulkCopy(connection)
        {
            DestinationTableName = "loadstone.RejectedRows",
        };
        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, cancellationToken);
    }

    public async Task<IReadOnlyList<RejectedRow>> GetRejectedRowsAsync(Guid jobId, int top = 1000, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Top) JobId, Entity, SourceLine, SourcePath, Field, Reason, RawValue
            FROM loadstone.RejectedRows
            WHERE JobId = @JobId
            ORDER BY Id;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Top", Math.Clamp(top, 1, 100_000));
        command.Parameters.AddWithValue("@JobId", jobId);

        var rows = new List<RejectedRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RejectedRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
