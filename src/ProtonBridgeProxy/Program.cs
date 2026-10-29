using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ProtonBridgeProxy;

if (args.Length != 2 || args[0] != "--experimental")
{
    Console.Error.WriteLine("Usage: ProtonBridgeProxy --experimental config.json (disposable accounts only; stock Bridge validation pending)");
    return 2;
}
try
{
    var options = JsonSerializer.Deserialize<Options>(File.ReadAllText(args[1])) ?? throw new InvalidDataException();
    options.Validate();
    Directory.CreateDirectory(options.StateDirectory);
    using var stateLock = new FileStream(Path.Combine(options.StateDirectory, "proxy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var certificate = X509Certificate2.CreateFromPemFile(options.Certificate, options.PrivateKey);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var slots = new SemaphoreSlim(options.MaxConnections);
    using var commands = new SemaphoreSlim(1); // Serialize identity reconciliation and COPY across sessions.
    using var listener = new TcpListener(IPAddress.Parse(options.ListenAddress), options.ListenPort);
    listener.Start();
    Console.WriteLine("Experimental proxy listening with implicit TLS. No protocol logging enabled.");
    var active = new HashSet<Task>();
    async Task Serve(TcpClient connection)
    {
        using (connection)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(options.SessionTimeoutSeconds));
            try
            {
                await using var tls = new SslStream(connection.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowRenegotiation = false
                }, timeout.Token);
                await using var bridge = new BridgeMailbox(options);
                await new Session(tls, bridge, options, commands).Run(timeout.Token);
            }
            catch (Exception) { Console.Error.WriteLine("Session closed. Details suppressed to protect credentials and message data."); }
            finally { slots.Release(); }
        }
    }
    try
    {
        while (!stop.IsCancellationRequested)
        {
            await slots.WaitAsync(stop.Token);
            TcpClient connection;
            try { connection = await listener.AcceptTcpClientAsync(stop.Token); }
            catch { slots.Release(); throw; }
            active.RemoveWhere(t => t.IsCompleted);
            active.Add(Serve(connection));
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    await Task.WhenAll(active);
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Proxy startup failed. Check configuration, certificate paths and exclusive state-directory access.");
    return 1;
}
