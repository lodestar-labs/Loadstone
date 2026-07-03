using Loadstone.Runtime.Datasets;
using Loadstone.Writers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Loadstone.Runtime.Workers;

/// <summary>
/// Startup initialization: creates Loadstone's system tables and loads persisted dataset
/// manifests into the registry, so the app is fully operational before the first request.
/// </summary>
public sealed class LoadstoneInitializer(
    IServiceScopeFactory scopeFactory,
    IDatasetRegistry registry,
    IManifestStore manifestStore,
    IOptions<LoadstoneOptions> options,
    ILogger<LoadstoneInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var schemaManager = scope.ServiceProvider.GetService<ISchemaManager>();
        if (schemaManager is not null)
        {
            // The database may still be starting (fresh container, cold Azure SQL): retry
            // instead of crashing the host on a race we can simply wait out.
            const int maxAttempts = 15;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await schemaManager.EnsureSystemObjectsAsync(cancellationToken);
                    break;
                }
                catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
                {
                    logger.LogWarning(
                        "Database not ready (attempt {Attempt}/{MaxAttempts}): {Reason}. Retrying in 3s",
                        attempt, maxAttempts, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
            }
        }

        var manifests = await manifestStore.LoadAllAsync(cancellationToken);
        foreach (var manifest in manifests)
        {
            registry.Register(manifest);
            if (options.Value.AutoCreateTargetTables && schemaManager is not null)
            {
                await schemaManager.ApplyTargetSchemaAsync(manifest, cancellationToken);
            }
        }

        logger.LogInformation(
            "Loadstone ready: {DatasetCount} datasets registered ({Datasets})",
            registry.All.Count,
            string.Join(", ", registry.All.Select(m => m.Name)));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
