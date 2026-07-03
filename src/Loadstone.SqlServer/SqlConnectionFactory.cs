using Loadstone.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Loadstone.SqlServer;

public sealed class SqlConnectionFactory(IOptions<LoadstoneOptions> options)
{
    private readonly string _connectionString = options.Value.ConnectionString
        ?? throw new InvalidOperationException(
            "Loadstone connection string is not configured. Set Loadstone:ConnectionString.");

    internal string ConnectionString => _connectionString;

    public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        OpenCoreAsync(_connectionString, cancellationToken);

    /// <summary>Opens a connection to the same server's master database (for CREATE DATABASE).</summary>
    internal Task<SqlConnection> OpenMasterAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        return OpenCoreAsync(builder.ConnectionString, cancellationToken);
    }

    private static async Task<SqlConnection> OpenCoreAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal string DatabaseName => new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
}

internal static class SqlIdentifier
{
    /// <summary>Bracket-quotes an identifier so manifest-supplied names can't break out of SQL.</summary>
    public static string Quote(string name) => $"[{name.Replace("]", "]]")}]";

    public static string Quote(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";
}
