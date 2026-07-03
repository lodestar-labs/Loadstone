using Loadstone.Lookups;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Loadstone.SqlServer;

/// <summary>
/// Built-in lookup provider ("codelist"): named lists of codes stored in
/// loadstone.CodeLists / loadstone.Codes, resolving a source string to the code's integer
/// id — the pattern used for reference data like country codes, species, units, statuses.
/// Supports auto-creating unknown codes when a dataset opts in.
/// </summary>
public sealed class CodeListLookupProvider(SqlConnectionFactory connectionFactory, ILogger<CodeListLookupProvider> logger)
    : ILookupProvider
{
    public string Key => "codelist";

    public async ValueTask<LookupResult> ResolveAsync(
        string list, string value, bool caseInsensitive, CancellationToken cancellationToken = default)
    {
        var comparison = caseInsensitive ? "CI" : "CS";
        var sql = $"""
            SELECT c.Id
            FROM loadstone.Codes c
            INNER JOIN loadstone.CodeLists l ON l.Id = c.CodeListId
            WHERE l.Name = @List
              AND c.Code COLLATE Latin1_General_100_{comparison}_AS = @Value COLLATE Latin1_General_100_{comparison}_AS;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@List", list);
        command.Parameters.AddWithValue("@Value", value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int id ? LookupResult.Hit(id) : LookupResult.Miss;
    }

    public async ValueTask<object?> CreateAsync(string list, string value, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DECLARE @ListId int = (SELECT Id FROM loadstone.CodeLists WHERE Name = @List);
            IF @ListId IS NULL
            BEGIN
                INSERT INTO loadstone.CodeLists (Name) VALUES (@List);
                SET @ListId = SCOPE_IDENTITY();
            END;

            DECLARE @CodeId int = (SELECT Id FROM loadstone.Codes WHERE CodeListId = @ListId AND Code = @Value);
            IF @CodeId IS NULL
            BEGIN
                INSERT INTO loadstone.Codes (CodeListId, Code) VALUES (@ListId, @Value);
                SET @CodeId = SCOPE_IDENTITY();
            END;

            SELECT @CodeId;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@List", list);
        command.Parameters.AddWithValue("@Value", value);

        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            // Unique violation: another worker created the list or code between our
            // check and insert.
            logger.LogDebug("Code '{Code}' in list '{List}' was created concurrently; re-resolving", value, list);
            var existing = await ResolveAsync(list, value, caseInsensitive: false, cancellationToken);
            if (existing.Found)
            {
                return existing.Value;
            }

            // The other worker created the list but not this code; a second attempt
            // finds the list in place and inserts the code.
            var retried = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(retried);
        }
    }
}
