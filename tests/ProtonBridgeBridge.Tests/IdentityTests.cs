using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class IdentityTests
{
    [Fact]
    public void MatchesBytesAcrossDifferentFolderUids()
    {
        Assert.Equal([7u], Identity.Resolve(1, 2, [new(7, "bytes")], [new(99, "bytes")], []));
        Assert.Empty(Identity.Resolve(1, 2, [new(7, "other")], [new(99, "bytes")], []));
    }

    [Fact]
    public void IdenticalDeliveriesRequireExactCopyUidMapping()
    {
        Fingerprint[] inbox = [new(7, "same"), new(8, "same")];
        Fingerprint[] label = [new(99, "same")];
        Assert.Throws<IdentityException>(() => Identity.Resolve(1, 2, inbox, label, []));
        Assert.Equal([7u], Identity.Resolve(1, 2, inbox, label, [new(1, 7, 2, 99, "same")]));

        // An extra unexplained label copy cannot safely be assigned to UID 8.
        Assert.Throws<IdentityException>(() => Identity.Resolve(1, 2, inbox, [.. label, new(100, "same")], [new(1, 7, 2, 99, "same")]));
    }

    [Fact]
    public void UidValidityResetDiscardsMappingAndFailsOnAmbiguity()
    {
        Assert.Throws<IdentityException>(() => Identity.Resolve(3, 2,
            [new(7, "same"), new(8, "same")], [new(99, "same")], [new(1, 7, 2, 99, "same")]));
        Assert.Equal([8u], Identity.Resolve(3, 4, [new(8, "same")], [new(100, "same")], [new(1, 7, 2, 99, "same")]));
    }

    [Fact]
    public void PendingIntentRecoversUniqueCopyAndBlocksUnresolvedOrAmbiguousCopy()
    {
        Mapping[] pending = [new(1, 7, 2, null, "same")];
        Assert.Equal([7u], Identity.Resolve(1, 2, [new(7, "same")], [new(99, "same")], pending));
        Assert.Throws<IdentityException>(() => Identity.Resolve(1, 2, [new(7, "same")], [], pending));
        Assert.Throws<IdentityException>(() => Identity.Resolve(1, 2, [new(7, "same"), new(8, "same")], [new(99, "same")], pending));
    }

    [Fact]
    public void KnownMappingDetectsByteInstabilityAndExternalLabelRemoval()
    {
        Mapping[] maps = [new(1, 7, 2, 99, "same")];
        Assert.Throws<IdentityException>(() => Identity.Resolve(1, 2, [new(7, "same")], [new(99, "changed")], maps));
        Assert.Empty(Identity.Resolve(1, 2, [new(7, "same")], [], maps));
    }

    [Fact]
    public void EpochSurvivesRestartAndRejectsUidReuse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            new Journal(dir, "user", "endpoint", "label").ValidateEpoch(10);
            var restarted = new Journal(dir, "user", "endpoint", "label");
            restarted.ValidateEpoch(10);
            Assert.Throws<IdentityException>(() => restarted.ValidateEpoch(11));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void JournalSurvivesRestartAndReloadsOtherSessions()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var a = new Journal(dir, "user", "bridge:1143", "label");
            var b = new Journal(dir, "user", "bridge:1143", "label");
            a.Save(new(1, 7, 2, 99, "hash"));
            b.Save(new(1, 8, 2, 100, "hash"));
            var restarted = new Journal(dir, "user", "bridge:1143", "label");
            Assert.Equal(2, restarted.Entries.Count);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
