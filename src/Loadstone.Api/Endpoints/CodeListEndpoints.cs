using Loadstone.SqlServer;

namespace Loadstone.Api.Endpoints;

public static class CodeListEndpoints
{
    public static IEndpointRouteBuilder MapCodeListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/codelists").WithTags("Code lists");

        group.MapGet("/", async (CodeListAdminService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapGet("/{list}", async (string list, CodeListAdminService service, CancellationToken cancellationToken) =>
            await service.GetAsync(list, cancellationToken) is { } codes
                ? Results.Ok(codes)
                : Results.NotFound());

        group.MapPut("/{list}", async (
            string list,
            CodeEntry[] entries,
            CodeListAdminService service,
            CancellationToken cancellationToken) =>
        {
            if (entries.Length == 0)
            {
                return Results.BadRequest(new { error = "Provide at least one code." });
            }

            if (entries.Any(e => string.IsNullOrWhiteSpace(e.Code)))
            {
                return Results.BadRequest(new { error = "Every entry requires a non-empty 'code'." });
            }

            var count = await service.UpsertAsync(list, entries, cancellationToken);
            return Results.Ok(new { list, upserted = count });
        });

        return app;
    }
}
