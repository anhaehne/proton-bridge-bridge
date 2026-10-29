using System.Text;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class ParserTests
{
    internal static async Task<IReadOnlyList<ImapValue>> ParseAsync(string input)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        return await new ImapReader(stream).ReadCommandAsync(default) ?? throw new Exception();
    }

    [Fact]
    public async Task ParsesPaperlessNestedCriteriaAndEscapedLabelAsync()
    {
        var result = await ParseAsync("a UID SEARCH CHARSET UTF-8 ((NOT (X-GM-LABELS \"Paperless-imported\") UNKEYWORD Paperless-imported) SINCE 1-Oct-2026)\r\n");
        Assert.Equal("a", result[0].Text());
        var criteria = Assert.IsType<ListValue>(result[5]);
        Assert.NotNull(new SearchParser("Paperless-imported", [42]).Parse(criteria.Values));
        Assert.Equal("a\"b\\c", (await ParseAsync("a LOGIN \"a\\\"b\\\\c\" pass\r\n"))[2].Text());
    }

    [Fact]
    public async Task ParsesOctetCountedLiteralWithoutTreatingItAsCommandsAsync()
    {
        var result = await ParseAsync("a LOGIN {5+}\r\na\r\nbc password\r\n");
        Assert.Equal("a\r\nbc", result[2].Text());
        Assert.Equal("password", result[3].Text());
    }

    [Theory]
    [InlineData("a LOGIN \"unclosed\r\n")]
    [InlineData("a UID SEARCH (ALL\r\n")]
    [InlineData("a LOGIN {9999999+}\r\n")]
    [InlineData("a LOGIN {4+}\r\nab")]
    [InlineData("a NOOP\n")]
    public async Task RejectsMalformedOrOversizedFramesAsync(string command) =>
        await Assert.ThrowsAsync<ImapSyntaxException>(() => ParseAsync(command));

    [Theory]
    [InlineData("1*")]
    [InlineData("**")]
    [InlineData("1::2")]
    [InlineData("0")]
    [InlineData("1,")]
    public void InvalidUidRangesAreRejected(string value) => Assert.Throws<ImapSyntaxException>(() => Syntax.Uids(value));

    [Fact]
    public void WildcardUidUsesMailboxMaximum()
    {
        Assert.Contains(new MailKit.UniqueId(8), Syntax.Uids("*", 8));
        Assert.DoesNotContain(new MailKit.UniqueId(7), Syntax.Uids("*", 8));
        Assert.Contains(new MailKit.UniqueId(7), Syntax.Uids("6:*", 8));
    }

    [Fact]
    public async Task UnknownLabelOrCriterionDoesNotBecomeAnEmptySearchAsync()
    {
        var parser = new SearchParser("Paperless-imported", []);
        Assert.Throws<ImapSyntaxException>(() => parser.Parse([new TextValue("X-GM-LABELS"), new TextValue("Other")]));
        Assert.Throws<ImapSyntaxException>(() => parser.Parse([new TextValue("MYSTERY")]));
        var values = await ParseAsync("a UID FETCH 1:9 (BODY.PEEK[] UID FLAGS RFC822.SIZE)\r\n");
        Assert.Equal("BODY.PEEK[]", Assert.IsType<ListValue>(values[4]).Values[0].Text());
    }
}
