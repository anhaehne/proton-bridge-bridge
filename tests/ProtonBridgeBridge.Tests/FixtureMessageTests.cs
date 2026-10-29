using MimeKit;

namespace ProtonBridgeBridge.Tests;

public class FixtureMessageTests
{
    [Fact]
    public void ControlledTestMessageContainsBothNamedAndInlinePdfParts()
    {
        using var message = MimeMessage.Load(Path.Combine(AppContext.BaseDirectory, "controlled-test.eml"));
        var parts = message.BodyParts.OfType<MimePart>().Where(part => part.ContentType.MimeType == "application/pdf").ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Contains(parts, part => part.ContentDisposition?.Disposition == "inline");
        Assert.Contains(parts, part => part.ContentDisposition?.Disposition == "attachment");
        foreach (var part in parts)
        {
            using var content = new MemoryStream();
            var payload = part.Content ?? throw new InvalidDataException("Fixture content is missing.");
            payload.DecodeTo(content);
            Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(content.ToArray()));
        }
    }
}
