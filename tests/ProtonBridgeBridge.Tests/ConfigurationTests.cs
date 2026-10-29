using System.Text.Json;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class ConfigurationTests
{
    [Fact]
    public void WritesRequireAnExplicitOptIn()
    {
        var options = new Options { Certificate = "unused", PrivateKey = "unused" };
        options.Validate();
        Assert.False(options.AllowLabelWrites);
        Assert.False(options.AllowHashFallback);
    }

    [Theory]
    [InlineData("Paperless imported")]
    [InlineData("Paperless\r\n")]
    [InlineData("Labels/name")]
    [InlineData("\\Flagged")]
    public void LabelMustBeSafeForPaperlessBareStore(string label)
    {
        var options = new Options { Certificate = "unused", PrivateKey = "unused", Label = label };
        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Fact]
    public void UnknownJsonSettingIsRejectedInsteadOfSilentlyIgnoringSafetyConfiguration()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"Certificate\":\"unused\",\"PrivateKey\":\"unused\",\"AllowLabeWrites\":true}");
            Assert.Throws<JsonException>(() => Options.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"SourceValidity\":1,\"SourceUid\":0,\"LabelValidity\":2,\"Hash\":\"bad\"}]")]
    [InlineData("not json")]
    public void InvalidJournalIsNotTreatedAsAnEmptyProcessedSet(string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var journal = new Journal(directory, "user", "endpoint", "label");
            journal.Save(new(1, 7, 2, 99, Identity.Hash([1])));
            File.WriteAllText(Assert.Single(Directory.GetFiles(directory)), contents);
            Assert.Throws<IdentityException>(journal.Reload);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
