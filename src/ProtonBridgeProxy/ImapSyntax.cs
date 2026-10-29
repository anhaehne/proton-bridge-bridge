using System.Text;

namespace ProtonBridgeProxy;

public sealed class ImapSyntaxException : Exception;
public abstract record ImapValue;
public sealed record TextValue(string Text) : ImapValue;
public sealed record ListValue(IReadOnlyList<ImapValue> Values) : ImapValue;

// Bounded recursive-descent IMAP command parser: octet-counted literals, quoted strings,
// nested lists and atoms. Never split protocol input on whitespace or use regex substitutions.
public sealed class ImapReader(Stream stream)
{
    private int consumed;
    private async Task<int> Read(CancellationToken ct)
    {
        if (++consumed > 65536) throw new ImapSyntaxException();
        var b = new byte[1];
        return await stream.ReadAsync(b, ct) == 0 ? -1 : b[0];
    }
    public async Task<IReadOnlyList<ImapValue>?> ReadCommand(CancellationToken ct)
    {
        consumed = 0;
        var values = new List<ImapValue>();
        int c = await Read(ct);
        if (c < 0) return null;
        async Task<List<ImapValue>> List(int depth, bool nested)
        {
            if (depth > 24) throw new ImapSyntaxException();
            var result = new List<ImapValue>();
            while (true)
            {
                while (c == ' ') c = await Read(ct);
                if (c == ')' && nested) { c = await Read(ct); return result; }
                if (c == '\r' && !nested) { if (await Read(ct) != '\n') throw new ImapSyntaxException(); return result; }
                if (c < 0 || c is '\n' or '\r' or ')') throw new ImapSyntaxException();
                if (c == '(') { c = await Read(ct); result.Add(new ListValue(await List(depth + 1, true))); continue; }
                var bytes = new List<byte>();
                if (c == '{')
                {
                    var digits = new StringBuilder();
                    c = await Read(ct);
                    while (c is >= '0' and <= '9') { digits.Append((char)c); c = await Read(ct); }
                    bool nonSync = c == '+';
                    if (nonSync) c = await Read(ct);
                    if (c != '}' || !int.TryParse(digits.ToString(), out int size) || size > 16384 ||
                        await Read(ct) != '\r' || await Read(ct) != '\n') throw new ImapSyntaxException();
                    if (!nonSync) { await stream.WriteAsync("+ Continue\r\n"u8.ToArray(), ct); await stream.FlushAsync(ct); }
                    for (int i = 0; i < size; i++) { c = await Read(ct); if (c < 0) throw new ImapSyntaxException(); bytes.Add((byte)c); }
                    c = await Read(ct);
                }
                else if (c == '"')
                {
                    c = await Read(ct);
                    while (c != '"')
                    {
                        if (c == '\\') { c = await Read(ct); if (c is not ('"' or '\\')) throw new ImapSyntaxException(); }
                        if (c < 0 || c is '\r' or '\n' or 0) throw new ImapSyntaxException();
                        bytes.Add((byte)c); c = await Read(ct);
                    }
                    c = await Read(ct);
                }
                else
                {
                    while (c > 32 && c is not ('(' or ')' or '{' or '"'))
                    { bytes.Add((byte)c); c = await Read(ct); }
                    if (bytes.Count == 0) throw new ImapSyntaxException();
                }
                if (bytes.Contains((byte)0)) throw new ImapSyntaxException();
                result.Add(new TextValue(new UTF8Encoding(false, true).GetString(bytes.ToArray())));
                if (c is not (' ' or ')' or '\r')) throw new ImapSyntaxException();
            }
        }
        values.AddRange(await List(0, false));
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
            if (bounds.Length is < 1 or > 2 || bounds.Any(b => b != "*" &&
                (b.Length == 0 || b.Any(c => c is < '0' or > '9') || !uint.TryParse(b, out var n) || n == 0)))
                throw new ImapSyntaxException();
        }
        if (!MailKit.UniqueIdSet.TryParse(value.Replace("*", maxUid.ToString(System.Globalization.CultureInfo.InvariantCulture)), out var result)) throw new ImapSyntaxException();
        return result;
    }

    public static string Text(this ImapValue value) => value is TextValue t ? t.Text : throw new ImapSyntaxException();
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    public static string Upper(this ImapValue value) => value.Text().ToUpperInvariant();
    public static void Count(this IReadOnlyList<ImapValue> values, int count) { if (values.Count != count) throw new ImapSyntaxException(); }
}
