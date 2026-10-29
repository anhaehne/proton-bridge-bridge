using System.Text;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class ParserRobustnessTests
{
    [Fact]
    public async Task GeneratedQuotedLabelsRoundTripAcrossFragmentedAndPipelinedCommandsAsync()
    {
        var random = new Random(578234);
        for (var sample = 0; sample < 300; sample++)
        {
            var alphabet = "abCD19 -_\\\"é界";
            var value = new string(Enumerable.Range(0, random.Next(1, 100)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var bytes = Encoding.UTF8.GetBytes($"a LOGIN {Syntax.Quote(value)} pass\r\nb NOOP\r\n");
            using var stream = new FragmentedStream(bytes, random.Next(1, 13));
            var reader = new ImapReader(stream);
            var first = await reader.ReadCommandAsync(default);
            Assert.Equal(value, first![2].Text());
            var next = await reader.ReadCommandAsync(default);
            Assert.Equal("NOOP", next![1].Text());
            Assert.Null(await reader.ReadCommandAsync(default));
        }
    }

    [Fact]
    public async Task SeededByteMutationsTerminateWithoutUnexpectedParserExceptionsAsync()
    {
        var random = new Random(91340);
        var seed = Encoding.UTF8.GetBytes("a UID SEARCH ((NOT (X-GM-LABELS \"Paperless-imported\") UNKEYWORD Paperless-imported))\r\n");
        for (var sample = 0; sample < 1500; sample++)
        {
            var bytes = seed.ToArray();
            for (var mutation = 0; mutation < random.Next(1, 8); mutation++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            using var stream = new MemoryStream(bytes);
            try
            {
                await new ImapReader(stream).ReadCommandAsync(default);
            }
            catch (ImapSyntaxException)
            {
            }
            catch (DecoderFallbackException)
            {
                // Invalid UTF-8 must never be converted into a different credential or label.
            }
        }
    }

    [Theory]
    [InlineData(25, 0)]
    [InlineData(0, 65537)]
    public async Task DepthAndFrameBudgetsRejectOversizedInputsAsync(int depth, int atomLength)
    {
        var command = "a " + new string('(', depth) + new string('x', atomLength) + "ALL" + new string(')', depth) + "\r\n";
        await Assert.ThrowsAsync<ImapSyntaxException>(() => ParserTests.ParseAsync(command));
    }

    [Fact]
    public async Task OctetCountedUtf8LiteralDoesNotConsumeNextCommandAsync()
    {
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes("a LOGIN {5+}\r\né界 pass\r\nb NOOP\r\n"), 1);
        var reader = new ImapReader(stream);
        Assert.Equal("é界", (await reader.ReadCommandAsync(default))![2].Text());
        Assert.Equal("NOOP", (await reader.ReadCommandAsync(default))![1].Text());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlatBooleanQueriesCannotBypassSearchBudgetsAsync(bool nestedNegation)
    {
        var terms = string.Join(' ', Enumerable.Repeat(nestedNegation ? "NOT" : "ALL", 5000));
        var command = await ParserTests.ParseAsync($"a UID SEARCH {terms} ALL\r\n");
        Assert.Throws<ImapSyntaxException>(() => new SearchParser("Paperless-imported", []).Parse(command.Skip(3).ToArray()));
    }

    private sealed class FragmentedStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
    }
}
