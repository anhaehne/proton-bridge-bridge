using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

internal sealed class RecoveryFixture : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ProtonBridgeBridge-recovery-" + Guid.NewGuid());
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(2));
    private readonly X509Certificate2 _certificate = WireTests.Certificate();
    private readonly List<ProxyProcess> _processes = [];
    private Options _options = null!;
    private string _configPath = null!;

    public Options Settings => _options;

    public StockBridge Stock { get; private set; } = null!;

    public static async Task<RecoveryFixture> StartAsync(bool identicalDeliveries, CancellationToken cancellationToken)
    {
        var fixture = new RecoveryFixture();
        try
        {
            Directory.CreateDirectory(fixture._directory);
            var certPath = Path.Combine(fixture._directory, "test.pem");
            var keyPath = Path.Combine(fixture._directory, "test.key");
            await File.WriteAllTextAsync(certPath, fixture._certificate.ExportCertificatePem(), cancellationToken);
            using var key = fixture._certificate.GetRSAPrivateKey()!;
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), cancellationToken);
            fixture.Stock = new StockBridge(fixture._certificate, false, fixture._stop.Token);
            if (!identicalDeliveries)
            {
                fixture.Stock.Inbox.Remove(8);
            }

            fixture.Stock.Start();
            using var imapPort = new TcpListener(IPAddress.Loopback, 0);
            using var healthPort = new TcpListener(IPAddress.Loopback, 0);
            imapPort.Start();
            healthPort.Start();
            fixture._options = new Options
            {
                Certificate = certPath,
                PrivateKey = keyPath,
                BridgeHost = "localhost",
                BridgePort = fixture.Stock.Port,
                BridgeTls = "SslOnConnect",
                BridgeTrust = certPath,
                ListenPort = ((IPEndPoint)imapPort.LocalEndpoint).Port,
                HealthPort = ((IPEndPoint)healthPort.LocalEndpoint).Port,
                AllowLabelWrites = true,
                AllowHashFallback = true,
                StateDirectory = Path.Combine(fixture._directory, "state")
            };
            fixture._configPath = Path.Combine(fixture._directory, "config.json");
            await File.WriteAllTextAsync(fixture._configPath, JsonSerializer.Serialize(fixture._options), cancellationToken);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public async Task ConfigureAsync(Options options, CancellationToken cancellationToken)
    {
        _options = options;
        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(options), cancellationToken);
    }

    public async Task<ProxyProcess> StartProxyAsync(CancellationToken cancellationToken)
    {
        var process = await ProxyProcess.StartAsync(_configPath, _options.HealthPort, cancellationToken);
        _processes.Add(process);
        return process;
    }

    public async Task CrashProxiesAsync(CancellationToken cancellationToken)
    {
        foreach (var process in _processes)
        {
            await process.CrashAsync(cancellationToken);
        }
    }

    public async Task<RecoveryConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        SslStream? tls = null;
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _options.ListenPort, cancellationToken);
            tls = new SslStream(tcp.GetStream(), false,
                (_, certificate, _, errors) => Trust.Validate(certificate, errors, _certificate));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, cancellationToken);
            var connection = new RecoveryConnection(tcp, tls);
            Assert.StartsWith("* OK", await connection.Reader.ReadLineAsync(cancellationToken));
            Assert.Contains("a OK", await connection.CommandAsync("a LOGIN fixture secret", cancellationToken));
            return connection;
        }
        catch
        {
            if (tls is not null)
            {
                await tls.DisposeAsync();
            }

            tcp.Dispose();
            throw;
        }
    }

    public IReadOnlyList<Mapping> ReadJournal()
    {
        var files = Directory.GetFiles(_options.StateDirectory, "*.json");
        if (files.Length == 0)
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<Mapping>>(File.ReadAllText(Assert.Single(files)))!;
    }

    public void AssertSourcePreserved(int expectedMessages)
    {
        Assert.Equal(expectedMessages, Stock.Inbox.Count);
        Assert.Equal(expectedMessages == 1 ? [7u] : new uint[] { 7, 8 }, Stock.Inbox.Keys.Order());
        Assert.All(Stock.Inbox.Values, message => Assert.Equal("\\Seen \\Flagged", message.Flags));
        Assert.DoesNotContain(Stock.Commands, command => command is "EXPUNGE" or "UID STORE" or "UID MOVE" or "DELETE");
    }

    public async ValueTask DisposeAsync()
    {
        // Reap every owned child before releasing synthetic upstream barriers or deleting its state.
        foreach (var process in _processes)
        {
            await process.DisposeAsync();
        }

        _stop.Cancel();
        if (Stock is not null)
        {
            await Stock.DisposeAsync();
        }

        _stop.Dispose();
        _certificate.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}

internal sealed class RecoveryConnection(TcpClient tcp, SslStream tls) : IAsyncDisposable
{
    public StreamReader Reader { get; } = new(tls, Encoding.UTF8, false, 1024, true);

    public async Task<string> CommandAsync(string command, CancellationToken cancellationToken)
    {
        await tls.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), cancellationToken);
        var tag = command.AsSpan(0, command.IndexOf(' ')).ToString();
        var response = new StringBuilder();
        while (await Reader.ReadLineAsync(cancellationToken) is { } line)
        {
            response.Append(line).Append("\r\n");
            if (line.StartsWith(tag + " ", StringComparison.Ordinal))
            {
                break;
            }
        }

        return response.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        Reader.Dispose();
        await tls.DisposeAsync();
        tcp.Dispose();
    }
}
