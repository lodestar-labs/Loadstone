using Loadstone.Jobs;
using Loadstone.Runtime;
using Loadstone.Writers;
using Microsoft.Extensions.DependencyInjection;

namespace Loadstone.SqlServer;

public static class LoadstoneSqlServerExtensions
{
    /// <summary>
    /// Uses SQL Server (or Azure SQL) for everything durable: the job queue, the job store
    /// and audit trail, code-list lookups, target schema management, and the bulk
    /// staging/merge writer.
    /// </summary>
    public static LoadstoneBuilder UseSqlServer(this LoadstoneBuilder builder, string? connectionString = null)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.Configure<LoadstoneOptions>(o => o.ConnectionString = connectionString);
        }

        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.AddSingleton<IImportJobQueue, SqlImportJobQueue>();
        builder.Services.AddSingleton<IImportJobStore, SqlImportJobStore>();
        builder.Services.AddSingleton<IImportWriter, SqlServerImportWriter>();
        builder.Services.AddSingleton<ISchemaManager, SqlServerSchemaManager>();
        builder.Services.AddSingleton<CodeListAdminService>();
        builder.AddLookupProvider<CodeListLookupProvider>();
        return builder;
    }

    /// <summary>
    /// Registers a configuration-defined lookup provider backed by a SQL query — typically
    /// an existing company lookup database. Manifests reference it through
    /// <c>field.lookup.provider = options.Key</c>.
    /// </summary>
    public static LoadstoneBuilder AddSqlLookup(this LoadstoneBuilder builder, SqlLookupOptions options)
    {
        builder.Services.AddSingleton<Loadstone.Lookups.ILookupProvider>(provider =>
            new SqlQueryLookupProvider(options, provider.GetRequiredService<SqlConnectionFactory>()));
        return builder;
    }

    public static LoadstoneBuilder AddSqlLookup(
        this LoadstoneBuilder builder, string key, string query, string? connectionString = null) =>
        builder.AddSqlLookup(new SqlLookupOptions { Key = key, Query = query, ConnectionString = connectionString });
}
