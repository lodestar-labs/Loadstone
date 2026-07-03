using Loadstone.Lookups;
using Loadstone.Manifests;
using Loadstone.Pipeline;
using Loadstone.Runtime.Datasets;
using Loadstone.Runtime.Files;
using Loadstone.Runtime.Lookups;
using Loadstone.Runtime.Steps;
using Loadstone.Runtime.Workers;
using Loadstone.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Loadstone.Runtime;

/// <summary>
/// Fluent registration surface returned by <see cref="ServiceCollectionExtensions.AddLoadstone"/>.
/// Providers (database, queue transport, file store, lookups, custom steps, readers) all
/// plug in here.
/// </summary>
public sealed class LoadstoneBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>Registers a compile-time dataset. The manifest is checked at startup.</summary>
    public LoadstoneBuilder AddDataset<TDataset>() where TDataset : IDataset =>
        AddDataset(TDataset.Manifest);

    public LoadstoneBuilder AddDataset(DatasetManifest manifest)
    {
        Services.AddSingleton(new StaticDatasetRegistration(manifest));
        return this;
    }

    public LoadstoneBuilder AddSourceReader<TReader>() where TReader : class, ISourceReader
    {
        Services.AddSingleton<ISourceReader, TReader>();
        return this;
    }

    public LoadstoneBuilder AddLookupProvider<TProvider>() where TProvider : class, ILookupProvider
    {
        Services.AddSingleton<ILookupProvider, TProvider>();
        return this;
    }

    /// <summary>Adds a custom pipeline step; runs between the built-ins and the writer by default.</summary>
    public LoadstoneBuilder AddStep<TStep>() where TStep : class, IImportStep
    {
        Services.AddSingleton<IImportStep, TStep>();
        return this;
    }

    public LoadstoneBuilder UseFileStore<TStore>() where TStore : class, Loadstone.Files.IFileStore
    {
        Services.Replace(ServiceDescriptor.Singleton<Loadstone.Files.IFileStore, TStore>());
        return this;
    }
}

/// <summary>Marker for datasets registered in code, folded into the registry at startup.</summary>
public sealed record StaticDatasetRegistration(DatasetManifest Manifest);

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Loadstone import platform: pipeline engine, dataset registry, built-in
    /// validation and lookup steps, local file store, and background queue workers. Combine
    /// with a database provider (e.g. <c>UseSqlServer</c>) and the source readers.
    /// </summary>
    public static LoadstoneBuilder AddLoadstone(this IServiceCollection services, Action<LoadstoneOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<LoadstoneOptions>();
        }

        services.AddMemoryCache();
        services.TryAddSingleton<IDatasetRegistry>(provider =>
        {
            var registry = new DatasetRegistry();
            foreach (var registration in provider.GetServices<StaticDatasetRegistration>())
            {
                registry.Register(registration.Manifest);
            }

            return registry;
        });

        services.TryAddSingleton<IManifestStore, ManifestDirectoryStore>();
        services.TryAddSingleton<Loadstone.Files.IFileStore, LocalFileStore>();
        services.TryAddSingleton<ILookupResolver, CachingLookupResolver>();
        services.AddSingleton<IImportStep, ValidateStep>();
        services.AddSingleton<IImportStep, LookupStep>();
        services.AddScoped<ImportEngine>();
        services.AddHostedService<LoadstoneInitializer>();
        services.AddHostedService<QueueWorkerService>();

        return new LoadstoneBuilder(services);
    }
}
