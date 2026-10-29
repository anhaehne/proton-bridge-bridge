using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ProtonBridgeProxy;

public sealed class ProxyListenerState
{
    private int _ready;

    public bool Ready => Volatile.Read(ref _ready) == 1;

    public void SetReady(bool ready) => Volatile.Write(ref _ready, ready ? 1 : 0);
}

public sealed class ProxyListenerService(Options options, ProxyListenerState state) : BackgroundService
{
    private readonly SemaphoreSlim _slots = new(options.MaxConnections);
    private readonly SemaphoreSlim _commands = new(1);
    private readonly HashSet<Task> _sessions = [];
    private TcpListener? _listener;
    private X509Certificate2? _certificate;
    private FileStream? _stateLock;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Complete startup checks before Kestrel can report the application as ready.
        Directory.CreateDirectory(options.StateDirectory);
        _stateLock = new FileStream(
            Path.Combine(options.StateDirectory, "proxy.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        _certificate = X509Certificate2.CreateFromPemFile(options.Certificate, options.PrivateKey);
        _listener = new TcpListener(IPAddress.Parse(options.ListenAddress), options.ListenPort);
        _listener.Start();
        state.SetReady(true);

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _slots.WaitAsync(stoppingToken);
                TcpClient connection;
                try
                {
                    connection = await _listener!.AcceptTcpClientAsync(stoppingToken);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }

                _sessions.RemoveWhere(task => task.IsCompleted);
                _sessions.Add(ServeAsync(connection, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            state.SetReady(false);
            _listener?.Stop();
            await Task.WhenAll(_sessions);
        }
    }

    private async Task ServeAsync(TcpClient connection, CancellationToken stoppingToken)
    {
        using (connection)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(options.SessionTimeoutSeconds));
            try
            {
                await using var tls = new SslStream(connection.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowRenegotiation = false
                }, timeout.Token);

                await using var bridge = new BridgeMailbox(options);
                await new Session(tls, bridge, options, _commands).RunAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                Console.Error.WriteLine("Session closed. Details suppressed to protect credentials and message data.");
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        state.SetReady(false);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        state.SetReady(false);
        _listener?.Stop();
        _certificate?.Dispose();
        _stateLock?.Dispose();
        _slots.Dispose();
        _commands.Dispose();
    }
}
