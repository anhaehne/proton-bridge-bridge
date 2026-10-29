using System.Text;

namespace ProtonBridgeBridge;

public sealed class ImapSyntaxException : Exception;

public abstract record ImapValue;

public sealed record TextValue(string Text) : ImapValue;

public sealed record ListValue(IReadOnlyList<ImapValue> Values) : ImapValue;

// Bounded recursive-descent IMAP command parser: octet-counted literals, quoted strings,
// nested lists and atoms. Never split protocol input on whitespace or use regex substitutions.
public sealed class ImapReader(Stream stream)
{
    private int _consumed;

    private async Task<int> ReadAsync(CancellationToken ct)
    {
        if (++_consumed > 65536)
        {
            throw new ImapSyntaxException();
        }

        var b = new byte[1];
        return await stream.ReadAsync(b, ct) == 0 ? -1 : b[0];
    }

    public async Task<IReadOnlyList<ImapValue>?> ReadCommandAsync(CancellationToken ct)
    {
        _consumed = 0;
        var values = new List<ImapValue>();
        var c = await ReadAsync(ct);
        if (c < 0)
        {
            return null;
        }

        async Task<List<ImapValue>> ListAsync(int depth, bool nested)
        {
            if (depth > 24)
            {
                throw new ImapSyntaxException();
            }

            var result = new List<ImapValue>();
            while (true)
            {
                while (c == ' ')
                {
                    c = await ReadAsync(ct);
                }

                if (c == ')' && nested)
                {
                    c = await ReadAsync(ct);
                    return result;
                }

                if (c == '\r' && !nested)
                {
                    if (await ReadAsync(ct) != '\n')
                    {
                        throw new ImapSyntaxException();
                    }

                    return result;
                }

                if (c < 0 || c is '\n' or '\r' or ')')
                {
                    throw new ImapSyntaxException();
                }

                if (c == '(')
                {
                    c = await ReadAsync(ct);
                    result.Add(new ListValue(await ListAsync(depth + 1, true)));
                    continue;
                }

                var bytes = new List<byte>();
                if (c == '{')
                {
                    var digits = new StringBuilder();
                    c = await ReadAsync(ct);
                    while (c is >= '0' and <= '9')
                    {
                        digits.Append((char)c);
                        c = await ReadAsync(ct);
                    }

                    var nonSync = c == '+';
                    if (nonSync)
                    {
                        c = await ReadAsync(ct);
                    }

                    if (c != '}' || !int.TryParse(digits.ToString(), out var size) || size > 16384
                        || await ReadAsync(ct) != '\r' || await ReadAsync(ct) != '\n')
                    {
                        throw new ImapSyntaxException();
                    }

                    if (!nonSync)
                    {
                        await stream.WriteAsync("+ Continue\r\n"u8.ToArray(), ct);
                        await stream.FlushAsync(ct);
                    }

                    for (var i = 0; i < size; i++)
                    {
                        c = await ReadAsync(ct);
                        if (c < 0)
                        {
                            throw new ImapSyntaxException();
                        }

                        bytes.Add((byte)c);
                    }
                    c = await ReadAsync(ct);
                }
                else if (c == '"')
                {
                    c = await ReadAsync(ct);
                    while (c != '"')
                    {
                        if (c == '\\')
                        {
                            c = await ReadAsync(ct);
                            if (c is not ('"' or '\\'))
                            {
                                throw new ImapSyntaxException();
                            }
                        }

                        if (c < 0 || c is '\r' or '\n' or 0)
                        {
                            throw new ImapSyntaxException();
                        }

                        bytes.Add((byte)c);
                        c = await ReadAsync(ct);
                    }
                    c = await ReadAsync(ct);
                }
                else
                {
                    while (c > 32 && c is not ('(' or ')' or '{' or '"'))
                    {
                        bytes.Add((byte)c);
                        c = await ReadAsync(ct);
                    }

                    if (bytes.Count == 0)
                    {
                        throw new ImapSyntaxException();
                    }
                }

                if (bytes.Contains((byte)0))
                {
                    throw new ImapSyntaxException();
                }

                result.Add(new TextValue(new UTF8Encoding(false, true).GetString(bytes.ToArray())));
                if (c is not (' ' or ')' or '\r'))
                {
                    throw new ImapSyntaxException();
                }
            }
        }

        values.AddRange(await ListAsync(0, false));
        return values;
    }
}

public static class Syntax
{
    public static MailKit.UniqueIdSet Uids(string value, uint maxUid = uint.MaxValue)
    {
        foreach (var range in value.Split(','))
        {
            var bounds = range.Split(':');
            if (bounds.Length is < 1 or > 2 || bounds.Any(b => b != "*"
                && (b.Length == 0 || b.Any(c => c is < '0' or > '9') || !uint.TryParse(b, out var n) || n == 0)))
            {
                throw new ImapSyntaxException();
            }
        }

        if (!MailKit.UniqueIdSet.TryParse(value.Replace("*", maxUid.ToString(System.Globalization.CultureInfo.InvariantCulture)), out var result))
        {
            throw new ImapSyntaxException();
        }

        return result;
    }

    public static string Text(this ImapValue value) => value is TextValue t ? t.Text : throw new ImapSyntaxException();

    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string Upper(this ImapValue value) => value.Text().ToUpperInvariant();

    public static void Count(this IReadOnlyList<ImapValue> values, int count)
    {
        if (values.Count != count)
        {
            throw new ImapSyntaxException();
        }
    }
}
