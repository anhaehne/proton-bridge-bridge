using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using System.Security.Cryptography.X509Certificates;

namespace ProtonBridgeProxy;

public sealed record InboxState(uint Validity, uint NextUid, IReadOnlyList<IMessageSummary> Messages);
public interface IBridgeMailbox : IAsyncDisposable
{
    bool Authenticated { get; }
    Task Login(string user, string password, CancellationToken ct);
    Task<InboxState> Select(CancellationToken ct);
    Task<HashSet<uint>> Processed(CancellationToken ct);
    Task<IList<UniqueId>> Search(SearchQuery query, CancellationToken ct);
    Task<byte[]> Read(uint uid, CancellationToken ct);
    Task AddLabel(uint uid, CancellationToken ct);
}

public sealed class BridgeMailbox(Options options) : IBridgeMailbox
{
    private readonly ImapClient client = new() { Timeout = 60000, CheckCertificateRevocation = false };
    private X509Certificate2? root;
    private Journal? journal;
    public bool Authenticated => client.IsAuthenticated;
    public async Task Login(string user, string password, CancellationToken ct)
    {
        if (Authenticated) throw new InvalidOperationException();
        if (options.BridgeTrust is { } file)
        {
            root = X509Certificate2.CreateFromPem(File.ReadAllText(file));
            client.ServerCertificateValidationCallback = (_, cert, _, errors) => Trust.Validate(cert, errors, root);
        }
        await client.ConnectAsync(options.BridgeHost, options.BridgePort, options.SocketOptions, ct);
        // Bridge alone validates the supplied Bridge-generated credentials.
        await client.AuthenticateAsync(user, password, ct);
        journal = new Journal(options.StateDirectory, user, $"{options.BridgeHost}:{options.BridgePort}", options.Label);
    }
    private async Task OpenInbox(CancellationToken ct)
    {
        if (!client.Inbox.IsOpen) await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
    }
    private async Task<IMailFolder> Label(CancellationToken ct) => await client.GetFolderAsync("Labels/" + options.Label, ct);
    public async Task<InboxState> Select(CancellationToken ct)
    {
        await OpenInbox(ct);
        await client.NoOpAsync(ct);
        journal!.ValidateEpoch(client.Inbox.UidValidity);
        if (client.Inbox.Count > options.MaxMailboxMessages) throw new InvalidDataException();
        var messages = client.Inbox.Count == 0 ? [] : await client.Inbox.FetchAsync(0, -1,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Size, ct);
        return new InboxState(client.Inbox.UidValidity, client.Inbox.UidNext?.Id ?? 1, messages.ToArray());
    }
    private async Task<byte[]> Read(IMailFolder folder, UniqueId uid, CancellationToken ct)
    {
        // MailKit GetStream uses BODY.PEEK[]; never reserialize MIME before hashing.
        using var raw = await folder.GetStreamAsync(uid, ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await raw.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > options.MaxMessageBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
    private async Task<List<Fingerprint>> Fingerprints(IMailFolder folder, CancellationToken ct)
    {
        if (folder.Count > options.MaxMailboxMessages) throw new InvalidDataException();
        var result = new List<Fingerprint>();
        foreach (var uid in await folder.SearchAsync(SearchQuery.All, ct))
            result.Add(new Fingerprint(uid.Id, Identity.Hash(await Read(folder, uid, ct))));
        return result;
    }
    public async Task<HashSet<uint>> Processed(CancellationToken ct)
    {
        journal!.Reload();
        await OpenInbox(ct);
        var validity = client.Inbox.UidValidity;
        var source = await Fingerprints(client.Inbox, ct);
        var label = await Label(ct);
        await label.OpenAsync(FolderAccess.ReadOnly, ct);
        try { return Identity.Resolve(validity, label.UidValidity, source, await Fingerprints(label, ct), journal!.Entries); }
        finally
        {
            await OpenInbox(ct);
            if (client.Inbox.UidValidity != validity) throw new IdentityException();
        }
    }
    public async Task<IList<UniqueId>> Search(SearchQuery query, CancellationToken ct)
    { await OpenInbox(ct); return await client.Inbox.SearchAsync(query, ct); }
    public async Task<byte[]> Read(uint uid, CancellationToken ct)
    { await OpenInbox(ct); return await Read(client.Inbox, new UniqueId(uid), ct); }
    public async Task AddLabel(uint uid, CancellationToken ct)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) throw new IdentityException();
        var processed = await Processed(ct);
        if (processed.Contains(uid)) return;
        var state = await Select(ct);
        if (!state.Messages.Any(m => m.UniqueId.Id == uid)) throw new IdentityException();
        var hash = Identity.Hash(await Read(uid, ct));
        if (client.Inbox.UidValidity != state.Validity) throw new IdentityException();
        var label = await Label(ct);
        await label.OpenAsync(FolderAccess.ReadOnly, ct);
        var labelValidity = label.UidValidity;
        await OpenInbox(ct);
        if (client.Inbox.UidValidity != state.Validity) throw new IdentityException();
        // Persist intent before the only remote mutation. Never blindly retry a pending COPY.
        journal!.Save(new Mapping(state.Validity, uid, labelValidity, null, hash));
        var copied = await client.Inbox.CopyToAsync(new UniqueId(uid), label, ct);
        if (copied is null) throw new IdentityException(); // COPYUID required for exact identical-delivery identity.
        await label.OpenAsync(FolderAccess.ReadOnly, ct);
        if (label.UidValidity != labelValidity || Identity.Hash(await Read(label, copied.Value, ct)) != hash) throw new IdentityException();
        await OpenInbox(ct);
        journal.Save(new Mapping(state.Validity, uid, labelValidity, copied.Value.Id, hash));
    }
    public async ValueTask DisposeAsync()
    {
        // Do not send EXPUNGE, even during shutdown or error recovery.
        try { if (client.IsConnected) await client.DisconnectAsync(false); }
        finally { client.Dispose(); root?.Dispose(); }
    }
}
