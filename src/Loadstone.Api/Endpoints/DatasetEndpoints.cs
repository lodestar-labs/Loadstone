using Loadstone.Manifests;
using Loadstone.Runtime;
using Loadstone.Runtime.Datasets;
using Loadstone.Writers;
using Microsoft.Extensions.Options;

namespace Loadstone.Api.Endpoints;

public static class DatasetEndpoints
{
    public static IEndpointRouteBuilder MapDatasetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/datasets").WithTags("Datasets");

        group.MapGet("/", (IDatasetRegistry registry) =>
            Results.Ok(registry.All.Select(DatasetSummary.From)));

        group.MapGet("/{name}", (string name, IDatasetRegistry registry) =>
            registry.Find(name) is { } manifest
                ? Results.Text(ManifestSerializer.Serialize(manifest), "application/json")
                : Results.NotFound());

        group.MapPut("/{name}", async (
            string name,
            HttpRequest request,
            IDatasetRegistry registry,
            IManifestStore store,
            ISchemaManager schemaManager,
            IOptions<LoadstoneOptions> options,
            CancellationToken cancellationToken) =>
        {
            DatasetManifest manifest;
            try
            {
                manifest = await ManifestSerializer.DeserializeAsync(request.Body, cancellationToken);
            }
            catch (ManifestException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Manifest is not valid JSON: {ex.Message}" });
            }

            if (!string.Equals(manifest.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"Manifest name '{manifest.Name}' does not match the route ('{name}')." });
            }

            registry.Register(manifest);
            await store.SaveAsync(manifest, cancellationToken);
            if (options.Value.AutoCreateTargetTables)
            {
                await schemaManager.ApplyTargetSchemaAsync(manifest, cancellationToken);
            }

            return Results.Ok(DatasetSummary.From(manifest));
        });

        group.MapDelete("/{name}", async (
            string name,
            IDatasetRegistry registry,
            IManifestStore store,
            CancellationToken cancellationToken) =>
        {
            if (!registry.Remove(name))
            {
                return Results.NotFound();
            }

            await store.DeleteAsync(name, cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/{name}/schema", (string name, IDatasetRegistry registry, ISchemaManager schemaManager) =>
            registry.Find(name) is { } manifest
                ? Results.Text(schemaManager.GenerateTargetSchemaScript(manifest), "text/plain")
                : Results.NotFound());

        group.MapPost("/{name}/schema/apply", async (
            string name,
            IDatasetRegistry registry,
            ISchemaManager schemaManager,
            CancellationToken cancellationToken) =>
        {
            if (registry.Find(name) is not { } manifest)
            {
                return Results.NotFound();
            }

            await schemaManager.ApplyTargetSchemaAsync(manifest, cancellationToken);
            return Results.Ok(new { applied = true });
        });

        return app;
    }
}

public sealed record DatasetSummary(
    string Name,
    string Version,
    string? Description,
    string Queue,
    int EntityCount,
    IReadOnlyList<string> Entities)
{
    public static DatasetSummary From(DatasetManifest manifest)
    {
        var entities = manifest.EnumerateEntities().Select(e => e.Name).ToArray();
        return new DatasetSummary(
            manifest.Name,
            manifest.Version,
            manifest.Description,
            manifest.QueueName,
            entities.Length,
            entities);
    }
}
