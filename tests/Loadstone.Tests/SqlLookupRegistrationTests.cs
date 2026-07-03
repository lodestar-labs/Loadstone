using Loadstone.Lookups;
using Loadstone.Runtime;
using Loadstone.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Loadstone.Tests;

[TestFixture]
public class SqlLookupRegistrationTests
{
    [Test]
    public void Configured_sql_lookups_register_as_providers_under_their_key()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddLoadstone(o => o.ConnectionString = "Server=unused;Database=unused")
            .UseSqlServer()
            .AddSqlLookup(new SqlLookupOptions
            {
                Key = "companyx-codes",
                Query = "SELECT Id FROM dbo.Codes WHERE List = @List AND Code = @Value",
            })
            .AddSqlLookup("warehouse", "SELECT SkuId FROM wh.Skus WHERE Sku = @Value", "Server=warehouse;Database=Ref");

        using var provider = services.BuildServiceProvider();
        var keys = provider.GetServices<ILookupProvider>().Select(p => p.Key).ToArray();

        Assert.That(keys, Is.SupersetOf(new[] { "codelist", "companyx-codes", "warehouse" }));
    }
}
