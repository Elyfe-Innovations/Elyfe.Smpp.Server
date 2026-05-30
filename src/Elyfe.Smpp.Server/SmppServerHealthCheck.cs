using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Elyfe.Smpp.Server;

/// <summary>
///     Reports the SMPP server's health for integration with ASP.NET Core health checks (and, by extension, the
///     Aspire dashboard). Healthy when the listener is accepting connections; degraded otherwise.
/// </summary>
public sealed class SmppServerHealthCheck(ISmppServer server) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["activeSessions"] = server.ActiveSessions,
            ["listening"] = server.IsListening
        };

        var result = server.IsListening
            ? HealthCheckResult.Healthy("SMPP server is accepting connections.", data)
            : HealthCheckResult.Unhealthy("SMPP server is not listening.", data: data);

        return Task.FromResult(result);
    }
}
