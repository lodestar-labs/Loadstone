using Loadstone.Jobs;
using Loadstone.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Loadstone.SqlServer;

/// <summary>
/// Durable queue on top of the loadstone.Jobs table. Claiming uses UPDLOCK/READPAST so any
/// number of workers (across processes and machines) compete safely without ever handing
/// the same job to two of them. Retries use exponential backoff; exhausted jobs dead-letter.
/// </summary>
public sealed class SqlImportJobQueue(SqlConnectionFactory connectionFactory, IOptions<LoadstoneOptions> options)
    : IImportJobQueue
{
    public async Task EnqueueAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO loadstone.Jobs
                (Id, Dataset, QueueName, FileName, FileReference, Format, RequestedBy, CorrelationId,
                 Status, Attempt, MaxAttempts, CreatedAt, NextAttemptAt)
            VALUES
                (@Id, @Dataset, @Queue, @FileName, @FileReference, @Format, @RequestedBy, @CorrelationId,
                 @Status, 0, @MaxAttempts, @CreatedAt, NULL);
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", job.Id);
        command.Parameters.AddWithValue("@Dataset", job.Dataset);
        command.Parameters.AddWithValue("@Queue", job.Queue);
        command.Parameters.AddWithValue("@FileName", job.FileName);
        command.Parameters.AddWithValue("@FileReference", job.FileReference);
        command.Parameters.AddWithValue("@Format", job.Format);
        command.Parameters.AddWithValue("@RequestedBy", (object?)job.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@CorrelationId", job.CorrelationId);
        command.Parameters.AddWithValue("@Status", ImportJobStatus.Pending.ToString());
        command.Parameters.AddWithValue("@MaxAttempts", job.MaxAttempts);
        command.Parameters.AddWithValue("@CreatedAt", job.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ImportJob?> TryClaimAsync(string queue, CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH next AS (
                SELECT TOP (1) *
                FROM loadstone.Jobs WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE QueueName = @Queue
                  AND Status IN (N'Pending', N'Failed')
                  AND (NextAttemptAt IS NULL OR NextAttemptAt <= SYSDATETIMEOFFSET())
                ORDER BY CreatedAt
            )
            UPDATE next
            SET Status = N'Processing',
                StartedAt = SYSDATETIMEOFFSET(),
                Attempt = Attempt + 1
            OUTPUT inserted.*;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Queue", queue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? JobMapper.Read(reader) : null;
    }

    public async Task CompleteAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE loadstone.Jobs
            SET Status = @Status,
                CompletedAt = SYSDATETIMEOFFSET(),
                Error = NULL,
                NextAttemptAt = NULL,
                RecordsRead = @RecordsRead,
                RecordsRejected = @RecordsRejected,
                RowsInserted = @RowsInserted,
                RowsUpdated = @RowsUpdated
            WHERE Id = @Id;
            """;

        job.CompletedAt = DateTimeOffset.UtcNow;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", job.Id);
        command.Parameters.AddWithValue("@Status", job.Status.ToString());
        command.Parameters.AddWithValue("@RecordsRead", job.RecordsRead);
        command.Parameters.AddWithValue("@RecordsRejected", job.RecordsRejected);
        command.Parameters.AddWithValue("@RowsInserted", job.RowsInserted);
        command.Parameters.AddWithValue("@RowsUpdated", job.RowsUpdated);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailAsync(ImportJob job, string error, CancellationToken cancellationToken = default)
    {
        var exhausted = job.Attempt >= job.MaxAttempts;
        if (exhausted)
        {
            job.Status = ImportJobStatus.DeadLettered;
            job.NextAttemptAt = null;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            var backoff = TimeSpan.FromSeconds(
                options.Value.RetryBaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, job.Attempt - 1)));
            job.Status = ImportJobStatus.Failed;
            job.NextAttemptAt = DateTimeOffset.UtcNow.Add(backoff);
        }

        job.Error = error;

        const string sql = """
            UPDATE loadstone.Jobs
            SET Status = @Status,
                Error = @Error,
                NextAttemptAt = @NextAttemptAt,
                CompletedAt = @CompletedAt
            WHERE Id = @Id;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", job.Id);
        command.Parameters.AddWithValue("@Status", job.Status.ToString());
        command.Parameters.AddWithValue("@Error", error);
        command.Parameters.AddWithValue("@NextAttemptAt", (object?)job.NextAttemptAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@CompletedAt", (object?)job.CompletedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ReclaimAbandonedAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE loadstone.Jobs
            SET Status = N'Pending', NextAttemptAt = NULL
            WHERE Status = N'Processing'
              AND StartedAt < DATEADD(SECOND, -@Seconds, SYSDATETIMEOFFSET());
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Seconds", (int)olderThan.TotalSeconds);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class JobMapper
{
    public static ImportJob Read(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(reader.GetOrdinal("Id")),
        Dataset = reader.GetString(reader.GetOrdinal("Dataset")),
        Queue = reader.GetString(reader.GetOrdinal("QueueName")),
        FileName = reader.GetString(reader.GetOrdinal("FileName")),
        FileReference = reader.GetString(reader.GetOrdinal("FileReference")),
        Format = reader.GetString(reader.GetOrdinal("Format")),
        RequestedBy = ReadNullableString(reader, "RequestedBy"),
        CorrelationId = reader.GetString(reader.GetOrdinal("CorrelationId")),
        Status = Enum.Parse<ImportJobStatus>(reader.GetString(reader.GetOrdinal("Status"))),
        Attempt = reader.GetInt32(reader.GetOrdinal("Attempt")),
        MaxAttempts = reader.GetInt32(reader.GetOrdinal("MaxAttempts")),
        CreatedAt = reader.GetDateTimeOffset(reader.GetOrdinal("CreatedAt")),
        NextAttemptAt = ReadNullableDate(reader, "NextAttemptAt"),
        StartedAt = ReadNullableDate(reader, "StartedAt"),
        CompletedAt = ReadNullableDate(reader, "CompletedAt"),
        Error = ReadNullableString(reader, "Error"),
        RecordsRead = reader.GetInt64(reader.GetOrdinal("RecordsRead")),
        RecordsRejected = reader.GetInt64(reader.GetOrdinal("RecordsRejected")),
        RowsInserted = reader.GetInt64(reader.GetOrdinal("RowsInserted")),
        RowsUpdated = reader.GetInt64(reader.GetOrdinal("RowsUpdated")),
    };

    private static string? ReadNullableString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTimeOffset(ordinal);
    }
}
