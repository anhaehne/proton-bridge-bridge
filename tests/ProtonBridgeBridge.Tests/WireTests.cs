using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class WireTests
{
    internal static X509Certificate2 Certificate(string dns = "localhost")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + dns, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    }

    [Fact]
    public void CustomTrustRetainsHostnameAndChainValidation()
    {
        using var cert = Certificate();
        using var wrong = Certificate();
        Assert.True(Trust.Validate(cert, SslPolicyErrors.RemoteCertificateChainErrors, cert));
        Assert.False(Trust.Validate(cert, SslPolicyErrors.RemoteCertificateNameMismatch, cert));
        Assert.False(Trust.Validate(cert, SslPolicyErrors.RemoteCertificateChainErrors, wrong));
        Assert.False(Trust.Validate(null, SslPolicyErrors.RemoteCertificateNotAvailable, cert));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMailKitToTlsUpstream_ReconnectCopyAndRepeatedPollAsync(bool startTls)
    {
        await using var fixture = new Fixture(startTls);
        await fixture.StartAsync();
        await using (var connection = await fixture.ConnectAsync())
        {
            Assert.Contains("OK", await connection.CommandAsync("a LOGIN fixture secret"));
            var selection = await connection.CommandAsync("b SELECT INBOX");
            Assert.True(selection.Contains("READ-WRITE"), fixture.Error?.ToString() ?? selection);
            Assert.Contains("UIDVALIDITY 10", await connection.CommandAsync("c STATUS INBOX (UIDVALIDITY)"));
            Assert.Contains("* SEARCH 7 8", await connection.CommandAsync("d UID SEARCH CHARSET UTF-8 ((NOT (X-GM-LABELS \"Paperless-imported\") UNKEYWORD Paperless-imported))"));
            var raw = await connection.CommandAsync("e UID FETCH 7 (BODY.PEEK[] UID FLAGS RFC822.SIZE)");
            Assert.Contains("application/pdf", raw);
            Assert.Contains("FLAGS (\\Seen \\Flagged)", raw);
            foreach (var command in new[] { "UID STORE 7 +FLAGS (\\Deleted)", "EXPUNGE", "UID MOVE 7 Trash", "UID COPY 7 Trash", "DELETE INBOX", "UID STORE 7 -X-GM-LABELS Paperless-imported", "UID STORE 7 +X-GM-LABELS Other" })
            {
                Assert.Contains("BAD", await connection.CommandAsync("f " + command));
            }
        }

        // Paperless applies its action in a distinct worker connection after consumption.
        await using (var action = await fixture.ConnectAsync())
        {
            await action.CommandAsync("a LOGIN fixture secret");
            await action.CommandAsync("b SELECT INBOX");
            Assert.Contains("OK", await action.CommandAsync("c UID STORE 7 +X-GM-LABELS Paperless-imported"));
            Assert.Contains("OK", await action.CommandAsync("d UID STORE 7 +X-GM-LABELS (Paperless-imported)"));
        }

        await using (var poll = await fixture.ConnectAsync())
        {
            await poll.CommandAsync("a LOGIN fixture secret");
            await poll.CommandAsync("b SELECT INBOX");
            var search = await poll.CommandAsync("c UID SEARCH ((NOT (X-GM-LABELS \"Paperless-imported\") UNKEYWORD Paperless-imported))");
            Assert.Contains("* SEARCH 8\r\n", search);
            Assert.DoesNotContain("* SEARCH 7", search);
            Assert.Contains("X-GM-LABELS (\"Paperless-imported\")", await poll.CommandAsync("d UID FETCH 7 (X-GM-LABELS)"));
        }

        Assert.Equal(1, fixture.Stock.Copies);
        Assert.Equal(2, fixture.Stock.Inbox.Count);
        Assert.All(fixture.Stock.Inbox.Values, m => Assert.Equal("\\Seen \\Flagged", m.Flags));
        Assert.DoesNotContain(fixture.Stock.Commands, c => c.Contains("EXPUNGE") || c.Contains("+FLAGS") || c.Contains("MOVE"));
    }

    [Fact]
    public async Task AuthenticationIsValidatedUpstreamAndFailedLoginCannotReadAsync()
    {
        await using var fixture = new Fixture(false);
        await fixture.StartAsync();
        await using var connection = await fixture.ConnectAsync();
        Assert.Contains("NO", await connection.CommandAsync("a LOGIN fixture wrong"));
        Assert.Contains("NO", await connection.CommandAsync("b SELECT INBOX"));
        Assert.Contains("AUTHENTICATE", fixture.Stock.Commands);
    }

    [Fact]
    public async Task UidValidityChangeFailsExistingSessionAsync()
    {
        await using var fixture = new Fixture(false);
        await fixture.StartAsync();
        await using var connection = await fixture.ConnectAsync();
        Assert.Contains("OK", await connection.CommandAsync("a LOGIN fixture secret"));
        Assert.Contains("OK", await connection.CommandAsync("b SELECT INBOX"));
        fixture.Stock.Validity = 20;

        // Force a fresh upstream selection to expose the reset.
        await connection.CommandAsync("c UID SEARCH ALL");
        Assert.Contains("NO", await connection.CommandAsync("d UID FETCH 7 (BODY.PEEK[] UID)"));
        Assert.Equal(0, fixture.Stock.Copies);
    }

    [Fact]
    public async Task SimultaneousWorkersApplyOnlyOneCopyAsync()
    {
        await using var fixture = new Fixture(false);
        await fixture.StartAsync();
        async Task WorkerAsync()
        {
            await using var connection = await fixture.ConnectAsync();
            Assert.Contains("OK", await connection.CommandAsync("a LOGIN fixture secret"));
            Assert.Contains("OK", await connection.CommandAsync("b SELECT INBOX"));
            Assert.Contains("OK", await connection.CommandAsync("c UID STORE 7 +X-GM-LABELS Paperless-imported"));
        }

        await Task.WhenAll(WorkerAsync(), WorkerAsync());
        Assert.Equal(1, fixture.Stock.Copies);
    }

    [Fact]
    public async Task UpstreamHostnameMismatchRejectsBeforeSendingCredentialsAsync()
    {
        await using var fixture = new Fixture(false);
        await fixture.StartAsync();
        await using var mailbox = new BridgeMailbox(fixture.Settings with
        {
            BridgeHost = "127.0.0.1"
        });
        await Assert.ThrowsAnyAsync<Exception>(() => mailbox.LoginAsync("fixture", "secret", default));
        Assert.DoesNotContain("AUTHENTICATE", fixture.Stock.Commands);
        Assert.DoesNotContain("LOGIN", fixture.Stock.Commands);
    }

    [PaperlessFact]
    public async Task PinnedImapToolsExecutesPaperlessCommandSequenceAsync()
    {
        await using var fixture = new Fixture(true);
        await fixture.StartAsync();
        var info = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("PAPERLESS_TEST_PYTHON")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "paperless_wire.py"));
        info.ArgumentList.Add(fixture.Port.ToString());
        info.ArgumentList.Add(fixture.TrustPath);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            process.Kill(true);
            throw;
        }

        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.Equal(2, fixture.Stock.Copies);
    }

    private sealed class Fixture(bool startTls) : IAsyncDisposable
    {
        private readonly X509Certificate2 _cert = Certificate();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly SemaphoreSlim _commands = new(1);
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Task> _sessions = [];
        private Task? _accept;
        private Options _options = null!;

        public StockBridge Stock { get; private set; } = null!;

        public Options Settings => _options;

        public Exception? Error { get; set; }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string TrustPath => Path.Combine(_dir, "root.pem");

        public async Task StartAsync()
        {
            Directory.CreateDirectory(_dir);
            await File.WriteAllTextAsync(Path.Combine(_dir, "root.pem"), _cert.ExportCertificatePem());
            Stock = new StockBridge(_cert, startTls, _stop.Token);
            Stock.Start();
            _options = new Options
            {
                Certificate = "unused",
                PrivateKey = "unused",
                BridgeHost = "localhost",
                BridgePort = Stock.Port,
                BridgeTrust = Path.Combine(_dir, "root.pem"),
                BridgeTls = startTls ? "StartTls" : "SslOnConnect",
                AllowLabelWrites = true,
                StateDirectory = _dir
            };
            _listener.Start();
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _sessions.Add(ServeAsync(tcp));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using (tcp)
            await using (var tls = new SslStream(tcp.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(_cert);
                    await using var bridge = new BridgeMailbox(_options);
                    await new Session(tls, new ObservedMailbox(bridge, ex => Error = ex), _options, _commands).RunAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                }
            }
        }

        public async Task<Connection> ConnectAsync()
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, _stop.Token);
            var tls = new SslStream(tcp.GetStream(), false, (_, c, _, e) => Trust.Validate(c, e, _cert));
            await tls.AuthenticateAsClientAsync("localhost");
            var connection = new Connection(tcp, tls);
            await connection.Reader.ReadLineAsync(_stop.Token);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            if (_accept is not null)
            {
                await _accept;
            }

            await Task.WhenAll(_sessions);
            await Stock.DisposeAsync();
            _commands.Dispose();
            _stop.Dispose();
            _cert.Dispose();
            Directory.Delete(_dir, true);
        }
    }

    // Diagnostics stay in the test harness; production never logs upstream exception messages.
    private sealed class ObservedMailbox(IBridgeMailbox inner, Action<Exception> error) : IBridgeMailbox
    {
        public bool Authenticated => inner.Authenticated;

        private async Task<T> ObserveAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return await action();
            }
            catch (Exception e)
            {
                error(e);
                throw;
            }
        }

        public Task LoginAsync(string user, string password, CancellationToken ct) => inner.LoginAsync(user, password, ct);

        public Task<InboxState> SelectAsync(CancellationToken ct) => ObserveAsync(() => inner.SelectAsync(ct));

        public Task<HashSet<uint>> ProcessedAsync(CancellationToken ct) => ObserveAsync(() => inner.ProcessedAsync(ct));

        public Task<IList<MailKit.UniqueId>> SearchAsync(MailKit.Search.SearchQuery query, CancellationToken ct) => ObserveAsync(() => inner.SearchAsync(query, ct));

        public Task<byte[]> ReadAsync(uint uid, CancellationToken ct) => ObserveAsync(() => inner.ReadAsync(uid, ct));

        public Task AddLabelAsync(uint uid, CancellationToken ct) => inner.AddLabelAsync(uid, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Connection(TcpClient tcp, SslStream tls) : IAsyncDisposable
    {
        public StreamReader Reader { get; } = new(tls, Encoding.UTF8, false, 1024, true);

        public async Task<string> CommandAsync(string command)
        {
            await tls.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"));
            var result = new StringBuilder();
            string? line;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tag = command.Split(' ')[0];
            while ((line = await Reader.ReadLineAsync(timeout.Token)) is not null)
            {
                result.Append(line).Append("\r\n");
                if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                {
                    break;
                }
            }

            return result.ToString();
        }

        public ValueTask DisposeAsync()
        {
            Reader.Dispose();
            tls.Dispose();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class PaperlessFactAttribute : FactAttribute
{
    public PaperlessFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAPERLESS_TEST_PYTHON")))
        {
            Skip = "Set PAPERLESS_TEST_PYTHON to Python with tests/requirements.txt installed to exercise Paperless's pinned imap-tools.";
        }
    }
}
