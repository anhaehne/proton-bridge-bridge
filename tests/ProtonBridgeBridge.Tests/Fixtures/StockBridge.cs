using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public enum CopyCheckpoint
{
    BeforeIntent,
    BeforeRemoteCopy,
    AfterRemoteCopy,
    BeforeJournalCommit
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

    private readonly Dictionary<uint, Message> _labels = [];

    // Optional barriers affect only this synthetic upstream, never the production proxy.
    public Func<CopyCheckpoint, CancellationToken, Task<bool>>? CheckpointAsync { get; set; }

    public int LabelCount => _labels.Count;

    public List<string> Commands { get; } = [];
    public int Copies { get; private set; }
    public uint Validity { get; set; } = 10;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<Task> _sessions = [];
    private Task? _accept;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _accept = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(ct);
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
        {
            Stream stream = tcp.GetStream();
            SslStream? tls = null;
            async Task UpgradeAsync()
            {
                tls = new SslStream(stream, false);
                await tls.AuthenticateAsServerAsync(cert);
                stream = tls;
            }
            async Task SendAsync(string s) => await stream.WriteAsync(Encoding.ASCII.GetBytes(s), ct);

            async Task<bool> ReachCheckpointAsync(CopyCheckpoint checkpoint) =>
                CheckpointAsync is not { } hook || await hook(checkpoint, ct);

            try
            {
                if (!startTls)
                {
                    await UpgradeAsync();
                }

                await SendAsync("* OK stock fixture\r\n");
                var label = false;
                var copiedInSession = false;
                while (!ct.IsCancellationRequested)
                {
                    var args = await new ImapReader(stream).ReadCommandAsync(ct);
                    if (args is null)
                    {
                        break;
                    }

                    var tag = args[0].Text();
                    var verb = args[1].Upper();

                    // Never record credentials or message bodies.
                    Commands.Add(verb);
                    var box = label ? _labels : Inbox;
                    switch (verb)
                    {
                        case "CAPABILITY":
                            await SendAsync("* CAPABILITY IMAP4rev1 UIDPLUS AUTH=PLAIN" + (startTls && tls is null ? " STARTTLS" : "") + "\r\n");
                            break;
                        case "STARTTLS":
                            await SendAsync(tag + " OK begin TLS\r\n");
                            await UpgradeAsync();
                            continue;
                        case "AUTHENTICATE":
                            await SendAsync("+ \r\n");
                            var auth = await new ImapReader(stream).ReadCommandAsync(ct);
                            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth![0].Text()));
                            if (decoded != "\0fixture\0secret")
                            {
                                await SendAsync(tag + " NO invalid credentials\r\n");
                                continue;
                            }
                            break;
                        case "LOGIN":
                            if (args[2].Text() != "fixture" || args[3].Text() != "secret")
                            {
                                await SendAsync(tag + " NO invalid credentials\r\n");
                                continue;
                            }
                            break;
                        case "LIST":
                            await SendAsync("* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\n* LIST (\\HasNoChildren) \"/\" \"Labels/Paperless-imported\"\r\n");
                            break;
                        case "EXAMINE":
                        case "SELECT":
                            if (copiedInSession && label && args[2].Upper() == "INBOX"
                                && !await ReachCheckpointAsync(CopyCheckpoint.BeforeJournalCommit))
                            {
                                return;
                            }

                            label = args[2].Text().StartsWith("Labels/", StringComparison.Ordinal);
                            box = label ? _labels : Inbox;
                            await SendAsync($"* FLAGS (\\Seen \\Flagged)\r\n* {box.Count} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {(label ? 30 : Validity)}] valid\r\n* OK [UIDNEXT {(label ? 100 : 9)}] next\r\n");
                            break;
                        case "FETCH":
                        case "UID":
                            if (verb == "FETCH")
                            {
                                args = args.Take(1).Concat(new ImapValue[] { new TextValue("UID"), new TextValue("FETCH") }).Concat(args.Skip(2)).ToArray();
                            }

                            var op = args[2].Upper();
                            Commands.Add("UID " + op);
                            if (op == "SEARCH")
                            {
                                var index = 3;
                                if (args[index].Upper() == "CHARSET")
                                {
                                    index += 2;
                                }

                                bool Eval(IReadOnlyList<ImapValue> query, ref int i, uint uid)
                                {
                                    var v = query[i++];
                                    if (v is ListValue list)
                                    {
                                        var j = 0;
                                        var r = true;
                                        while (j < list.Values.Count)
                                        {
                                            r &= Eval(list.Values, ref j, uid);
                                        }

                                        return r;
                                    }
                                    switch (v.Upper())
                                    {
                                        case "ALL":
                                            return true;
                                        case "NOT":
                                            return !Eval(query, ref i, uid);
                                        case "OR":
                                            var a = Eval(query, ref i, uid);
                                            var b = Eval(query, ref i, uid);
                                            return a || b;
                                        case "UID":
                                            return Syntax.Uids(query[i++].Text()).Contains(new MailKit.UniqueId(uid));
                                        default:
                                            throw new InvalidOperationException("Unexpected fixture search criterion: " + v.Text());
                                    }
                                }

                                var found = box.Keys.Where(uid =>
                                {
                                    var i = index;
                                    var r = true;
                                    while (i < args.Count)
                                    {
                                        r &= Eval(args, ref i, uid);
                                    }

                                    return r;
                                }).ToArray();
                                await SendAsync("* SEARCH" + (found.Length == 0 ? "" : " " + string.Join(' ', found)) + "\r\n");
                            }
                            else if (op == "FETCH")
                            {
                                var ids = Syntax.Uids(args[3].Text());
                                var attrs = args[4] is ListValue list ? list.Values.Select(v => v.Upper()).ToArray() : [args[4].Upper()];
                                var sequence = 0;
                                foreach (var (uid, message) in box)
                                {
                                    sequence++;
                                    if (!ids.Contains(new MailKit.UniqueId(verb == "FETCH" ? (uint)sequence : uid)))
                                    {
                                        continue;
                                    }

                                    if (attrs.Contains("BODY.PEEK[]"))
                                    {
                                        if (!label && !copiedInSession && !await ReachCheckpointAsync(CopyCheckpoint.BeforeIntent))
                                        {
                                            return;
                                        }

                                        await SendAsync($"* {sequence} FETCH (UID {uid} BODY[] {{{message.Raw.Length}}}\r\n");
                                        await stream.WriteAsync(message.Raw, ct);
                                        await SendAsync(")\r\n");
                                    }
                                    else
                                    {
                                        await SendAsync($"* {sequence} FETCH (UID {uid} FLAGS ({message.Flags}) RFC822.SIZE {message.Raw.Length})\r\n");
                                    }
                                }
                            }
                            else if (op == "COPY")
                            {
                                var uid = uint.Parse(args[3].Text());
                                if (args[4].Text() != "Labels/Paperless-imported")
                                {
                                    throw new InvalidOperationException();
                                }

                                if (!await ReachCheckpointAsync(CopyCheckpoint.BeforeRemoteCopy))
                                {
                                    return;
                                }

                                var dest = 100u + (uint)Copies++;
                                _labels[dest] = Inbox[uid];
                                copiedInSession = true;
                                if (!await ReachCheckpointAsync(CopyCheckpoint.AfterRemoteCopy))
                                {
                                    return;
                                }

                                await SendAsync($"{tag} OK [COPYUID 30 {uid} {dest}] copied\r\n");
                                continue;
                            }
                            else
                            {
                                throw new InvalidOperationException("Unexpected fixture operation: " + op);
                            }

                            break;
                        case "NOOP":
                            break;
                        default:
                            throw new InvalidOperationException("Unexpected fixture command: " + verb);
                    }

                    await SendAsync(tag + (verb is "SELECT" or "EXAMINE" ? " OK [READ-ONLY] complete\r\n" : " OK complete\r\n"));
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
            }
            finally
            {
                if (tls is not null)
                {
                    await tls.DisposeAsync();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        if (_accept is not null)
        {
            await _accept;
        }

        await Task.WhenAll(_sessions);
    }
}
