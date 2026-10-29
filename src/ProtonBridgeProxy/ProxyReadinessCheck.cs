using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ProtonBridgeProxy;

public sealed class ProxyReadinessCheck(Options options, ProxyListenerState state) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!state.Ready)
        {
            return HealthCheckResult.Unhealthy();
        }

        try
        {
            using var client = BridgeConnection.Create(options);
            await client.ConnectAsync(options.BridgeHost, options.BridgePort, options.SocketOptions, cancellationToken);
            await client.DisconnectAsync(false, cancellationToken);
            return state.Ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (Exception)
        {
            // No credentials are sent, and no exception details reach health responses or logs.
            return HealthCheckResult.Unhealthy();
        }
    }
}
