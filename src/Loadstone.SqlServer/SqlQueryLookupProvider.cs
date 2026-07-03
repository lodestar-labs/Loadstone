using Loadstone.Lookups;
using Microsoft.Data.SqlClient;

namespace Loadstone.SqlServer;

/// <summary>
/// A lookup provider defined entirely by configuration: a provider key, a SQL query, and
/// optionally its own connection string. This is how an existing, company-specific lookup
/// database (a legacy code table, a shared reference-data warehouse) plugs into imports
/// without writing any code — the query receives <c>@List</c> and <c>@Value</c> and returns
/// the resolved value as a single scalar.
/// </summary>
public sealed class SqlLookupOptions
{
    /// <summary>Provider key that manifests reference via <c>field.lookup.provider</c>.</summary>
    public required string Key { get; set; }

    /// <summary>
    /// Query returning one scalar (the resolved value) or no rows (a miss).
    /// Parameters: <c>@List</c> = the manifest's lookup list name, <c>@Value</c> = the raw
    /// source value. Case sensitivity follows the queried columns' collation.
    /// </summary>
    public required string Query { get; set; }

    /// <summary>Connection string of the lookup database. Defaults to Loadstone's own.</summary>
    public string? ConnectionString { get; set; }
}

public sealed class SqlQueryLookupProvider(SqlLookupOptions options, SqlConnectionFactory connectionFactory)
    : ILookupProvider
{
    public string Key => options.Key;

    public async ValueTask<LookupResult> ResolveAsync(
        string list, string value, bool caseInsensitive, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(options.Query, connection);
        command.Parameters.AddWithValue("@List", list);
        command.Parameters.AddWithValue("@Value", value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? LookupResult.Miss : LookupResult.Hit(result);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (options.ConnectionString is null)
        {
            return await connectionFactory.OpenAsync(cancellationToken);
        }

        var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
