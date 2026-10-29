using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

namespace ProtonBridgeProxy;

public sealed record InboxState(uint Validity, uint NextUid, IReadOnlyList<IMessageSummary> Messages);

public interface IBridgeMailbox : IAsyncDisposable
{
    bool Authenticated { get; }

    /// <summary>Validates Bridge-generated credentials through the verified upstream TLS connection.</summary>
    Task LoginAsync(string user, string password, CancellationToken ct);

    Task<InboxState> SelectAsync(CancellationToken ct);

    /// <summary>Resolves processed-label membership, rejecting ambiguous message identities.</summary>
    Task<HashSet<uint>> ProcessedAsync(CancellationToken ct);

    Task<IList<UniqueId>> SearchAsync(SearchQuery query, CancellationToken ct);

    /// <summary>Returns complete message octets without changing the message's read status.</summary>
    Task<byte[]> ReadAsync(uint uid, CancellationToken ct);

    /// <summary>Adds only the configured label, preserving source membership and flags.</summary>
    Task AddLabelAsync(uint uid, CancellationToken ct);
}

public sealed class BridgeMailbox(Options options) : IBridgeMailbox
{
    private readonly ImapClient _client = new() { Timeout = 60000, CheckCertificateRevocation = false };
    private X509Certificate2? _root;
    private Journal? _journal;

    public bool Authenticated => _client.IsAuthenticated;

    public async Task LoginAsync(string user, string password, CancellationToken ct)
    {
        if (Authenticated)
        {
            throw new InvalidOperationException();
        }

        if (options.BridgeTrust is { } file)
        {
            _root = X509Certificate2.CreateFromPem(File.ReadAllText(file));
            _client.ServerCertificateValidationCallback = (_, cert, _, errors) => Trust.Validate(cert, errors, _root);
        }

        await _client.ConnectAsync(options.BridgeHost, options.BridgePort, options.SocketOptions, ct);

        // Bridge alone validates the supplied Bridge-generated credentials.
        await _client.AuthenticateAsync(user, password, ct);
        _journal = new Journal(options.StateDirectory, user, $"{options.BridgeHost}:{options.BridgePort}", options.Label);
    }

    private async Task OpenInboxAsync(CancellationToken ct)
    {
        if (!_client.Inbox.IsOpen)
        {
            await _client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        }
    }

    private async Task<IMailFolder> LabelAsync(CancellationToken ct) => await _client.GetFolderAsync("Labels/" + options.Label, ct);

    public async Task<InboxState> SelectAsync(CancellationToken ct)
    {
        await OpenInboxAsync(ct);
        await _client.NoOpAsync(ct);
        _journal!.ValidateEpoch(_client.Inbox.UidValidity);
        if (_client.Inbox.Count > options.MaxMailboxMessages)
        {
            throw new InvalidDataException();
        }

        var messages = _client.Inbox.Count == 0 ? [] : await _client.Inbox.FetchAsync(0, -1,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Size, ct);
        return new InboxState(_client.Inbox.UidValidity, _client.Inbox.UidNext?.Id ?? 1, messages.ToArray());
    }

    private async Task<byte[]> ReadAsync(IMailFolder folder, UniqueId uid, CancellationToken ct)
    {
        // MailKit GetStream uses BODY.PEEK[]; never reserialize MIME before hashing.
        using var raw = await folder.GetStreamAsync(uid, ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await raw.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > options.MaxMessageBytes)
            {
                throw new InvalidDataException();
            }

            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    private async Task<List<Fingerprint>> FingerprintsAsync(IMailFolder folder, CancellationToken ct)
    {
        if (folder.Count > options.MaxMailboxMessages)
        {
            throw new InvalidDataException();
        }

        var result = new List<Fingerprint>();
        foreach (var uid in await folder.SearchAsync(SearchQuery.All, ct))
        {
            result.Add(new Fingerprint(uid.Id, Identity.Hash(await ReadAsync(folder, uid, ct))));
        }

        return result;
    }

    public async Task<HashSet<uint>> ProcessedAsync(CancellationToken ct)
    {
        _journal!.Reload();
        await OpenInboxAsync(ct);
        var validity = _client.Inbox.UidValidity;
        var source = await FingerprintsAsync(_client.Inbox, ct);
        var label = await LabelAsync(ct);
        await label.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            return Identity.Resolve(validity, label.UidValidity, source, await FingerprintsAsync(label, ct), _journal!.Entries);
        }
        finally
        {
            await OpenInboxAsync(ct);
            if (_client.Inbox.UidValidity != validity)
            {
                throw new IdentityException();
            }
        }
    }

    public async Task<IList<UniqueId>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        await OpenInboxAsync(ct);
        return await _client.Inbox.SearchAsync(query, ct);
    }

    public async Task<byte[]> ReadAsync(uint uid, CancellationToken ct)
    {
        await OpenInboxAsync(ct);
        return await ReadAsync(_client.Inbox, new UniqueId(uid), ct);
    }

    public async Task AddLabelAsync(uint uid, CancellationToken ct)
    {
        if (!_client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            throw new IdentityException();
        }

        var processed = await ProcessedAsync(ct);
        if (processed.Contains(uid))
        {
            return;
        }

        var state = await SelectAsync(ct);
        if (!state.Messages.Any(m => m.UniqueId.Id == uid))
        {
            throw new IdentityException();
        }

        var hash = Identity.Hash(await ReadAsync(uid, ct));
        if (_client.Inbox.UidValidity != state.Validity)
        {
            throw new IdentityException();
        }

        var label = await LabelAsync(ct);
        await label.OpenAsync(FolderAccess.ReadOnly, ct);
        var labelValidity = label.UidValidity;
        await OpenInboxAsync(ct);
        if (_client.Inbox.UidValidity != state.Validity)
        {
            throw new IdentityException();
        }

        // Persist intent before the only remote mutation. Never blindly retry a pending COPY.
        _journal!.Save(new Mapping(state.Validity, uid, labelValidity, null, hash));
        var copied = await _client.Inbox.CopyToAsync(new UniqueId(uid), label, ct);
        if (copied is null)
        {
            // COPYUID required for exact identical-delivery identity.
            throw new IdentityException();
        }

        await label.OpenAsync(FolderAccess.ReadOnly, ct);
        if (label.UidValidity != labelValidity || Identity.Hash(await ReadAsync(label, copied.Value, ct)) != hash)
        {
            throw new IdentityException();
        }

        await OpenInboxAsync(ct);
        _journal.Save(new Mapping(state.Validity, uid, labelValidity, copied.Value.Id, hash));
    }

    public async ValueTask DisposeAsync()
    {
        // Do not send EXPUNGE, even during shutdown or error recovery.
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(false);
            }
        }
        finally
        {
            _client.Dispose();
            _root?.Dispose();
        }
    }
}
