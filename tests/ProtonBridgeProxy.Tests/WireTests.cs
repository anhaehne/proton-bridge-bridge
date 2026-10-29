using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProtonBridgeProxy;

namespace ProtonBridgeProxy.Tests;

public class WireTests
{
    internal static X509Certificate2 Certificate(string dns = "localhost")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + dns, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(dns); request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    }
    [Fact]
    public void CustomTrustRetainsHostnameAndChainValidation()
    {
        using var cert = Certificate(); using var wrong = Certificate();
        Assert.True(Trust.Validate(cert, SslPolicyErrors.RemoteCertificateChainErrors, cert));
        Assert.False(Trust.Validate(cert, SslPolicyErrors.RemoteCertificateNameMismatch, cert));
        Assert.False(Trust.Validate(cert, SslPolicyErrors.RemoteCertificateChainErrors, wrong));
        Assert.False(Trust.Validate(null, SslPolicyErrors.RemoteCertificateNotAvailable, cert));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMailKitToTlsUpstream_ReconnectCopyAndRepeatedPoll(bool startTls)
    {
        await using var fixture = new Fixture(startTls);
        await fixture.Start();
        await using (var connection = await fixture.Connect())
        {
            Assert.Contains("OK", await connection.Command("a LOGIN fixture secret"));
            var selection = await connection.Command("b SELECT INBOX");
            Assert.True(selection.Contains("READ-WRITE"), fixture.Error?.ToString() ?? selection);
            Assert.Contains("UIDVALIDITY 10", await connection.Command("c STATUS INBOX (UIDVALIDITY)"));
            Assert.Contains("* SEARCH 7 8", await connection.Command("d UID SEARCH CHARSET UTF-8 ((NOT (X-GM-LABELS \"Paperless-imported\") UNKEYWORD Paperless-imported))"));
            var raw = await connection.Command("e UID FETCH 7 (BODY.PEEK[] UID FLAGS RFC822.SIZE)");
            Assert.Contains("application/pdf", raw);
            Assert.Contains("FLAGS (\\Seen \\Flagged)", raw);
            foreach (var command in new[] { "UID STORE 7 +FLAGS (\\Deleted)", "EXPUNGE", "UID MOVE 7 Trash", "UID COPY 7 Trash", "DELETE INBOX", "UID STORE 7 -X-GM-LABELS Paperless-imported", "UID STORE 7 +X-GM-LABELS Other" })
                Assert.Contains("BAD", await connection.Command("f " + command));
        }
        // Paperless applies its action in a distinct worker connection after consumption.
        await using (var action = await fixture.Connect())
        {
            await action.Command("a LOGIN fixture secret"); await action.Command("b SELECT INBOX");
            Assert.Contains("OK", await action.Command("c UID STORE 7 +X-GM-LABELS Paperless-imported"));
            Assert.Contains("OK", await action.Command("d UID STORE 7 +X-GM-LABELS (Paperless-imported)"));
        }
        await using (var poll = await fixture.Connect())
        {
            await poll.Command("a LOGIN fixture secret"); await poll.Command("b SELECT INBOX");
            var search = await poll.Command("c UID SEARCH ((NOT (X-GM-LABELS \"Paperless-imported\") UNKEYWORD Paperless-imported))");
            Assert.Contains("* SEARCH 8\r\n", search);
            Assert.DoesNotContain("* SEARCH 7", search);
            Assert.Contains("X-GM-LABELS (\"Paperless-imported\")", await poll.Command("d UID FETCH 7 (X-GM-LABELS)"));
        }
        Assert.Equal(1, fixture.Stock.Copies);
        Assert.Equal(2, fixture.Stock.Inbox.Count);
        Assert.All(fixture.Stock.Inbox.Values, m => Assert.Equal("\\Seen \\Flagged", m.Flags));
        Assert.DoesNotContain(fixture.Stock.Commands, c => c.Contains("EXPUNGE") || c.Contains("+FLAGS") || c.Contains("MOVE"));
    }
    [Fact]
    public async Task AuthenticationIsValidatedUpstreamAndFailedLoginCannotRead()
    {
        await using var fixture = new Fixture(false); await fixture.Start();
        await using var connection = await fixture.Connect();
        Assert.Contains("NO", await connection.Command("a LOGIN fixture wrong"));
        Assert.Contains("NO", await connection.Command("b SELECT INBOX"));
        Assert.Contains("AUTHENTICATE", fixture.Stock.Commands);
    }
    [Fact]
    public async Task UidValidityChangeFailsExistingSession()
    {
        await using var fixture = new Fixture(false); await fixture.Start();
        await using var connection = await fixture.Connect();
        Assert.Contains("OK", await connection.Command("a LOGIN fixture secret")); Assert.Contains("OK", await connection.Command("b SELECT INBOX"));
        fixture.Stock.Validity = 20;
        // Force a fresh upstream selection to expose the reset.
        await connection.Command("c UID SEARCH ALL");
        Assert.Contains("NO", await connection.Command("d UID FETCH 7 (BODY.PEEK[] UID)"));
        Assert.Equal(0, fixture.Stock.Copies);
    }

    [Fact]
    public async Task SimultaneousWorkersApplyOnlyOneCopy()
    {
        await using var fixture = new Fixture(false); await fixture.Start();
        async Task Worker()
        {
            await using var connection = await fixture.Connect();
            Assert.Contains("OK", await connection.Command("a LOGIN fixture secret"));
            Assert.Contains("OK", await connection.Command("b SELECT INBOX"));
            Assert.Contains("OK", await connection.Command("c UID STORE 7 +X-GM-LABELS Paperless-imported"));
        }
        await Task.WhenAll(Worker(), Worker());
        Assert.Equal(1, fixture.Stock.Copies);
    }
    [Fact]
    public async Task UpstreamHostnameMismatchRejectsBeforeSendingCredentials()
    {
        await using var fixture = new Fixture(false); await fixture.Start();
        await using var mailbox = new BridgeMailbox(fixture.Settings with { BridgeHost = "127.0.0.1" });
        await Assert.ThrowsAnyAsync<Exception>(() => mailbox.Login("fixture", "secret", default));
        Assert.DoesNotContain("AUTHENTICATE", fixture.Stock.Commands);
        Assert.DoesNotContain("LOGIN", fixture.Stock.Commands);
    }
    [PaperlessFact]
    public async Task PinnedImapToolsExecutesPaperlessCommandSequence()
    {
        await using var fixture = new Fixture(true); await fixture.Start();
        var info = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("PAPERLESS_TEST_PYTHON")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "paperless_wire.py"));
        info.ArgumentList.Add(fixture.Port.ToString()); info.ArgumentList.Add(fixture.TrustPath);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.Equal(2, fixture.Stock.Copies);
    }
    private sealed class Fixture(bool startTls) : IAsyncDisposable
    {
        private readonly X509Certificate2 cert = Certificate();
        private readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
        private readonly SemaphoreSlim commands = new(1);
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly List<Task> sessions = [];
        private Task? accept;
        private Options options = null!;
        public StockBridge Stock { get; private set; } = null!;
        public Options Settings => options;
        public Exception? Error { get; set; }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public string TrustPath => Path.Combine(dir, "root.pem");
        public async Task Start()
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "root.pem"), cert.ExportCertificatePem());
            Stock = new StockBridge(cert, startTls, stop.Token); Stock.Start();
            options = new Options
            {
                Certificate = "unused",
                PrivateKey = "unused",
                BridgeHost = "localhost",
                BridgePort = Stock.Port,
                BridgeTrust = Path.Combine(dir, "root.pem"),
                BridgeTls = startTls ? "StartTls" : "SslOnConnect",
                StateDirectory = dir
            };
            listener.Start();
            accept = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                        sessions.Add(Serve(tcp));
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        private async Task Serve(TcpClient tcp)
        {
            using (tcp)
            await using (var tls = new SslStream(tcp.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(cert);
                    await using var bridge = new BridgeMailbox(options);
                    await new Session(tls, new ObservedMailbox(bridge, ex => Error = ex), options, commands).Run(stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException) { }
            }
        }
        public async Task<Connection> Connect()
        {
            var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
            var tls = new SslStream(tcp.GetStream(), false, (_, c, _, e) => Trust.Validate(c, e, cert));
            await tls.AuthenticateAsClientAsync("localhost");
            var connection = new Connection(tcp, tls); await connection.Reader.ReadLineAsync(stop.Token); return connection;
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            if (accept is not null) await accept;
            await Task.WhenAll(sessions); await Stock.DisposeAsync();
            commands.Dispose(); stop.Dispose(); cert.Dispose(); Directory.Delete(dir, true);
        }
    }
    // Diagnostics stay in the test harness; production never logs upstream exception messages.
    private sealed class ObservedMailbox(IBridgeMailbox inner, Action<Exception> error) : IBridgeMailbox
    {
        public bool Authenticated => inner.Authenticated;
        private async Task<T> Observe<T>(Func<Task<T>> action) { try { return await action(); } catch (Exception e) { error(e); throw; } }
        public Task Login(string user, string password, CancellationToken ct) => inner.Login(user, password, ct);
        public Task<InboxState> Select(CancellationToken ct) => Observe(() => inner.Select(ct));
        public Task<HashSet<uint>> Processed(CancellationToken ct) => Observe(() => inner.Processed(ct));
        public Task<IList<MailKit.UniqueId>> Search(MailKit.Search.SearchQuery query, CancellationToken ct) => Observe(() => inner.Search(query, ct));
        public Task<byte[]> Read(uint uid, CancellationToken ct) => Observe(() => inner.Read(uid, ct));
        public Task AddLabel(uint uid, CancellationToken ct) => inner.AddLabel(uid, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Connection(TcpClient tcp, SslStream tls) : IAsyncDisposable
    {
        public StreamReader Reader { get; } = new(tls, Encoding.UTF8, false, 1024, true);
        public async Task<string> Command(string command)
        {
            await tls.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"));
            var result = new StringBuilder(); string? line;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string tag = command.Split(' ')[0];
            while ((line = await Reader.ReadLineAsync(timeout.Token)) is not null)
            { result.Append(line).Append("\r\n"); if (line.StartsWith(tag + " ", StringComparison.Ordinal)) break; }
            return result.ToString();
        }
        public ValueTask DisposeAsync() { Reader.Dispose(); tls.Dispose(); tcp.Dispose(); return ValueTask.CompletedTask; }
    }
}

// Deterministic stock-like fixture, NOT a substitute for official Bridge validation.
internal sealed class StockBridge(X509Certificate2 cert, bool startTls, CancellationToken ct) : IAsyncDisposable
{
    public sealed record Message(byte[] Raw, string Flags);
    public Dictionary<uint, Message> Inbox { get; } = new()
    {
        [7] = new(Encoding.ASCII.GetBytes("Message-ID: <same@test>\r\nContent-Type: application/pdf\r\nContent-Disposition: inline; filename=a.pdf\r\n\r\n%PDF-fixture\r\n"), "\\Seen \\Flagged"),
        [8] = new(Encoding.ASCII.GetBytes("Message-ID: <same@test>\r\nContent-Type: application/pdf\r\nContent-Disposition: inline; filename=a.pdf\r\n\r\n%PDF-fixture\r\n"), "\\Seen \\Flagged")
    };
    private readonly Dictionary<uint, Message> labels = [];
    public List<string> Commands { get; } = [];
    public int Copies { get; private set; }
    public uint Validity { get; set; } = 10;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly List<Task> sessions = [];
    private Task? accept;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start()
    {
        listener.Start();
        accept = Task.Run(async () =>
        {
            try { while (!ct.IsCancellationRequested) { var tcp = await listener.AcceptTcpClientAsync(ct); sessions.Add(Serve(tcp)); } }
            catch (OperationCanceledException) { }
        });
    }
    private async Task Serve(TcpClient tcp)
    {
        using (tcp)
        {
            Stream stream = tcp.GetStream();
            SslStream? tls = null;
            async Task Upgrade() { tls = new SslStream(stream, false); await tls.AuthenticateAsServerAsync(cert); stream = tls; }
            async Task Send(string s) => await stream.WriteAsync(Encoding.ASCII.GetBytes(s), ct);
            try
            {
                if (!startTls) await Upgrade();
                await Send("* OK stock fixture\r\n");
                bool label = false;
                while (!ct.IsCancellationRequested)
                {
                    var args = await new ImapReader(stream).ReadCommand(ct); if (args is null) break;
                    var tag = args[0].Text(); var verb = args[1].Upper();
                    Commands.Add(verb); // Never record credentials or message bodies.
                    var box = label ? labels : Inbox;
                    switch (verb)
                    {
                        case "CAPABILITY": await Send("* CAPABILITY IMAP4rev1 UIDPLUS AUTH=PLAIN" + (startTls && tls is null ? " STARTTLS" : "") + "\r\n"); break;
                        case "STARTTLS": await Send(tag + " OK begin TLS\r\n"); await Upgrade(); continue;
                        case "AUTHENTICATE":
                            await Send("+ \r\n");
                            var auth = await new ImapReader(stream).ReadCommand(ct);
                            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth![0].Text()));
                            if (decoded != "\0fixture\0secret") { await Send(tag + " NO invalid credentials\r\n"); continue; }
                            break;
                        case "LOGIN":
                            if (args[2].Text() != "fixture" || args[3].Text() != "secret") { await Send(tag + " NO invalid credentials\r\n"); continue; }
                            break;
                        case "LIST":
                            await Send("* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\n* LIST (\\HasNoChildren) \"/\" \"Labels/Paperless-imported\"\r\n"); break;
                        case "EXAMINE":
                        case "SELECT":
                            label = args[2].Text().StartsWith("Labels/", StringComparison.Ordinal);
                            box = label ? labels : Inbox;
                            await Send($"* FLAGS (\\Seen \\Flagged)\r\n* {box.Count} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {(label ? 30 : Validity)}] valid\r\n* OK [UIDNEXT {(label ? 100 : 9)}] next\r\n"); break;
                        case "FETCH":
                        case "UID":
                            if (verb == "FETCH") args = args.Take(1).Concat(new ImapValue[] { new TextValue("UID"), new TextValue("FETCH") }).Concat(args.Skip(2)).ToArray();
                            var op = args[2].Upper(); Commands.Add("UID " + op);
                            if (op == "SEARCH")
                            {
                                int index = 3;
                                if (args[index].Upper() == "CHARSET") index += 2;
                                bool Eval(IReadOnlyList<ImapValue> query, ref int i, uint uid)
                                {
                                    var v = query[i++];
                                    if (v is ListValue list) { int j = 0; bool r = true; while (j < list.Values.Count) r &= Eval(list.Values, ref j, uid); return r; }
                                    switch (v.Upper())
                                    {
                                        case "ALL": return true;
                                        case "NOT": return !Eval(query, ref i, uid);
                                        case "OR": var a = Eval(query, ref i, uid); var b = Eval(query, ref i, uid); return a || b;
                                        case "UID": return Syntax.Uids(query[i++].Text()).Contains(new MailKit.UniqueId(uid));
                                        default: throw new InvalidOperationException("Unexpected fixture search criterion: " + v.Text());
                                    }
                                }
                                var found = box.Keys.Where(uid => { int i = index; bool r = true; while (i < args.Count) r &= Eval(args, ref i, uid); return r; }).ToArray();
                                await Send("* SEARCH" + (found.Length == 0 ? "" : " " + string.Join(' ', found)) + "\r\n");
                            }
                            else if (op == "FETCH")
                            {
                                var ids = Syntax.Uids(args[3].Text());
                                var attrs = args[4] is ListValue list ? list.Values.Select(v => v.Upper()).ToArray() : [args[4].Upper()];
                                int sequence = 0;
                                foreach (var (uid, message) in box)
                                {
                                    sequence++; if (!ids.Contains(new MailKit.UniqueId(verb == "FETCH" ? (uint)sequence : uid))) continue;
                                    if (attrs.Contains("BODY.PEEK[]"))
                                    {
                                        await Send($"* {sequence} FETCH (UID {uid} BODY[] {{{message.Raw.Length}}}\r\n"); await stream.WriteAsync(message.Raw, ct); await Send(")\r\n");
                                    }
                                    else await Send($"* {sequence} FETCH (UID {uid} FLAGS ({message.Flags}) RFC822.SIZE {message.Raw.Length})\r\n");
                                }
                            }
                            else if (op == "COPY")
                            {
                                uint uid = uint.Parse(args[3].Text());
                                if (args[4].Text() != "Labels/Paperless-imported") throw new InvalidOperationException();
                                uint dest = 100u + (uint)Copies++; labels[dest] = Inbox[uid];
                                await Send($"{tag} OK [COPYUID 30 {uid} {dest}] copied\r\n"); continue;
                            }
                            else throw new InvalidOperationException("Unexpected fixture operation: " + op);
                            break;
                        case "NOOP": break;
                        default: throw new InvalidOperationException("Unexpected fixture command: " + verb);
                    }
                    await Send(tag + (verb is "SELECT" or "EXAMINE" ? " OK [READ-ONLY] complete\r\n" : " OK complete\r\n"));
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
            finally { if (tls is not null) await tls.DisposeAsync(); }
        }
    }
    public async ValueTask DisposeAsync() { listener.Stop(); if (accept is not null) await accept; await Task.WhenAll(sessions); }
}

public sealed class PaperlessFactAttribute : FactAttribute
{
    public PaperlessFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAPERLESS_TEST_PYTHON")))
            Skip = "Set PAPERLESS_TEST_PYTHON to Python with tests/requirements.txt installed to exercise Paperless's pinned imap-tools.";
    }
}
