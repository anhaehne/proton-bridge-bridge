using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

namespace ProtonBridgeBridge;

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

public sealed class BridgeMailbox : IBridgeMailbox
{
    private readonly Options _options;
    private readonly FetchBudget _budget;
    private readonly ImapClient _client;
    private Journal? _journal;

    public BridgeMailbox(Options options)
    {
        _options = options;
        _budget = new FetchBudget(options.MaxMessageBytes);
        _client = BridgeConnection.Create(options, _budget);
    }

    public bool Authenticated => _client.IsAuthenticated;

    public async Task LoginAsync(string user, string password, CancellationToken ct)
    {
        if (Authenticated)
        {
            throw new InvalidOperationException();
        }

        await _client.ConnectAsync(_options.BridgeHost, _options.BridgePort, _options.SocketOptions, ct);

        // Bridge alone validates the supplied Bridge-generated credentials.
        await _client.AuthenticateAsync(user, password, ct);
        _journal = new Journal(_options.StateDirectory, user, $"{_options.BridgeHost}:{_options.BridgePort}", _options.Label);
    }

    private async Task OpenInboxAsync(CancellationToken ct)
    {
        if (!_client.Inbox.IsOpen)
        {
            await _client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        }
    }

    private async Task<IMailFolder> LabelAsync(CancellationToken ct) => await _client.GetFolderAsync("Labels/" + _options.Label, ct);

    public async Task<InboxState> SelectAsync(CancellationToken ct)
    {
        await OpenInboxAsync(ct);
        await _client.NoOpAsync(ct);
        _journal!.ValidateEpoch(_client.Inbox.UidValidity);
        if (_client.Inbox.Count > _options.MaxMailboxMessages)
        {
            throw new ResourceLimitException();
        }

        var messages = _client.Inbox.Count == 0 ? [] : await _client.Inbox.FetchAsync(0, -1,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Size, ct);
        return new InboxState(_client.Inbox.UidValidity, _client.Inbox.UidNext?.Id ?? 1, messages.ToArray());
    }

    private Task<byte[]> ReadAsync(IMailFolder folder, UniqueId uid, CancellationToken ct) =>
        RawMessages.ReadAsync(folder, uid, _options.MaxMessageBytes, ct);

    private async Task<List<Fingerprint>> FingerprintsAsync(IMailFolder folder, IEnumerable<UniqueId> uids, CancellationToken ct)
    {
        var result = new List<Fingerprint>();
        foreach (var uid in uids)
        {
            result.Add(new Fingerprint(uid.Id, await RawMessages.HashAsync(folder, uid, ct)));
        }

        return result;
    }

    public async Task<HashSet<uint>> ProcessedAsync(CancellationToken ct)
    {
        _journal!.Reload();
        using var scan = _budget.BeginScan(_options.MaxScanBytes);
        await OpenInboxAsync(ct);
        var validity = _client.Inbox.UidValidity;
        _journal.ValidateEpoch(validity);
        if (_client.Inbox.Count > _options.MaxMailboxMessages)
        {
            throw new ResourceLimitException();
        }

        var sourceUids = await _client.Inbox.SearchAsync(SearchQuery.All, ct);
        var label = await LabelAsync(ct);
        await label.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            if (label.Count > _options.MaxMailboxMessages)
            {
                throw new ResourceLimitException();
            }

            var labelUids = await label.SearchAsync(SearchQuery.All, ct);
            var mappings = _journal.Entries.Where(mapping => mapping.SourceValidity == validity && mapping.LabelValidity == label.UidValidity).ToArray();
            var knownLabelUids = mappings.Where(mapping => mapping.LabelUid is not null).Select(mapping => mapping.LabelUid!.Value).ToHashSet();
            var unknownLabels = labelUids.Any(uid => !knownLabelUids.Contains(uid.Id));
            var pending = _journal.Entries.Any(mapping => mapping.SourceValidity == validity && mapping.LabelUid is null);
            if (!_options.AllowHashFallback && (unknownLabels || pending))
            {
                throw new IdentityException();
            }

            var labels = await FingerprintsAsync(label, labelUids, ct);
            var mappedSourceUids = mappings.Select(mapping => mapping.SourceUid).ToHashSet();

            // Unknown label membership needs a full ambiguity check; exact COPYUID mappings need only their source bytes.
            var neededUids = unknownLabels || pending ? sourceUids : sourceUids.Where(uid => mappedSourceUids.Contains(uid.Id));
            await OpenInboxAsync(ct);
            var source = await FingerprintsAsync(_client.Inbox, neededUids, ct);
            return Identity.Resolve(validity, label.UidValidity, source, labels, _journal.Entries, _options.AllowHashFallback);
        }
        finally
        {
            if (_client.IsConnected && _client.IsAuthenticated)
            {
                await OpenInboxAsync(ct);
                if (_client.Inbox.UidValidity != validity)
                {
                    throw new IdentityException();
                }
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
        if (!_options.AllowLabelWrites || _options.AllowedWriteUids.Length > 0 && !_options.AllowedWriteUids.Contains(uid))
        {
            throw new LabelWriteDeniedException();
        }

        await OpenInboxAsync(ct);
        if (_options.AllowedWriteUids.Length > 0 && _client.Inbox.UidValidity != _options.AllowedWriteUidValidity)
        {
            throw new IdentityException();
        }

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

        var hash = await RawMessages.HashAsync(_client.Inbox, new UniqueId(uid), ct);
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
        if (label.UidValidity != labelValidity || await RawMessages.HashAsync(label, copied.Value, ct) != hash)
        {
            throw new IdentityException();
        }

        await OpenInboxAsync(ct);
        if (_client.Inbox.UidValidity != state.Validity)
        {
            throw new IdentityException();
        }

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
        }
    }
}
