using MailKit;
using MailKit.Net.Imap;

namespace ProtonBridgeBridge;

public sealed record PreflightReport(
    bool Success,
    string Stage,
    int CheckedMessages,
    int CrossFolderByteMatches,
    bool LabelCopyTested = false,
    int DistinctRawMessages = 0);

public sealed record DiscoveryReport(bool Success, string Stage, uint[] Uids, uint InboxUidValidity = 0);

public static class BridgePreflight
{
    /// <summary>Checks authentication, bounded repeated reads and existing label bytes without issuing mailbox mutations.</summary>
    public static async Task<PreflightReport> RunAsync(
        Options options,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        options.Validate();
        if (options.PreflightUids.Length == 0)
        {
            return new(false, "test_uids_required", 0, 0);
        }

        var stage = "verified_tls";
        var checkedMessages = 0;
        var crossFolderMatches = 0;
        var budget = new FetchBudget(options.MaxMessageBytes);
        using var scan = budget.BeginScan(options.MaxScanBytes);
        using var client = BridgeConnection.Create(options, budget);
        try
        {
            await client.ConnectAsync(options.BridgeHost, options.BridgePort, options.SocketOptions, cancellationToken);
            stage = "authentication";
            await client.AuthenticateAsync(username, password, cancellationToken);
            stage = "uidplus";
            if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            {
                return new(false, stage, 0, 0);
            }

            stage = "inbox";
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (client.Inbox.Count > options.MaxMailboxMessages)
            {
                return new(false, "mailbox_limit", 0, 0);
            }

            var validity = client.Inbox.UidValidity;
            if (options.AllowedWriteUidValidity is { } expected && validity != expected)
            {
                return new(false, "test_epoch_changed", 0, 0);
            }

            var uids = options.PreflightUids.Select(uid => new UniqueId(uid)).ToArray();
            var summaries = await client.Inbox.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken);
            if (summaries.Count != uids.Length)
            {
                return new(false, "test_uid_missing", 0, 0);
            }

            stage = "repeated_message_bytes";
            var hashes = new HashSet<string>();
            foreach (var uid in uids)
            {
                var hash = await RawMessages.HashAsync(client.Inbox, uid, cancellationToken);
                if (hash != await RawMessages.HashAsync(client.Inbox, uid, cancellationToken))
                {
                    return new(false, stage, checkedMessages, 0);
                }

                hashes.Add(hash);
                checkedMessages++;
            }

            stage = "label_mailbox";
            var label = await client.GetFolderAsync("Labels/" + options.Label, cancellationToken);
            await label.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (label.Count > options.MaxMailboxMessages)
            {
                return new(false, "mailbox_limit", checkedMessages, 0);
            }

            stage = "existing_label_bytes";
            foreach (var uid in await label.SearchAsync(MailKit.Search.SearchQuery.All, cancellationToken))
            {
                if (hashes.Contains(await RawMessages.HashAsync(label, uid, cancellationToken)))
                {
                    crossFolderMatches++;
                }
            }

            stage = "preserved_flags_and_epoch";
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var after = await client.Inbox.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken);
            const MessageFlags ProtectedFlags = MessageFlags.Seen | MessageFlags.Flagged;
            if (client.Inbox.UidValidity != validity || after.Count != summaries.Count
                || summaries.Any(before => !after.Any(message => message.UniqueId == before.UniqueId
                    && (message.Flags & ProtectedFlags) == (before.Flags & ProtectedFlags))))
            {
                return new(false, stage, checkedMessages, crossFolderMatches);
            }

            await client.DisconnectAsync(false, cancellationToken);
            return new(true, "read_only_checks_passed", checkedMessages, crossFolderMatches, DistinctRawMessages: hashes.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResourceLimitException)
        {
            return new(false, "read_limit", checkedMessages, crossFolderMatches);
        }
        catch (Exception)
        {
            // Stable stage codes are actionable without exposing exception text, credentials or message contents.
            return new(false, stage, checkedMessages, crossFolderMatches);
        }
    }

    /// <summary>Lists only UIDs matching an explicit test subject, without fetching message contents.</summary>
    public static async Task<DiscoveryReport> DiscoverAsync(
        Options options, string username, string password, string testSubject, CancellationToken cancellationToken)
    {
        options.Validate();
        if (!testSubject.StartsWith("ProtonBridgeBridge test ", StringComparison.Ordinal)
            || testSubject.Length < 28 || testSubject.Any(char.IsControl))
        {
            return new(false, "unique_test_subject_required", []);
        }

        var stage = "verified_tls";
        using var client = BridgeConnection.Create(options);
        try
        {
            await client.ConnectAsync(options.BridgeHost, options.BridgePort, options.SocketOptions, cancellationToken);
            stage = "authentication";
            await client.AuthenticateAsync(username, password, cancellationToken);
            stage = "test_subject_search";
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var validity = client.Inbox.UidValidity;
            var uids = await client.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(testSubject), cancellationToken);
            if (uids.Count is 0 or > 32)
            {
                return new(false, "review_test_subject_scope", []);
            }

            await client.DisconnectAsync(false, cancellationToken);
            return new(true, "review_test_uids", uids.Select(uid => uid.Id).ToArray(), validity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, stage, []);
        }
    }

}
