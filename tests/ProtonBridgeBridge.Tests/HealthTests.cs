using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class HealthTests
{
    [Theory]
    [InlineData("healthy")]
    [InlineData("unavailable")]
    [InlineData("hostname-mismatch")]
    public async Task HealthEndpointsDistinguishLivenessFromVerifiedBridgeReadinessAsync(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fixture = await HostFixture.StartAsync(scenario, timeout.Token);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(fixture.App.Urls.Single())
        };

        using var live = await client.GetAsync("/health/live", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync(timeout.Token));

        using var ready = await client.GetAsync("/health/ready", timeout.Token);
        var healthy = scenario == "healthy";
        Assert.Equal(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(healthy ? "Healthy" : "Unhealthy", await ready.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(healthy, await HealthProbe.CheckAsync(fixture.Settings, timeout.Token));
        Assert.DoesNotContain("AUTHENTICATE", fixture.Stock.Commands);
        Assert.DoesNotContain("LOGIN", fixture.Stock.Commands);
        Assert.Equal(0, fixture.Stock.Copies);
        Assert.Equal(["proxy.lock"], Directory.GetFiles(fixture.Settings.StateDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task HostedListenerAcceptsTlsAndGracefullyClosesIdleClientsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fixture = await HostFixture.StartAsync("healthy", timeout.Token);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, fixture.Settings.ListenPort, timeout.Token);
        await using var tls = new SslStream(connection.GetStream(), false,
            (_, certificate, _, errors) => Trust.Validate(certificate, errors, fixture.Certificate));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
        using var reader = new StreamReader(tls);
        Assert.StartsWith("* OK", await reader.ReadLineAsync(timeout.Token));

        await fixture.App.StopAsync(timeout.Token);
        Assert.False(fixture.App.Services.GetRequiredService<ProxyListenerState>().Ready);
        Assert.Null(await reader.ReadLineAsync(timeout.Token));
    }

    [Fact]
    public async Task NotStartedListenerFailsReadinessWithoutOpeningBridgeAsync()
    {
        var options = new Options { Certificate = "unused", PrivateKey = "unused" };
        var check = new ProxyReadinessCheck(options, new ProxyListenerState());
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
        Assert.Null(result.Description);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));

        public X509Certificate2 Certificate { get; } = WireTests.Certificate();

        public StockBridge Stock { get; private set; } = null!;

        public Microsoft.AspNetCore.Builder.WebApplication App { get; private set; } = null!;

        public Options Settings { get; private set; } = null!;

        public static async Task<HostFixture> StartAsync(string scenario, CancellationToken cancellationToken)
        {
            var fixture = new HostFixture();
            try
            {
                Directory.CreateDirectory(fixture._directory);
                var certificatePath = Path.Combine(fixture._directory, "proxy.pem");
                var keyPath = Path.Combine(fixture._directory, "proxy.key");
                await File.WriteAllTextAsync(certificatePath, fixture.Certificate.ExportCertificatePem(), cancellationToken);
                using var key = fixture.Certificate.GetRSAPrivateKey()!;
                await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), cancellationToken);
                fixture.Stock = new StockBridge(fixture.Certificate, false, fixture._stop.Token);
                fixture.Stock.Start();
                fixture.Settings = new Options
                {
                    Certificate = certificatePath,
                    PrivateKey = keyPath,
                    BridgeHost = scenario == "hostname-mismatch" ? "127.0.0.1" : "localhost",
                    BridgePort = scenario == "unavailable" ? AvailablePort() : fixture.Stock.Port,
                    BridgeTls = "SslOnConnect",
                    BridgeTrust = certificatePath,
                    ListenPort = AvailablePort(),
                    HealthPort = 0,
                    StateDirectory = Path.Combine(fixture._directory, "state")
                };
                fixture.App = ProxyHost.Create(fixture.Settings);
                await fixture.App.StartAsync(cancellationToken);
                fixture.Settings = fixture.Settings with { HealthPort = new Uri(fixture.App.Urls.Single()).Port };
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private static int AvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public async ValueTask DisposeAsync()
        {
            if (App is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await App.StopAsync(timeout.Token);
                await App.DisposeAsync();
            }

            _stop.Cancel();
            if (Stock is not null)
            {
                await Stock.DisposeAsync();
            }

            _stop.Dispose();
            Certificate.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}
