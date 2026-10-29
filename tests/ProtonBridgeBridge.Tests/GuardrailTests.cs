using MailKit;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class GuardrailTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedLiteralHeaderIsRejectedWithoutPayloadAsync(bool uidAfterBody)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await RecoveryFixture.StartAsync(false, timeout.Token);
        fixture.Stock.DeclaredBodyLength = 64 * 1024 * 1024;
        fixture.Stock.UidAfterBody = uidAfterBody;
        using var client = BridgeConnection.Create(fixture.Settings with { MaxMessageBytes = 1024 });
        await client.ConnectAsync("localhost", fixture.Stock.Port, fixture.Settings.SocketOptions, timeout.Token);
        await client.AuthenticateAsync("fixture", "secret", timeout.Token);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, timeout.Token);

        await Assert.ThrowsAsync<ResourceLimitException>(() => client.Inbox.GetStreamAsync(new UniqueId(7), timeout.Token));
        Assert.Equal(0, fixture.Stock.Copies);
    }

    [Fact]
    public async Task MessageAtExactLimitPreservesOriginalOctetsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await RecoveryFixture.StartAsync(false, timeout.Token);
        fixture.Stock.UidAfterBody = true;
        await using var mailbox = new BridgeMailbox(fixture.Settings with { MaxMessageBytes = fixture.Stock.Inbox[7].Raw.Length });
        await mailbox.LoginAsync("fixture", "secret", timeout.Token);
        Assert.Equal(fixture.Stock.Inbox[7].Raw, await mailbox.ReadAsync(7, timeout.Token));
        fixture.AssertSourcePreserved(1);
    }

    [Fact]
    public async Task ScanBudgetIncludesInboxAndLabelAndResetsOnReconnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fixture = await RecoveryFixture.StartAsync(true, timeout.Token);
        await using (var mailbox = new BridgeMailbox(fixture.Settings))
        {
            await mailbox.LoginAsync("fixture", "secret", timeout.Token);
            await mailbox.SelectAsync(timeout.Token);
            await mailbox.AddLabelAsync(7, timeout.Token);
        }

        var messageSize = fixture.Stock.Inbox[7].Raw.Length;
        await using (var limited = new BridgeMailbox(fixture.Settings with { MaxScanBytes = messageSize }))
        {
            await limited.LoginAsync("fixture", "secret", timeout.Token);
            await limited.SelectAsync(timeout.Token);
            await Assert.ThrowsAsync<ResourceLimitException>(() => limited.ProcessedAsync(timeout.Token));
        }

        await using var reconnected = new BridgeMailbox(fixture.Settings with { MaxScanBytes = messageSize * 2 });
        await reconnected.LoginAsync("fixture", "secret", timeout.Token);
        await reconnected.SelectAsync(timeout.Token);
        Assert.Equal([7u], await reconnected.ProcessedAsync(timeout.Token));
        Assert.Equal(1, fixture.Stock.Copies);
    }

    [Fact]
    public async Task DisabledWritesAndUidScopePreventRemoteMutationsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await RecoveryFixture.StartAsync(true, timeout.Token);
        await using (var disabled = new BridgeMailbox(fixture.Settings with { AllowLabelWrites = false }))
        {
            await disabled.LoginAsync("fixture", "secret", timeout.Token);
            await disabled.SelectAsync(timeout.Token);
            await Assert.ThrowsAsync<LabelWriteDeniedException>(() => disabled.AddLabelAsync(7, timeout.Token));
        }

        await using var scoped = new BridgeMailbox(fixture.Settings with { AllowedWriteUids = [7], AllowedWriteUidValidity = 10 });
        await scoped.LoginAsync("fixture", "secret", timeout.Token);
        await scoped.SelectAsync(timeout.Token);
        await Assert.ThrowsAsync<LabelWriteDeniedException>(() => scoped.AddLabelAsync(8, timeout.Token));
        Assert.DoesNotContain("UID COPY", fixture.Stock.Commands);
        await scoped.AddLabelAsync(7, timeout.Token);
        Assert.Equal(1, fixture.Stock.Copies);
        await using var wrongEpoch = new BridgeMailbox(fixture.Settings with { AllowedWriteUids = [8], AllowedWriteUidValidity = 99 });
        await wrongEpoch.LoginAsync("fixture", "secret", timeout.Token);
        await Assert.ThrowsAsync<IdentityException>(() => wrongEpoch.AddLabelAsync(8, timeout.Token));
        Assert.Equal(1, fixture.Stock.Copies);
    }

    [Fact]
    public async Task EpochChangeAfterCopyVerificationRetainsPendingIntentAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await RecoveryFixture.StartAsync(false, timeout.Token);
        fixture.Stock.CheckpointAsync = (checkpoint, _) =>
        {
            if (checkpoint == CopyCheckpoint.BeforeJournalCommit)
            {
                fixture.Stock.Validity = 11;
            }

            return Task.FromResult(true);
        };
        await using var mailbox = new BridgeMailbox(fixture.Settings);
        await mailbox.LoginAsync("fixture", "secret", timeout.Token);
        await mailbox.SelectAsync(timeout.Token);
        await Assert.ThrowsAsync<IdentityException>(() => mailbox.AddLabelAsync(7, timeout.Token));
        Assert.Null(Assert.Single(fixture.ReadJournal()).LabelUid);
        Assert.Equal(1, fixture.Stock.Copies);
    }

    [Fact]
    public async Task BatchScopeAndExamineRejectBeforeAnyCopyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fixture = await RecoveryFixture.StartAsync(true, timeout.Token);
        await fixture.ConfigureAsync(fixture.Settings with { AllowedWriteUids = [7], AllowedWriteUidValidity = 10 }, timeout.Token);
        await fixture.StartProxyAsync(timeout.Token);
        await using var client = await fixture.ConnectAsync(timeout.Token);
        Assert.Contains("b OK", await client.CommandAsync("b SELECT INBOX", timeout.Token));
        Assert.Contains("NOPERM", await client.CommandAsync("c UID STORE 7,8 +X-GM-LABELS Paperless-imported", timeout.Token));
        Assert.Equal(0, fixture.Stock.Copies);
        Assert.Contains("d OK", await client.CommandAsync("d EXAMINE INBOX", timeout.Token));
        Assert.Contains("NOPERM", await client.CommandAsync("e UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token));
        Assert.Equal(0, fixture.Stock.Copies);
        Assert.Contains("f OK", await client.CommandAsync("f SELECT INBOX", timeout.Token));
        Assert.Contains("g OK", await client.CommandAsync("g UID STORE 7 +X-GM-LABELS Paperless-imported", timeout.Token));
        Assert.Equal(1, fixture.Stock.Copies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyPreflightChecksBytesAndFlagsWithoutStateOrCopyAsync(bool existingLabel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fixture = await RecoveryFixture.StartAsync(false, timeout.Token);
        if (existingLabel)
        {
            await using var setup = new BridgeMailbox(fixture.Settings);
            await setup.LoginAsync("fixture", "secret", timeout.Token);
            await setup.SelectAsync(timeout.Token);
            await setup.AddLabelAsync(7, timeout.Token);
        }

        var options = fixture.Settings with { StateDirectory = Path.Combine(fixture.Settings.StateDirectory, "unused"), PreflightUids = [7] };
        var copies = fixture.Stock.Copies;
        var report = await BridgePreflight.RunAsync(options, "fixture", "secret", timeout.Token);
        Assert.True(report.Success);
        Assert.Equal(1, report.CheckedMessages);
        Assert.Equal(existingLabel ? 1 : 0, report.CrossFolderByteMatches);
        Assert.False(report.LabelCopyTested);
        Assert.Equal(copies, fixture.Stock.Copies);
        Assert.False(Directory.Exists(options.StateDirectory));
        fixture.AssertSourcePreserved(1);
    }

    [Fact]
    public async Task PreflightReportsByteIdenticalDeliveriesWithoutRelyingOnMessageIdAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await RecoveryFixture.StartAsync(true, timeout.Token);
        var report = await BridgePreflight.RunAsync(fixture.Settings with { PreflightUids = [7, 8] }, "fixture", "secret", timeout.Token);
        Assert.True(report.Success);
        Assert.Equal(2, report.CheckedMessages);
        Assert.Equal(1, report.DistinctRawMessages);
        Assert.Equal(0, fixture.Stock.Copies);
    }

    [Fact]
    public async Task PreflightWrongCredentialsAndLimitsFailWithSafeStageCodesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fixture = await RecoveryFixture.StartAsync(false, timeout.Token);
        var options = fixture.Settings with { PreflightUids = [7] };
        var rejected = await BridgePreflight.RunAsync(options, "fixture", "wrong", timeout.Token);
        Assert.False(rejected.Success);
        Assert.Equal("authentication", rejected.Stage);
        var limited = await BridgePreflight.RunAsync(options with { MaxScanBytes = 1 }, "fixture", "secret", timeout.Token);
        Assert.False(limited.Success);
        Assert.Equal("read_limit", limited.Stage);
        Assert.Equal(0, fixture.Stock.Copies);
    }
}
