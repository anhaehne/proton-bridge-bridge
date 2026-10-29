using System.Text;
using MailKit;

namespace ProtonBridgeProxy;

public sealed class Session(Stream stream, IBridgeMailbox bridge, Options options, SemaphoreSlim commandLock)
{
    private const string Capabilities = "IMAP4rev1 AUTH=PLAIN X-GM-EXT-1";
    private InboxState? selected;
    private Task Write(string value, CancellationToken ct) => stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct).AsTask();
    private static string Flags(MessageFlags flags) => string.Join(' ', new[] {
        (MessageFlags.Seen, "\\Seen"), (MessageFlags.Flagged, "\\Flagged"), (MessageFlags.Answered, "\\Answered"),
        (MessageFlags.Deleted, "\\Deleted"), (MessageFlags.Draft, "\\Draft"), (MessageFlags.Recent, "\\Recent")
    }.Where(f => flags.HasFlag(f.Item1)).Select(f => f.Item2));
    private void RequireSelected() { if (!bridge.Authenticated || selected is null) throw new InvalidOperationException(); }
    public async Task Run(CancellationToken ct)
    {
        var reader = new ImapReader(stream);
        await Write("* OK [CAPABILITY " + Capabilities + "] Proton Bridge compatibility prototype\r\n", ct);
        while (!ct.IsCancellationRequested)
        {
            var values = await reader.ReadCommand(ct);
            if (values is null) return;
            if (values.Count < 2) throw new ImapSyntaxException();
            string tag = values[0].Text();
            if (tag.Length > 64 || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) throw new ImapSyntaxException();
            string command = values[1].Upper();
            var args = values.Skip(2).ToArray();
            await commandLock.WaitAsync(ct);
            try
            {
                switch (command)
                {
                    case "CAPABILITY":
                        args.Count(0); await Write("* CAPABILITY " + Capabilities + "\r\n", ct); break;
                    case "LOGIN":
                        args.Count(2); await bridge.Login(args[0].Text(), args[1].Text(), ct); break;
                    case "AUTHENTICATE":
                        args.Count(1);
                        if (args[0].Upper() != "PLAIN") throw new ImapSyntaxException();
                        await Write("+ \r\n", ct);
                        var auth = await reader.ReadCommand(ct) ?? throw new ImapSyntaxException();
                        auth.Count(1);
                        var fields = Encoding.UTF8.GetString(Convert.FromBase64String(auth[0].Text())).Split('\0');
                        if (fields.Length != 3 || (fields[0].Length != 0 && fields[0] != fields[1])) throw new ImapSyntaxException();
                        await bridge.Login(fields[1], fields[2], ct); break;
                    case "SELECT":
                    case "EXAMINE":
                        args.Count(1);
                        if (!bridge.Authenticated || !args[0].Text().Equals("INBOX", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                        selected = await bridge.Select(ct);
                        await Write($"* FLAGS (\\Seen \\Flagged \\Answered \\Deleted \\Draft)\r\n* OK [PERMANENTFLAGS ()] Flags protected\r\n* {selected.Messages.Count} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {selected.Validity}] UIDs valid\r\n* OK [UIDNEXT {selected.NextUid}] Next UID\r\n", ct);
                        break;
                    case "STATUS":
                        if (!bridge.Authenticated || args.Length != 2 || args[0].Upper() != "INBOX" || args[1] is not ListValue status || status.Values.Any(v => v.Upper() is not ("UIDVALIDITY" or "UIDNEXT" or "MESSAGES"))) throw new ImapSyntaxException();
                        var state = await bridge.Select(ct);
                        await Write("* STATUS INBOX (" + string.Join(' ', status.Values.Select(v => v.Upper() switch
                        {
                            "UIDVALIDITY" => $"UIDVALIDITY {state.Validity}",
                            "UIDNEXT" => $"UIDNEXT {state.NextUid}",
                            _ => $"MESSAGES {state.Messages.Count}"
                        })) + ")\r\n", ct); break;
                    case "LIST":
                    case "LSUB":
                        if (!bridge.Authenticated) throw new InvalidOperationException();
                        args.Count(2); await Write($"* {command} (\\HasNoChildren) \"/\" \"INBOX\"\r\n", ct); break;
                    case "UID":
                        RequireSelected();
                        if (args.Length < 2) throw new ImapSyntaxException();
                        await Uid(args[0].Upper(), args.Skip(1).ToArray(), ct); break;
                    case "NOOP": args.Count(0); break;
                    case "CLOSE":
                    case "UNSELECT":
                        args.Count(0); RequireSelected(); selected = null; break; // No upstream CLOSE/EXPUNGE.
                    case "LOGOUT":
                        args.Count(0); await Write($"* BYE Closing\r\n{tag} OK LOGOUT complete\r\n", ct); return;
                    default: throw new ImapSyntaxException();
                }
                await Write($"{tag} OK{(command == "SELECT" ? " [READ-WRITE]" : command == "EXAMINE" ? " [READ-ONLY]" : "")} {command} complete\r\n", ct);
            }
            catch (ImapSyntaxException) { await Write($"{tag} BAD Unsupported or invalid command\r\n", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not IOException)
            {
                // No exception messages: upstream exceptions may contain addresses or protocol data.
                await Write($"{tag} NO Operation failed; check trust, authentication, label and identity configuration\r\n", ct);
            }
            finally { commandLock.Release(); }
        }
    }
    private async Task Uid(string command, IReadOnlyList<ImapValue> args, CancellationToken ct)
    {
        // Do not silently act on UIDs after a mailbox reset in an existing session.
        var current = await bridge.Select(ct);
        if (current.Validity != selected!.Validity || !current.Messages.Select(m => m.UniqueId).SequenceEqual(selected.Messages.Select(m => m.UniqueId))) throw new IdentityException();
        if (command == "SEARCH")
        {
            var criteria = args;
            if (criteria[0] is TextValue charset && charset.Text.Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
            {
                if (criteria.Count < 3 || criteria[1].Upper() is not ("UTF-8" or "US-ASCII")) throw new ImapSyntaxException();
                criteria = criteria.Skip(2).ToArray();
            }
            var processed = await bridge.Processed(ct);
            var query = new SearchParser(options.Label, processed, selected.Messages.Select(m => m.UniqueId.Id).DefaultIfEmpty(1u).Max()).Parse(criteria);
            var uids = await bridge.Search(query, ct);
            await Write("* SEARCH" + (uids.Count == 0 ? "" : " " + string.Join(' ', uids.Select(u => u.Id))) + "\r\n", ct);
            return;
        }
        var set = Syntax.Uids(args[0].Text(), selected.Messages.Select(m => m.UniqueId.Id).DefaultIfEmpty(1u).Max());
        var messages = selected.Messages.Where(m => set.Contains(m.UniqueId)).ToArray();
        if (command == "STORE")
        {
            args.Count(3);
            if (args[1].Upper() is not ("+X-GM-LABELS" or "+X-GM-LABELS.SILENT")) throw new ImapSyntaxException();
            string label = args[2] is ListValue list && list.Values.Count == 1 ? list.Values[0].Text() : args[2].Text();
            if (label != options.Label) throw new ImapSyntaxException();
            foreach (var message in messages) await bridge.AddLabel(message.UniqueId.Id, ct);
            return;
        }
        if (command != "FETCH") throw new ImapSyntaxException();
        args.Count(2);
        var attributes = args[1] is ListValue items ? items.Values.Select(v => v.Upper()).ToArray() : [args[1].Upper()];
        if (attributes.Length == 0 || attributes.Any(a => a is not ("UID" or "FLAGS" or "RFC822.SIZE" or "BODY.PEEK[]" or "X-GM-LABELS"))) throw new ImapSyntaxException();
        var processedLabels = attributes.Contains("X-GM-LABELS") ? await bridge.Processed(ct) : [];
        foreach (var message in messages)
        {
            if (!current.Messages.Any(m => m.UniqueId == message.UniqueId)) throw new IdentityException();
            // Session sequence numbers remain stable until reconnect. Disappearing messages fail closed.
            int index = selected.Messages.ToList().IndexOf(message) + 1;
            string fields = $"UID {message.UniqueId.Id}";
            if (attributes.Contains("FLAGS")) fields += $" FLAGS ({Flags(current.Messages.Single(m => m.UniqueId == message.UniqueId).Flags ?? MessageFlags.None)})";
            if (attributes.Contains("RFC822.SIZE")) fields += $" RFC822.SIZE {message.Size ?? 0}";
            if (attributes.Contains("X-GM-LABELS")) fields += $" X-GM-LABELS ({(processedLabels.Contains(message.UniqueId.Id) ? Syntax.Quote(options.Label) : "")})";
            if (attributes.Contains("BODY.PEEK[]"))
            {
                var raw = await bridge.Read(message.UniqueId.Id, ct);
                await Write($"* {index} FETCH ({fields} BODY[] {{{raw.Length}}}\r\n", ct);
                await stream.WriteAsync(raw, ct); await Write(")\r\n", ct);
            }
            else await Write($"* {index} FETCH ({fields})\r\n", ct);
        }
    }
}
