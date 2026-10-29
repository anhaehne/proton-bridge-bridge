using System.Text;
using MailKit;

namespace ProtonBridgeBridge;

public sealed class LabelWriteDeniedException : Exception;

public sealed class Session(Stream stream, IBridgeMailbox bridge, Options options, SemaphoreSlim commandLock)
{
    private const string Capabilities = "IMAP4rev1 AUTH=PLAIN X-GM-EXT-1";
    private InboxState? _selected;
    private bool _readOnly;

    private Task WriteAsync(string value, CancellationToken ct) => stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct).AsTask();

    private static string Flags(MessageFlags flags) => string.Join(' ', new[]
    {
        (MessageFlags.Seen, "\\Seen"),
        (MessageFlags.Flagged, "\\Flagged"),
        (MessageFlags.Answered, "\\Answered"),
        (MessageFlags.Deleted, "\\Deleted"),
        (MessageFlags.Draft, "\\Draft"),
        (MessageFlags.Recent, "\\Recent")
    }.Where(f => flags.HasFlag(f.Item1)).Select(f => f.Item2));

    private void RequireSelected()
    {
        if (!bridge.Authenticated || _selected is null)
        {
            throw new InvalidOperationException();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var reader = new ImapReader(stream);
        await WriteAsync("* OK [CAPABILITY " + Capabilities + "] ProtonBridgeBridge experimental IMAP proxy\r\n", ct);
        while (!ct.IsCancellationRequested)
        {
            var values = await reader.ReadCommandAsync(ct);
            if (values is null)
            {
                return;
            }

            if (values.Count < 2)
            {
                throw new ImapSyntaxException();
            }

            var tag = values[0].Text();
            if (tag.Length > 64 || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            {
                throw new ImapSyntaxException();
            }

            var command = values[1].Upper();
            var args = values.Skip(2).ToArray();
            await commandLock.WaitAsync(ct);
            try
            {
                switch (command)
                {
                    case "CAPABILITY":
                        args.Count(0);
                        await WriteAsync("* CAPABILITY " + Capabilities + "\r\n", ct);
                        break;
                    case "LOGIN":
                        args.Count(2);
                        await bridge.LoginAsync(args[0].Text(), args[1].Text(), ct);
                        break;
                    case "AUTHENTICATE":
                        args.Count(1);
                        if (args[0].Upper() != "PLAIN")
                        {
                            throw new ImapSyntaxException();
                        }

                        await WriteAsync("+ \r\n", ct);
                        var auth = await reader.ReadCommandAsync(ct) ?? throw new ImapSyntaxException();
                        auth.Count(1);
                        var fields = Encoding.UTF8.GetString(Convert.FromBase64String(auth[0].Text())).Split('\0');
                        if (fields.Length != 3 || (fields[0].Length != 0 && fields[0] != fields[1]))
                        {
                            throw new ImapSyntaxException();
                        }

                        await bridge.LoginAsync(fields[1], fields[2], ct);
                        break;
                    case "SELECT":
                    case "EXAMINE":
                        args.Count(1);
                        if (!bridge.Authenticated || !args[0].Text().Equals("INBOX", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException();
                        }

                        _selected = await bridge.SelectAsync(ct);
                        _readOnly = command == "EXAMINE";
                        await WriteAsync($"* FLAGS (\\Seen \\Flagged \\Answered \\Deleted \\Draft)\r\n* OK [PERMANENTFLAGS ()] Flags protected\r\n* {_selected.Messages.Count} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {_selected.Validity}] UIDs valid\r\n* OK [UIDNEXT {_selected.NextUid}] Next UID\r\n", ct);
                        break;
                    case "STATUS":
                        if (!bridge.Authenticated
                            || args.Length != 2
                            || args[0].Upper() != "INBOX"
                            || args[1] is not ListValue status
                            || status.Values.Any(v => v.Upper() is not ("UIDVALIDITY" or "UIDNEXT" or "MESSAGES")))
                        {
                            throw new ImapSyntaxException();
                        }

                        var state = await bridge.SelectAsync(ct);
                        await WriteAsync("* STATUS INBOX (" + string.Join(' ', status.Values.Select(v => v.Upper() switch
                        {
                            "UIDVALIDITY" => $"UIDVALIDITY {state.Validity}",
                            "UIDNEXT" => $"UIDNEXT {state.NextUid}",
                            _ => $"MESSAGES {state.Messages.Count}"
                        })) + ")\r\n", ct);
                        break;
                    case "LIST":
                    case "LSUB":
                        if (!bridge.Authenticated)
                        {
                            throw new InvalidOperationException();
                        }

                        args.Count(2);
                        await WriteAsync($"* {command} (\\HasNoChildren) \"/\" \"INBOX\"\r\n", ct);
                        break;
                    case "UID":
                        RequireSelected();
                        if (args.Length < 2)
                        {
                            throw new ImapSyntaxException();
                        }

                        await UidAsync(args[0].Upper(), args.Skip(1).ToArray(), ct);
                        break;
                    case "NOOP":
                        args.Count(0);
                        break;
                    case "CLOSE":
                    case "UNSELECT":
                        args.Count(0);
                        RequireSelected();
                        // Release only local selection; never send upstream CLOSE or EXPUNGE.
                        _selected = null;
                        break;
                    case "LOGOUT":
                        args.Count(0);
                        await WriteAsync($"* BYE Closing\r\n{tag} OK LOGOUT complete\r\n", ct);
                        return;
                    default:
                        throw new ImapSyntaxException();
                }

                await WriteAsync($"{tag} OK{(command == "SELECT" ? " [READ-WRITE]" : command == "EXAMINE" ? " [READ-ONLY]" : "")} {command} complete\r\n", ct);
            }
            catch (LabelWriteDeniedException)
            {
                await WriteAsync($"{tag} NO [NOPERM] Label writes are disabled or UID is outside the allowed test scope\r\n", ct);
            }
            catch (ResourceLimitException)
            {
                await WriteAsync($"{tag} NO [LIMIT] Configured message or scan limit exceeded; reconnect after reviewing limits\r\n", ct);
                return;
            }
            catch (IdentityException)
            {
                await WriteAsync($"{tag} NO [CANNOT] Mailbox identity or pending COPY requires operator review\r\n", ct);
            }
            catch (ImapSyntaxException)
            {
                await WriteAsync($"{tag} BAD Unsupported or invalid command\r\n", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not IOException)
            {
                // No exception messages: upstream exceptions may contain addresses or protocol data.
                await WriteAsync($"{tag} NO Operation failed; check trust, authentication, label and identity configuration\r\n", ct);
            }
            finally
            {
                commandLock.Release();
            }
        }
    }

    private async Task UidAsync(string command, IReadOnlyList<ImapValue> args, CancellationToken ct)
    {
        // Do not silently act on UIDs after a mailbox reset in an existing session.
        var current = await bridge.SelectAsync(ct);
        if (current.Validity != _selected!.Validity
            || !current.Messages.Select(m => m.UniqueId).SequenceEqual(_selected.Messages.Select(m => m.UniqueId)))
        {
            throw new IdentityException();
        }

        if (command == "SEARCH")
        {
            var criteria = args;
            if (criteria[0] is TextValue charset && charset.Text.Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
            {
                if (criteria.Count < 3 || criteria[1].Upper() is not ("UTF-8" or "US-ASCII"))
                {
                    throw new ImapSyntaxException();
                }

                criteria = criteria.Skip(2).ToArray();
            }

            var processed = await bridge.ProcessedAsync(ct);
            var query = new SearchParser(options.Label, processed, _selected.Messages.Select(m => m.UniqueId.Id).DefaultIfEmpty(1u).Max()).Parse(criteria);
            var uids = await bridge.SearchAsync(query, ct);
            await WriteAsync("* SEARCH" + (uids.Count == 0 ? "" : " " + string.Join(' ', uids.Select(u => u.Id))) + "\r\n", ct);
            return;
        }

        var set = Syntax.Uids(args[0].Text(), _selected.Messages.Select(m => m.UniqueId.Id).DefaultIfEmpty(1u).Max());
        var messages = _selected.Messages.Where(m => set.Contains(m.UniqueId)).ToArray();
        if (command == "STORE")
        {
            args.Count(3);
            if (args[1].Upper() is not ("+X-GM-LABELS" or "+X-GM-LABELS.SILENT"))
            {
                throw new ImapSyntaxException();
            }

            var label = args[2] is ListValue list && list.Values.Count == 1 ? list.Values[0].Text() : args[2].Text();
            if (label != options.Label)
            {
                throw new ImapSyntaxException();
            }

            if (_readOnly || !options.AllowLabelWrites || options.AllowedWriteUids.Length > 0
                && messages.Any(message => !options.AllowedWriteUids.Contains(message.UniqueId.Id)))
            {
                throw new LabelWriteDeniedException();
            }

            if (options.AllowedWriteUids.Length > 0 && current.Validity != options.AllowedWriteUidValidity)
            {
                throw new IdentityException();
            }

            foreach (var message in messages)
            {
                await bridge.AddLabelAsync(message.UniqueId.Id, ct);
            }

            return;
        }

        if (command != "FETCH")
        {
            throw new ImapSyntaxException();
        }

        args.Count(2);
        var attributes = args[1] is ListValue items ? items.Values.Select(v => v.Upper()).ToArray() : [args[1].Upper()];
        if (attributes.Length == 0 || attributes.Any(a => a is not ("UID" or "FLAGS" or "RFC822.SIZE" or "BODY.PEEK[]" or "X-GM-LABELS")))
        {
            throw new ImapSyntaxException();
        }

        var processedLabels = attributes.Contains("X-GM-LABELS") ? await bridge.ProcessedAsync(ct) : [];
        foreach (var message in messages)
        {
            if (!current.Messages.Any(m => m.UniqueId == message.UniqueId))
            {
                throw new IdentityException();
            }

            // Session sequence numbers remain stable until reconnect. Disappearing messages fail closed.
            var index = _selected.Messages.ToList().IndexOf(message) + 1;
            var fields = $"UID {message.UniqueId.Id}";
            if (attributes.Contains("FLAGS"))
            {
                fields += $" FLAGS ({Flags(current.Messages.Single(m => m.UniqueId == message.UniqueId).Flags ?? MessageFlags.None)})";
            }

            if (attributes.Contains("RFC822.SIZE"))
            {
                fields += $" RFC822.SIZE {message.Size ?? 0}";
            }

            if (attributes.Contains("X-GM-LABELS"))
            {
                fields += $" X-GM-LABELS ({(processedLabels.Contains(message.UniqueId.Id) ? Syntax.Quote(options.Label) : "")})";
            }

            if (attributes.Contains("BODY.PEEK[]"))
            {
                var raw = await bridge.ReadAsync(message.UniqueId.Id, ct);
                await WriteAsync($"* {index} FETCH ({fields} BODY[] {{{raw.Length}}}\r\n", ct);
                await stream.WriteAsync(raw, ct);
                await WriteAsync(")\r\n", ct);
            }
            else
            {
                await WriteAsync($"* {index} FETCH ({fields})\r\n", ct);
            }
        }
    }
}
