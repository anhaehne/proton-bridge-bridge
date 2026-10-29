using System.Net;

namespace ProtonBridgeProxy;

public static class HealthProbe
{
    /// <summary>Checks local HTTP readiness without creating a host or accessing mailbox credentials.</summary>
    public static async Task<bool> CheckAsync(Options options, CancellationToken cancellationToken)
    {
        var address = IPAddress.Parse(options.HealthListenAddress);
        if (address.Equals(IPAddress.Any))
        {
            address = IPAddress.Loopback;
        }
        else if (address.Equals(IPAddress.IPv6Any))
        {
            address = IPAddress.IPv6Loopback;
        }

        var uri = new UriBuilder(Uri.UriSchemeHttp, address.ToString(), options.HealthPort, "/health/ready").Uri;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(7)
        };
        using var response = await client.GetAsync(uri, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
