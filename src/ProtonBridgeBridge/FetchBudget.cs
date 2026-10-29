using MailKit;
using MailKit.Net.Imap;

namespace ProtonBridgeBridge;

public sealed class ResourceLimitException : IOException;

internal sealed class FetchBudget(int maxMessageBytes)
{
    private long? _remaining;

    public IDisposable BeginScan(long maxBytes)
    {
        if (_remaining is not null)
        {
            throw new InvalidOperationException();
        }

        _remaining = maxBytes;
        return new ScanScope(this);
    }

    public void Reserve(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > maxMessageBytes || length > maxMessageBytes - offset
            || _remaining is { } remaining && length > remaining)
        {
            throw new ResourceLimitException();
        }

        if (_remaining is not null)
        {
            _remaining -= length;
        }
    }

    private sealed class ScanScope(FetchBudget budget) : IDisposable
    {
        public void Dispose() => budget._remaining = null;
    }
}

internal sealed class BoundedImapClient(FetchBudget budget) : ImapClient
{
    protected override ImapFolder CreateImapFolder(ImapFolderConstructorArgs args) => new BoundedFolder(args, budget);

    private sealed class BoundedFolder(ImapFolderConstructorArgs args, FetchBudget budget) : ImapFolder(args)
    {
        protected override Stream CreateStream(UniqueId? uid, string section, int offset, int length)
        {
            // MailKit invokes this hook after reading the literal header, before allocating or reading its body.
            budget.Reserve(offset, length);
            return base.CreateStream(uid, section, offset, length);
        }
    }
}
