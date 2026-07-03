using Microsoft.Data.SqlClient;

namespace Loadstone.SqlServer;

public sealed record CodeListSummary(string Name, int CodeCount);

public sealed record CodeEntry(string Code, string? Description, int? Id = null);

/// <summary>Management operations for the built-in code lists (the "codelist" lookup provider).</summary>
public sealed class CodeListAdminService(SqlConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<CodeListSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT l.Name, COUNT(c.Id)
            FROM loadstone.CodeLists l
            LEFT JOIN loadstone.Codes c ON c.CodeListId = l.Id
            GROUP BY l.Name
            ORDER BY l.Name;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var lists = new List<CodeListSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lists.Add(new CodeListSummary(reader.GetString(0), reader.GetInt32(1)));
        }

        return lists;
    }

    public async Task<IReadOnlyList<CodeEntry>?> GetAsync(string list, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT c.Code, c.Description, c.Id
            FROM loadstone.Codes c
            INNER JOIN loadstone.CodeLists l ON l.Id = c.CodeListId
            WHERE l.Name = @List
            ORDER BY c.Code;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        if (!await ListExistsAsync(connection, list, cancellationToken))
        {
            return null;
        }

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@List", list);
        var codes = new List<CodeEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            codes.Add(new CodeEntry(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2)));
        }

        return codes;
    }

    /// <summary>Creates the list when missing and upserts the supplied codes. Returns codes affected.</summary>
    public async Task<int> UpsertAsync(string list, IReadOnlyList<CodeEntry> entries, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DECLARE @ListId int = (SELECT Id FROM loadstone.CodeLists WHERE Name = @List);
            IF @ListId IS NULL
            BEGIN
                INSERT INTO loadstone.CodeLists (Name) VALUES (@List);
                SET @ListId = SCOPE_IDENTITY();
            END;

            MERGE loadstone.Codes WITH (HOLDLOCK) AS t
            USING (SELECT @Code AS Code, @Description AS Description) AS s
            ON t.CodeListId = @ListId AND t.Code = s.Code
            WHEN MATCHED THEN UPDATE SET t.Description = s.Description
            WHEN NOT MATCHED THEN INSERT (CodeListId, Code, Description) VALUES (@ListId, s.Code, s.Description);
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var entry in entries)
            {
                await using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("@List", list);
                command.Parameters.AddWithValue("@Code", entry.Code);
                command.Parameters.AddWithValue("@Description", (object?)entry.Description ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return entries.Count;
    }

    private static async Task<bool> ListExistsAsync(SqlConnection connection, string list, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT 1 FROM loadstone.CodeLists WHERE Name = @List;", connection);
        command.Parameters.AddWithValue("@List", list);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
