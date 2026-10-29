using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class RecoveryTests
{
    [Theory]
    [InlineData(CopyCheckpoint.BeforeIntent, false, true)]
    [InlineData(CopyCheckpoint.BeforeIntent, true, true)]
    [InlineData(CopyCheckpoint.BeforeRemoteCopy, false, true)]
    [InlineData(CopyCheckpoint.BeforeRemoteCopy, true, true)]
    [InlineData(CopyCheckpoint.AfterRemoteCopy, false, true)]
    [InlineData(CopyCheckpoint.AfterRemoteCopy, true, true)]
    [InlineData(CopyCheckpoint.BeforeJournalCommit, false, true)]
    [InlineData(CopyCheckpoint.BeforeJournalCommit, true, true)]
    [InlineData(CopyCheckpoint.AfterRemoteCopy, false, false)]
    [InlineData(CopyCheckpoint.BeforeJournalCommit, false, false)]
    public async Task AbruptExitAtCopyBoundaryRecoversOrBlocksWithoutDuplicateCopyAsync(
        CopyCheckpoint checkpoint,
        bool identicalDeliveries,
        bool allowHashFallback)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var fixture = await RecoveryFixture.StartAsync(identicalDeliveries, timeout.Token);
        await fixture.ConfigureAsync(fixture.Settings with { AllowHashFallback = allowHashFallback }, timeout.Token);
        var barrier = new CopyBarrier(checkpoint);
        fixture.Stock.CheckpointAsync = barrier.ReachAsync;
        var original = await fixture.StartProxyAsync(timeout.Token);
        await using var interruptedClient = await fixture.ConnectAsync(timeout.Token);
        Assert.Contains("b OK", await interruptedClient.CommandAsync("b SELECT INBOX", timeout.Token));

        var interruptedCommand = interruptedClient.CommandAsync("c UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token);
        await barrier.WaitAsync(timeout.Token);
        var originalJournal = fixture.ReadJournal();
        var remoteCopySucceeded = checkpoint is CopyCheckpoint.AfterRemoteCopy or CopyCheckpoint.BeforeJournalCommit;
        Assert.Equal(remoteCopySucceeded ? 1 : 0, fixture.Stock.Copies);
        Assert.Equal(fixture.Stock.Copies, fixture.Stock.LabelCount);
        if (checkpoint == CopyCheckpoint.BeforeIntent)
        {
            Assert.Empty(originalJournal);
        }
        else
        {
            var pending = Assert.Single(originalJournal);
            Assert.Equal(7u, pending.SourceUid);
            Assert.Equal(10u, pending.SourceValidity);
            Assert.Equal(30u, pending.LabelValidity);
            Assert.Null(pending.LabelUid);
            Assert.Equal(Identity.Hash(fixture.Stock.Inbox[7].Raw), pending.Hash);
        }

        await original.CrashAsync(timeout.Token);
        barrier.Abort();
        await AssertInterruptedAsync(interruptedCommand);
        var restarted = await fixture.StartProxyAsync(timeout.Token);
        Assert.NotEqual(original.Id, restarted.Id);
        Assert.Equal(originalJournal, fixture.ReadJournal());
        await using var client = await fixture.ConnectAsync(timeout.Token);
        Assert.Contains("b OK", await client.CommandAsync("b SELECT INBOX", timeout.Token));

        var canRecover = checkpoint == CopyCheckpoint.BeforeIntent || remoteCopySucceeded && !identicalDeliveries && allowHashFallback;
        var store = await client.CommandAsync("c UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token);
        Assert.Contains(canRecover ? "c OK" : "c NO", store);
        var search = await client.CommandAsync(
            "d UID SEARCH ((NOT (X-GM-LABELS \"Paperless-imported\") UNKEYWORD Paperless-imported))", timeout.Token);
        if (canRecover)
        {
            Assert.Contains(identicalDeliveries ? "* SEARCH 8\r\n" : "* SEARCH\r\n", search);
            Assert.Contains("d OK", search);
            Assert.Equal(1, fixture.Stock.Copies);
        }
        else
        {
            Assert.Contains("d NO", search);
            Assert.Equal(remoteCopySucceeded ? 1 : 0, fixture.Stock.Copies);
            Assert.Equal(originalJournal, fixture.ReadJournal());
        }

        // Repeating the action must neither create a second copy nor clear an uncertain intent.
        var repeatedStore = await client.CommandAsync("e UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token);
        Assert.Contains(canRecover ? "e OK" : "e NO", repeatedStore);
        Assert.Equal(canRecover || remoteCopySucceeded ? 1 : 0, fixture.Stock.Copies);
        Assert.Equal(fixture.Stock.Copies, fixture.Stock.LabelCount);
        fixture.AssertSourcePreserved(identicalDeliveries ? 2 : 1);
    }

    [Fact]
    public async Task CompletedCopyUidMappingsSurviveCrashesAndConcurrentIdenticalDeliveriesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var fixture = await RecoveryFixture.StartAsync(true, timeout.Token);
        var original = await fixture.StartProxyAsync(timeout.Token);
        await using (var client = await fixture.ConnectAsync(timeout.Token))
        {
            Assert.Contains("b OK", await client.CommandAsync("b SELECT INBOX", timeout.Token));
            Assert.Contains("c OK", await client.CommandAsync("c UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token));
        }

        var completed = Assert.Single(fixture.ReadJournal());
        Assert.Equal(100u, completed.LabelUid);
        await original.CrashAsync(timeout.Token);
        await fixture.StartProxyAsync(timeout.Token);

        async Task ApplyAsync(uint uid)
        {
            await using var client = await fixture.ConnectAsync(timeout.Token);
            Assert.Contains("b OK", await client.CommandAsync("b SELECT INBOX", timeout.Token));
            Assert.Contains("c OK", await client.CommandAsync($"c UID STORE {uid} +X-GM-LABELS Paperless-imported", timeout.Token));
        }

        await Task.WhenAll(ApplyAsync(7), ApplyAsync(8), ApplyAsync(8));
        var mappings = fixture.ReadJournal().OrderBy(mapping => mapping.SourceUid).ToArray();
        Assert.Equal(2, fixture.Stock.Copies);
        Assert.Equal(2, fixture.Stock.LabelCount);
        Assert.Equal([7u, 8u], mappings.Select(mapping => mapping.SourceUid));
        Assert.Equal([100u, 101u], mappings.Select(mapping => mapping.LabelUid));
        Assert.All(mappings, mapping => Assert.Equal(completed.Hash, mapping.Hash));

        // A further process restart must preserve both exact identities despite identical octets.
        await fixture.CrashProxiesAsync(timeout.Token);
        await fixture.StartProxyAsync(timeout.Token);
        await using var poll = await fixture.ConnectAsync(timeout.Token);
        Assert.Contains("b OK", await poll.CommandAsync("b SELECT INBOX", timeout.Token));
        Assert.Contains("* SEARCH\r\n", await poll.CommandAsync("c UID SEARCH UNKEYWORD Paperless-imported", timeout.Token));
        Assert.Equal(2, fixture.Stock.Copies);
        fixture.AssertSourcePreserved(2);
    }

    [Fact]
    public async Task RestartWithChangedUidValidityCannotLabelAReusedUidAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var fixture = await RecoveryFixture.StartAsync(false, timeout.Token);
        var original = await fixture.StartProxyAsync(timeout.Token);
        await using (var client = await fixture.ConnectAsync(timeout.Token))
        {
            Assert.Contains("b OK", await client.CommandAsync("b SELECT INBOX", timeout.Token));
            Assert.Contains("c OK", await client.CommandAsync("c UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token));
        }

        var journal = fixture.ReadJournal();
        await original.CrashAsync(timeout.Token);
        fixture.Stock.Validity = 11;
        fixture.Stock.Inbox[7] = fixture.Stock.Inbox[7] with { Raw = [1, 2, 3] };
        await fixture.StartProxyAsync(timeout.Token);
        await using var staleWorker = await fixture.ConnectAsync(timeout.Token);
        Assert.Contains("b NO", await staleWorker.CommandAsync("b SELECT INBOX", timeout.Token));
        Assert.Contains("c NO", await staleWorker.CommandAsync("c UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token));
        Assert.Equal(1, fixture.Stock.Copies);
        Assert.Equal(journal, fixture.ReadJournal());
        fixture.AssertSourcePreserved(1);
    }

    private static async Task AssertInterruptedAsync(Task<string> command)
    {
        try
        {
            Assert.DoesNotContain("c OK", await command);
        }
        catch (IOException)
        {
            // An abruptly killed TLS peer can close with EOF or an I/O error.
        }
    }
}
