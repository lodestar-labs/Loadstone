using Loadstone.SqlServer;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Loadstone.Api;

public sealed class DatabaseHealthCheck(SqlConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy("Database reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
